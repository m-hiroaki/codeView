namespace CodeView.Contracts;

/// <summary>A one-based line and UTF-16 column in the original document.</summary>
internal readonly record struct TextPosition
{
    public long Line { get; }
    public long Column { get; }

    public TextPosition(long line, long column)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(line, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        Line = line;
        Column = column;
    }
}
