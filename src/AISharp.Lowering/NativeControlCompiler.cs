using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AISharp.Compiler;

namespace AISharp.Lowering;

/// <summary>
/// Closed native control-flow capability (N2): checked Int32 arithmetic, comparisons, bool, if/else,
/// while/for with break/continue. Verified IR is the only input to C11 emission; source names and text never reach C.
/// </summary>
public static class NativeControlCompiler
{
    public const string Capability = "native-control-0.1";
    /// <summary>Process exit status of a native program that terminated with a thrown (faulted) outcome.</summary>
    public const int ThrewExitCode = 70;
    public sealed record Preparation(string SourceSha256, string? CSource, IReadOnlyList<Diagnostic> Diagnostics)
    {
        public bool Success => CSource is not null && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    private enum Ty { Int, Bool }
    private enum Op { Add, Sub, Mul, Div, Rem, Eq, Ne, Lt, Le, Gt, Ge }

    // Closed, backend-neutral IR. Slots and loop ids are compiler-assigned; no source text is retained.
    private abstract record Value(SourceLocation Location) { public abstract Ty Type { get; } }
    private sealed record Const(Ty Kind, int Number, SourceLocation At) : Value(At) { public override Ty Type => Kind; }
    private sealed record Read(int Slot, Ty Kind, SourceLocation At) : Value(At) { public override Ty Type => Kind; }
    private sealed record Negate(Value Operand, SourceLocation At) : Value(At) { public override Ty Type => Ty.Int; }
    private sealed record Not(Value Operand, SourceLocation At) : Value(At) { public override Ty Type => Ty.Bool; }
    private sealed record Binary(Op Operator, Value Left, Value Right, SourceLocation At) : Value(At)
    { public override Ty Type => Operator <= Op.Rem ? Ty.Int : Ty.Bool; }
    private sealed record Logical(bool IsAnd, Value Left, Value Right, SourceLocation At) : Value(At) { public override Ty Type => Ty.Bool; }

    private abstract record Statement(SourceLocation Location);
    private sealed record Declare(int Slot, Ty Kind, Value? Initializer, SourceLocation At) : Statement(At);
    private sealed record Store(int Slot, Value Value, SourceLocation At) : Statement(At);
    private sealed record Return(Value Value, SourceLocation At) : Statement(At);
    private sealed record Block(IReadOnlyList<Statement> Statements, SourceLocation At) : Statement(At);
    private sealed record If(Value Condition, IReadOnlyList<Statement> Then, IReadOnlyList<Statement> Else, SourceLocation At) : Statement(At);
    private sealed record Loop(int Id, Value Condition, IReadOnlyList<Statement> Body, IReadOnlyList<Statement> Step, bool NeedsContinueLabel, SourceLocation At) : Statement(At);
    private sealed record Break(SourceLocation At) : Statement(At);
    private sealed record Continue(int LoopId, bool ViaLabel, SourceLocation At) : Statement(At);

    private sealed class Rejected(SourceLocation at, string message) : Exception(message)
    { public SourceLocation At { get; } = at; }

    public static Preparation Prepare(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        var parsed = AISharpParser.Parse(source, ScalarLanguage.StrictProfile);
        var diagnostics = parsed.Diagnostics.ToList();
        if (!parsed.Success) return new(hash, null, diagnostics);
        try
        {
            var functions = parsed.Document.ScalarFunctions;
            if (functions.Count != 1) throw new Rejected(new(1, 1, 0), "Native control requires one entry function.");
            var function = functions[0];
            if (function.Name != "main" || function.Parameters.Count != 0 || function.ReturnType != ScalarType.Int)
                throw new Rejected(function.Location, "Native control requires fn main() -> int.");
            if (function.Contracts.Count != 1 || function.Contracts[0].Kind != ContractKind.Ensures)
                throw new Rejected(function.Location, "Native control requires exactly one ensures true contract.");
            if (function.Contracts[0].Expression is not { Operation: "literal", Type: ScalarType.Bool, Value: "true", Left: null, Right: null })
                throw new Rejected(function.Contracts[0].Expression.Location, "Only the proven ensures true contract is supported.");
            var body = new List<Statement>();
            var completes = new Verifier().VerifyEntry(function.Body, body);
            if (completes) throw new Rejected(function.Location, "Native entry must return on every reaching path.");
            return new(hash, new Emitter().Emit(body), diagnostics);
        }
        catch (Rejected error)
        {
            diagnostics.Add(new("AI3201", DiagnosticSeverity.Error, error.Message, error.At));
            return new(hash, null, diagnostics);
        }
    }

