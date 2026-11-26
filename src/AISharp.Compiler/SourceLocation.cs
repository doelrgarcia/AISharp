namespace AISharp.Compiler;

public readonly record struct SourceLocation(int Line, int Column, int Offset)
{
    public override string ToString() => $"{Line}:{Column}";
}
