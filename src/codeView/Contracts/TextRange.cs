namespace CodeView.Contracts;

internal readonly record struct TextRange
{
    public long Offset { get; }
    public long Length { get; }
    public long End => checked(Offset + Length);
    public TextPosition Start { get; }

    public TextRange(long offset, long length, TextPosition start)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _ = checked(offset + length);
        // Value types can be default-initialized; reject an invalid position at the boundary.
        ArgumentOutOfRangeException.ThrowIfLessThan(start.Line, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(start.Column, 1);
        Offset = offset;
        Length = length;
        Start = start;
    }
}
