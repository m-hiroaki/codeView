namespace CodeView.Contracts;

/// <summary>Tracks original UTF-16 positions across arbitrary read chunk boundaries.</summary>
internal sealed class TextCursor
{
    private bool _previousWasCarriageReturn;
    public long Offset { get; private set; }
    public TextPosition Position { get; private set; } = new(1, 1);

    public void Advance(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            Offset = checked(Offset + 1);
            if (c == '\r')
            {
                Position = new(checked(Position.Line + 1), 1);
            }
            else if (c == '\n')
            {
                if (!_previousWasCarriageReturn)
                    Position = new(checked(Position.Line + 1), 1);
            }
            else
            {
                Position = new(Position.Line, checked(Position.Column + 1));
            }
            _previousWasCarriageReturn = c == '\r';
        }
    }
}
