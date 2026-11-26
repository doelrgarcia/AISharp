using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AISharp.Compiler;

namespace AISharp.Lowering;

/// <summary>
/// Native program capability (N3). Builds on the N2 control-flow subset with compile-time constants, do/while,
/// checked/unchecked contexts, additional integer types and conversions, and functions with parameters and calls.
/// Verified IR is the only input to C11 emission; source names and text never reach C.
/// </summary>
public static partial class NativeProgramCompiler
{
    public const string Capability = "native-program-0.74.1";
    /// <summary>Process exit status of a native program that terminated with a thrown (faulted) outcome.</summary>
    public const int ThrewExitCode = 70;
    private const ScalarFeatures Features = ScalarFeatures.Control | ScalarFeatures.Checked | ScalarFeatures.Types | ScalarFeatures.Functions | ScalarFeatures.Bits | ScalarFeatures.Floats | ScalarFeatures.Chars | ScalarFeatures.Strings | ScalarFeatures.Decimals | ScalarFeatures.Enums | ScalarFeatures.Structs | ScalarFeatures.Patterns | ScalarFeatures.Classes | ScalarFeatures.Generics | ScalarFeatures.Exceptions | ScalarFeatures.Foreach | ScalarFeatures.Using | ScalarFeatures.Delegates | ScalarFeatures.Events | ScalarFeatures.Iterators | ScalarFeatures.Async | ScalarFeatures.Queries | ScalarFeatures.QueryJoins | ScalarFeatures.QueryGroups | ScalarFeatures.NullStrings | ScalarFeatures.Names | ScalarFeatures.RequiredMembers | ScalarFeatures.NativeIntegers | ScalarFeatures.ProcessArguments | ScalarFeatures.StaticMembers | ScalarFeatures.StaticInitialization | ScalarFeatures.ByReferenceParameters | ScalarFeatures.ReferenceVariables | ScalarFeatures.ReferenceReturns | ScalarFeatures.ReferenceMembers | ScalarFeatures.Indexers | ScalarFeatures.MutableStructAccessors | ScalarFeatures.ExtendedAccessors | ScalarFeatures.InstanceInitializers | ScalarFeatures.RecordInitializers | ScalarFeatures.RecordMembers | ScalarFeatures.RecordInheritance | ScalarFeatures.RecordConstructors | ScalarFeatures.RecordDisplay | ScalarFeatures.RecordHashing | ScalarFeatures.TypeIdentity | ScalarFeatures.TypeShapes | ScalarFeatures.ReadonlyStructs | ScalarFeatures.MutableStructMethods | ScalarFeatures.Boxing | ScalarFeatures.TypedArrays | ScalarFeatures.EscapedIdentifiers | ScalarFeatures.UnicodeIdentifiers | ScalarFeatures.Imports | ScalarFeatures.CompilationUnits | ScalarFeatures.StaticImports | ScalarFeatures.NestedTypes | ScalarFeatures.PartialTypes | ScalarFeatures.PartialMembers | ScalarFeatures.FileLocalTypes | ScalarFeatures.NamedArguments;
    public sealed record Preparation(string SourceSha256, string? CSource, IReadOnlyList<Diagnostic> Diagnostics)
    {
        public int PointerBits { get; init; } = 64;
        public bool AcceptsProcessArguments { get; init; }
        public bool Success => CSource is not null && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    private enum Op { Add, Sub, Mul, Div, Rem, Eq, Ne, Lt, Le, Gt, Ge, And, Or, Xor, Shl, Shr, UShr }

    // Closed, backend-neutral IR. Slots and loop ids are compiler-assigned; no source text is retained.
    private abstract record Value(SourceLocation Location) { public abstract ScalarType Type { get; } }
    private sealed record Const(ScalarType Kind, BigInteger Number, SourceLocation At) : Value(At) { public override ScalarType Type => Kind; }
    // f32 constants are held as the exactly equal double.
    private sealed record FConst(ScalarType Kind, double Number, SourceLocation At) : Value(At) { public override ScalarType Type => Kind; }
    // An immutable UTF-16 string literal; the text may contain lone surrogates.
    // A System.Decimal constant: 96-bit mantissa, sign and scale (the scale is part of the value, 1.10m != 1.1m as text).
    private sealed record DConst(decimal Number, SourceLocation At) : Value(At) { public override ScalarType Type => ScalarType.Decimal; }
    private sealed record SConst(string Text, SourceLocation At) : Value(At) { public override ScalarType Type => ScalarType.String; }
    // N26: the null string ({ NULL, 0 }); every non-null string, also the empty one, has a non-null pointer.
    private sealed record SNull(SourceLocation At) : Value(At) { public override ScalarType Type => ScalarType.String; }
    private sealed record StrLength(Value Operand, SourceLocation At) : Value(At) { public override ScalarType Type => ScalarType.Int; }
    private sealed record StrIndex(Value Text, Value Index, SourceLocation At) : Value(At) { public override ScalarType Type => ScalarType.Char; }
    private sealed record Read(int Slot, ScalarType Kind, SourceLocation At) : Value(At) { public override ScalarType Type => Kind; }
    private sealed record Negate(Value Operand, bool Wraps, SourceLocation At) : Value(At) { public override ScalarType Type => Operand.Type; }
    private sealed record Conversion(ScalarType To, Value Operand, bool Explicit, bool Wraps, SourceLocation At) : Value(At) { public override ScalarType Type => To; }
    private sealed record BitNot(Value Operand, SourceLocation At) : Value(At) { public override ScalarType Type => Operand.Type; }
    private sealed record Not(Value Operand, SourceLocation At) : Value(At) { public override ScalarType Type => ScalarType.Bool; }
    private sealed record Binary(Op Operator, Value Left, Value Right, bool Wraps, SourceLocation At) : Value(At)
    { public override ScalarType Type => Operator <= Op.Rem || Operator >= Op.And ? Left.Type : ScalarType.Bool; }
    private sealed record Logical(bool IsAnd, Value Left, Value Right, SourceLocation At) : Value(At) { public override ScalarType Type => ScalarType.Bool; }

    // Struct values: the all-zero value, a field of a struct value, a copy with fields replaced in order, and a store into a field path of a local.
    private sealed record Zero(ScalarType Kind, SourceLocation At, bool InitializeType = true) : Value(At) { public override ScalarType Type => Kind; }
    // Lenient: the read of the assigned field inside a compound assignment to an object field; a null object yields zero here and faults at the store.
    private sealed record FieldGet(Value Operand, int Index, ScalarType Kind, SourceLocation At, bool Lenient = false) : Value(At) { public override ScalarType Type => Kind; }
    private sealed record RecordCopy(Value Source, SourceLocation At) : Value(At)
    {
        public override ScalarType Type => Source.Type;
        public IReadOnlyList<RecordEqualityTarget> CopyConstructors { get; init; } = [];
    }
    private sealed record WithFields(Value Base, IReadOnlyList<(int Index, Value Value)> Fields, SourceLocation At, bool Constructor = false) : Value(At) { public override ScalarType Type => Base.Type; }
    private sealed record RuntimeValue(Value Value, SourceLocation At) : Value(At) { public override ScalarType Type => Value.Type; }
    private sealed record InitializeMembers(int Slot, Value Origin, IReadOnlyList<Value> Actions, SourceLocation At) : Value(At) { public override ScalarType Type => Origin.Type; }
    // A switch expression: the subject is stored in Slot, arm tests run in order, the first true test selects its result; no match faults.
    private sealed record SwitchValue(int Slot, Value Subject, IReadOnlyList<(Value Test, Value Result)> Arms, ScalarType Kind, bool Exhaustive, SourceLocation At) : Value(At) { public override ScalarType Type => Kind; public ScalarParameterMode SubjectMode {get;init;} }
    // A switch statement body: break inside it (not inside a nested loop) leaves the block.
    private sealed record SwitchBlock(int Id, IReadOnlyList<Statement> Body, bool UsesBreak, SourceLocation At) : Statement(At);
    private sealed record SwitchBreak(int Id, SourceLocation At) : Statement(At);
    private sealed record StoreField(int Slot, IReadOnlyList<int> Path, Value Value, SourceLocation At) : Statement(At);
    // Class objects: the null reference, and a store into a field path of an object. The object is evaluated first, then the value;
    // the null check happens after the value is evaluated, for direct fields and for paths into struct fields of the object alike (as observed on .NET 9 x64, tested).
    private sealed record NullRef(ScalarType Kind, SourceLocation At) : Value(At) { public override ScalarType Type => Kind; }
    private sealed record StoreRef(Value Target, IReadOnlyList<int> Path, Value Value, SourceLocation At, int ReceiverCapture = -1) : Statement(At);

    // The struct layouts of the program being compiled (storage field types: enums are already their underlying types).
    [ThreadStatic] private static IReadOnlyList<ScalarStruct>? layouts;
    private static bool IsStructType(ScalarType type) => layouts is { } all && (int)type >= ScalarLanguage.StructTypeBase && (int)type < ScalarLanguage.StructTypeBase + all.Count;
    private static ScalarStruct Layout(ScalarType type) => layouts![(int)type - ScalarLanguage.StructTypeBase];
    private static bool IsClassType(ScalarType type) => IsStructType(type) && Layout(type).IsClass;
    private static bool IsObjectType(ScalarType type) => (int)type == ScalarLanguage.ObjectTypeId;
    private static bool IsReferenceType(ScalarType type) => IsClassType(type) || IsObjectType(type);
    // object values: an upcast of a class reference, a run-time class test (false for null) and a checked downcast (InvalidCastException; null passes).
    private sealed record ToObject(Value Operand, SourceLocation At) : Value(At) { public override ScalarType Type => (ScalarType)ScalarLanguage.ObjectTypeId; }
    // Exact: the run-time class is Class itself (virtual dispatch); otherwise Class or any class derived from it or implementing it.
    // A pattern variable store inside a test: the value is stored into the (declared, mutable) local and the result is true.
    private sealed record BindStore(int Slot, Value Value, SourceLocation At) : Value(At) { public override ScalarType Type => ScalarType.Bool; }
    private sealed record BindField(StoreRef Store, SourceLocation At) : Value(At) { public override ScalarType Type => ScalarType.Bool; }
    private sealed record IsClassTest(Value Operand, ScalarType Class, SourceLocation At, bool Exact = false) : Value(At) { public override ScalarType Type => ScalarType.Bool; }
    // A derived class reference converted to a base class or an implemented interface: the same pointer.
    private sealed record Upcast(Value Operand, ScalarType Class, SourceLocation At) : Value(At) { public override ScalarType Type => Class; }
    private sealed record Downcast(Value Operand, ScalarType Class, SourceLocation At) : Value(At) { public override ScalarType Type => Class; }

    // CheckReceiver: an instance method of a class faults with NullReferenceException on a null receiver after the arguments are evaluated (C# callvirt).
    private sealed record Call(int Function, ScalarType Kind, IReadOnlyList<Value> Arguments, SourceLocation At, bool CheckReceiver = false) : Value(At) { public override ScalarType Type => Kind; public ScalarParameterMode ReturnMode { get; init; } }
    private sealed record ReceiverRead(int Capture, ScalarType Kind, SourceLocation At) : Value(At) { public override ScalarType Type => Kind; }

    private abstract record Statement(SourceLocation Location);
    private sealed record Eval(Value Value, SourceLocation At) : Statement(At);
    private sealed record Declare(int Slot, ScalarType Kind, Value? Initializer, SourceLocation At) : Statement(At);
    private sealed record Store(int Slot, Value Value, SourceLocation At) : Statement(At);
    private sealed record Return(Value? Value, SourceLocation At) : Statement(At);
    private sealed record Block(IReadOnlyList<Statement> Statements, SourceLocation At) : Statement(At);
    private sealed record If(Value Condition, IReadOnlyList<Statement> Then, IReadOnlyList<Statement> Else, SourceLocation At) : Statement(At);
    private sealed record Loop(int Id, Value Condition, IReadOnlyList<Statement> Body, IReadOnlyList<Statement> Step, bool NeedsContinueLabel, bool PostTest, SourceLocation At) : Statement(At);
    private sealed record Break(SourceLocation At) : Statement(At);
    // N17: try { Body } catch-clauses (Names: run-time exception names, or null for every exception) finally { Finally }; throw (Name null: rethrow the exception of try RethrowOf).
    private sealed record Try(int Id, IReadOnlyList<Statement> Body, IReadOnlyList<(IReadOnlyList<string>? Names, IReadOnlyList<Statement> Body)> Catches, IReadOnlyList<Statement>? Finally, SourceLocation At) : Statement(At);
    private sealed record Throw(string? Name, int RethrowOf, SourceLocation At) : Statement(At);
    private sealed record Continue(int LoopId, bool ViaLabel, SourceLocation At) : Statement(At);

    private sealed record Signature(int Index, IReadOnlyList<ScalarType> Parameters, ScalarType Return) { public IReadOnlyList<ScalarParameterMode> Modes { get; init; } = []; public IReadOnlyList<bool> Scoped { get; init; } = []; public ScalarParameterMode ReturnMode { get; init; } }
    private sealed record FunctionIr(int Index, IReadOnlyList<ScalarType> Parameters, ScalarType Return, IReadOnlyList<Statement> Body, IReadOnlySet<int> Callees) { public IReadOnlyList<ScalarParameterMode> Modes { get; init; } = []; public ScalarParameterMode ReturnMode { get; init; } public bool HasValueUpdates { get; init; } }

    private sealed class Rejected(SourceLocation at, string message) : Exception(message)
    { public SourceLocation At { get; } = at; }

    public static Preparation Prepare(string source)
        => Prepare(source, 64);
    public static Preparation Prepare(string source, int pointerBits)
    {
        ArgumentNullException.ThrowIfNull(source);if(pointerBits is not (32 or 64))throw new ArgumentOutOfRangeException(nameof(pointerBits));
        if(!ScalarLanguage.RequiresCompilationBinding(source))return PrepareCore(source,pointerBits,null);
        var result=PrepareCompilation(ScalarLanguage.BindCompilation(source),pointerBits);
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return new(hash,result.CSource,result.Diagnostics.Select(d=>d.Diagnostic).ToArray()){PointerBits=pointerBits,AcceptsProcessArguments=result.AcceptsProcessArguments};
    }
    private static Preparation PrepareCore(string source, int pointerBits, ScalarCompilation? compilation)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (pointerBits is not (32 or 64)) throw new ArgumentOutOfRangeException(nameof(pointerBits));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        IReadOnlyList<ScalarEnum> enums; IReadOnlyList<ScalarStruct> structs; ScalarNativeIntegerInfo target;
        var parsed = compilation is null ? ScalarLanguage.ParseExtended(source, Features, pointerBits, out enums, out structs, out target) : ScalarLanguage.ParseCompilation(compilation, Features, pointerBits, out enums, out structs, out target);
        var diagnostics = parsed.Diagnostics.ToList();
        if (!parsed.Success) return new(hash, null, diagnostics) { PointerBits = pointerBits };
        var previousLayouts = layouts; layouts = structs; var previousTypeEnums=typeEnums;typeEnums=enums;var previousTypeBits=typePointerBits;typePointerBits=pointerBits;var previousTypeModule=typeModule;typeModule=parsed.Document.Module;
        try
        {
            if(!ScalarRuntimeTypeConstraints.CheckEnumMetadata(structs,enums,out var enumFailure))throw new Rejected(new(1,1,0),enumFailure);
            ValidateReadonlyStructLayouts();
            ValidateStructMethodLayouts();
            ValidateValueBoxLayouts();
            ValidateTypedArrayLayouts();
            ValidateArrayEnumerableLayouts();
            ValidateArrayCollectionLayouts();
            var functions = parsed.Document.ScalarFunctions;
            if (functions.Count == 0) throw new Rejected(new(1, 1, 0), "Native program requires an entry function.");
            var signatures = new Dictionary<string, Signature>(StringComparer.Ordinal);
            for (var i = 0; i < functions.Count; i++)
            {
                var candidate = functions[i];
                if (!signatures.TryAdd(candidate.Name, new(i, candidate.Parameters.Select(p => p.Type).ToList(), candidate.ReturnType) { Modes = candidate.Parameters.Select(p => p.Mode).ToArray(), Scoped = candidate.Parameters.Select(p => p.Scoped).ToArray(), ReturnMode = candidate.ReturnMode }))
                    throw new Rejected(candidate.Location, "Duplicate function name.");
                if (candidate.Contracts.Count != 1 || candidate.Contracts[0].Kind != ContractKind.Ensures)
                    throw new Rejected(candidate.Location, "Native program requires exactly one ensures true contract per function.");
                if (candidate.Contracts[0].Expression is not { Operation: "literal", Type: ScalarType.Bool, Value: "true", Left: null, Right: null })
                    throw new Rejected(candidate.Contracts[0].Expression.Location, "Only the proven ensures true contract is supported.");
            }
            var entry = functions.FirstOrDefault(f => f.Name == "main") ?? throw new Rejected(functions[0].Location, "Native program requires an entry function named 'main'.");
            var nativeEntry = ScalarConstants.IsInteger(entry.ReturnType) && parsed.Document.Functions.Any(f => f.Name == "main" && f.ReturnType is "nint" or "nuint");
            if (entry.ReturnMode != ScalarParameterMode.Value || entry.Parameters.Any(p => p.Mode != ScalarParameterMode.Value)) throw new Rejected(entry.Location, "An entry point cannot have by-reference parameters.");
            var entryArguments = entry.Parameters.Count == 1 && IsStringArrayType(entry.Parameters[0].Type);
            if (entry.Parameters.Count != 0 && !entryArguments || !nativeEntry && entry.ReturnType is not (ScalarType.Int or ScalarType.Byte or ScalarType.SByte or ScalarType.Short or ScalarType.Bool or ScalarType.F32 or ScalarType.F64 or ScalarType.String or ScalarType.Decimal))
                throw new Rejected(entry.Location, "Native program requires fn main() or fn main(args: string[]) with an int/byte/sbyte/short/bool/nint/nuint/f32/f64/string/decimal result.");
            var verified = new List<FunctionIr>();
            foreach (var function in functions)
            {
                var exitDiagnostic = ScalarLanguage.ValidateReferenceExits(function, structs);
                if (exitDiagnostic is not null) throw new Rejected(exitDiagnostic.Location, exitDiagnostic.Message);
                var verifier = new Verifier(signatures, function.Name);
                var body = new List<Statement>();
                var completes = verifier.VerifyFunction(function, body);
                if (completes && function.ReturnType != ScalarType.Unit) throw new Rejected(function.Location, "Native function must return on every reaching path.");
                verified.Add(new(signatures[function.Name].Index, signatures[function.Name].Parameters, function.ReturnType, body, verifier.Callees) { Modes = signatures[function.Name].Modes, ReturnMode = function.ReturnMode, HasValueUpdates=verifier.HasValueUpdates });
            }
            return new(hash, new Emitter().Emit(verified, signatures["main"].Index, entry.ReturnType, target.Used ? pointerBits : null, entryArguments ? entry.Parameters[0].Type : null, signatures), diagnostics) { PointerBits = pointerBits, AcceptsProcessArguments = entryArguments };
        }
        catch (Rejected error)
        {
            diagnostics.Add(new("AI3201", DiagnosticSeverity.Error, error.Message, error.At));
            return new(hash, null, diagnostics) { PointerBits = pointerBits };
        }
        finally { layouts = previousLayouts;typeEnums=previousTypeEnums;typePointerBits=previousTypeBits;typeModule=previousTypeModule; }
    }