    private sealed class LoopInfo(int id, bool hasStep) { public int Id { get; } = id; public bool HasStep { get; } = hasStep; public bool Breaks; public bool Continues; }

    /// <summary>Independently re-checks scope, types, mutability, definite assignment, reachability and jump targets.</summary>
    private sealed class Verifier
    {
        private sealed record Binding(int Slot, Ty Type, bool Mutable);
        private readonly Dictionary<string, Binding> bindings = new(StringComparer.Ordinal);
        private HashSet<string> assigned = new(StringComparer.Ordinal);
        private readonly Stack<LoopInfo> loops = new();
        private int nextSlot, nextLoop;

        private static Ty Map(ScalarType type, SourceLocation at) => type switch
        {
            ScalarType.Int => Ty.Int, ScalarType.Bool => Ty.Bool,
            _ => throw new Rejected(at, "Native control supports Int32 and bool values only.")
        };
        private Binding Lookup(ScalarExpression expression, bool reading)
        {
            if (expression.Operation != "local" || expression.Value is null || !bindings.TryGetValue(expression.Value, out var binding)
                || binding.Type != Map(expression.Type, expression.Location))
                throw new Rejected(expression.Location, "Expected an in-scope typed local.");
            if (reading && !assigned.Contains(expression.Value)) throw new Rejected(expression.Location, "Local read requires definite assignment.");
            return binding;
        }
        private static Value Require(Value value, Ty type, SourceLocation at) =>
            value.Type == type ? value : throw new Rejected(at, "Operand type is outside the verified native operation.");

