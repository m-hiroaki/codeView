namespace CodeView.Search;

internal readonly record struct SearchSnippet(string Text, int MatchStart, int MatchLength)
{
    public const int MaxChars = 200;

    // Borrowed matcher storage is inspected only inside the synchronous hit callback.
    public static SearchSnippet Capture(ReadOnlySpan<char> text, int matchStart, int matchLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(matchStart);
        ArgumentOutOfRangeException.ThrowIfNegative(matchLength);
        if (matchStart > text.Length || matchLength > text.Length - matchStart)
            throw new ArgumentOutOfRangeException(nameof(matchLength));

        var start = matchStart;
        var leftBudget = Math.Min(60, Math.Max(0, MaxChars - matchLength));
        while (start > 0 && matchStart - start < leftBudget && text[start - 1] is not ('\r' or '\n')) start--;
        if (start < text.Length && char.IsLowSurrogate(text[start])) start++;
        var end = Math.Min(text.Length, start + MaxChars);
        for (var index = matchStart; index < end; index++)
            if (text[index] is '\r' or '\n') { end = index; break; }
        if (end > start && char.IsHighSurrogate(text[end - 1])) end--;
        return new(text[start..end].ToString(), matchStart - start, Math.Min(matchLength, end - matchStart));
    }
}