    private sealed class LoopInfo(int id, bool needsLabelTarget, bool isSwitch = false) { public int Id { get; } = id; public bool UsesLabel { get; } = needsLabelTarget; public bool IsSwitch { get; } = isSwitch; public bool Breaks; public bool Continues; public List<HashSet<string>> BreakStates { get; } = []; }

    /// <summary>Independently re-checks scope, types, mutability, constants, definite assignment, reachability and jump targets.</summary>
    private sealed partial class Verifier
    {
        private sealed record Binding(int Slot, ScalarType Type, bool Mutable, Value? Constant) { public ScalarParameterMode Mode { get; init; } public int Context { get; init; } = 2; }
        private readonly Dictionary<string, Binding> bindings = new(StringComparer.Ordinal);
        private HashSet<string> assigned = new(StringComparer.Ordinal);
        private readonly Stack<LoopInfo> loops = new();
        private int nextSlot, nextLoop, nextTry;
        private bool constantInitializer;
        private readonly Stack<int> catchTries = new();
        private readonly Stack<int> finallyLoops = new();
        private readonly IReadOnlyDictionary<string, Signature> signatures;
        private readonly string currentFunctionName;
        private ScalarType returnType = ScalarType.Int;
        public HashSet<int> Callees { get; } = [];
        public Verifier(IReadOnlyDictionary<string, Signature> signatures, string functionName)
        {
            this.signatures = signatures; currentFunctionName = functionName; initializingOwner = StaticConstructorOwner(functionName);
            if (PreciseInitializationOwner(functionName) is { } owner) AddTypeInitialization(owner, default!);
        }
        // C# (as observed on .NET 9 x64) faults for a null object in 'o.f op= v' only after v is evaluated; the reads of o.f in the value are lenient.
        private readonly List<ScalarExpression> lenient = [];
        private (ScalarExpression Source, ReceiverRead Read)? savedReceiver;
        private int nextReceiver;
        // Erasure clones source nodes. Locations and the full invocation tree distinguish a
        // synthesized reread of one receiver from a second call actually written by the user.
        private static bool SameSource(ScalarExpression a, ScalarExpression b) => ReferenceEquals(a, b)
            || a.Operation == b.Operation && a.Value == b.Value && a.Type == b.Type && Equals(a.Location, b.Location)
            && (a.Left is null ? b.Left is null : b.Left is not null && SameSource(a.Left, b.Left))
            && (a.Right is null ? b.Right is null : b.Right is not null && SameSource(a.Right, b.Right))
            && (a.Arguments is null ? b.Arguments is null : b.Arguments is not null && a.Arguments.Count == b.Arguments.Count
                && a.Arguments.Zip(b.Arguments).All(pair => SameSource(pair.First, pair.Second)));
        private static bool SameRead(ScalarExpression a, ScalarExpression b) =>
            a.Operation == b.Operation && a.Value == b.Value && a.Type == b.Type && (a.Operation == "local" || a.Operation == "field" && a.Left is not null && b.Left is not null && SameRead(a.Left, b.Left));

        private static ScalarType Map(ScalarType type, SourceLocation at) =>
            type is ScalarType.Bool or ScalarType.String or ScalarType.Decimal || ScalarConstants.IsInteger(type) && !ScalarConstants.IsNative(type) || ScalarConstants.IsFloat(type) || IsStructType(type) || IsObjectType(type) ? type : throw new Rejected(at, "Native program supports integer, floating-point, decimal, string, bool, struct, class and object values only.");
        private static ScalarStructField FieldOf(ScalarType type, string? index, SourceLocation at)
        {
            if (!IsStructType(type) || !int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var i) || i >= Layout(type).Fields.Count)
                throw new Rejected(at, "Expected a field of a verified struct.");
            return Layout(type).Fields[i];
        }
        // Arithmetic and comparison also accept f32 and f64.
        private static ScalarType Arith(ScalarType type, SourceLocation at) =>
            ScalarConstants.IsFloat(type) || type == ScalarType.Decimal ? type : Promoted(type, at);
        // Arithmetic and comparison operate on the promoted types only; narrower operands carry explicit conversions.
        private static ScalarType Promoted(ScalarType type, SourceLocation at) =>
            type is ScalarType.Int or ScalarType.UInt or ScalarType.Long or ScalarType.ULong ? type
            : throw new Rejected(at, "Operands must be int, uint, long or ulong after conversion.");
        private Binding Lookup(ScalarExpression expression, bool reading)
        {
            if (expression.Operation != "local" || expression.Value is null || !bindings.TryGetValue(expression.Value, out var binding)
                || binding.Type != Map(expression.Type, expression.Location))
                throw new Rejected(expression.Location, "Expected an in-scope typed local.");
            if (reading && !ReferenceAssigned(expression)) throw new Rejected(expression.Location, "Local read requires definite assignment.");
            return binding;
        }
        private static Value Require(Value value, ScalarType type, SourceLocation at) =>
            value.Type == type ? value : throw new Rejected(at, "Operand type is outside the verified native operation.");

        private static Const FoldArithmetic(Op op, Const left, Const right, bool wraps, SourceLocation at)
        {
            var symbol = op switch { Op.Add => "+", Op.Sub => "-", Op.Mul => "*", Op.Div => "/", _ => "%" };
            if (ScalarConstants.Arithmetic(symbol, left.Type, left.Number, right.Number, wraps, out var result) != ScalarFault.None)
                throw new Rejected(at, "Constant arithmetic faults and cannot be folded.");
            return new Const(left.Type, result, at);
        }

