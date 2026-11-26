using AISharp.Compiler;

namespace AISharp.Lowering;

public sealed record NativeCompilationPreparation(string SourceSha256, string? CSource, IReadOnlyList<ScalarUnitDiagnostic> Diagnostics)
{
    public int PointerBits { get; init; }
    public bool AcceptsProcessArguments { get; init; }
    public string NormalizedSourceSha256 { get; init; } = "";
    public IReadOnlyList<ScalarCompilationBinding> Bindings { get; init; } = [];
    public bool Success => CSource is not null && Diagnostics.All(d => d.Diagnostic.Severity != DiagnosticSeverity.Error);
}

public static partial class NativeProgramCompiler
{
    public static NativeCompilationPreparation Prepare(IReadOnlyList<ScalarSourceUnit> units, string entryModule, int pointerBits = 64)
    {
        if (pointerBits is not (32 or 64)) throw new ArgumentOutOfRangeException(nameof(pointerBits));
        var compilation = ScalarLanguage.BindCompilation(units, entryModule);
        return PrepareCompilation(compilation,pointerBits);
    }
    private static NativeCompilationPreparation PrepareCompilation(ScalarCompilation compilation,int pointerBits)
    {
        if (!compilation.Success) return new(compilation.SourceSha256, null, compilation.Diagnostics) { PointerBits = pointerBits, Bindings = compilation.Bindings };
        var prepared = PrepareCore(compilation.Source, pointerBits, compilation);
        return new(compilation.SourceSha256, prepared.CSource, prepared.Diagnostics.Select(compilation.MapDiagnostic).ToArray())
        { PointerBits = pointerBits, AcceptsProcessArguments = prepared.AcceptsProcessArguments, NormalizedSourceSha256 = prepared.SourceSha256, Bindings = compilation.Bindings };
    }
}
