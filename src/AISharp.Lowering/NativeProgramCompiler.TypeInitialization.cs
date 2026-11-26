using System.Globalization;
using AISharp.Compiler;

namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed record TypeInitialized(int Owner, Value Operand, SourceLocation At) : Value(At) { public override ScalarType Type => Operand.Type; }
    private static ScalarStructMethod? TypeConstructor(int owner) => layouts![owner].Methods.FirstOrDefault(m => m.IsStaticConstructor);
    private static bool HasPreciseInitialization(int owner) => TypeConstructor(owner) is { IsImplicitStaticConstructor: false };
    private static bool SameTypeDeclaration(int a, int b) => a == b || layouts![a].Template >= 0 && layouts[a].Template == layouts[b].Template;
    private static bool IsStaticFunction(string name) => layouts!.Any(t => ScalarLanguage.IsRecordFactory(t,name)||t.IsRecord&&t.Methods.Any(m=>m.IsConstructor&&m.QualifiedName==name)||t.Methods.Any(m => m.IsStatic && m.QualifiedName == name));
    private static int? StaticConstructorOwner(string name)
    {
        for (var i = 0; i < layouts!.Count; i++) if (TypeConstructor(i)?.QualifiedName == name) return i;
        return null;
    }
    private static int? PreciseInitializationOwner(string name)
    {
        for (var i = 0; i < layouts!.Count; i++)
        {
            if (!HasPreciseInitialization(i)) continue;
            var type = layouts[i]; var method = type.Methods.FirstOrDefault(m => m.QualifiedName == name);
            if (method?.IsStaticConstructor == true) continue;
            if (method is { IsStatic: true } || type.IsRecord&&method?.IsConstructor==true || !type.IsClass && method is not null
                || !type.IsStatic && (name == type.Name + ".new" || name == type.Name + ".init")) return i;
        }
        return null;
    }

    private sealed partial class Verifier
    {
        private readonly int? initializingOwner;
        private void AddTypeInitialization(int owner, SourceLocation at)
        {
            if (TypeConstructor(owner) is not { } constructor) return;
            if (!signatures.TryGetValue(constructor.QualifiedName, out var signature) || signature.Parameters.Count != 0 || signature.Return != ScalarType.Unit)
                throw new Rejected(at.Line > 0 ? at : constructor.Declaration.Location, "A type initializer must be a verified parameterless void function.");
            Callees.Add(signature.Index);
        }
    }

    private sealed partial class Emitter
    {
        private bool hasTypeInitialization;
        private readonly Dictionary<int, int> typeInitializers = [];
        private readonly Dictionary<int, int> functionGuards = [];
        private void ConfigureTypeInitialization(IReadOnlyDictionary<string, Signature> signatures)
        {
            for (var i = 0; i < layouts!.Count; i++)
                if (TypeConstructor(i) is { } constructor) typeInitializers.Add(i, signatures[constructor.QualifiedName].Index);
            foreach (var (name, signature) in signatures)
                if (PreciseInitializationOwner(name) is { } owner) functionGuards.Add(signature.Index, owner);
        }
        private string TypeInitializationCall(int owner) => typeInitializers.TryGetValue(owner, out var function)
            ? "ai_type_init(" + owner.ToString(CultureInfo.InvariantCulture) + ", " + FunctionName(function) + ");\n" : "";
        private void EnsureType(int owner) => c.Append(TypeInitializationCall(owner));

        // One coordination lock protects state publication and the wait graph. Initializer code runs
        // outside the lock. Wait-graph cycle detection permits partial values rather than deadlocking.
        private static string TypeInitializationRuntime() => "static struct { int state; DWORD owner; } ai_types["
            + layouts!.Count.ToString(CultureInfo.InvariantCulture) + "] = {0};\n" + """
typedef struct ai_init_waiter { DWORD thread; int type; struct ai_init_waiter *next; } ai_init_waiter;
static SRWLOCK ai_init_lock = SRWLOCK_INIT;
static CONDITION_VARIABLE ai_init_changed = CONDITION_VARIABLE_INIT;
static ai_init_waiter *ai_init_waiters = NULL;
static int ai_init_cycle(DWORD thread, DWORD owner) {
 size_t limit = 1; for (ai_init_waiter *p = ai_init_waiters; p != NULL; p = p->next) ++limit;
 while (owner != 0 && limit-- != 0) {
  if (owner == thread) return 1;
  ai_init_waiter *p = ai_init_waiters; while (p != NULL && p->thread != owner) p = p->next;
  if (p == NULL || ai_types[p->type].state != 1) return 0;
  owner = ai_types[p->type].owner;
 }
 return 0;
}
static void ai_type_init(int type, void (*initialize)(void)) {
 DWORD thread = GetCurrentThreadId();
 AcquireSRWLockExclusive(&ai_init_lock);
 for (;;) {
  int state = ai_types[type].state;
  if (state == 2) { ReleaseSRWLockExclusive(&ai_init_lock); return; }
  if (state == 3) { ReleaseSRWLockExclusive(&ai_init_lock); ai_fault("System.TypeInitializationException"); }
  if (state == 0) { ai_types[type].state = 1; ai_types[type].owner = thread; break; }
  if (ai_init_cycle(thread, ai_types[type].owner)) { ReleaseSRWLockExclusive(&ai_init_lock); return; }
  ai_init_waiter waiter = { thread, type, ai_init_waiters }; ai_init_waiters = &waiter;
  BOOL woke = SleepConditionVariableSRW(&ai_init_changed, &ai_init_lock, INFINITE, 0);
  ai_init_waiter **link = &ai_init_waiters; while (*link != &waiter) link = &(*link)->next; *link = waiter.next;
  if (!woke) { ReleaseSRWLockExclusive(&ai_init_lock); ai_fault("System.InvalidOperationException"); }
 }
 ReleaseSRWLockExclusive(&ai_init_lock);
 ai_handler guard; guard.prev = ai_top; guard.depth = ai_depth; ai_top = &guard;
 int failed = setjmp(guard.env);
 if (failed == 0) initialize();
 ai_top = guard.prev; ai_depth = (int32_t)guard.depth;
 AcquireSRWLockExclusive(&ai_init_lock);
 ai_types[type].owner = 0; ai_types[type].state = failed == 0 ? 2 : 3;
 WakeAllConditionVariable(&ai_init_changed); ReleaseSRWLockExclusive(&ai_init_lock);
 if (failed != 0) ai_fault("System.TypeInitializationException");
}

""";
        // Allocation can fault via longjmp. Always release the arena lock before propagating it.
        private static string SynchronizedArenaRuntime() => ArenaRuntime().Replace("ai_arena_alloc(", "ai_arena_alloc_unlocked(", StringComparison.Ordinal) + """
static SRWLOCK ai_arena_lock = SRWLOCK_INIT;
static void *ai_arena_alloc(size_t bytes) {
 ai_handler guard; guard.prev = ai_top; guard.depth = ai_depth;
 AcquireSRWLockExclusive(&ai_arena_lock); ai_top = &guard;
 if (setjmp(guard.env) == 0) {
  void *result = ai_arena_alloc_unlocked(bytes); ai_top = guard.prev;
  ReleaseSRWLockExclusive(&ai_arena_lock); return result;
 }
 ai_top = guard.prev; ai_depth = (int32_t)guard.depth;
 ReleaseSRWLockExclusive(&ai_arena_lock); ai_fault(ai_exc);
}

""";
    }
}