        private Value VerifyValue(ScalarExpression? expression, SourceLocation fallback)
        {
            if (expression is null) throw new Rejected(fallback, "Expected a value.");
            if(expression.RuntimeTypeShape is not null&&expression.Operation!="type-shape-literal")throw new Rejected(expression.Location,"A structural type description is valid only on a structural type literal.");
            if(expression.RuntimeTypeProof is not null&&expression.Operation!="type-shape-literal")throw new Rejected(expression.Location,"A generic type-query proof is valid only on a structural type literal.");
            if (savedReceiver is { } saved && SameSource(expression, saved.Source)) return saved.Read;
            var at = expression.Location;
            if(expression.Operation is "new" or "new-uninitialized"&&IsStructType(expression.Type)&&(Layout(expression.Type).IsNativeType||Layout(expression.Type).IsNativeRuntimeType))
                throw new Rejected(at,"Native Type descriptors are compiler-owned and cannot be constructed by source or forged allocation IR.");
            if(expression.Operation is "new" or "new-uninitialized" or "init" or "init-record" or "init-members"&&IsStructType(expression.Type)&&Layout(expression.Type).IsStringObject)
                throw new Rejected(at,"Native string identity storage can be created only by its verified immutable reference bridge.");
            if ((expression.Operation is "new" or "new-uninitialized" or "init" or "init-record" or "init-members") && IsStructType(expression.Type) && Layout(expression.Type).ArrayElementType is not null)
                throw new Rejected(at, "Native typed arrays require verified array allocation and stores.");
            Value Operand(ScalarExpression? e, ScalarType type) => Require(VerifyValue(e, at), type, e?.Location ?? at);
            switch (expression.Operation)
            {
                case "reference-probe": return VerifyReferenceProbe(expression);
                case "reference-return": return VerifyReferenceReturn(expression);
                case "ref-select": return VerifyReferenceChoice(expression, false, true);
                case "ref-reassign": return VerifyReferenceReassignment(expression);
                case "reference-read": return VerifyReferenceRead(expression);
                case "accessor-mutation": return VerifyAccessorMutation(expression);
                case "reference-store": case "reference-update": return VerifyReferenceMutation(expression);
                case "=" when expression.Left is { } store && (IndirectReferenceLocation(store) || store.Operation == "local" && Lookup(store,false).Mode != ScalarParameterMode.Value): return VerifyReferenceMutation(expression with { Operation = "reference-store" });
                case "=": return VerifyAssignedValue(expression);
                case "pre++": case "post++": case "pre--": case "post--": return VerifyIncrementValue(expression);
                case "field" when ReferencePath(expression) is not null:
                    if (!ReferenceAssigned(expression)) throw new Rejected(at, "A field read requires definite assignment.");
                    return VerifyReferenceStorage(expression, false, false, true);
                case "type-init-value" when expression is { Left: not null, Right: null, Arguments: null }:
                {
                    if (!int.TryParse(expression.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var owner)
                        || owner < 0 || owner >= layouts!.Count || !layouts[owner].IsRecord || !HasPreciseInitialization(owner))
                        throw new Rejected(at, "A generated record operator needs a verified type initializer.");
                    AddTypeInitialization(owner, at);
                    return new TypeInitialized(owner, Require(VerifyValue(expression.Left, at), expression.Type, at), at);
                }
                case "static-field": return VerifyStatic(expression);
                case "static-event-update": return VerifyStaticEventUpdate(expression);
                case "array-new" or "array-index" or "array-store" or "array-copy": return VerifyArrayValue(expression, at);
                case "runtime" when expression is { Left: not null, Right: null, Arguments: null }:
                    return new RuntimeValue(Require(VerifyValue(expression.Left, at), expression.Type, at), at);
                case "literal" when expression is { Left: null, Right: null }:
                    if (expression.Type == ScalarType.Bool && expression.Value is "true" or "false")
                        return new Const(ScalarType.Bool, expression.Value == "true" ? 1 : 0, at);
                    if (expression.Type == ScalarType.F64 && double.TryParse(expression.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wide) && double.IsFinite(wide))
                        return new FConst(ScalarType.F64, wide, at);
                    if (expression.Type == ScalarType.F32 && float.TryParse(expression.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var narrow) && float.IsFinite(narrow))
                        return new FConst(ScalarType.F32, narrow, at);
                    if (expression.Type == ScalarType.Decimal && decimal.TryParse(expression.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact))
                        return new DConst(exact, at);
                    if (expression.Type == ScalarType.String && expression.Value is { } text)
                        return new SConst(text, at);
                    if (ScalarConstants.IsInteger(expression.Type)
                        && BigInteger.TryParse(expression.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                        && ScalarConstants.Fits(number, expression.Type))
                        return new Const(expression.Type, number, at);
                    break;
                case "local":
                {
                    var binding = Lookup(expression, true);
                    return binding.Constant switch
                    {
                        Const constant => new Const(constant.Kind, constant.Number, at),
                        FConst constant => new FConst(constant.Kind, constant.Number, at),
                        DConst constant => new DConst(constant.Number, at),
                        SConst constant => new SConst(constant.Text, at),
                        SNull => new SNull(at),
                        NullRef constant => new NullRef(constant.Kind, at),
                        _ => new Read(binding.Slot, Map(expression.Type, at), at)
                    };
                }
                case "unary+" when expression.Right is null:
                    return Operand(expression.Left, Arith(expression.Type, at));
                case "unary-" when expression.Right is null && expression.Type == ScalarType.Decimal:
                {
                    var operand = Operand(expression.Left, ScalarType.Decimal);
                    return operand is DConst d ? new DConst(-d.Number, at) : new Negate(operand, false, at);
                }
                case "unary-" when expression.Right is null && ScalarConstants.IsFloat(expression.Type):
                {
                    var operand = Operand(expression.Left, expression.Type);
                    return operand is FConst f ? new FConst(f.Kind, -f.Number, at) : new Negate(operand, false, at);
                }
                case "unary-" when expression.Right is null:
                {
                    var type = expression.Type;
                    if (type is not (ScalarType.Int or ScalarType.Long)) throw new Rejected(at, "Negation is verified for Int32 and Int64 operands only.");
                    var operand = Operand(expression.Left, type);
                    if (operand is Const c)
                    {
                        if (ScalarConstants.Negate(type, c.Number, expression.Wraps, out var negated) != ScalarFault.None)
                            throw new Rejected(at, "Constant negation faults and cannot be folded.");
                        return new Const(type, negated, at);
                    }
                    return new Negate(operand, expression.Wraps, at);
                }
                case "unary!" when expression.Right is null:
                {
                    var operand = Operand(expression.Left, ScalarType.Bool);
                    return operand is Const c ? new Const(ScalarType.Bool, c.Number.IsZero ? 1 : 0, at) : new Not(operand, at);
                }
                case "convert" when expression.Right is null && expression.Left is not null:
                {
                    var operand = VerifyValue(expression.Left, at); var to = Map(expression.Type, at);
                    if (operand.Type == to || !ScalarConstants.ImplicitNumeric(operand.Type, to))
                        throw new Rejected(at, "Implicit conversion is not an implicit numeric conversion.");
                    if (to == ScalarType.Decimal && operand is Const dk) return new DConst((decimal)dk.Number, at);
                    if (ScalarConstants.IsFloat(to) && operand is Const ik) return new FConst(to, ScalarConstants.IntegerToFloat(ik.Number, to), at);
                    if (ScalarConstants.IsFloat(to) && operand is FConst fk) return new FConst(to, fk.Number, at);
                    return operand is Const k ? new Const(to, k.Number, at) : new Conversion(to, operand, false, false, at);
                }
                case "cast" when expression.Right is null && expression.Left is not null:
                {
                    var operand = VerifyValue(expression.Left, at); var to = Map(expression.Type, at);
                    var numeric = (Func<ScalarType, bool>)(t => ScalarConstants.IsInteger(t) || ScalarConstants.IsFloat(t) || t == ScalarType.Decimal);
                    if (!numeric(operand.Type) || !numeric(to)) throw new Rejected(at, "Only numeric casts are verified.");
                    if (to == ScalarType.Decimal && operand.Type != ScalarType.Decimal)
                    {
                        // Explicit conversion to decimal: exact for integers, float and double round to 7 and 15 significant digits (System.Decimal); out-of-range faults.
                        if (operand is Const di) return new DConst((decimal)di.Number, at);
                        if (operand is FConst df)
                        {
                            try { return new DConst(df.Kind == ScalarType.F32 ? (decimal)(float)df.Number : (decimal)df.Number, at); }
                            catch (OverflowException) { throw new Rejected(at, "Constant conversion faults and cannot be folded."); }
                        }
                        return new Conversion(to, operand, true, false, at);
                    }
                    if (operand.Type == ScalarType.Decimal)
                    {
                        if (to == ScalarType.Decimal) return operand;
                        if (operand is DConst dd)
                        {
                            if (ScalarConstants.IsFloat(to)) return new FConst(to, to == ScalarType.F32 ? (double)(float)dd.Number : (double)dd.Number, at);
                            var truncated = new BigInteger(decimal.Truncate(dd.Number));
                            if (!ScalarConstants.Fits(truncated, to)) throw new Rejected(at, "Constant conversion faults and cannot be folded.");
                            return new Const(to, truncated, at);
                        }
                        return new Conversion(to, operand, true, false, at);
                    }
                    if (ScalarConstants.IsFloat(to) && operand is Const ik) return new FConst(to, ScalarConstants.IntegerToFloat(ik.Number, to), at);
                    if (ScalarConstants.IsFloat(to) && operand is FConst fk) return new FConst(to, ScalarConstants.RoundTo(to, fk.Number), at);
                    if (ScalarConstants.IsFloat(operand.Type) && ScalarConstants.IsInteger(to) && operand is FConst fi)
                    {
                        if (ScalarConstants.FloatConstantToInteger(to, fi.Number, expression.Wraps, out var truncated) != ScalarFault.None)
                            throw new Rejected(at, "Constant conversion faults and cannot be folded.");
                        return new Const(to, truncated, at);
                    }
                    if (operand is Const k)
                    {
                        if (ScalarConstants.Convert(to, k.Number, expression.Wraps, out var converted) != ScalarFault.None)
                            throw new Rejected(at, "Constant conversion faults and cannot be folded.");
                        return new Const(to, converted, at);
                    }
                    return new Conversion(to, operand, true, expression.Wraps, at);
                }
                case "+" or "-" or "*" or "/" or "%":
                {
                    var op = expression.Operation switch { "+" => Op.Add, "-" => Op.Sub, "*" => Op.Mul, "/" => Op.Div, _ => Op.Rem };
                    if (expression.Type == ScalarType.String)
                    {
                        if (op != Op.Add) throw new Rejected(at, "Only + is defined for strings.");
                        var a = Operand(expression.Left, ScalarType.String); var b = Operand(expression.Right, ScalarType.String);
                        if ((constantInitializer || layouts!.Any(s=>s.IsStringObject)) && a is SConst or SNull && b is SConst or SNull)
                        {
                            var x = (a as SConst)?.Text; var y = (b as SConst)?.Text;
                            if ((long)(x?.Length ?? 0) + (y?.Length ?? 0) > ScalarLanguage.MaxConstantStringUnits)
                                throw new Rejected(at, "A string constant exceeds the supported UTF-16 length limit.");
                            return new SConst(x + y, at);
                        }
                        return new Binary(Op.Add, a, b, false, at);
                    }
                    var type = Arith(expression.Type, at);
                    var left = Operand(expression.Left, type); var right = Operand(expression.Right, type);
                    if (type == ScalarType.Decimal)
                    {
                        if (left is DConst dleft && right is DConst dright)
                        {
                            try
                            {
                                var (l0, r0) = (dleft.Number, dright.Number);
                                return new DConst(op switch { Op.Add => l0 + r0, Op.Sub => l0 - r0, Op.Mul => l0 * r0, Op.Div => l0 / r0, _ => l0 % r0 }, at);
                            }
                            catch (Exception fault) when (fault is OverflowException or DivideByZeroException) { throw new Rejected(at, "Constant arithmetic faults and cannot be folded."); }
                        }
                        return new Binary(op, left, right, false, at);
                    }
                    if (ScalarConstants.IsFloat(type))
                        return left is FConst fl && right is FConst fr ? new FConst(type, ScalarConstants.FloatArithmetic(expression.Operation, type, fl.Number, fr.Number), at) : new Binary(op, left, right, false, at);
                    return left is Const l && right is Const r ? FoldArithmetic(op, l, r, expression.Wraps, at) : new Binary(op, left, right, expression.Wraps, at);
                }
                case "<" or "<=" or ">" or ">=":
                {
                    var op = expression.Operation switch { "<" => Op.Lt, "<=" => Op.Le, ">" => Op.Gt, _ => Op.Ge };
                    var left = VerifyValue(expression.Left, at); var right = VerifyValue(expression.Right, at);
                    Arith(left.Type, at);
                    if (right.Type != left.Type) throw new Rejected(at, "Comparison operands must have one promoted type.");
                    if (left is FConst fl && right is FConst fr) return new Const(ScalarType.Bool, ScalarConstants.FloatCompare(expression.Operation, fl.Number, fr.Number) ? 1 : 0, at);
                    if (left is DConst dl && right is DConst dr)
                    {
                        var dc = decimal.Compare(dl.Number, dr.Number);
                        return new Const(ScalarType.Bool, (op switch { Op.Lt => dc < 0, Op.Le => dc <= 0, Op.Gt => dc > 0, _ => dc >= 0 }) ? 1 : 0, at);
                    }
                    if (left is Const l && right is Const r)
                    {
                        var c = l.Number.CompareTo(r.Number);
                        var truth = op switch { Op.Lt => c < 0, Op.Le => c <= 0, Op.Gt => c > 0, _ => c >= 0 };
                        return new Const(ScalarType.Bool, truth ? 1 : 0, at);
                    }
                    return new Binary(op, left, right, false, at);
                }
                case "==" or "!=":
                {
                    var left = VerifyValue(expression.Left, at); var right = VerifyValue(expression.Right, at);
                    if (left.Type != right.Type) throw new Rejected(at, "Equality operands must have one type.");
                    if (left.Type is not (ScalarType.Bool or ScalarType.String) && !IsReferenceType(left.Type)) Arith(left.Type, at);
                    if (left is FConst fl && right is FConst fr) return new Const(ScalarType.Bool, ScalarConstants.FloatCompare(expression.Operation, fl.Number, fr.Number) ? 1 : 0, at);
                    if (left is DConst dl && right is DConst dr) return new Const(ScalarType.Bool, (decimal.Compare(dl.Number, dr.Number) == 0) == (expression.Operation == "==") ? 1 : 0, at);
                    if (left is Const l && right is Const r) return new Const(ScalarType.Bool, l.Number == r.Number == (expression.Operation == "==") ? 1 : 0, at);
                    if (constantInitializer && left is SConst or SNull && right is SConst or SNull)
                        return new Const(ScalarType.Bool, string.Equals((left as SConst)?.Text, (right as SConst)?.Text, StringComparison.Ordinal) == (expression.Operation == "==") ? 1 : 0, at);
                    return new Binary(expression.Operation == "==" ? Op.Eq : Op.Ne, left, right, false, at);
                }
                case "record-equal":case "record-equals":case "record-impl-equals":return VerifyRecordEqual(expression);
                case "record-contract-equal":return VerifyRecordContractEqual(expression);
                case "record-copy":return VerifyRecordCopy(expression);
                case "display-value":return VerifyDisplayValue(expression);
                case "type-literal":return VerifyTypeLiteral(expression);
                case "type-shape-literal":return VerifyTypeShapeLiteral(expression);
                case "runtime-type":return VerifyRuntimeType(expression);
                case "box-value":case "receiver-box":return VerifyBoxValue(expression);
                case "unbox-value":return VerifyUnboxValue(expression);
                case "string-object":return VerifyStringObject(expression);
                case "object-string":case "object-as-string":return VerifyObjectString(expression);
                case "type-hash":return VerifyNativeTypeHash(expression);
                case "type-name":return VerifyNativeTypeName(expression);
                case "hash-scalar":return VerifyScalarHash(expression);
                case "hash-contract":return VerifyHashContract(expression);
                case "hash-identity":return VerifyIdentityHash(expression);
                case "new-uninitialized" when expression is { Left: null, Right: null, Arguments: null } && IsClassType(expression.Type):
                    return new Zero(expression.Type, at, false);
                case "new" when expression is { Left: null, Right: null, Arguments: null } && IsStructType(expression.Type):
                    if (IsClassType(expression.Type) && HasPreciseInitialization((int)expression.Type - ScalarLanguage.StructTypeBase)) AddTypeInitialization((int)expression.Type - ScalarLanguage.StructTypeBase, at);
                    return new Zero(expression.Type, at);
                case "null" when expression is { Left: null, Right: null, Arguments: null } && expression.Type == ScalarType.String:
                    return new SNull(at);
                case "null" when expression is { Left: null, Right: null, Arguments: null } && IsReferenceType(expression.Type):
                    return new NullRef(expression.Type, at);
                case "toobject" when expression is { Left: not null, Right: null } && IsObjectType(expression.Type):
                {
                    var operand = VerifyValue(expression.Left, at);
                    if (!IsClassType(operand.Type)) throw new Rejected(at, "Only class references convert to object.");
                    return new ToObject(operand, at);
                }
                case "bindpat" when expression is { Left: { Operation: "local" } target, Right: not null } && expression.Type == ScalarType.Bool:
                {
                    var binding = Lookup(target, false);
                    if (!binding.Mutable || !assigned.Contains(target.Value!)) throw new Rejected(at, "A pattern variable must be a declared mutable local.");
                    return new BindStore(binding.Slot, Require(VerifyValue(expression.Right, at), binding.Type, expression.Right.Location), at);
                }
                case "bindpat" when expression is { Left: { Operation: "field" }, Right: not null } && expression.Type == ScalarType.Bool:
                {
                    // Async lowering hoists a declared binding into its continuation object.
                    // Reuse the typed field-store verifier rather than trusting the field path.
                    var store = VerifyUpdate(expression with { Operation = "=" }, at);
                    if (store is not StoreRef reference) throw new Rejected(at, "A hoisted pattern binding needs verified object storage.");
                    return new BindField(reference, at);
                }
                case "istype" or "isexact" when expression is { Left: not null, Right: null, Value: { } id } && expression.Type == ScalarType.Bool:
                {
                    var operand = VerifyValue(expression.Left, at);
                    if (!IsReferenceType(operand.Type)) throw new Rejected(at, "A type test needs a reference operand.");
                    var tested = (ScalarType)int.Parse(id, NumberStyles.None, CultureInfo.InvariantCulture);
                    if (!IsClassType(tested)) throw new Rejected(at, "A type test needs a verified class.");
                    return new IsClassTest(operand, tested, at, expression.Operation == "isexact");
                }
                case "downcast" when expression is { Left: not null, Right: null } && IsClassType(expression.Type):
                {
                    var operand = VerifyValue(expression.Left, at);
                    if (!IsReferenceType(operand.Type)) throw new Rejected(at, "A downcast needs a reference operand.");
                    return new Downcast(operand, expression.Type, at);
                }
                case "upcast" when expression is { Left: not null, Right: null } && IsClassType(expression.Type):
                {
                    var operand = VerifyValue(expression.Left, at);
                    if (!IsClassType(operand.Type) || !ScalarLanguage.Derives(layouts!, (int)operand.Type - ScalarLanguage.StructTypeBase, (int)expression.Type - ScalarLanguage.StructTypeBase))
                        throw new Rejected(at, "An upcast needs a class derived from its target.");
                    return new Upcast(operand, expression.Type, at);
                }
                case "field" when expression is { Left: not null, Right: null }:
                {
                    var operand = VerifyValue(expression.Left, at);
                    var field = FieldOf(operand.Type, expression.Value, at);
                    if (field.StorageType != expression.Type) throw new Rejected(at, "Field type does not match the struct layout.");
                    return new FieldGet(operand, int.Parse(expression.Value!, CultureInfo.InvariantCulture), field.StorageType, at, lenient.Any(read => SameRead(read, expression)));
                }
                case "init" or "init-record" when expression is { Left: not null, Right: null, Arguments: { } values, Value: { } list } && IsStructType(expression.Type):
                {
                    var origin = Require(VerifyValue(expression.Left, at), expression.Type, at);
                    var indices = list.Length == 0 ? [] : list.Split(',');
                    if (indices.Length != values.Count || indices.Distinct().Count() != indices.Length) throw new Rejected(at, "Object initializer fields do not match their values.");
                    var fields = new List<(int, Value)>();
                    for (var i = 0; i < values.Count; i++)
                    {
                        var field = FieldOf(expression.Type, indices[i], at);
                        fields.Add((int.Parse(indices[i], CultureInfo.InvariantCulture), Require(VerifyValue(values[i], at), field.StorageType, values[i].Location)));
                    }
                    var constructor = expression.Operation == "init-record";
                    if (constructor)
                    {
                        var owner = (int)expression.Type - ScalarLanguage.StructTypeBase;
                        if (!Layout(expression.Type).IsRecord || !HasPreciseInitialization(owner) || expression.Left.Operation != "new"
                            || fields.Count != Layout(expression.Type).Positional || !fields.Select(f => f.Item1).SequenceEqual(Enumerable.Range(0, fields.Count)))
                            throw new Rejected(at, "A record construction must initialize its positional members after evaluating its arguments.");
                        AddTypeInitialization(owner, at);
                    }
                    return new WithFields(origin, fields, at, constructor);
                }
                case "init-members" when expression is { Left: not null, Right: null, Value: { } name, Arguments: { Count: > 0 } actions } && IsStructType(expression.Type):
                {
                    var origin = Require(VerifyValue(expression.Left, at), expression.Type, at);
                    if (bindings.ContainsKey(name)) throw new Rejected(at, "Expected a fresh initializer receiver.");
                    var slot = nextSlot++;
                    bindings.Add(name, new(slot, origin.Type, !IsClassType(origin.Type), null)); assigned.Add(name);
                    var verified = new List<Value>();
                    try
                    {
                        foreach (var action in actions)
                        {
                            static bool Receiver(ScalarExpression? candidate, string expected, bool boxedSetter = false) => candidate is { Operation: "local" } ? candidate.Value == expected
                                : candidate is { Operation: "upcast" or "argument-ref" or "argument-in", Left: { } inner } ? Receiver(inner, expected, boxedSetter)
                                : boxedSetter && candidate is { Operation: "receiver-box", Left: { } payload } && Receiver(payload, expected);
                            var fieldUpdate = action.Operation == "init" && Receiver(action.Left, name);
                            bool DeclaredAccessor(string? function, bool getter) => layouts!.Any(t => t.Properties.Any(p =>
                            {
                                var accessor = getter ? p.Getter : p.Setter;
                                return accessor is not null && (accessor == function || t.Methods.Any(m => m.QualifiedName == accessor && m.Slot + ".virtual" == function));
                            }));
                            var setterCall = action is { Operation: "call", Arguments: { Count: >= 2 } args } && Receiver(args[0], name, boxedSetter: true) && DeclaredAccessor(action.Value, false);
                            var accessorStore = action is { Operation: "accessor-mutation", Value: "store", Left: { Operation: "call", Arguments: { Count: > 0 } setterArguments } setter }
                                && Receiver(setterArguments[0], name) && DeclaredAccessor(setter.Value, false);
                            var referenceStore = action is { Operation: "reference-store", Left: { Operation: "call", Arguments: { Count: > 0 } getterArguments } getter }
                                && Receiver(getterArguments[0], name) && DeclaredAccessor(getter.Value, true);
                            if (!fieldUpdate && !setterCall && !accessorStore && !referenceStore) throw new Rejected(action.Location, "An initializer action must target its own receiver and a declared field or accessor.");
                            var value = VerifyValue(action, at);
                            if (!accessorStore && !referenceStore && value.Type != ScalarType.Unit && value.Type != origin.Type) throw new Rejected(action.Location, "An initializer must update its receiver or invoke a unit setter.");
                            verified.Add(value);
                        }
                    }
                    finally { bindings.Remove(name); assigned.Remove(name); }
                    return new InitializeMembers(slot, origin, verified, at);
                }
                case "switch" or "switch-copy" or "switch-ref" or "switch-in" when expression is { Left: not null, Right: null, Value: { } name, Arguments: { Count: > 0 } arms } && arms.Count % 2 == 0:
                {
                    var mode=expression.Operation=="switch-ref"?ScalarParameterMode.Ref:expression.Operation=="switch-in"?ScalarParameterMode.In:ScalarParameterMode.Value;
                    if((mode!=ScalarParameterMode.Value||expression.Operation=="switch-copy")&&(!IsStructType(expression.Left.Type)||IsClassType(expression.Left.Type)))throw new Rejected(at,"A reference pattern subject requires a value-struct location.");
                    var subject = mode==ScalarParameterMode.Value?VerifyValue(expression.Left, at):VerifyReferenceStorage(expression.Left,mode==ScalarParameterMode.Ref,true);
                    if (subject.Type == ScalarType.Unit || bindings.ContainsKey(name)) throw new Rejected(at, "Expected a fresh switch subject.");
                    var kind = Map(expression.Type, at);
                    var slot = nextSlot++;
                    bindings.Add(name, new(slot, subject.Type, mode==ScalarParameterMode.Ref||expression.Operation=="switch-copy", null){Mode=mode,Context=mode==ScalarParameterMode.Value?2:ReferenceContext(expression.Left)}); assigned.Add(name);
                    var verified = new List<(Value, Value)>();
                    try
                    {
                        for (var i = 0; i < arms.Count; i += 2)
                            verified.Add((Require(VerifyValue(arms[i], at), ScalarType.Bool, arms[i].Location), Require(VerifyValue(arms[i + 1], at), kind, arms[i + 1].Location)));
                    }
                    finally { bindings.Remove(name); assigned.Remove(name); }
                    var exhaustive = verified[^1].Item1 is Const { Kind: ScalarType.Bool } last && !last.Number.IsZero;
                    return new SwitchValue(slot, subject, verified, kind, exhaustive, at){SubjectMode=mode};
                }
                case "length" when expression.Right is null && expression.Type == ScalarType.Int:
                    return new StrLength(Operand(expression.Left, ScalarType.String), at);
                case "index" when expression.Type == ScalarType.Char:
                    return new StrIndex(Operand(expression.Left, ScalarType.String), Operand(expression.Right, ScalarType.Int), at);
                case "call" when expression.Value is not null && expression.Arguments is not null && expression.Left is null && expression.Right is null:
                    return VerifyCall(expression);
                case "&" or "|" or "^":
                {
                    var op = expression.Operation switch { "&" => Op.And, "|" => Op.Or, _ => Op.Xor };
                    var left = VerifyValue(expression.Left, at); var right = VerifyValue(expression.Right, at);
                    if (left.Type != right.Type || left.Type != expression.Type) throw new Rejected(at, "Bitwise operands must have one type.");
                    if (left.Type != ScalarType.Bool) Promoted(left.Type, at);
                    if (left is Const l && right is Const r)
                        return new Const(left.Type, left.Type == ScalarType.Bool ? ScalarConstants.Bitwise(expression.Operation, l.Number, r.Number) : ScalarConstants.Bitwise(expression.Operation, l.Number, r.Number), at);
                    return new Binary(op, left, right, false, at);
                }
                case "<<" or ">>" or ">>>":
                {
                    var op = expression.Operation switch { "<<" => Op.Shl, ">>" => Op.Shr, _ => Op.UShr };
                    var type = Promoted(expression.Type, at);
                    var left = Operand(expression.Left, type); var right = Operand(expression.Right, ScalarType.Int);
                    if (left is Const l && right is Const r) return new Const(type, ScalarConstants.Shift(expression.Operation, type, l.Number, r.Number), at);
                    return new Binary(op, left, right, false, at);
                }
                case "unary~" when expression.Right is null:
                {
                    var type = Promoted(expression.Type, at); var operand = Operand(expression.Left, type);
                    return operand is Const c ? new Const(type, ScalarConstants.BitNot(type, c.Number), at) : new BitNot(operand, at);
                }
                case "&&" or "||":
                {
                    var left = Operand(expression.Left, ScalarType.Bool);
                    var beforeRight = new HashSet<string>(assigned, StringComparer.Ordinal);
                    var right = Operand(expression.Right, ScalarType.Bool);
                    var isAnd = expression.Operation == "&&";
                    if (Constant(left) != isAnd) assigned.IntersectWith(beforeRight);
                    if (left is Const l && right is Const r) return new Const(ScalarType.Bool, (isAnd ? !l.Number.IsZero && !r.Number.IsZero : !l.Number.IsZero || !r.Number.IsZero) ? 1 : 0, at);
                    return new Logical(isAnd, left, right, at);
                }
            }
            throw new Rejected(at, "Expression is outside the native program capability (assignment/update expressions are statements only).");
        }

        private static bool? Constant(Value value) => value is Const { Kind: ScalarType.Bool } c ? !c.Number.IsZero : null;

        public bool VerifyFunction(ScalarFunction function, List<Statement> output)
        {
            returnType = function.ReturnType == ScalarType.Unit ? ScalarType.Unit : Map(function.ReturnType, function.Location); returnMode = function.ReturnMode;
            if (returnMode is not (ScalarParameterMode.Value or ScalarParameterMode.Ref or ScalarParameterMode.RefReadonly)) throw new Rejected(function.Location, "Invalid return reference mode.");
            foreach (var parameter in function.Parameters)
            {
                var type = Map(parameter.Type, function.Location);
                if (bindings.ContainsKey(parameter.Name)) throw new Rejected(function.Location, "Duplicate parameter binding.");
                if (!Enum.IsDefined(parameter.Mode)) throw new Rejected(function.Location, "Invalid parameter passing mode.");
                bindings.Add(parameter.Name, new(nextSlot++, type, parameter.Mode is not (ScalarParameterMode.In or ScalarParameterMode.RefReadonly), null) { Mode = parameter.Mode, Context = parameter.Mode is ScalarParameterMode.Ref or ScalarParameterMode.In or ScalarParameterMode.RefReadonly && !parameter.Scoped ? 1 : 2 });
                if (parameter.Mode != ScalarParameterMode.Out) assigned.Add(parameter.Name);
            }
            return VerifyBlock(function.Body, output);
        }

        // Returns true when control can fall off the end of the list.
        private bool VerifyBlock(IReadOnlyList<ScalarStatement> statements, List<Statement> output)
        {
            referenceDepth++;
            var declared = new List<string>(); var completes = true;
            foreach (var statement in statements)
            {
                if (!completes) throw new Rejected(statement.Location, "Unreachable statements are outside the native program capability.");
                completes = VerifyStatement(statement, output, declared);
            }
            foreach (var name in declared) { bindings.Remove(name); assigned.Remove(name); }
            referenceDepth--; return completes;
        }

        private bool VerifyStatement(ScalarStatement statement, List<Statement> output, List<string> declared)
        {
            switch (statement.Kind)
            {
                case "ref-local": case "ref-local-slot": return VerifyReferenceDeclaration(statement, output, declared);
                case "local":
                {
                    var type = Map(statement.Type, statement.Location);
                    if (statement.Name is null || bindings.ContainsKey(statement.Name))
                        throw new Rejected(statement.Location, "Expected a uniquely bound local declaration.");
                    Value? value;
                    var savedConstant = constantInitializer; constantInitializer = statement.Const;
                    try { value = statement.Expression is null ? null : Require(VerifyValue(statement.Expression, statement.Location), type, statement.Expression.Location); }
                    finally { constantInitializer = savedConstant; }
                    if (statement.Const)
                    {
                        // A const is a compile-time value: it owns no storage and emits no native code.
                        if (value is not (Const or FConst or DConst or SConst or SNull or NullRef) || statement.Mutable)
                            throw new Rejected(statement.Expression?.Location ?? statement.Location, "A const requires an immutable compile-time constant.");
                        if (value is SConst text && text.Text.Length > ScalarLanguage.MaxConstantStringUnits)
                            throw new Rejected(statement.Expression?.Location ?? statement.Location, "A string constant exceeds the supported UTF-16 length limit.");
                        bindings.Add(statement.Name, new(-1, type, false, value)); declared.Add(statement.Name);
                        assigned.Add(statement.Name);
                        return true;
                    }
                    if (!statement.Mutable && value is null) throw new Rejected(statement.Location, "Immutable let requires initialization.");
                    var slot = nextSlot++;
                    bindings.Add(statement.Name, new(slot, type, statement.Mutable, null) { Context = referenceDepth }); declared.Add(statement.Name);
                    if (value is not null) assigned.Add(statement.Name);
                    output.Add(new Declare(slot, type, value, statement.Location));
                    return true;
                }
                case "expression" when statement.Expression is { Operation: "call" or "runtime-type" or "new" or "init" or "init-record" or "init-members" or "array-store" or "array-copy" or "static-event-update" or "reference-probe" or "ref-reassign" or "reference-store" or "reference-update" or "accessor-mutation" }:
                    output.Add(new Eval(VerifyValue(statement.Expression, statement.Location), statement.Location)); return true;
                case "expression":
                    output.Add(VerifyUpdate(statement.Expression, statement.Location)); return true;
                case "block":
                {
                    var nested = new List<Statement>(); var completes = VerifyBlock(statement.Body ?? [], nested);
                    output.Add(new Block(nested, statement.Location)); return completes;
                }
                case "return" when finallyLoops.Count > 0: throw new Rejected(statement.Location, "Control cannot leave the body of a finally clause.");
                case "return" when returnType == ScalarType.Unit:
                    if (statement.Expression is not null) throw new Rejected(statement.Expression.Location, "A unit function cannot return a value.");
                    output.Add(new Return(null, statement.Location)); return false;
                case "return":
                {
                    if (returnMode != ScalarParameterMode.Value && statement.Expression?.Operation != "reference-return" || returnMode == ScalarParameterMode.Value && statement.Expression?.Operation == "reference-return") throw new Rejected(statement.Location, "Return reference mode does not match the signature.");
                    var value = Require(VerifyValue(statement.Expression, statement.Location), returnType, statement.Expression?.Location ?? statement.Location);
                    output.Add(new Return(value, statement.Location)); return false;
                }
                case "if":
                {
                    var condition = Require(VerifyValue(statement.Expression, statement.Location), ScalarType.Bool, statement.Expression?.Location ?? statement.Location);
                    var before = new HashSet<string>(assigned, StringComparer.Ordinal);
                    var yes = new List<Statement>(); var yesCompletes = VerifyBlock(statement.Body ?? [], yes);
                    var afterYes = assigned; assigned = new(before, StringComparer.Ordinal);
                    var no = new List<Statement>(); var noCompletes = VerifyBlock(statement.Alternative ?? [], no);
                    var afterNo = assigned;
                    if (Constant(condition) is { } selected)
                    {
                        assigned = selected ? afterYes : afterNo;
                        output.Add(new Block(selected ? yes : no, statement.Location));
                        return selected ? yesCompletes : noCompletes;
                    }
                    assigned = (yesCompletes, noCompletes) switch
                    {
                        (true, true) => new HashSet<string>(afterYes.Intersect(afterNo), StringComparer.Ordinal),
                        (true, false) => afterYes,
                        (false, true) => afterNo,
                        _ => before
                    };
                    output.Add(new If(condition, yes, no, statement.Location));
                    return yesCompletes || noCompletes;
                }
                case "while":
                    output.Add(VerifyLoop(statement, null)); return LoopCompletes(output[^1]);
                case "dowhile":
                    output.Add(VerifyDoWhile(statement)); return LoopCompletes(output[^1]);
                case "for":
                {
                    // The initializer scope covers the loop only; it is a nested block in verified IR.
                    var scoped = new List<Statement>(); var scopeNames = new List<string>();
                    if (statement.Initializer is not { Kind: "local" or "ref-local" } && statement.Initializer is not { Kind: "expression", Expression.Operation: "ref-reassign" }) throw new Rejected(statement.Location, "For requires a local initializer.");
                    VerifyStatement(statement.Initializer, scoped, scopeNames);
                    scoped.Add(VerifyLoop(statement, statement.Increment));
                    foreach (var name in scopeNames) { bindings.Remove(name); assigned.Remove(name); }
                    output.Add(new Block(scoped, statement.Location)); return LoopCompletes(scoped[^1]);
                }
                case "switch":
                {
                    var info = new LoopInfo(nextLoop++, false, true);
                    loops.Push(info);
                    var body = new List<Statement>(); var completes = VerifyBlock(statement.Body ?? [], body);
                    loops.Pop();
                    // After the switch, a local is assigned only if it is assigned on every path that reaches the end.
                    var states = info.BreakStates.ToList(); if (completes) states.Add(assigned);
                    if (states.Count > 0) { var merged = new HashSet<string>(states[0], StringComparer.Ordinal); foreach (var state in states.Skip(1)) merged.IntersectWith(state); assigned = merged; }
                    output.Add(new SwitchBlock(info.Id, body, info.Breaks, statement.Location));
                    return completes || info.Breaks;
                }
                case "try":
                {
                    var id = nextTry++;
                    var before = new HashSet<string>(assigned, StringComparer.Ordinal);
                    var body = new List<Statement>(); var bodyCompletes = VerifyBlock(statement.Body ?? [], body);
                    var states = new List<HashSet<string>>(); if (bodyCompletes) states.Add(assigned);
                    var catches = new List<(IReadOnlyList<string>?, IReadOnlyList<Statement>)>(); List<Statement>? cleanup = null; HashSet<string>? afterCleanup = null;
                    var completes = bodyCompletes;
                    foreach (var clause in statement.Alternative ?? [])
                    {
                        assigned = new(before, StringComparer.Ordinal);
                        if (clause.Kind == "catch" && cleanup is null)
                        {
                            if (string.IsNullOrEmpty(clause.Name)) throw new Rejected(clause.Location, "A catch clause needs its exception names.");
                            catchTries.Push(id);
                            var handler = new List<Statement>(); bool handlerCompletes;
                            try { handlerCompletes = VerifyBlock(clause.Body ?? [], handler); } finally { catchTries.Pop(); }
                            if (handlerCompletes) states.Add(assigned);
                            completes |= handlerCompletes;
                            catches.Add((clause.Name == "*" ? null : clause.Name.Split(','), handler));
                        }
                        else if (clause.Kind == "finally" && cleanup is null)
                        {
                            cleanup = []; finallyLoops.Push(loops.Count);
                            bool cleanupCompletes;
                            try { cleanupCompletes = VerifyBlock(clause.Body ?? [], cleanup); } finally { finallyLoops.Pop(); }
                            if (!cleanupCompletes) throw new Rejected(clause.Location, "Control cannot leave the body of a finally clause.");
                            afterCleanup = assigned;
                        }
                        else throw new Rejected(clause.Location, "Expected catch clauses and at most one final finally clause.");
                    }
                    if (catches.Count == 0 && cleanup is null) throw new Rejected(statement.Location, "A try statement needs a catch or finally clause.");
                    var merged = states.Count == 0 ? new HashSet<string>(before, StringComparer.Ordinal) : new HashSet<string>(states[0], StringComparer.Ordinal);
                    foreach (var state in states.Skip(1)) merged.IntersectWith(state);
                    if (afterCleanup is not null) merged.UnionWith(afterCleanup);
                    assigned = merged;
                    output.Add(new Try(id, body, catches, cleanup, statement.Location));
                    return completes;
                }
                case "throw":
                    if (statement.Name is null)
                    {
                        if (catchTries.Count == 0) throw new Rejected(statement.Location, "A rethrow is allowed only in a catch clause.");
                        output.Add(new Throw(null, catchTries.Peek(), statement.Location));
                    }
                    else
                    {
                        if (!System.Text.RegularExpressions.Regex.IsMatch(statement.Name, @"^System\.([A-Za-z]*|Runtime\.CompilerServices\.SwitchExpression|Reflection\.TargetInvocation)Exception$")) throw new Rejected(statement.Location, "Expected a built-in exception type.");
                        output.Add(new Throw(statement.Name, -1, statement.Location));
                    }
                    return false;
                case "break":
                    if (loops.Count == 0) throw new Rejected(statement.Location, "break requires a loop.");
                    if (finallyLoops.Count > 0 && loops.Count <= finallyLoops.Peek()) throw new Rejected(statement.Location, "Control cannot leave the body of a finally clause.");
                    loops.Peek().Breaks = true;
                    if (loops.Peek().IsSwitch) { loops.Peek().BreakStates.Add(new(assigned, StringComparer.Ordinal)); output.Add(new SwitchBreak(loops.Peek().Id, statement.Location)); }
                    else output.Add(new Break(statement.Location));
                    return false;
                case "continue":
                {
                    var target = loops.FirstOrDefault(l => !l.IsSwitch) ?? throw new Rejected(statement.Location, "continue requires a loop.");
                    if (finallyLoops.Count > 0 && loops.Count - loops.ToList().IndexOf(target) <= finallyLoops.Peek()) throw new Rejected(statement.Location, "Control cannot leave the body of a finally clause.");
                    target.Continues = true;
                    output.Add(new Continue(target.Id, target.UsesLabel, statement.Location)); return false;
                }
                default: throw new Rejected(statement.Location, "Statement is outside the native program capability.");
            }
        }

        private readonly Dictionary<Loop, bool> loopCompletion = new(ReferenceEqualityComparer.Instance);
        private bool LoopCompletes(Statement loop) => loopCompletion[(Loop)loop];

        private Loop VerifyLoop(ScalarStatement statement, ScalarExpression? increment)
        {
            var condition = Require(VerifyValue(statement.Expression, statement.Location), ScalarType.Bool, statement.Expression?.Location ?? statement.Location);
            var before = new HashSet<string>(assigned, StringComparer.Ordinal);
            var info = new LoopInfo(nextLoop++, increment is not null);
            var step = new List<Statement>();
            if (increment is not null) { step.Add(VerifyUpdate(increment, statement.Location)); assigned = new(before, StringComparer.Ordinal); }
            loops.Push(info);
            var body = new List<Statement>(); VerifyBlock(statement.Body ?? [], body);
            loops.Pop();
            // Zero iterations are possible, so nothing assigned inside the loop is assigned after it.
            assigned = before;
            var loop = new Loop(info.Id, condition, body, step, info.UsesLabel && info.Continues, false, statement.Location);
            loopCompletion[loop] = Constant(condition) != true || info.Breaks;
            return loop;
        }

        private Loop VerifyDoWhile(ScalarStatement statement)
        {
            var before = new HashSet<string>(assigned, StringComparer.Ordinal);
            var info = new LoopInfo(nextLoop++, true);
            loops.Push(info);
            var body = new List<Statement>(); var bodyCompletes = VerifyBlock(statement.Body ?? [], body);
            loops.Pop();
            var afterBody = assigned;
            // continue skips the remainder of the body, so the condition may only rely on what held before it.
            assigned = info.Continues ? new(before, StringComparer.Ordinal) : new(afterBody, StringComparer.Ordinal);
            var condition = Require(VerifyValue(statement.Expression, statement.Location), ScalarType.Bool, statement.Expression?.Location ?? statement.Location);
            assigned = info.Breaks || info.Continues ? before : afterBody;
            var loop = new Loop(info.Id, condition, body, [], info.Continues, true, statement.Location);
            var reachesCondition = bodyCompletes || info.Continues;
            loopCompletion[loop] = info.Breaks || (reachesCondition && Constant(condition) != true);
            return loop;
        }

        // Statement-level assignment/update forms; every one becomes a verified Store.
        private Statement VerifyUpdate(ScalarExpression? expression, SourceLocation fallback)
        {
            if (expression?.Left is null) throw new Rejected(expression?.Location ?? fallback, "Expected an assignment or update statement.");
            var at = expression.Location;
            switch (expression.Operation)
            {
                case "array-store": return new Eval(VerifyArrayValue(expression, at), at);
                case "=" when IndirectReferenceLocation(expression.Left):
                    return new ReferenceStore(VerifyReferenceStorage(expression.Left, true, false), Require(VerifyValue(expression.Right, at), expression.Left.Type, at), at);
                case "static-event-update": return new Eval(VerifyStaticEventUpdate(expression), at);
                case "=" when expression.Right is not null && expression.Left.Operation == "static-field":
                    return VerifyStaticStore(expression.Left, [], Require(VerifyValue(expression.Right, at), expression.Left.Type, expression.Right.Location), at);
                case "=" when expression.Right is not null && expression.Left.Operation == "field":
                {
                    // A store into a field path of a mutable, definitely assigned struct local, or into a field path of a class object.
                    var path = new List<int>(); var node = expression.Left;
                    while (node.Operation == "field")
                    {
                        if(node.Left is not null&&IsClassType(node.Left.Type)&&Layout(node.Left.Type).IsStringObject)
                            throw new Rejected(node.Location,"Native string identity payloads cannot be assigned or updated.");
                        if(node.Left is not null&&IsClassType(node.Left.Type)&&Layout(node.Left.Type).ArrayElementType is not null)
                            throw new Rejected(node.Location,"Native array length storage is immutable.");
                        var field = FieldOf(node.Left?.Type ?? ScalarType.Unit, node.Value, node.Location);
                        if (field.StorageType != node.Type) throw new Rejected(node.Location, "Field type does not match the struct layout.");
                        path.Insert(0, int.Parse(node.Value!, CultureInfo.InvariantCulture)); node = node.Left!;
                        if (IsClassType(node.Type))
                        {
                            var target = VerifyValue(node, at);
                            var root = node; while (root.Operation == "field") root = root.Left!;
                            var capture = expression.Value == "compound" && root.Operation is not ("local" or "static-field") ? nextReceiver++ : -1;
                            var previousReceiver = savedReceiver;
                            if (capture >= 0) savedReceiver = (node, new ReceiverRead(capture, target.Type, node.Location));
                            Value stored;
                            try { stored = Require(VerifyValue(expression.Right, at), expression.Left.Type, expression.Right.Location); }
                            finally { lenient.Clear(); savedReceiver = previousReceiver; }
                            return new StoreRef(target, path, stored, at, capture);
                        }
                    }
                    if (node.Operation == "static-field") return VerifyStaticStore(node, path, Require(VerifyValue(expression.Right, at), expression.Left.Type, expression.Right.Location), at);
                    var binding = Lookup(node, false);
                    if (!binding.Mutable) throw new Rejected(node.Location, "Cannot assign a field of an immutable let.");
                    var value = Require(VerifyValue(expression.Right, at), expression.Left.Type, expression.Right.Location);
                    AssignReference(expression.Left);
                    return new StoreField(binding.Slot, path, value, at);
                }
                case "=" when expression.Right is not null:
                {
                    var binding = Lookup(expression.Left, false);
                    if (!binding.Mutable) throw new Rejected(expression.Left.Location, "Cannot assign immutable let.");
                    var value = Require(VerifyValue(expression.Right, at), binding.Type, expression.Right.Location);
                    AssignReference(expression.Left);
                    return new Store(binding.Slot, value, at);
                }
                case "+=" or "-=" when expression.Right is not null:
                {
                    var binding = Lookup(expression.Left, true);
                    if (!binding.Mutable || binding.Type != ScalarType.Int) throw new Rejected(expression.Left.Location, "Compound assignment requires a mutable Int32 local.");
                    var value = Require(VerifyValue(expression.Right, at), ScalarType.Int, expression.Right.Location);
                    return new Store(binding.Slot, new Binary(expression.Operation == "+=" ? Op.Add : Op.Sub,
                        new Read(binding.Slot, ScalarType.Int, expression.Left.Location), value, expression.Wraps, at), at);
                }
                case "pre++" or "post++" or "pre--" or "post--" when expression.Right is null:
                {
                    var binding = Lookup(expression.Left, true);
                    if (!binding.Mutable || binding.Type != ScalarType.Int) throw new Rejected(expression.Left.Location, "Increment requires a mutable Int32 local.");
                    return new Store(binding.Slot, new Binary(expression.Operation.EndsWith("++", StringComparison.Ordinal) ? Op.Add : Op.Sub,
                        new Read(binding.Slot, ScalarType.Int, expression.Left.Location), new Const(ScalarType.Int, 1, at), expression.Wraps, at), at);
                }
            }
            throw new Rejected(at, "Expression statement is outside the native program capability.");
        }
    }

