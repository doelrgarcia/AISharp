namespace AISharp.Runtime;

// ─── Contract check mode ──────────────────────────────────────────────────────

/// <summary>Determines when a contract check is emitted at runtime.</summary>
public enum ContractCheckMode
{
    /// <summary>Check is always enforced at runtime.</summary>
    Always,
    /// <summary>Check is enforced only in debug builds.</summary>
    DebugOnly,
    /// <summary>Check is emitted only as a verification condition; no runtime enforcement.</summary>
    VerificationOnly
}

// ─── Contract exception ───────────────────────────────────────────────────────

/// <summary>
/// Thrown when an AI# contract clause fails at runtime.
/// Carries the contract kind, the original expression text, and the AI# source location.
/// </summary>
public sealed class AISharpContractException : Exception
{
    /// <summary>Initialises a new <see cref="AISharpContractException"/>.</summary>
    public AISharpContractException(
        string contractKind,
        string expression,
        int line,
        int column,
        int offset)
        : base($"AI# contract '{contractKind}' failed at {line}:{column}: {expression}")
    {
        ContractKind = contractKind;
        Expression = expression;
        Line = line;
        Column = column;
        Offset = offset;
    }

    /// <summary>The kind of contract that failed: <c>requires</c>, <c>ensures</c>, or <c>invariant</c>.</summary>
    public string ContractKind { get; }

    /// <summary>The AI# expression text that evaluated to false.</summary>
    public string Expression { get; }

    /// <summary>One-based source line of the failing contract clause.</summary>
    public int Line { get; }

    /// <summary>One-based source column of the failing contract clause.</summary>
    public int Column { get; }

    /// <summary>Zero-based character offset of the failing contract clause.</summary>
    public int Offset { get; }
}

// ─── Contract helpers ─────────────────────────────────────────────────────────

/// <summary>
/// Runtime helpers for AI# contract clauses.
/// Generated lowered code calls these methods to enforce <c>requires</c>, <c>ensures</c>,
/// and <c>invariant</c> clauses with stable source-location information.
/// </summary>
public static class AISharpContracts
{
    /// <summary>Checks a <c>requires</c> precondition.</summary>
    public static void Requires(bool condition, string expression, int line, int column, int offset) =>
        Check(condition, "requires", expression, line, column, offset);

    /// <summary>Checks an <c>ensures</c> postcondition.</summary>
    public static void Ensures(bool condition, string expression, int line, int column, int offset) =>
        Check(condition, "ensures", expression, line, column, offset);

    /// <summary>Checks an <c>invariant</c> clause.</summary>
    public static void Invariant(bool condition, string expression, int line, int column, int offset) =>
        Check(condition, "invariant", expression, line, column, offset);

    private static void Check(bool condition, string contractKind, string expression, int line, int column, int offset)
    {
        if (!condition)
            throw new AISharpContractException(contractKind, expression, line, column, offset);
    }
}