        private Value VerifyValue(ScalarExpression? expression, SourceLocation fallback)
        {
            if (expression is null) throw new Rejected(fallback, "Expected a value.");
            var at = expression.Location;
            Value Operand(ScalarExpression? e, Ty type) => Require(VerifyValue(e, at), type, e?.Location ?? at);
            switch (expression.Operation)
            {
                case "literal" when expression is { Left: null, Right: null }:
                    if (expression.Type == ScalarType.Bool && expression.Value is "true" or "false")
                        return new Const(Ty.Bool, expression.Value == "true" ? 1 : 0, at);
                    if (expression.Type == ScalarType.Int
                        && int.TryParse(expression.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                        return new Const(Ty.Int, number, at);
                    break;
                case "local":
                    return new Read(Lookup(expression, true).Slot, Map(expression.Type, at), at);
                case "unary+" when expression.Right is null:
                    return Operand(expression.Left, Ty.Int);
                case "unary-" when expression.Right is null:
                    if (expression.Left is { Operation: "literal", Type: ScalarType.Int, Left: null, Right: null } literal
                        && int.TryParse(literal.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude))
                        return new Const(Ty.Int, -magnitude, at);
                    return new Negate(Operand(expression.Left, Ty.Int), at);
                case "unary!" when expression.Right is null:
                    return new Not(Operand(expression.Left, Ty.Bool), at);
                case "+" or "-" or "*" or "/" or "%":
                {
                    var op = expression.Operation switch { "+" => Op.Add, "-" => Op.Sub, "*" => Op.Mul, "/" => Op.Div, _ => Op.Rem };
                    return new Binary(op, Operand(expression.Left, Ty.Int), Operand(expression.Right, Ty.Int), at);
                }
                case "<" or "<=" or ">" or ">=":
                {
                    var op = expression.Operation switch { "<" => Op.Lt, "<=" => Op.Le, ">" => Op.Gt, _ => Op.Ge };
                    return new Binary(op, Operand(expression.Left, Ty.Int), Operand(expression.Right, Ty.Int), at);
                }
                case "==" or "!=":
                {
                    var left = VerifyValue(expression.Left, at); var right = VerifyValue(expression.Right, at);
                    if (left.Type != right.Type) throw new Rejected(at, "Equality operands must have one type.");
                    return new Binary(expression.Operation == "==" ? Op.Eq : Op.Ne, left, right, at);
                }
                case "&&" or "||":
                    return new Logical(expression.Operation == "&&", Operand(expression.Left, Ty.Bool), Operand(expression.Right, Ty.Bool), at);
            }
            throw new Rejected(at, "Expression is outside the native control capability (assignment/update expressions are statements only).");
        }

        private static bool? Constant(Value value) => value switch
        {
            Const { Kind: Ty.Bool } c => c.Number != 0,
            Not n => Constant(n.Operand) is bool v ? !v : null,
            Logical { IsAnd: true } l => Constant(l.Left) is bool a && Constant(l.Right) is bool b ? a && b : null,
            Logical l => Constant(l.Left) is bool a && Constant(l.Right) is bool b ? a || b : null,
            _ => null
        };

        public bool VerifyEntry(IReadOnlyList<ScalarStatement> statements, List<Statement> output) => VerifyBlock(statements, output);

        // Returns true when control can fall off the end of the list.
        private bool VerifyBlock(IReadOnlyList<ScalarStatement> statements, List<Statement> output)
        {
            var declared = new List<string>(); var completes = true;
            foreach (var statement in statements)
            {
                if (!completes) throw new Rejected(statement.Location, "Unreachable statements are outside the native control capability.");
                completes = VerifyStatement(statement, output, declared);
            }
            foreach (var name in declared) { bindings.Remove(name); assigned.Remove(name); }
            return completes;
        }

        private bool VerifyStatement(ScalarStatement statement, List<Statement> output, List<string> declared)
        {
            switch (statement.Kind)
            {
                case "local":
                {
                    var type = Map(statement.Type, statement.Location);
                    if (statement.Name is null || bindings.ContainsKey(statement.Name))
                        throw new Rejected(statement.Location, "Expected a uniquely bound local declaration.");
                    var value = statement.Expression is null ? null : Require(VerifyValue(statement.Expression, statement.Location), type, statement.Expression.Location);
                    if (!statement.Mutable && value is null) throw new Rejected(statement.Location, "Immutable let requires initialization.");
                    var slot = nextSlot++;
                    bindings.Add(statement.Name, new(slot, type, statement.Mutable)); declared.Add(statement.Name);
                    if (value is not null) assigned.Add(statement.Name);
                    output.Add(new Declare(slot, type, value, statement.Location));
                    return true;
                }
                case "expression":
                    output.Add(VerifyUpdate(statement.Expression, statement.Location)); return true;
                case "block":
                {
                    var nested = new List<Statement>(); var completes = VerifyBlock(statement.Body ?? [], nested);
                    output.Add(new Block(nested, statement.Location)); return completes;
                }
                case "return":
                {
                    var value = Require(VerifyValue(statement.Expression, statement.Location), Ty.Int, statement.Expression?.Location ?? statement.Location);
                    output.Add(new Return(value, statement.Location)); return false;
                }
                case "if":
                {
                    var condition = Require(VerifyValue(statement.Expression, statement.Location), Ty.Bool, statement.Expression?.Location ?? statement.Location);
                    if (Constant(condition) is not null)
                        throw new Rejected(statement.Location, "Constant if conditions are outside the native control capability.");
                    var before = new HashSet<string>(assigned, StringComparer.Ordinal);
                    var yes = new List<Statement>(); var yesCompletes = VerifyBlock(statement.Body ?? [], yes);
                    var afterYes = assigned; assigned = new(before, StringComparer.Ordinal);
                    var no = new List<Statement>(); var noCompletes = VerifyBlock(statement.Alternative ?? [], no);
                    var afterNo = assigned;
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
                case "for":
                {
                    // The initializer scope covers the loop only; it is a nested block in verified IR.
                    var scoped = new List<Statement>(); var scopeNames = new List<string>();
                    if (statement.Initializer is not { Kind: "local" }) throw new Rejected(statement.Location, "For requires a local initializer.");
                    VerifyStatement(statement.Initializer, scoped, scopeNames);
                    scoped.Add(VerifyLoop(statement, statement.Increment));
                    foreach (var name in scopeNames) { bindings.Remove(name); assigned.Remove(name); }
                    output.Add(new Block(scoped, statement.Location)); return LoopCompletes(scoped[^1]);
                }
                case "break":
                    if (loops.Count == 0) throw new Rejected(statement.Location, "break requires a loop.");
                    loops.Peek().Breaks = true; output.Add(new Break(statement.Location)); return false;
                case "continue":
                {
                    if (loops.Count == 0) throw new Rejected(statement.Location, "continue requires a loop.");
                    var target = loops.Peek(); target.Continues = true;
                    output.Add(new Continue(target.Id, target.HasStep, statement.Location)); return false;
                }
                default: throw new Rejected(statement.Location, "Statement is outside the native control capability.");
            }
        }

        private readonly Dictionary<Loop, bool> loopCompletion = new(ReferenceEqualityComparer.Instance);
        private bool LoopCompletes(Statement loop) => loopCompletion[(Loop)loop];

        private Loop VerifyLoop(ScalarStatement statement, ScalarExpression? increment)
        {
            var condition = Require(VerifyValue(statement.Expression, statement.Location), Ty.Bool, statement.Expression?.Location ?? statement.Location);
            var before = new HashSet<string>(assigned, StringComparer.Ordinal);
            var info = new LoopInfo(nextLoop++, increment is not null);
            var step = new List<Statement>();
            if (increment is not null) { step.Add(VerifyUpdate(increment, statement.Location)); assigned = new(before, StringComparer.Ordinal); }
            loops.Push(info);
            var body = new List<Statement>(); VerifyBlock(statement.Body ?? [], body);
            loops.Pop();
            // Zero iterations are possible, so nothing assigned inside the loop is assigned after it.
            assigned = before;
            var loop = new Loop(info.Id, condition, body, step, info.HasStep && info.Continues, statement.Location);
            loopCompletion[loop] = Constant(condition) != true || info.Breaks;
            return loop;
        }

        // Statement-level assignment/update forms; every one becomes a verified Store.
        private Statement VerifyUpdate(ScalarExpression? expression, SourceLocation fallback)
        {
            if (expression?.Left is null) throw new Rejected(expression?.Location ?? fallback, "Expected an assignment or update statement.");
            var at = expression.Location;
            switch (expression.Operation)
            {
                case "=" when expression.Right is not null:
                {
                    var binding = Lookup(expression.Left, false);
                    if (!binding.Mutable) throw new Rejected(expression.Left.Location, "Cannot assign immutable let.");
                    var value = Require(VerifyValue(expression.Right, at), binding.Type, expression.Right.Location);
                    assigned.Add(expression.Left.Value!);
                    return new Store(binding.Slot, value, at);
                }
                case "+=" or "-=" when expression.Right is not null:
                {
                    var binding = Lookup(expression.Left, true);
                    if (!binding.Mutable || binding.Type != Ty.Int) throw new Rejected(expression.Left.Location, "Compound assignment requires a mutable Int32 local.");
                    var value = Require(VerifyValue(expression.Right, at), Ty.Int, expression.Right.Location);
                    return new Store(binding.Slot, new Binary(expression.Operation == "+=" ? Op.Add : Op.Sub,
                        new Read(binding.Slot, Ty.Int, expression.Left.Location), value, at), at);
                }
                case "pre++" or "post++" or "pre--" or "post--" when expression.Right is null:
                {
                    var binding = Lookup(expression.Left, true);
                    if (!binding.Mutable || binding.Type != Ty.Int) throw new Rejected(expression.Left.Location, "Increment requires a mutable Int32 local.");
                    return new Store(binding.Slot, new Binary(expression.Operation.EndsWith("++", StringComparison.Ordinal) ? Op.Add : Op.Sub,
                        new Read(binding.Slot, Ty.Int, expression.Left.Location), new Const(Ty.Int, 1, at), at), at);
                }
            }
            throw new Rejected(at, "Expression statement is outside the native control capability.");
        }
    }

    private sealed class Emitter
    {
        private readonly StringBuilder c = new();
        private readonly Dictionary<string, string> helpers = new(StringComparer.Ordinal);
        private int temp;
        private bool usesFault;
        private static string Slot(int slot) => "ai_local_" + slot.ToString(CultureInfo.InvariantCulture);
        private string Temp() => "ai_t" + (temp++).ToString(CultureInfo.InvariantCulture);

        private const string FaultHelper =
            "#if defined(_MSC_VER)\n#define AI_NORETURN __declspec(noreturn)\n#else\n#define AI_NORETURN _Noreturn\n#endif\n"
            + "AI_NORETURN static void ai_fault(const char *exception) {\n"
            + " if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"threw\\\",\\\"exception\\\":\\\"%s\\\"}\\n\", exception) < 0) exit(74);\n"
            + " if (fflush(stdout) != 0) exit(74);\n exit(70);\n}\n";
        private string Helper(string name, string text) { helpers.TryAdd(name, text); usesFault = true; return name; }

        private string Atom(Value value)
        {
            switch (value)
            {
                case Const { Kind: Ty.Int, Number: int.MinValue }: return "(-2147483647 - 1)";
                case Const constant: return constant.Number.ToString(CultureInfo.InvariantCulture);
                case Read read: return Slot(read.Slot);
                case Negate negate:
                {
                    var operand = Atom(negate.Operand); var t = Temp();
                    var name = Helper("ai_neg", "static int32_t ai_neg(int32_t a) { if (a == INT32_MIN) ai_fault(\"System.OverflowException\"); return -a; }\n");
                    c.Append("int32_t ").Append(t).Append(" = ").Append(name).Append('(').Append(operand).AppendLine(");"); return t;
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
                    switch (binary.Operator)
                    {
                        case Op.Add: expression = Helper("ai_add", "static int32_t ai_add(int32_t a, int32_t b) { int64_t r = (int64_t)a + (int64_t)b; if (r < INT32_MIN || r > INT32_MAX) ai_fault(\"System.OverflowException\"); return (int32_t)r; }\n") + $"({left}, {right})"; break;
                        case Op.Sub: expression = Helper("ai_sub", "static int32_t ai_sub(int32_t a, int32_t b) { int64_t r = (int64_t)a - (int64_t)b; if (r < INT32_MIN || r > INT32_MAX) ai_fault(\"System.OverflowException\"); return (int32_t)r; }\n") + $"({left}, {right})"; break;
                        case Op.Mul: expression = Helper("ai_mul", "static int32_t ai_mul(int32_t a, int32_t b) { int64_t r = (int64_t)a * (int64_t)b; if (r < INT32_MIN || r > INT32_MAX) ai_fault(\"System.OverflowException\"); return (int32_t)r; }\n") + $"({left}, {right})"; break;
                        case Op.Div: expression = Helper("ai_div", "static int32_t ai_div(int32_t a, int32_t b) { if (b == 0) ai_fault(\"System.DivideByZeroException\"); if (b == -1 && a == INT32_MIN) ai_fault(\"System.OverflowException\"); return a / b; }\n") + $"({left}, {right})"; break;
                        case Op.Rem: expression = Helper("ai_rem", "static int32_t ai_rem(int32_t a, int32_t b) { if (b == 0) ai_fault(\"System.DivideByZeroException\"); if (b == -1 && a == INT32_MIN) ai_fault(\"System.OverflowException\"); return a % b; }\n") + $"({left}, {right})"; break;
                        case Op.Eq: expression = $"({left} == {right})"; break;
                        case Op.Ne: expression = $"({left} != {right})"; break;
                        case Op.Lt: expression = $"({left} < {right})"; break;
                        case Op.Le: expression = $"({left} <= {right})"; break;
                        case Op.Gt: expression = $"({left} > {right})"; break;
                        case Op.Ge: expression = $"({left} >= {right})"; break;
                        default: throw new InvalidOperationException("Unverified native operator.");
                    }
                    c.Append("int32_t ").Append(t).Append(" = ").Append(expression).AppendLine(";"); return t;
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
                    case Declare local:
                    {
                        var init = local.Initializer is null ? null : Atom(local.Initializer);
                        c.Append("int32_t ").Append(Slot(local.Slot));
                        if (init is not null) c.Append(" = ").Append(init);
                        // Address-only use suppresses unused-local warnings without reading uninitialized storage.
                        c.Append("; (void)&").Append(Slot(local.Slot)).AppendLine(";"); break;
                    }
                    case Store store: { var value = Atom(store.Value); c.Append(Slot(store.Slot)).Append(" = ").Append(value).AppendLine(";"); break; }
                    case Return returned: { var value = Atom(returned.Value); c.Append("return ").Append(value).AppendLine(";"); break; }
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
                        var guard = Guard(loop.Condition);
                        c.Append("if (!").Append(guard).AppendLine(") break;");
                        c.AppendLine("{"); Statements(loop.Body); c.AppendLine("}");
                        if (loop.NeedsContinueLabel) c.Append("ai_continue_").Append(loop.Id.ToString(CultureInfo.InvariantCulture)).AppendLine(": ;");
                        if (loop.Step.Count > 0) { c.AppendLine("{"); Statements(loop.Step); c.AppendLine("}"); }
                        c.AppendLine("}"); break;
                    }
                    case Break: c.AppendLine("break;"); break;
                    case Continue jump:
                        if (jump.ViaLabel) c.Append("goto ai_continue_").Append(jump.LoopId.ToString(CultureInfo.InvariantCulture)).AppendLine(";");
                        else c.AppendLine("continue;");
                        break;
                    default: throw new InvalidOperationException("Unverified native statement.");
                }
        }

        private static bool CanFallThrough(IReadOnlyList<Statement> statements) => statements.All(CanFallThrough);
        private static bool CanFallThrough(Statement statement) => statement switch
        {
            Return or Break or Continue => false,
            If branch => CanFallThrough(branch.Then) || CanFallThrough(branch.Else),
            Block block => CanFallThrough(block.Statements),
            _ => true
        };

        public string Emit(IReadOnlyList<Statement> body)
        {
            Statements(body);
            // A verified entry cannot fall through, but a C compiler sees every loop as exitable through its guard.
            // State the impossible fall-through only where C can reach it, so no unreachable code is generated.
            if (CanFallThrough(body)) { usesFault = true; c.AppendLine("ai_fault(\"System.InvalidProgramException\");"); }
            var text = new StringBuilder("#include <stdint.h>\n#include <stdio.h>\n#include <stdlib.h>\n#include <inttypes.h>\n");
            text.Append("_Static_assert(sizeof(int32_t) == 4, \"AI# requires int32_t\");\n");
            // Optimizers legitimately prove checked operations always fault; code after such a point is unreachable by design.
            text.Append("#if defined(_MSC_VER)\n#pragma warning(disable: 4702)\n#endif\n");
            if (usesFault) text.Append(FaultHelper);
            foreach (var helper in helpers.OrderBy(h => h.Key, StringComparer.Ordinal)) text.Append(helper.Value);
            text.Append("static int32_t aisharp_entry(void) {\n").Append(c).Append("}\n");
            text.Append("int main(void) { int32_t result = aisharp_entry();\n if (printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"value\\\":%\" PRId32 \"}\\n\", result) < 0) return 74;\n return fflush(stdout) == 0 ? 0 : 74; }\n");
            return text.ToString();
        }
    }
}