    private sealed partial class Emitter
    {
        private StringBuilder c = new();
        private readonly Dictionary<string, string> helpers = new(StringComparer.Ordinal);
        private int temp;
        private bool usesConcat, usesIndex, usesEquality, usesLength;
        private readonly Dictionary<string, int> stringLiterals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> decimalLiterals = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> receiverNames = [];
        private string Slot(int slot) => readonlyObjectSlots.Contains(slot) ? ReadonlyObjectRead(RawSlot(slot)) : referenceSlots.Contains(slot) ? "(*" + RawSlot(slot) + ")" : RawSlot(slot);
        private string Temp() => "ai_t" + (temp++).ToString(CultureInfo.InvariantCulture);

        private const string FaultHelper =
            "#if defined(_MSC_VER)\n#define AI_NORETURN __declspec(noreturn)\n#else\n#define AI_NORETURN _Noreturn\n#endif\n"
            + "AI_NORETURN static void ai_fault(const char *exception) {\n"
            + " if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"threw\\\",\\\"exception\\\":\\\"%s\\\"}\\n\", exception) < 0) exit(74);\n"
            + " if (fflush(stdout) != 0) exit(74);\n exit(70);\n}\n";
        private static string ReportFloat(string fname, string ct, bool single)
        {
            var parse = single ? "strtof" : "strtod"; var max = single ? "FLT_MAX" : "DBL_MAX"; var limit = single ? 9 : 17;
            var head = "static int ai_report_" + fname + "(" + ct + " v) {\n"
                + " const char *prefix = \"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"type\\\":\\\"" + fname + "\\\",\\\"value\\\":\";\n"
                + " if (v != v) return printf(\"%s\\\"NaN\\\"}\\n\", prefix);\n"
                + " if (v > " + max + ") return printf(\"%s\\\"Infinity\\\"}\\n\", prefix);\n"
                + " if (v < -" + max + ") return printf(\"%s\\\"-Infinity\\\"}\\n\", prefix);\n"
                + " char text[64] = \"\"; int digits;\n"
                + " for (digits = 1; digits <= " + limit + "; digits++) { snprintf(text, sizeof text, \"%.*g\", digits, (double)v); if ((" + ct + ")" + parse + "(text, NULL) == v) break; }\n"
                + " if (strpbrk(text, \".eEn\") == NULL) strcat_s(text, sizeof text, \".0\");\n"
                + " return printf(\"%s%s}\\n\", prefix, text);\n}\n";
            return head;
        }
        private string Helper(string name, string text) { helpers.TryAdd(name, text); return name; }
        // Objects live in the per-run arena (zeroed, 8-byte aligned, freed at exit); a null reference access faults with NullReferenceException.
        private bool usesObjects;
        private string NewObject(ScalarType type, bool initializeType = true)
        {
            usesObjects = true; var ct = CType(type); var name = "ai_new_" + ((int)type - ScalarLanguage.StructTypeBase).ToString(CultureInfo.InvariantCulture) + (initializeType ? "" : "_raw");
            return Helper(name, $"static {ct}{name}(void) {{ {(initializeType && HasPreciseInitialization((int)type - ScalarLanguage.StructTypeBase) ? TypeInitializationCall((int)type - ScalarLanguage.StructTypeBase) : "")}{ct}p = ({ct})ai_arena_alloc(sizeof *p); memset(p, 0, sizeof *p); p->ai_tag = {ClassTag(type)}; return p; }}\n");
        }
        // ai_belongs_K(p) (helpers are emitted in name order, so it precedes its user ai_cast_K): p is non-null and its class is K, derives from K or implements K (the program is closed, so the set of class tags is known).
        private string IsInstance(ScalarType type)
        {
            var name = "ai_belongs_" + ClassTag(type);
            var tags = ScalarLanguage.ConcreteOf(layouts!, (int)type - ScalarLanguage.StructTypeBase).Select(k => "t == " + k.ToString(CultureInfo.InvariantCulture)).ToList();
            return Helper(name, tags.Count == 0
                ? $"static int32_t {name}(const void *p) {{ (void)p; return 0; }}\n"
                : $"static int32_t {name}(const void *p) {{ int32_t t; if (p == NULL) return 0; t = *(const int32_t *)p; return {string.Join(" || ", tags)}; }}\n");
        }
        private static string ClassTag(ScalarType type) => ((int)type - ScalarLanguage.StructTypeBase).ToString(CultureInfo.InvariantCulture);
        private string NonNull(ScalarType type)
        {
            var ct = CType(type); var name = "ai_nn_" + ((int)type - ScalarLanguage.StructTypeBase).ToString(CultureInfo.InvariantCulture);
            return Helper(name, $"static {ct}{name}({ct}p) {{ if (p == NULL) ai_fault(\"System.NullReferenceException\"); return p; }}\n");
        }

