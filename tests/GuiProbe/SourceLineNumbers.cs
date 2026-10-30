namespace CodeView.Presentation;

/// <summary>Probe-only projection. A display line never replaces the original line number.</summary>
internal readonly record struct SourceLineNumbers
{
    public long FirstSourceLine { get; }
    public long SourceLineCount { get; }
    public int DisplayLineCount { get; }
    public bool IsValid => FirstSourceLine > 0;
    public int WidthInDigits
    {
        get
        {
            var digits = 1;
            for (var value = SourceLineCount; value >= 10; value /= 10) digits++;
            return Math.Max(2, digits);
        }
    }

    public SourceLineNumbers(long firstSourceLine, long sourceLineCount, int displayLineCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstSourceLine, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceLineCount, firstSourceLine);
        ArgumentOutOfRangeException.ThrowIfLessThan(displayLineCount, 1);
        // Subtraction also validates the last line without overflowing near long.MaxValue.
        if ((long)displayLineCount - 1 > sourceLineCount - firstSourceLine)
            throw new ArgumentOutOfRangeException(nameof(displayLineCount));
        FirstSourceLine = firstSourceLine;
        SourceLineCount = sourceLineCount;
        DisplayLineCount = displayLineCount;
    }

    public long GetSourceLine(int displayLine)
    {
        if (!IsValid) throw new InvalidOperationException("原文の行番号が未接続です。");
        if (displayLine < 1 || displayLine > DisplayLineCount)
            throw new ArgumentOutOfRangeException(nameof(displayLine));
        return FirstSourceLine + (displayLine - 1L);
    }
}