        // The string runtime: immutable (pointer, length) values over UTF-16 code units, literals as static data, and one lazily created
        // arena for concatenation results. The arena is never freed before exit; chunks are released by an atexit handler registered at the first allocation.
        private const string OutOfMemory = "ai_fault(\"System.OutOfMemoryException\");";
        private const int ArenaCapBytes = 268435456, ArenaChunkBytes = 65536;
        // The per-run arena: chunks are allocated lazily, never freed before exit, and released by an atexit handler registered at the first allocation.
        // Allocation sizes are rounded up to 8 bytes, so every allocation is 8-byte aligned. Exceeding the cap faults with OutOfMemoryException.
        private static string ArenaRuntime()
        {
            var t = new StringBuilder();
                t.Append("#define AI_ARENA_CAP ((size_t)").Append(ArenaCapBytes.ToString(CultureInfo.InvariantCulture)).Append(")\n#define AI_ARENA_CHUNK ((size_t)").Append(ArenaChunkBytes.ToString(CultureInfo.InvariantCulture)).Append(")\n");
                t.Append("typedef struct ai_chunk { struct ai_chunk *next; size_t capacity; size_t used; } ai_chunk;\n");
                t.Append("static ai_chunk *ai_arena_head = NULL;\nstatic size_t ai_arena_total = 0;\n");
                t.Append("static void ai_arena_release(void) { while (ai_arena_head != NULL) { ai_chunk *next = ai_arena_head->next; free(ai_arena_head); ai_arena_head = next; } }\n");
                t.Append("static void *ai_arena_alloc(size_t bytes) {\n bytes = (bytes + 7u) & ~(size_t)7u;\n ai_chunk *chunk = ai_arena_head;\n");
                t.Append(" if (chunk == NULL || bytes > chunk->capacity - chunk->used) {\n  int dedicated = bytes > AI_ARENA_CHUNK / 2;\n  size_t want = dedicated ? bytes : AI_ARENA_CHUNK;\n");
                t.Append("  if (want > AI_ARENA_CAP - ai_arena_total) ").Append(OutOfMemory).Append("\n");
                t.Append("  ai_chunk *fresh = (ai_chunk *)malloc(sizeof(ai_chunk) + want);\n  if (fresh == NULL) ").Append(OutOfMemory).Append("\n");
                t.Append("  if (ai_arena_head == NULL && atexit(ai_arena_release) != 0) { free(fresh); ").Append(OutOfMemory).Append(" }\n");
                t.Append("  ai_arena_total += want; fresh->capacity = want; fresh->used = 0;\n");
                t.Append("  if (dedicated && ai_arena_head != NULL) { fresh->next = ai_arena_head->next; ai_arena_head->next = fresh; }\n  else { fresh->next = ai_arena_head; ai_arena_head = fresh; }\n  chunk = fresh;\n }\n");
                t.Append(" void *result = (char *)(chunk + 1) + chunk->used;\n chunk->used += bytes;\n return result;\n}\n");
            return t.ToString();
        }
        private string StringRuntime(bool report)
        {
            var t = new StringBuilder("typedef struct { const uint16_t *p; int32_t n; } ai_str;\n");
            foreach (var (text, id) in stringLiterals.OrderBy(l => l.Value))
            {
                t.Append("static const uint16_t ai_strdata_").Append(id.ToString(CultureInfo.InvariantCulture)).Append("[] = {");
                for (var i = 0; i < text.Length; i++) { if (i % 24 == 0) t.Append('\n'); t.Append(((int)text[i]).ToString(CultureInfo.InvariantCulture)).Append(','); }
                t.Append("0};\nstatic const ai_str ai_strlit_").Append(id.ToString(CultureInfo.InvariantCulture)).Append(" = { ai_strdata_").Append(id.ToString(CultureInfo.InvariantCulture)).Append(", ").Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(" };\n");
            }
            if (usesConcat)
            {
                t.Append("static uint16_t *ai_alloc(size_t units) { return (uint16_t *)ai_arena_alloc(units * sizeof(uint16_t)); }\n");
                // C# string concatenation treats null as empty and never returns null.
                t.Append("static const uint16_t ai_str_emptydata[1] = { 0 };\n");
                t.Append("static ai_str ai_str_concat(ai_str a, ai_str b) {\n if (a.n == 0) { if (b.p != NULL) return b; ai_str e = { ai_str_emptydata, 0 }; return e; }\n if (b.n == 0) return a;\n");
                t.Append(" if ((int64_t)a.n + (int64_t)b.n > (int64_t)(AI_ARENA_CAP / sizeof(uint16_t))) ").Append(OutOfMemory).Append("\n");
                t.Append(" int32_t n = a.n + b.n;\n uint16_t *p = ai_alloc((size_t)n);\n");
                t.Append(" memcpy(p, a.p, (size_t)a.n * sizeof(uint16_t));\n memcpy(p + a.n, b.p, (size_t)b.n * sizeof(uint16_t));\n ai_str r = { p, n };\n return r;\n}\n");
            }
            if (usesIndex)
                t.Append("static uint16_t ai_str_at(ai_str s, int32_t i) { if (s.p == NULL) ai_fault(\"System.NullReferenceException\"); if (i < 0 || i >= s.n) ai_fault(\"System.IndexOutOfRangeException\"); return s.p[i]; }\n");
            if (usesLength)
                t.Append("static int32_t ai_str_len(ai_str s) { if (s.p == NULL) ai_fault(\"System.NullReferenceException\"); return s.n; }\n");
            if (usesEquality)
                t.Append("static int32_t ai_str_equals(ai_str a, ai_str b) { if (a.p == NULL || b.p == NULL) return a.p == b.p; return a.n == b.n && (a.n == 0 || memcmp(a.p, b.p, (size_t)a.n * sizeof(uint16_t)) == 0); }\n");
            if (report)
            {
                // ASCII-only JSON: quote, backslash and control characters use the short escapes, every other code unit outside 0x20..0x7E is \uXXXX (surrogates stay individual units).
                t.Append("static int ai_report_string(ai_str s) {\n");
                t.Append(" if (s.p == NULL) return printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"type\\\":\\\"string\\\",\\\"value\\\":null}\\n\");\n");
                t.Append(" if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"type\\\":\\\"string\\\",\\\"value\\\":\\\"\") < 0) return -1;\n");
                t.Append(" for (int32_t i = 0; i < s.n; i++) {\n  uint16_t u = s.p[i]; int r;\n");
                t.Append("  if (u == 0x22) r = fputs(\"\\\\\\\"\", stdout);\n  else if (u == 0x5C) r = fputs(\"\\\\\\\\\", stdout);\n");
                t.Append("  else if (u == 8) r = fputs(\"\\\\b\", stdout);\n  else if (u == 12) r = fputs(\"\\\\f\", stdout);\n  else if (u == 10) r = fputs(\"\\\\n\", stdout);\n");
                t.Append("  else if (u == 13) r = fputs(\"\\\\r\", stdout);\n  else if (u == 9) r = fputs(\"\\\\t\", stdout);\n");
                t.Append("  else if (u < 0x20 || u > 0x7E) r = printf(\"\\\\u%04X\", (unsigned)u);\n  else r = putc((int)u, stdout);\n");
                t.Append("  if (r < 0) return -1;\n }\n return printf(\"\\\"}\\n\");\n}\n");
            }
            return t.ToString();
        }

        private static string CType(ScalarType type) => IsObjectType(type) ? "void *" : IsStructType(type) ? "ai_struct_" + ((int)type - ScalarLanguage.StructTypeBase).ToString(CultureInfo.InvariantCulture) + (Layout(type).IsClass ? " *" : "") : type switch
        {
            ScalarType.Bool or ScalarType.Int => "int32_t", ScalarType.Long => "int64_t", ScalarType.UInt => "uint32_t", ScalarType.ULong => "uint64_t",
            ScalarType.Short => "int16_t", ScalarType.UShort or ScalarType.Char => "uint16_t", ScalarType.Byte => "uint8_t", ScalarType.SByte => "int8_t",
            ScalarType.F32 => "float", ScalarType.F64 => "double", ScalarType.String => "ai_str", ScalarType.Decimal => "ai_dec",
            _ => throw new InvalidOperationException("Unverified native type.")
        };
        private static string Tag(ScalarType type) => type switch
        {
            ScalarType.Int => "i32", ScalarType.Long => "i64", ScalarType.UInt => "u32", ScalarType.ULong => "u64",
            ScalarType.Short => "i16", ScalarType.UShort or ScalarType.Char => "u16", ScalarType.Byte => "u8", ScalarType.SByte => "i8",
            ScalarType.F32 => "f32", ScalarType.F64 => "f64", ScalarType.String => "str", ScalarType.Decimal => "dec",
            _ => throw new InvalidOperationException("Unverified native type.")
        };
        private static string UnsignedOf(ScalarType type) => type switch
        {
            ScalarType.Int or ScalarType.UInt => "uint32_t", ScalarType.Long or ScalarType.ULong => "uint64_t",
            _ => throw new InvalidOperationException("Unverified native arithmetic type.")
        };
        private static string MinText(ScalarType type) => type == ScalarType.Int ? "INT32_MIN" : "INT64_MIN";

        private static string Literal(Const constant)
        {
            if (constant.Kind == ScalarType.Bool) return constant.Number.IsZero ? "0" : "1";
            var number = constant.Number; var text = number.ToString(CultureInfo.InvariantCulture);
            string spelled = constant.Kind switch
            {
                ScalarType.Int => number == int.MinValue ? "(-2147483647 - 1)" : text,
                ScalarType.Long => number == long.MinValue ? "(-9223372036854775807LL - 1)" : text + "LL",
                ScalarType.UInt => text + "U",
                ScalarType.ULong => text + "ULL",
                _ => "((" + CType(constant.Kind) + ")" + text + ")"
            };
            return number.Sign < 0 && !spelled.StartsWith('(') ? "(" + spelled + ")" : spelled;
        }

        // Float constants are emitted as their exact bit patterns, so no decimal parsing or compiler constant folding is involved.
        private string FloatLiteral(FConst constant)
        {
            if (constant.Kind == ScalarType.F64)
            {
                Helper("ai_f64_bits", "static double ai_f64_bits(uint64_t bits) { double value; memcpy(&value, &bits, sizeof value); return value; }\n");
                return $"ai_f64_bits(0x{BitConverter.DoubleToUInt64Bits(constant.Number):X16}ULL)";
            }
            Helper("ai_f32_bits", "static float ai_f32_bits(uint32_t bits) { float value; memcpy(&value, &bits, sizeof value); return value; }\n");
            return $"ai_f32_bits(0x{BitConverter.SingleToUInt32Bits((float)constant.Number):X8}U)";
        }

        private const string Overflow = "ai_fault(\"System.OverflowException\");";
        private const string DivideZero = "ai_fault(\"System.DivideByZeroException\");";

        // Checked and wrapping helpers are generated per verified (operation, type, context); nothing here reads source text.
        private string ArithmeticHelper(Op op, ScalarType type, bool wraps)
        {
            var ct = CType(type); var tag = Tag(type); var signed = ScalarConstants.IsSigned(type); var bits = ScalarConstants.Bits(type);
            if (bits < 32) throw new InvalidOperationException("Arithmetic is performed on promoted operands.");
            var kind = op switch { Op.Add => "add", Op.Sub => "sub", Op.Mul => "mul", Op.Div => "div", Op.Rem => "rem", _ => throw new InvalidOperationException("Unverified native operator.") };
            var symbol = op switch { Op.Add => "+", Op.Sub => "-", Op.Mul => "*", Op.Div => "/", _ => "%" };
            var name = $"ai_{kind}{(wraps && op <= Op.Mul ? "w" : "")}_{tag}";
            string body;
            if (op is Op.Div or Op.Rem)
            {
                body = $"if (b == 0) {DivideZero} " + (signed ? $"if (b == -1 && a == {MinText(type)}) {Overflow} " : "") + $"return ({ct})(a {symbol} b);";
            }
            else if (wraps)
            {
                body = signed ? $"return ({ct})(({UnsignedOf(type)})a {symbol} ({UnsignedOf(type)})b);" : $"return ({ct})(a {symbol} b);";
            }
            else if (bits == 32)
            {
                var wide = signed ? "int64_t" : "uint64_t"; var (low, high) = signed ? ("INT32_MIN", "INT32_MAX") : ("0", "UINT32_MAX");
                body = signed ? $"{wide} r = ({wide})a {symbol} ({wide})b; if (r < {low} || r > {high}) {Overflow} return ({ct})r;"
                              : $"{wide} r = ({wide})a {symbol} ({wide})b; if (r > {high}) {Overflow} return ({ct})r;";
            }
            else if (signed)
            {
                body = op switch
                {
                    Op.Add => $"int64_t r = (int64_t)((uint64_t)a + (uint64_t)b); if ((b >= 0) ? (r < a) : (r >= a)) {Overflow} return r;",
                    Op.Sub => $"int64_t r = (int64_t)((uint64_t)a - (uint64_t)b); if ((b >= 0) ? (r > a) : (r <= a)) {Overflow} return r;",
                    _ => $"if (a == 0 || b == 0) return 0; if ((a == -1 && b == INT64_MIN) || (b == -1 && a == INT64_MIN)) {Overflow} int64_t r = (int64_t)((uint64_t)a * (uint64_t)b); if (r / a != b) {Overflow} return r;"
                };
            }
            else
            {
                body = op switch
                {
                    Op.Add => $"uint64_t r = a + b; if (r < a) {Overflow} return r;",
                    Op.Sub => $"if (a < b) {Overflow} return a - b;",
                    _ => $"if (a != 0 && b > UINT64_MAX / a) {Overflow} return a * b;"
                };
            }
            return Helper(name, $"static {ct} {name}({ct} a, {ct} b) {{ {body} }}\n");
        }

        // Shift counts are masked to the operand width and shifts never fault; signed right shift is arithmetic without relying on C's implementation-defined behaviour.
        private string ShiftHelper(Op op, ScalarType type)
        {
            var ct = CType(type); var ut = UnsignedOf(type); var signed = ScalarConstants.IsSigned(type); var mask = ScalarConstants.Bits(type) - 1;
            var kind = op switch { Op.Shl => "shl", Op.Shr => "shr", _ => "ushr" };
            var name = $"ai_{kind}_{Tag(type)}";
            var body = op switch
            {
                Op.Shl => $"return ({ct})(({ut})a << (n & {mask}));",
                Op.Shr when signed => $"{ct} s = ({ct})(n & {mask}); return a < 0 ? ({ct})~(~a >> s) : ({ct})(a >> s);",
                Op.Shr => $"return ({ct})(a >> (n & {mask}));",
                _ => $"return ({ct})(({ut})a >> (n & {mask}));"
            };
            return Helper(name, $"static {ct} {name}({ct} a, int32_t n) {{ {body} }}\n");
        }

        private string NegateHelper(ScalarType type, bool wraps)
        {
            var ct = CType(type); var name = (wraps ? "ai_negw_" : "ai_neg_") + Tag(type);
            if (!ScalarConstants.IsSigned(type) || ScalarConstants.Bits(type) < 32) throw new InvalidOperationException("Unverified native negation type.");
            var body = wraps ? $"return ({ct})(0 - ({UnsignedOf(type)})a);" : $"if (a == {MinText(type)}) {Overflow} return ({ct})-a;";
            return Helper(name, $"static {ct} {name}({ct} a) {{ {body} }}\n");
        }

        // Small unchecked targets narrow a saturated Int32 conversion, as on .NET9.
        // Wider unchecked targets saturate directly; checked targets fault outside their range.
        private string FloatToIntegerHelper(ScalarType to, bool wraps)
        {
            var ct = CType(to); var name = $"ai_f2i{(wraps ? "s" : "")}_{Tag(to)}";
            if (wraps && ScalarConstants.Bits(to) < 32)
                return Helper(name, $"static {ct} {name}(double v) {{ if (v != v) return 0; double t = trunc(v); int32_t n; if (t < -2147483648.0) n = INT32_MIN; else if (t >= 2147483648.0) n = INT32_MAX; else n = (int32_t)t; return ({ct})n; }}\n");
            var low = ScalarConstants.Min(to); var high = ScalarConstants.Max(to) + 1;
            var lowText = low.ToString(CultureInfo.InvariantCulture) + ".0"; var highText = high.ToString(CultureInfo.InvariantCulture) + ".0";
            var nan = wraps ? "return 0;" : Overflow;
            var stem = (ScalarConstants.IsSigned(to) ? "INT" : "UINT") + ScalarConstants.Bits(to).ToString(CultureInfo.InvariantCulture);
            var below = wraps ? (ScalarConstants.IsSigned(to) ? $"return {stem}_MIN;" : "return 0;") : Overflow;
            var above = wraps ? $"return {stem}_MAX;" : Overflow;
            // 2^63 and above: split so the C conversion is exact for the full unsigned range.
            var convert = to == ScalarType.ULong
                ? "if (t >= 9223372036854775808.0) return (uint64_t)(int64_t)(t - 9223372036854775808.0) + 9223372036854775808ULL; return (uint64_t)(int64_t)t;"
                : $"return ({ct})t;";
            var minBound = to == ScalarType.Long ? "-9223372036854775808.0" : lowText;
            return Helper(name, $"static {ct} {name}(double v) {{ if (v != v) {{ {nan} }} double t = trunc(v); if (t < {minBound}) {{ {below} }} if (t >= {highText}) {{ {above} }} {convert} }}\n");
        }
        // Integer to float with a single rounding; uint64 uses a sticky-bit halving so the signed conversion rounds correctly.
        private string IntegerToFloatExpression(ScalarType from, ScalarType to, string operand)
        {
            var ct = CType(to);
            if (from != ScalarType.ULong) return $"({ct})({operand})";
            var name = $"ai_u64_to_{Tag(to)}";
            return Helper(name, $"static {ct} {name}(uint64_t v) {{ if (v <= INT64_MAX) return ({ct})(int64_t)v; return ({ct})(int64_t)((v >> 1) | (v & 1)) * ({ct})2; }}\n") + $"({operand})";
        }

        private string ConversionExpression(Conversion conversion, string operand)
        {
            var from = conversion.Operand.Type; var to = conversion.To; var ct = CType(to);
            if (to == ScalarType.Decimal)
            {
                // Integers are exact; float and double go through System.Decimal's conversion (7 and 15 significant digits) and fault when out of range.
                if (from == ScalarType.F32) return $"ai_dec_from_f32({operand})";
                if (from == ScalarType.F64) return $"ai_dec_from_f64({operand})";
                return ScalarConstants.IsSigned(from) ? $"ai_dec_from_i64((int64_t)({operand}))" : $"ai_dec_from_u64((uint64_t)({operand}))";
            }
            if (from == ScalarType.Decimal)
            {
                // Decimal to integer truncates toward zero and always faults (OverflowException) when the result does not fit, in every overflow context.
                if (to == ScalarType.F32) return $"ai_dec_to_f32({operand})";
                if (to == ScalarType.F64) return $"ai_dec_to_f64({operand})";
                var origin = new SourceLocation(1, 1, 0);
                return ScalarConstants.IsSigned(to)
                    ? $"({ct})ai_dec_to_si({operand}, {Literal(new Const(ScalarType.Long, ScalarConstants.Min(to), origin))}, {Literal(new Const(ScalarType.Long, ScalarConstants.Max(to), origin))})"
                    : $"({ct})ai_dec_to_ui({operand}, {Literal(new Const(ScalarType.ULong, ScalarConstants.Max(to), origin))})";
            }
            if (ScalarConstants.IsFloat(to))
                return ScalarConstants.IsFloat(from) ? $"({ct})({operand})" : IntegerToFloatExpression(from, to, operand);
            if (ScalarConstants.IsFloat(from)) return FloatToIntegerHelper(to, conversion.Wraps) + $"((double)({operand}))";
            var contained = ScalarConstants.Min(to) <= ScalarConstants.Min(from) && ScalarConstants.Max(to) >= ScalarConstants.Max(from);
            if (!conversion.Explicit || conversion.Wraps || contained) return $"({ct})({operand})";
            var checks = new List<string>();
            var unsignedWide = from == ScalarType.ULong;
            var wide = unsignedWide ? "uint64_t" : "int64_t";
            if (ScalarConstants.Min(to) > ScalarConstants.Min(from))
                checks.Add($"(int64_t)v < {ScalarConstants.Min(to).ToString(CultureInfo.InvariantCulture)}LL");
            if (ScalarConstants.Max(to) < ScalarConstants.Max(from))
                checks.Add($"({wide})v > {ScalarConstants.Max(to).ToString(CultureInfo.InvariantCulture)}{(unsignedWide ? "ULL" : "LL")}");
            var name = $"ai_conv_{Tag(from)}_{Tag(to)}";
            return Helper(name, $"static {ct} {name}({CType(from)} v) {{ if ({string.Join(" || ", checks)}) {Overflow} return ({ct})v; }}\n") + $"({operand})";
        }

        private string Atom(Value value)
        {
            switch (value)
            {
                case Const constant: return Literal(constant);
                case ReceiverRead receiver: return receiverNames[receiver.Capture];
                case FConst fconstant: return FloatLiteral(fconstant);
                case DConst dconstant:
                {
                    var bits = decimal.GetBits(dconstant.Number);
                    var key = string.Join(",", bits.Select(b => ((uint)b).ToString(CultureInfo.InvariantCulture)));
                    if (!decimalLiterals.TryGetValue(key, out var id)) decimalLiterals[key] = id = decimalLiterals.Count;
                    return "ai_declit_" + id.ToString(CultureInfo.InvariantCulture);
                }
                case DisplayValue display:return EmitDisplayValue(display);
                case TypeLiteral type:return EmitTypeLiteral(type);
                case TypeShapeLiteral type:return EmitTypeShapeLiteral(type);
                case RuntimeTypeValue type:return EmitRuntimeType(type);
                case NativeTypeHash hash:return EmitNativeTypeHash(hash);
                case NativeTypeName name:return EmitNativeTypeName(name);
                case ScalarHash hash:return EmitScalarHash(hash);
                case HashContract contract:return EmitHashContract(contract);
                case IdentityHash identity:return EmitIdentityHash(identity);
                case SConst text:
                {
                    if (!stringLiterals.TryGetValue(text.Text, out var id)) stringLiterals[text.Text] = id = stringLiterals.Count;
                    return "ai_strlit_" + id.ToString(CultureInfo.InvariantCulture);
                }
                case SNull:
                {
                    var t = Temp();
                    c.Append("ai_str ").Append(t).AppendLine(" = { NULL, 0 };"); return t;
                }
                case StrLength length:
                {
                    usesLength = true; var operand = Atom(length.Operand); var t = Temp();
                    c.Append("int32_t ").Append(t).Append(" = ai_str_len(").Append(operand).AppendLine(");"); return t;
                }
                case StrIndex index:
                {
                    usesIndex = true; var text = Atom(index.Text); var position = Atom(index.Index); var t = Temp();
                    c.Append("uint16_t ").Append(t).Append(" = ai_str_at(").Append(text).Append(", ").Append(position).AppendLine(");"); return t;
                }
                case ReferenceArgument reference: return ReferenceAtom(reference);
                case ReferenceReturn returned: return ReadonlyObjectReference(returned.Type,currentReturnMode)?ReadonlyObjectAddress(returned.Storage):StorageAddress(returned.Storage);
                case ReferenceOld old: return referenceOldNames[old.Capture];
                case ReferenceMutation mutation: return ReferenceMutationAtom(mutation);
                case AccessorMutation mutation: return AccessorMutationAtom(mutation);
                case AssignedValue assignment: return AssignedValueAtom(assignment);
                case ReferenceChoice choice: { var pointer = StorageAddress(choice); var t = Temp(); c.Append(CType(choice.Type)).Append(' ').Append(t).Append(" = ").Append(ReferenceValue(choice,pointer)).AppendLine(";"); return t; }
                case ReferenceRebind rebind: { var pointer = readonlyObjectSlots.Contains(rebind.Slot)?ReadonlyObjectAddress(rebind):StorageAddress(rebind); var t = Temp(); c.Append(CType(rebind.Type)).Append(' ').Append(t).Append(" = ").Append(ReferenceValue(rebind,pointer)).AppendLine(";"); return t; }
                case ReferenceProbe probe:
                {
                    var pointer = ReferenceAtom(probe.Argument); c.Append("(void)").Append(pointer).AppendLine(";"); return "0";
                }
                case Read read:
                {
                    var storage = Slot(read.Slot);
                    if (!usesByReferenceParameters) return storage;
                    var temporary = Temp(); c.Append(CType(read.Type)).Append(' ').Append(temporary).Append(" = ").Append(storage).AppendLine(";"); return temporary;
                }
                case TypeInitialized initialized: EnsureType(initialized.Owner); return Atom(initialized.Operand);
                case StaticGet field:
                {
                    EnsureType(field.Owner); var temporary = Temp();
                    var name = StaticName(field);
                    c.Append(CType(field.Kind)).Append(' ').Append(temporary).Append(" = ");
                    if (IsStaticEvent(field)) c.Append(AtomicEventLoad(field, name));
                    else c.Append(name);
                    c.AppendLine(";");
                    return temporary;
                }
                case StaticEventUpdate update: return EmitStaticEventUpdate(update);
                case SwitchValue sv:
                {
                    if(sv.SubjectMode==ScalarParameterMode.Value)
                    {
                        var subject = Atom(sv.Subject);
                        c.Append(CType(sv.Subject.Type)).Append(' ').Append(Slot(sv.Slot)).Append(" = ").Append(subject).Append("; (void)&").Append(Slot(sv.Slot)).AppendLine(";");
                    }
                    else
                    {
                        var address=StorageAddress(sv.Subject);referenceSlots.Add(sv.Slot);
                        c.Append(ParameterDeclaration(sv.Subject.Type,sv.SubjectMode,sv.Slot,true)).Append(" = ").Append(address).Append("; (void)&").Append(RawSlot(sv.Slot)).AppendLine(";");
                    }
                    var result = Temp(); var done = Temp();
                    c.Append(CType(sv.Kind)).Append(' ').Append(result).Append("; memset(&").Append(result).Append(", 0, sizeof ").Append(result).AppendLine(");");
                    c.Append("int32_t ").Append(done).AppendLine(" = 0;");
                    foreach (var (test, armValue) in sv.Arms)
                    {
                        c.Append("if (!").Append(done).AppendLine(") {");
                        var guard = Guard(test);
                        c.Append("if (").Append(guard).AppendLine(") {");
                        var chosen = Atom(armValue);
                        c.Append(result).Append(" = ").Append(chosen).Append("; ").Append(done).AppendLine(" = 1;");
                        c.AppendLine("}").AppendLine("}");
                    }
                    if (!sv.Exhaustive) c.Append("if (!").Append(done).AppendLine(") ai_fault(\"System.Runtime.CompilerServices.SwitchExpressionException\");");
                    return result;
                }
                case Zero zero when IsClassType(zero.Kind):
                {
                    var t = Temp();
                    c.Append(CType(zero.Kind)).Append(' ').Append(t).Append(" = ").Append(NewObject(zero.Kind, zero.InitializeType)).AppendLine("();"); return t;
                }
                case BoxValueValue box:return EmitBoxValue(box);
                case UnboxValueValue unbox:return EmitUnboxValue(unbox);
                case StringObjectValue text:return EmitStringObject(text);
                case ObjectStringValue text:return EmitObjectString(text);
                case ToObject up:
                {
                    var operand = Atom(up.Operand); var t = Temp();
                    c.Append("void * ").Append(t).Append(" = (void *)").Append(operand).AppendLine(";"); return t;
                }
                case BindStore bind:
                {
                    var stored = Atom(bind.Value);
                    c.Append(Slot(bind.Slot)).Append(" = ").Append(stored).AppendLine(";"); return "1";
                }
                case BindField bind:
                {
                    Statements([bind.Store]); return "1";
                }
                case IsClassTest test when test.Exact:
                {
                    // Every object starts with its int32_t class tag.
                    var operand = Atom(test.Operand); var t = Temp();
                    c.Append("int32_t ").Append(t).Append(" = ").Append(operand).Append(" != NULL && *(const int32_t *)").Append(operand).Append(" == ").Append(ClassTag(test.Class)).AppendLine(";"); return t;
                }
                case IsClassTest test:
                {
                    var operand = Atom(test.Operand); var t = Temp();
                    c.Append("int32_t ").Append(t).Append(" = ").Append(IsInstance(test.Class)).Append("((const void *)").Append(operand).AppendLine(");"); return t;
                }
                case Upcast up:
                {
                    var operand = Atom(up.Operand); var t = Temp(); var ct = CType(up.Class);
                    c.Append(ct).Append(' ').Append(t).Append(" = (").Append(ct).Append(')').Append(operand).AppendLine(";"); return t;
                }
                case Downcast down:
                {
                    var operand = Atom(down.Operand); var t = Temp(); var ct = CType(down.Class); var name = "ai_cast_" + ClassTag(down.Class); var test = IsInstance(down.Class);
                    Helper(name, $"static {ct}{name}(void *p) {{ if (p != NULL && !{test}(p)) ai_fault(\"System.InvalidCastException\"); return ({ct})p; }}\n");
                    c.Append(ct).Append(' ').Append(t).Append(" = ").Append(name).Append("((void *)").Append(operand).AppendLine(");"); return t;
                }
                case NullRef nul:
                {
                    var t = Temp();
                    c.Append(CType(nul.Kind)).Append(' ').Append(t).AppendLine(" = NULL;"); return t;
                }
                case Zero zero:
                {
                    var t = Temp();
                    c.Append(CType(zero.Kind)).Append(' ').Append(t).Append("; memset(&").Append(t).Append(", 0, sizeof ").Append(t).AppendLine(");"); return t;
                }
                case FieldGet get:
                {
                    var operand = Atom(get.Operand); var t = Temp();
                    if (IsClassType(get.Operand.Type) && get.Lenient)
                    {
                        var index = get.Index.ToString(CultureInfo.InvariantCulture);
                        c.Append(CType(get.Kind)).Append(' ').Append(t).Append("; if (").Append(operand).Append(" != NULL) ").Append(t).Append(" = ").Append(operand).Append("->f").Append(index)
                            .Append("; else memset(&").Append(t).Append(", 0, sizeof ").Append(t).AppendLine(");");
                        return t;
                    }
                    if (IsClassType(get.Operand.Type)) operand = NonNull(get.Operand.Type) + "(" + operand + ")->f";
                    else operand += ".f";
                    c.Append(CType(get.Kind)).Append(' ').Append(t).Append(" = ").Append(operand).Append(get.Index.ToString(CultureInfo.InvariantCulture)).AppendLine(";"); return t;
                }
                case InitializeMembers initialized:
                {
                    var origin = Atom(initialized.Origin);
                    c.Append(CType(initialized.Type)).Append(' ').Append(Slot(initialized.Slot)).Append(" = ").Append(origin).AppendLine(";");
                    foreach (var action in initialized.Actions)
                    {
                        var result = Atom(action);
                        if (action is WithFields) c.Append(Slot(initialized.Slot)).Append(" = ").Append(result).AppendLine(";");
                    }
                    return Slot(initialized.Slot);
                }
                case ArrayNew or ArrayGet or ArraySet or ArrayCopy: return ArrayAtom(value);
                case RuntimeValue runtime:
                {
                    var rendered = Atom(runtime.Value); var temporary = Temp();
                    c.Append(CType(runtime.Type)).Append(' ').Append(temporary).Append(" = ").Append(rendered).AppendLine(";"); return temporary;
                }
                case RecordContractEqual contract:
                {
                    var left=Atom(contract.Left);var a=Temp();c.Append(CType(contract.Left.Type)).Append(' ').Append(a).Append(" = ").Append(left).AppendLine(";");
                    var right=Atom(contract.Right);var b=Temp();c.Append(CType(contract.Right.Type)).Append(' ').Append(b).Append(" = ").Append(right).AppendLine(";");
                    return RecordContractHelper(contract.Left.Type)+"("+a+", "+b+")";
                }
                case RecordEqual equal:
                {
                    var left=Atom(equal.Left);var a=Temp();c.Append(CType(equal.Left.Type)).Append(' ').Append(a).Append(" = ").Append(left).AppendLine(";");
                    var right=Atom(equal.Right);var b=Temp();c.Append(CType(equal.Right.Type)).Append(' ').Append(b).Append(" = ").Append(right).AppendLine(";");
                    return RecordEqualHelper(equal.Left.Type,equal.Comparers,equal.Mode)+"("+a+", "+b+")";
                }
                case RecordCopy copy:
                {
                    var origin=Atom(copy.Source);var source=Temp();
                    c.Append(CType(copy.Type)).Append(' ').Append(source).Append(" = ").Append(origin).AppendLine(";");
                    if(!IsClassType(copy.Type))return source;
                    return RecordClone(copy.Type,copy.CopyConstructors)+"("+source+")";
                }
                case WithFields wf:
                {
                    var arguments = wf.Constructor ? wf.Fields.Select(f => Atom(f.Value)).ToList() : null;
                    if (wf.Constructor) EnsureType((int)wf.Type - ScalarLanguage.StructTypeBase);
                    var origin = Atom(wf.Base); var t = Temp();
                    c.Append(CType(wf.Type)).Append(' ').Append(t).Append(" = ").Append(origin).AppendLine(";");
                    var position = 0;
                    foreach (var (index, item) in wf.Fields)
                    {
                        var assigned = arguments is null ? Atom(item) : arguments[position++];
                        c.Append(t).Append(IsClassType(wf.Type) ? "->f" : ".f").Append(index.ToString(CultureInfo.InvariantCulture)).Append(" = ").Append(assigned).AppendLine(";");
                    }
                    return t;
                }
                case Negate negate:
                {
                    var operand = Atom(negate.Operand); var t = Temp(); var type = negate.Type;
                    if (type == ScalarType.Decimal) { c.Append("ai_dec ").Append(t).Append(" = ai_dec_neg(").Append(operand).AppendLine(");"); return t; }
                    if (ScalarConstants.IsFloat(type)) { c.Append(CType(type)).Append(' ').Append(t).Append(" = -").Append(operand).AppendLine(";"); return t; }
                    c.Append(CType(type)).Append(' ').Append(t).Append(" = ").Append(NegateHelper(type, negate.Wraps)).Append('(').Append(operand).AppendLine(");"); return t;
                }
                case Conversion conversion:
                {
                    var operand = Atom(conversion.Operand); var t = Temp();
                    c.Append(CType(conversion.To)).Append(' ').Append(t).Append(" = ").Append(ConversionExpression(conversion, operand)).AppendLine(";"); return t;
                }
                case Call call when call.ReturnMode != ScalarParameterMode.Value:
                { var pointer = ReferenceCallAddress(call); var t = Temp(); c.Append(CType(call.Type)).Append(' ').Append(t).Append(" = ").Append(ReferenceValue(call,pointer)).AppendLine(";"); return t; }
                case Call call when call.Kind == ScalarType.Unit:
                {
                    var arguments = call.Arguments.Select(Atom).ToList();
                    if (call.CheckReceiver) c.Append("(void)").Append(NonNull(call.Arguments[0].Type)).Append('(').Append(arguments[0]).AppendLine(");");
                    c.Append(FunctionName(call.Function)).Append('(').Append(string.Join(", ", arguments)).AppendLine(");"); return "0";
                }
                case Call call:
                {
                    var arguments = call.Arguments.Select(Atom).ToList(); var t = Temp();
                    if (call.CheckReceiver) c.Append("(void)").Append(NonNull(call.Arguments[0].Type)).Append('(').Append(arguments[0]).AppendLine(");");
                    c.Append(CType(call.Kind)).Append(' ').Append(t).Append(" = ").Append(FunctionName(call.Function)).Append('(').Append(string.Join(", ", arguments)).AppendLine(");"); return t;
                }
                case BitNot bitNot:
                {
                    var operand = Atom(bitNot.Operand); var t = Temp(); var type = bitNot.Type;
                    c.Append(CType(type)).Append(' ').Append(t).Append(" = (").Append(CType(type)).Append(")(~").Append(operand).AppendLine(");"); return t;
                }
                case Not not:
                {
                    var operand = Atom(not.Operand); var t = Temp();
                    c.Append("int32_t ").Append(t).Append(" = !").Append(operand).AppendLine(";"); return t;
                }
                case Binary binary:
                {
                    var left = Atom(binary.Left); var right = Atom(binary.Right); var t = Temp();
                    string expression;
                    if (binary.Left.Type == ScalarType.String)
                    {
                        if (binary.Operator == Op.Add) { usesConcat = true; expression = $"ai_str_concat({left}, {right})"; }
                        else if (binary.Operator is Op.Eq or Op.Ne) { usesEquality = true; expression = (binary.Operator == Op.Ne ? "!" : "") + $"ai_str_equals({left}, {right})"; }
                        else throw new InvalidOperationException("Unverified native string operator.");
                        c.Append(CType(binary.Type)).Append(' ').Append(t).Append(" = ").Append(expression).AppendLine(";"); return t;
                    }
                    if (binary.Left.Type == ScalarType.Decimal)
                    {
                        expression = binary.Operator switch
                        {
                            Op.Add => $"ai_dec_add({left}, {right})", Op.Sub => $"ai_dec_sub({left}, {right})", Op.Mul => $"ai_dec_mul({left}, {right})",
                            Op.Div => $"ai_dec_div({left}, {right})", Op.Rem => $"ai_dec_mod({left}, {right})",
                            Op.Eq => $"(ai_dec_cmp({left}, {right}) == 0)", Op.Ne => $"(ai_dec_cmp({left}, {right}) != 0)",
                            Op.Lt => $"(ai_dec_cmp({left}, {right}) < 0)", Op.Le => $"(ai_dec_cmp({left}, {right}) <= 0)",
                            Op.Gt => $"(ai_dec_cmp({left}, {right}) > 0)", Op.Ge => $"(ai_dec_cmp({left}, {right}) >= 0)",
                            _ => throw new InvalidOperationException("Unverified native decimal operator.")
                        };
                        c.Append(CType(binary.Type)).Append(' ').Append(t).Append(" = ").Append(expression).AppendLine(";"); return t;
                    }
                    switch (binary.Operator)
                    {
                        case Op.Add or Op.Sub or Op.Mul or Op.Div or Op.Rem when ScalarConstants.IsFloat(binary.Left.Type):
                            expression = binary.Operator == Op.Rem ? $"{(binary.Left.Type == ScalarType.F32 ? "fmodf" : "fmod")}({left}, {right})"
                                : $"({left} {binary.Operator switch { Op.Add => "+", Op.Sub => "-", Op.Mul => "*", _ => "/" }} {right})";
                            break;
                        case Op.Add or Op.Sub or Op.Mul or Op.Div or Op.Rem:
                            expression = ArithmeticHelper(binary.Operator, binary.Left.Type, binary.Wraps) + $"({left}, {right})"; break;
                        case Op.And: expression = $"({left} & {right})"; break;
                        case Op.Or: expression = $"({left} | {right})"; break;
                        case Op.Xor: expression = $"({left} ^ {right})"; break;
                        case Op.Shl or Op.Shr or Op.UShr:
                            expression = ShiftHelper(binary.Operator, binary.Left.Type) + $"({left}, {right})"; break;
                        case Op.Eq: expression = $"({left} == {right})"; break;
                        case Op.Ne: expression = $"({left} != {right})"; break;
                        case Op.Lt: expression = $"({left} < {right})"; break;
                        case Op.Le: expression = $"({left} <= {right})"; break;
                        case Op.Gt: expression = $"({left} > {right})"; break;
                        case Op.Ge: expression = $"({left} >= {right})"; break;
                        default: throw new InvalidOperationException("Unverified native operator.");
                    }
                    c.Append(CType(binary.Type)).Append(' ').Append(t).Append(" = ").Append(expression).AppendLine(";"); return t;
                }
                case Logical logical:
                {
                    // Short-circuit: the right operand is evaluated, with all its checks, only on the taken path.
                    var left = Atom(logical.Left); var t = Temp(); var guard = Temp();
                    c.Append("int32_t ").Append(t).Append(" = ").Append(logical.IsAnd ? "0" : "1").AppendLine(";");
                    c.Append("int32_t ").Append(guard).Append(" = ").Append(left).AppendLine(";");
                    c.Append("if (").Append(logical.IsAnd ? "" : "!").Append(guard).AppendLine(") {");
                    var right = Atom(logical.Right);
                    c.Append(t).Append(" = ").Append(right).AppendLine(";").AppendLine("}"); return t;
                }
                default: throw new InvalidOperationException("Unverified native value.");
            }
        }

        // Conditions always pass through a variable, so no compile-time-constant condition reaches a C statement.
        private string Guard(Value condition)
        {
            var atom = Atom(condition); var guard = Temp();
            c.Append("int32_t ").Append(guard).Append(" = ").Append(atom).AppendLine(";"); return guard;
        }

        private void Statements(IReadOnlyList<Statement> statements)
        {
            foreach (var statement in statements)
                switch (statement)
                {
                    case ReferenceDeclare local:
                    {
                        var descriptor=ReadonlyObjectReference(local.Kind,local.Mode);
                        var address = local.Storage is null ? null : descriptor?ReadonlyObjectAddress(local.Storage):StorageAddress(local.Storage); referenceSlots.Add(local.Slot);
                        if(descriptor)readonlyObjectSlots.Add(local.Slot);
                        c.Append(ReferencePointer(local.Kind, local.Mode)).Append(volatileSlots ? " volatile " : " ").Append(RawSlot(local.Slot));
                        if (address is not null) c.Append(" = ").Append(address); c.AppendLine(";");
                        c.Append(local.Storage is null ? "(void)&" : "(void)").Append(RawSlot(local.Slot)).AppendLine(";"); break;
                    }
                    case ReferenceStore store:
                    { var pointer = StorageAddress(store.Storage); var saved = Temp(); c.Append(ReferencePointer(store.Storage.Type, ScalarParameterMode.Ref)).Append(' ').Append(saved).Append(" = ").Append(pointer).AppendLine(";"); var value = Atom(store.Value); c.Append('*').Append(saved).Append(" = ").Append(value).AppendLine(";"); break; }
                    case Declare local:
                    {
                        var init = local.Initializer is null ? null : Atom(local.Initializer);
                        c.Append(Qualified(local.Kind)).Append(Slot(local.Slot));
                        if (init is not null) c.Append(" = ").Append(init);
                        // Address-only use suppresses unused-local warnings without reading uninitialized storage.
                        c.Append("; (void)&").Append(Slot(local.Slot)).AppendLine(";"); break;
                    }
                    case Eval { Value: ReferenceRebind rebound }: { var pointer = readonlyObjectSlots.Contains(rebound.Slot)?ReadonlyObjectAddress(rebound):StorageAddress(rebound); c.Append("(void)").Append(pointer).AppendLine(";"); break; }
                    case Eval eval: { var value = Atom(eval.Value); if (eval.Value.Type != ScalarType.Unit) c.Append("(void)").Append(value).AppendLine(";"); break; }
                    case StoreStatic field:
                    {
                        if (field.Path.Count > 0) EnsureType(field.Root.Owner);
                        var value = Atom(field.Value);
                        if (field.Path.Count == 0) EnsureType(field.Root.Owner);
                        if (IsStaticEvent(field.Root) && field.Path.Count == 0)
                        {
                            c.Append("(void)InterlockedExchangePointer((PVOID volatile *)&").Append(StaticName(field.Root)).Append(", (PVOID)").Append(value).AppendLine(");"); break;
                        }
                        c.Append(StaticName(field.Root));
                        foreach (var index in field.Path) c.Append(".f").Append(index.ToString(CultureInfo.InvariantCulture));
                        c.Append(" = ").Append(value).AppendLine(";"); break;
                    }
                    case StoreField field:
                    {
                        var value = Atom(field.Value);
                        c.Append(Slot(field.Slot));
                        foreach (var index in field.Path) c.Append(".f").Append(index.ToString(CultureInfo.InvariantCulture));
                        c.Append(" = ").Append(value).AppendLine(";"); break;
                    }
                    case StoreRef reference:
                    {
                        // The object reference is copied first, so evaluating the value cannot change which object is written.
                        var objectRef = Atom(reference.Target); var target = Temp(); var check = NonNull(reference.Target.Type);
                        c.Append(CType(reference.Target.Type)).Append(' ').Append(target).Append(" = ").Append(objectRef).AppendLine(";");
                        if(reference.Path.Count>1)c.Append("(void)").Append(check).Append('(').Append(target).AppendLine(");");
                        if (reference.ReceiverCapture >= 0) receiverNames.Add(reference.ReceiverCapture, target);
                        string value;
                        try { value = Atom(reference.Value); }
                        finally { if (reference.ReceiverCapture >= 0) receiverNames.Remove(reference.ReceiverCapture); }
                        c.Append(check).Append('(').Append(target).Append(")->f").Append(string.Join(".f", reference.Path.Select(i => i.ToString(CultureInfo.InvariantCulture))));
                        c.Append(" = ").Append(value).AppendLine(";"); break;
                    }
                    case Store store: { var value = Atom(store.Value); c.Append(Slot(store.Slot)).Append(" = ").Append(value).AppendLine(";"); break; }
                    case Return returned:
                    {
                        var value = returned.Value is null ? null : Atom(returned.Value);
                        if (regions.Count == 0) { if (value is null) c.AppendLine("return;"); else c.Append("return ").Append(value).AppendLine(";"); break; }
                        Leave(new Exit("return", -1, false), value, regions.Count - 1); break;
                    }
                    case Try tried: EmitTry(tried); break;
                    case Throw thrown:
                        if (thrown.Name is null) c.Append("ai_fault(ai_x_").Append(thrown.RethrowOf.ToString(CultureInfo.InvariantCulture)).AppendLine(");");
                        else c.Append("ai_fault(\"").Append(thrown.Name).AppendLine("\");");
                        break;
                    case Block block: c.AppendLine("{"); Statements(block.Statements); c.AppendLine("}"); break;
                    case If branch:
                    {
                        var guard = Guard(branch.Condition);
                        c.Append("if (").Append(guard).AppendLine(") {"); Statements(branch.Then); c.AppendLine("}");
                        if (branch.Else.Count > 0) { c.AppendLine("else {"); Statements(branch.Else); c.AppendLine("}"); }
                        break;
                    }
                    case Loop loop:
                    {
                        c.AppendLine("for (;;) {");
                        if (!loop.PostTest)
                        {
                            var guard = Guard(loop.Condition);
                            c.Append("if (!").Append(guard).AppendLine(") break;");
                        }
                        c.AppendLine("{"); regions.Add(new LoopRegion(loop.Id)); Statements(loop.Body); regions.RemoveAt(regions.Count - 1); c.AppendLine("}");
                        if (loop.NeedsContinueLabel) c.Append("ai_continue_").Append(loop.Id.ToString(CultureInfo.InvariantCulture)).AppendLine(": ;");
                        if (loop.Step.Count > 0) { c.AppendLine("{"); Statements(loop.Step); c.AppendLine("}"); }
                        if (loop.PostTest)
                        {
                            c.AppendLine("{");
                            var guard = Guard(loop.Condition);
                            c.Append("if (!").Append(guard).AppendLine(") break;");
                            c.AppendLine("}");
                        }
                        c.AppendLine("}"); break;
                    }
                    case Break: Leave(new Exit("break", -1, false), null, regions.Count - 1); break;
                    case SwitchBlock block:
                        c.AppendLine("{"); regions.Add(new SwitchRegion(block.Id)); Statements(block.Body); regions.RemoveAt(regions.Count - 1); c.AppendLine("}");
                        if (block.UsesBreak) c.Append("ai_switch_end_").Append(block.Id.ToString(CultureInfo.InvariantCulture)).AppendLine(": ;");
                        break;
                    case SwitchBreak leave: Leave(new Exit("switchbreak", leave.Id, false), null, regions.Count - 1); break;
                    case Continue jump: Leave(new Exit("continue", jump.LoopId, jump.ViaLabel), null, regions.Count - 1); break;
                    default: throw new InvalidOperationException("Unverified native statement.");
                }
        }

        private static bool CanFallThrough(IReadOnlyList<Statement> statements) => statements.All(CanFallThrough);
        private static bool CanFallThrough(Statement statement) => statement switch
        {
            Return or Break or Continue or SwitchBreak or Throw => false,
            Try tried => tried.Finally is not null || CanFallThrough(tried.Body) || tried.Catches.Any(clause => CanFallThrough(clause.Body)),
            SwitchBlock block => block.UsesBreak || CanFallThrough(block.Body),
            If branch => CanFallThrough(branch.Then) || CanFallThrough(branch.Else),
            Block block => CanFallThrough(block.Statements),
            _ => true
        };

        // N17 exceptions: a handler stack of setjmp buffers. ai_fault longjmps to the innermost handler (restoring the call depth there) or,
        // with no handler, reports the exception and exits. In a function with a try statement every local and parameter is volatile,
        // so values written in a try body are well defined after the longjmp.
        private const string HandlerHelper =
            "#if defined(_MSC_VER)\n#define AI_NORETURN __declspec(noreturn)\n#else\n#define AI_NORETURN _Noreturn\n#endif\n"
            + "typedef struct ai_handler { jmp_buf env; struct ai_handler *prev; int64_t depth; } ai_handler;\n"
            + "static ai_handler *ai_top = NULL;\nstatic const char *ai_exc = NULL;\n"
            + "AI_NORETURN static void ai_fault(const char *exception) {\n"
            + " if (ai_top != NULL) { ai_exc = exception; longjmp(ai_top->env, 1); }\n"
            + " if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"threw\\\",\\\"exception\\\":\\\"%s\\\"}\\n\", exception) < 0) exit(74);\n"
            + " if (fflush(stdout) != 0) exit(74);\n exit(70);\n}\n";
        private static bool HasTry(IEnumerable<Statement> statements) => statements.Any(statement => statement switch
        {
            Try => true, Block b => HasTry(b.Statements), If i => HasTry(i.Then) || HasTry(i.Else), Loop l => HasTry(l.Body) || HasTry(l.Step), SwitchBlock w => HasTry(w.Body), _ => false
        });
        private bool volatileSlots, anyTry;
        private string Qualified(ScalarType type) => volatileSlots ? CType(type).TrimEnd() + " volatile " : CType(type) + " ";
        // The enclosing constructs of the statement being emitted, innermost last: loops, switch blocks and try regions.
        private abstract record Region;
        private sealed record LoopRegion(int Id) : Region;
        private sealed record SwitchRegion(int Id) : Region;
        private sealed record TryRegion(int Id, bool HasFinally) : Region;
        private sealed record CatchRegion(int Id, bool Guarded) : Region;
        private sealed record FinallyRegion(int Id) : Region;
        private readonly List<Region> regions = [];
        // A pending exit through a finally clause: return (value in ai_ret), break, continue or a switch break.
        private sealed record Exit(string Kind, int Target, bool ViaLabel);
        private readonly Dictionary<int, List<Exit>> pendingExits = [];
        private ScalarType currentReturn;
        private void Leave(Exit exit, string? value, int from)
        {
            for (var i = from; i >= 0; i--)
            {
                switch (regions[i])
                {
                    case LoopRegion loop when exit.Kind == "break" || exit.Kind == "continue" && loop.Id == exit.Target:
                        if (exit.Kind == "break") c.AppendLine("break;");
                        else if (exit.ViaLabel) c.Append("goto ai_continue_").Append(loop.Id.ToString(CultureInfo.InvariantCulture)).AppendLine(";");
                        else c.AppendLine("continue;");
                        return;
                    case SwitchRegion block when exit.Kind == "switchbreak" && block.Id == exit.Target:
                        c.Append("goto ai_switch_end_").Append(block.Id.ToString(CultureInfo.InvariantCulture)).AppendLine(";"); return;
                    case TryRegion tried:
                        c.Append("ai_top = ai_h_").Append(tried.Id.ToString(CultureInfo.InvariantCulture)).AppendLine(".prev;");
                        if (tried.HasFinally) { Pend(tried.Id, exit, value); return; }
                        break;
                    case CatchRegion handler when handler.Guarded:
                        c.Append("ai_top = ai_g_").Append(handler.Id.ToString(CultureInfo.InvariantCulture)).AppendLine(".prev;");
                        Pend(handler.Id, exit, value); return;
                    case FinallyRegion: throw new InvalidOperationException("Control cannot leave a finally clause.");
                }
            }
            if (exit.Kind != "return") throw new InvalidOperationException("Unverified jump target.");
            if (value is null) c.AppendLine("return;"); else c.Append("return ").Append(value).AppendLine(";");
        }
        private void Pend(int id, Exit exit, string? value)
        {
            var list = pendingExits.TryGetValue(id, out var known) ? known : pendingExits[id] = [];
            var code = list.IndexOf(exit); if (code < 0) { list.Add(exit); code = list.Count - 1; }
            if (exit.Kind == "return" && value is not null) c.Append("ai_ret = ").Append(value).AppendLine(";");
            c.Append("ai_p_").Append(id.ToString(CultureInfo.InvariantCulture)).Append(" = ").Append((code + 2).ToString(CultureInfo.InvariantCulture))
                .Append("; goto ai_fin_").Append(id.ToString(CultureInfo.InvariantCulture)).AppendLine(";");
        }
        private void EmitTry(Try tried)
        {
            var k = tried.Id.ToString(CultureInfo.InvariantCulture); var hasFinally = tried.Finally is not null;
            c.AppendLine("{");
            c.Append("ai_handler ai_h_").Append(k).AppendLine(";");
            c.Append("const char *volatile ai_x_").Append(k).AppendLine(" = NULL;");
            if (hasFinally) c.Append("int32_t volatile ai_p_").Append(k).AppendLine(" = 0;");
            c.Append("ai_h_").Append(k).Append(".prev = ai_top; ai_h_").Append(k).Append(".depth = ai_depth; ai_top = &ai_h_").Append(k).AppendLine(";");
            c.Append("if (setjmp(ai_h_").Append(k).AppendLine(".env) == 0) {");
            regions.Add(new TryRegion(tried.Id, hasFinally)); Statements(tried.Body); regions.RemoveAt(regions.Count - 1);
            c.Append("ai_top = ai_h_").Append(k).AppendLine(".prev;");
            c.AppendLine("} else {");
            c.Append("ai_top = ai_h_").Append(k).Append(".prev; ai_depth = (int32_t)ai_h_").Append(k).Append(".depth; ai_x_").Append(k).AppendLine(" = ai_exc;");
            var guarded = hasFinally && tried.Catches.Count > 0;
            if (guarded)
            {
                c.Append("ai_handler ai_g_").Append(k).AppendLine(";");
                c.Append("ai_g_").Append(k).Append(".prev = ai_top; ai_g_").Append(k).Append(".depth = ai_depth; ai_top = &ai_g_").Append(k).AppendLine(";");
                c.Append("if (setjmp(ai_g_").Append(k).AppendLine(".env) == 0) {");
            }
            regions.Add(new CatchRegion(tried.Id, guarded));
            var opened = 0; var general = false;
            foreach (var (names, body) in tried.Catches)
            {
                if (names is null) { if (opened > 0) c.AppendLine("else {"); else c.AppendLine("{"); Statements(body); c.AppendLine("}"); general = true; break; }
                c.Append(opened > 0 ? "else if (" : "if (").Append(string.Join(" || ", names.Select(n => "strcmp(ai_x_" + k + ", \"" + n + "\") == 0"))).AppendLine(") {");
                Statements(body); c.AppendLine("}"); opened++;
            }
            regions.RemoveAt(regions.Count - 1);
            if (!general)
            {
                if (opened > 0) c.AppendLine("else {"); else c.AppendLine("{");
                if (hasFinally) c.Append("ai_p_").Append(k).AppendLine(" = 1;"); else c.Append("ai_fault(ai_x_").Append(k).AppendLine(");");
                c.AppendLine("}");
            }
            if (guarded)
            {
                c.Append("ai_top = ai_g_").Append(k).AppendLine(".prev;");
                c.Append("} else { ai_top = ai_g_").Append(k).Append(".prev; ai_depth = (int32_t)ai_g_").Append(k).Append(".depth; ai_x_").Append(k).Append(" = ai_exc; ai_p_").Append(k).AppendLine(" = 1; }");
            }
            c.AppendLine("}");
            if (hasFinally)
            {
                if (pendingExits.ContainsKey(tried.Id)) c.Append("ai_fin_").Append(k).AppendLine(": ;");
                regions.Add(new FinallyRegion(tried.Id)); c.AppendLine("{"); Statements(tried.Finally!); c.AppendLine("}"); regions.RemoveAt(regions.Count - 1);
                c.Append("if (ai_p_").Append(k).Append(" == 1) ai_fault(ai_x_").Append(k).AppendLine(");");
                if (pendingExits.TryGetValue(tried.Id, out var exits))
                    for (var i = 0; i < exits.Count; i++)
                    {
                        c.Append("if (ai_p_").Append(k).Append(" == ").Append((i + 2).ToString(CultureInfo.InvariantCulture)).AppendLine(") {");
                        Leave(exits[i], exits[i].Kind == "return" && currentReturn != ScalarType.Unit ? "ai_ret" : null, regions.Count - 1);
                        c.AppendLine("}");
                    }
            }
            c.AppendLine("}");
        }
        private static string RetType(ScalarType type) => type == ScalarType.Unit ? "void" : CType(type);
        private static string FunctionName(int index) => "ai_fn_" + index.ToString(CultureInfo.InvariantCulture);
        private const int MaxCallDepth = 20000;

        public string Emit(IReadOnlyList<FunctionIr> functions, int entry, ScalarType entryType, int? pointerBits, ScalarType? argumentType, IReadOnlyDictionary<string, Signature> signatures)
        {
            ConfigureTypeInitialization(signatures);
            usesByReferenceParameters = functions.Any(f => f.Modes.Any(m => m != ScalarParameterMode.Value) || f.ReturnMode != ScalarParameterMode.Value || HasReferenceLocals(f.Body) || f.HasValueUpdates);
            var entryPrelude = argumentType is { } arrayType ? ProcessEntryPrelude(arrayType) : "int main(void) { ";
            var entryCall = FunctionName(entry) + (argumentType is not null ? "(ai_args)" : "()");
            // Only functions reachable from the entry are emitted, so every emitted function is referenced.
            var reachable = new SortedSet<int> { entry }; var work = new Stack<int>([entry]);
            var byIndex = functions.ToDictionary(f => f.Index);
            while (work.Count > 0)
                foreach (var callee in byIndex[work.Pop()].Callees) if (reachable.Add(callee)) work.Push(callee);
            hasTypeInitialization = typeInitializers.Values.Any(reachable.Contains);
            var prototypes = new StringBuilder(); var definitions = new StringBuilder();
            foreach (var index in reachable)
            {
                var function = byIndex[index]; var name = FunctionName(index);
                currentReturnMode = function.ReturnMode; var returnCType = function.ReturnMode == ScalarParameterMode.Value ? RetType(function.Return) : ReferencePointer(function.Return, function.ReturnMode);
                referenceSlots.Clear();readonlyObjectSlots.Clear();
                for (var slot = 0; slot < function.Modes.Count; slot++) if (function.Modes[slot] != ScalarParameterMode.Value)
                { referenceSlots.Add(slot);if(ReadonlyObjectReference(function.Parameters[slot],function.Modes[slot]))readonlyObjectSlots.Add(slot); }
                var parameterList = function.Parameters.Count == 0 ? "void" : string.Join(", ", function.Parameters.Select((type, i) => ParameterDeclaration(type, function.Modes[i], i, false)));
                volatileSlots = HasTry(function.Body); anyTry |= volatileSlots; currentReturn = function.Return; pendingExits.Clear(); regions.Clear();
                var bodyParameters = !volatileSlots || function.Parameters.Count == 0 ? parameterList : string.Join(", ", function.Parameters.Select((type, i) => ParameterDeclaration(type, function.Modes[i], i, true)));
                var argumentList = string.Join(", ", Enumerable.Range(0, function.Parameters.Count).Select(RawSlot));
                prototypes.Append("static ").Append(returnCType).Append(' ').Append(name).Append('(').Append(parameterList).AppendLine(");");
                c = new StringBuilder();
                for (var i = 0; i < function.Parameters.Count; i++) c.Append("(void)").Append(RawSlot(i)).AppendLine(";");
                if (volatileSlots && function.ReturnMode != ScalarParameterMode.Value) c.Append(returnCType).Append(ReadonlyObjectReference(function.Return,function.ReturnMode)?" volatile ai_ret = { 0 }; (void)&ai_ret;\n":" volatile ai_ret = NULL; (void)&ai_ret;\n");
                if (volatileSlots && function.ReturnMode == ScalarParameterMode.Value && function.Return != ScalarType.Unit) { var at = function.Body.Count > 0 ? function.Body[0].Location : default!; Value initial = IsClassType(function.Return) ? new NullRef(function.Return, at) : new Zero(function.Return, at); var zero = Atom(initial); c.Append(Qualified(function.Return)).Append("ai_ret = ").Append(zero).AppendLine("; (void)&ai_ret;"); }
                Statements(function.Body);
                // A verified function cannot fall through, but a C compiler sees every loop as exitable through its guard.
                // State the impossible fall-through only where C can reach it, so no unreachable code is generated.
                if (function.Return != ScalarType.Unit && CanFallThrough(function.Body)) c.AppendLine("ai_fault(\"System.InvalidProgramException\");");
                definitions.Append("static ").Append(returnCType).Append(' ').Append(name).Append("_body(").Append(bodyParameters).AppendLine(") {").Append(c).AppendLine("}");
                volatileSlots = false;
                definitions.Append("static ").Append(returnCType).Append(' ').Append(name).Append('(').Append(parameterList).AppendLine(") {");
                if (functionGuards.TryGetValue(index, out var owner)) definitions.Append(TypeInitializationCall(owner));
                definitions.Append(" if (++ai_depth > ").Append(MaxCallDepth).AppendLine(") ai_fault(\"System.InsufficientExecutionStackException\");");
                if (function.Return == ScalarType.Unit)
                    definitions.Append(' ').Append(name).Append("_body(").Append(argumentList).AppendLine(");").AppendLine(" --ai_depth;\n}");
                else
                {
                    definitions.Append(' ').Append(returnCType).Append(" ai_result = ").Append(name).Append("_body(").Append(argumentList).AppendLine(");");
                    definitions.AppendLine(" --ai_depth;\n return ai_result;\n}");
                }
            }
            var text = new StringBuilder("#include <stdint.h>\n#include <stdio.h>\n#include <stdlib.h>\n#include <inttypes.h>\n#include <string.h>\n#include <math.h>\n#include <float.h>\n");
            if (usesCovariantArrays) text.Append("#include <stddef.h>\n");
            var threadSynchronization = hasTypeInitialization || usesStringReference || usedStatics.Any(f => layouts![f.Owner].StaticFields[f.Index].IsEvent);
            text.Append("_Static_assert(sizeof(int32_t) == 4, \"AI# requires int32_t\");\n");
            if (pointerBits is { } bits) text.Append("_Static_assert(sizeof(void*) * 8 == ").Append(bits.ToString(CultureInfo.InvariantCulture)).Append(", \"AI# native integer target width mismatch\");\n");
            // Optimizers legitimately prove checked operations always fault; code after such a point is unreachable by design.
            // Call depth is bounded by ai_depth; the reserved stack is sized so that bound is reached before the stack is.
            text.Append("#if defined(_MSC_VER)\n#pragma warning(disable: 4702 4723)\n#pragma comment(linker, \"/STACK:268435456\")\n#endif\n");
            if (threadSynchronization) anyTry = true;
            if (anyTry) text.Insert(text.ToString().IndexOf("#include <stdint.h>", StringComparison.Ordinal), "#include <setjmp.h>\n");
            if (threadSynchronization) text.Insert(0, "#define WIN32_LEAN_AND_MEAN\n#define NOMINMAX\n#include <windows.h>\n#if defined(_MSC_VER)\n#define AI_THREAD_LOCAL __declspec(thread)\n#else\n#define AI_THREAD_LOCAL _Thread_local\n#endif\n");
            if(usesHashSeed){text.Insert(0,"#define _CRT_RAND_S\n");entryPrelude+="ai_hash_seed_init(); ";}
            if (anyTry) text.Append("#if defined(_MSC_VER)\n#pragma warning(disable: 4324)\n#endif\n");
            var faultRuntime = anyTry ? HandlerHelper : FaultHelper;
            if (threadSynchronization) faultRuntime = faultRuntime.Replace("static ai_handler *ai_top", "static AI_THREAD_LOCAL ai_handler *ai_top", StringComparison.Ordinal).Replace("static const char *ai_exc", "static AI_THREAD_LOCAL const char *ai_exc", StringComparison.Ordinal);
            text.Append(faultRuntime).Append(threadSynchronization ? "static AI_THREAD_LOCAL int32_t ai_depth = 0;\n" : "static int32_t ai_depth = 0;\n");
            if (prototypes.ToString().Contains("ai_readonly_object_ref",StringComparison.Ordinal)||definitions.ToString().Contains("ai_readonly_object_ref",StringComparison.Ordinal)) text.Append(ReadonlyObjectReferenceRuntime);
            if (hasTypeInitialization) text.Append(TypeInitializationRuntime());
            if (argumentType is not null) text.Append("#include <wchar.h>\n_Static_assert(sizeof(wchar_t) == sizeof(uint16_t), \"AI# requires UTF-16 process arguments\");\n");
            var usesStrings = (layouts?.Any(l => l.IsStringArray) ?? false) || entryType == ScalarType.String || (layouts?.Any(l => l.Fields.Concat(l.StaticFields).Any(f => f.StorageType == ScalarType.String)) ?? false) || definitions.ToString().Contains("ai_str", StringComparison.Ordinal) || prototypes.ToString().Contains("ai_str", StringComparison.Ordinal);
            var strings = usesStrings ? StringRuntime(entryType == ScalarType.String) : "";
            if (usesConcat || usesObjects || usesDisplay) text.Append(threadSynchronization ? SynchronizedArenaRuntime() : ArenaRuntime());
            text.Append(strings);
            // Struct layouts, inner structs first: a struct is a C struct of its fields f0..fn (an empty struct holds one unused byte).
            var typedefs = new StringBuilder(); var done = new HashSet<int>();
            for (var i = 0; i < (layouts?.Count ?? 0); i++)
                if (layouts![i].IsClass) typedefs.Append("typedef struct ai_struct_").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" ai_struct_").Append(i.ToString(CultureInfo.InvariantCulture)).Append(";\n");
            void Define(int index)
            {
                if (!done.Add(index)) return;
                var layout = layouts![index];
                foreach (var field in layout.Fields) if (IsStructType(field.StorageType) && !IsClassType(field.StorageType)) Define((int)field.StorageType - ScalarLanguage.StructTypeBase);
                if (layout.ArrayElementType is { } element && IsStructType(element) && !IsClassType(element)) Define((int)element - ScalarLanguage.StructTypeBase);
                typedefs.Append(layout.IsClass ? "struct ai_struct_" + index.ToString(CultureInfo.InvariantCulture) + " { " : "typedef struct { ");
                if (layout.IsClass) typedefs.Append("int32_t ai_tag; ");
                else if (layout.Fields.Count == 0) typedefs.Append("uint8_t ai_empty; ");
                for (var i = 0; i < layout.Fields.Count; i++) typedefs.Append(CType(layout.Fields[i].StorageType)).Append(" f").Append(i.ToString(CultureInfo.InvariantCulture)).Append("; ");
                if (layout.IsStringArray) typedefs.Append("ai_str *ai_items; ");
                if (layout.ArrayElementType is not null) typedefs.Append(CType(ArrayStorageType(layout.Id))).Append(" *ai_items; ");
                typedefs.Append(layout.IsClass ? "};\n" : "} ai_struct_" + index.ToString(CultureInfo.InvariantCulture) + ";\n");
            }
            for (var i = 0; i < (layouts?.Count ?? 0); i++) Define(i);
            var usesDecimal = usedStatics.Any(f => layouts![f.Owner].StaticFields[f.Index].StorageType == ScalarType.Decimal) || entryType == ScalarType.Decimal || decimalLiterals.Count > 0 || definitions.ToString().Contains("ai_dec", StringComparison.Ordinal) || prototypes.ToString().Contains("ai_dec", StringComparison.Ordinal) || typedefs.ToString().Contains("ai_dec", StringComparison.Ordinal);
            if (usesDecimal)
            {
                text.Append(NativeDecimalRuntime.Source);
                foreach (var (key, id) in decimalLiterals.OrderBy(l => l.Value))
                    text.Append("static const ai_dec ai_declit_").Append(id.ToString(CultureInfo.InvariantCulture)).Append(" = { ")
                        .Append(string.Join("u, ", key.Split(','))).Append("u };\n");
            }
            text.Append(typedefs);text.Append(TypeLiteralDeclarations());
            foreach (var field in usedStatics.OrderBy(f => f.Owner).ThenBy(f => f.Index))
            {
                var metadata = layouts![field.Owner].StaticFields[field.Index];
                text.Append("static ").Append(CType(metadata.StorageType)).Append(metadata.IsEvent ? " volatile ai_static_" : " ai_static_").Append(field.Owner.ToString(CultureInfo.InvariantCulture)).Append('_').Append(field.Index.ToString(CultureInfo.InvariantCulture)).AppendLine(" = {0};");
            }
            if (hasTypeInitialization) text.Append(prototypes);
            foreach (var helper in helpers.OrderBy(h => h.Key, StringComparer.Ordinal)) text.Append(helper.Value);
            if (!hasTypeInitialization) text.Append(prototypes);
            text.Append(definitions);
            if (entryType == ScalarType.Bool)
                text.Append(entryPrelude).Append("int32_t result = ").Append(entryCall).Append(";\n if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"type\\\":\\\"bool\\\",\\\"value\\\":%s}\\n\", result ? \"true\" : \"false\") < 0) return 74;\n return fflush(stdout) == 0 ? 0 : 74; }\n");
            else if (entryType == ScalarType.Byte)
                text.Append(entryPrelude).Append("uint8_t result = ").Append(entryCall).Append(";\n if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"type\\\":\\\"byte\\\",\\\"value\\\":%\" PRIu32 \"}\\n\", (uint32_t)result) < 0) return 74;\n return fflush(stdout) == 0 ? 0 : 74; }\n");
            else if (entryType == ScalarType.SByte)
                text.Append(entryPrelude).Append("int8_t result = ").Append(entryCall).Append(";\n if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"type\\\":\\\"sbyte\\\",\\\"value\\\":%\" PRId32 \"}\\n\", (int32_t)result) < 0) return 74;\n return fflush(stdout) == 0 ? 0 : 74; }\n");
            else if (entryType == ScalarType.Short)
                text.Append(entryPrelude).Append("int16_t result = ").Append(entryCall).Append(";\n if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"type\\\":\\\"short\\\",\\\"value\\\":%\" PRId32 \"}\\n\", (int32_t)result) < 0) return 74;\n return fflush(stdout) == 0 ? 0 : 74; }\n");
            else if (entryType == ScalarType.Int)
                text.Append(entryPrelude).Append("int32_t result = ").Append(entryCall).Append(";\n if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"value\\\":%\" PRId32 \"}\\n\", result) < 0) return 74;\n return fflush(stdout) == 0 ? 0 : 74; }\n");
            else if (entryType is ScalarType.UInt or ScalarType.Long or ScalarType.ULong)
            {
                var format = entryType == ScalarType.UInt ? "PRIu32" : entryType == ScalarType.Long ? "PRId64" : "PRIu64";
                text.Append(entryPrelude).Append(CType(entryType)).Append(" result = ").Append(entryCall).Append(";\n if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"value\\\":%\" ").Append(format).Append(" \"}\\n\", result) < 0) return 74;\n return fflush(stdout) == 0 ? 0 : 74; }\n");
            }
            else if (entryType == ScalarType.Decimal)
            {
                // Decimal results are JSON strings in invariant-culture text that preserves the scale (1.10m reports "1.10").
                text.Append("static int ai_report_decimal(ai_dec v) {\n char text[48]; ai_dec_format(v, text);\n return printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"type\\\":\\\"decimal\\\",\\\"value\\\":\\\"%s\\\"}\\n\", text);\n}\n");
                text.Append(entryPrelude).Append("ai_dec result = ").Append(entryCall).Append(";\n if (ai_report_decimal(result) < 0) return 74;\n return fflush(stdout) == 0 ? 0 : 74; }\n");
            }
            else if (entryType == ScalarType.String)
                text.Append(entryPrelude).Append("ai_str result = ").Append(entryCall).Append(";\n if (ai_report_string(result) < 0) return 74;\n return fflush(stdout) == 0 ? 0 : 74; }\n");
            else
            {
                // The report never calls setlocale, so the C locale applies: the decimal separator is always '.'. The shortest digit count that round-trips is used.
                var single = entryType == ScalarType.F32; var fname = single ? "f32" : "f64"; var ct = CType(entryType);
                text.Append(ReportFloat(fname, ct, single));
                text.Append(entryPrelude).Append(ct).Append(" result = ").Append(entryCall).Append(";\n if (ai_report_").Append(fname).Append("(result) < 0) return 74;\n return fflush(stdout) == 0 ? 0 : 74; }\n");
            }
            return text.ToString();
        }
    }
}
