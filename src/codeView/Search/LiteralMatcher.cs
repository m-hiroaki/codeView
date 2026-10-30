using System.Globalization;
using System.Text;
using CodeView.Contracts;

namespace CodeView.Search;

internal readonly record struct SearchOptions(bool MatchCase = false, bool WholeWord = false, bool UseRegex = false);

internal static class SearchLimits
{
    public const int QueryChars = 16 * 1024;
    public const int LiteralScanBlockChars = 16 * 1024;
}

internal static class UnicodeWordBoundary
{
    public static bool IsWord(Rune value) => Rune.GetUnicodeCategory(value) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber
        or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation
        or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format;
}

/// <summary>One serialized scanner per query; reset between files, never retain a whole line.</summary>
internal sealed class LiteralMatcher
{
    private readonly string _query;
    private readonly StringComparison _comparison;
    private readonly bool _wholeWord;
    private readonly char[] _buffer;
    private TextCursor _cursor = new();
    private int _length;
    private int _scanStart;
    public bool IsFinished { get; private set; }
    public int BufferCapacity => _buffer.Length;

    private LiteralMatcher(string query, SearchOptions options)
    {
        _query = query;
        _comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _wholeWord = options.WholeWord;
        _buffer = new char[query.Length + SearchLimits.LiteralScanBlockChars + 4];
    }

    public static OperationResult<LiteralMatcher> Create(string? query, SearchOptions options = default)
    {
        if (string.IsNullOrEmpty(query) || query.Length > SearchLimits.QueryChars || options.UseRegex
            || query.AsSpan().IndexOfAny('\r', '\n') >= 0)
            return Invalid();
        for (var index = 0; index < query.Length;)
        {
            if (!Rune.TryGetRuneAt(query, index, out var value)) return Invalid();
            index += value.Utf16SequenceLength;
        }
        return OperationResult<LiteralMatcher>.Success(new(query, options));

        static OperationResult<LiteralMatcher> Invalid() => OperationResult<LiteralMatcher>.Failure(new(ErrorCode.InvalidPattern));
    }

    public bool Push(ReadOnlySpan<char> text, Func<TextRange, bool> consume, CancellationToken ct)
    {
        VerifyActive(consume);
        try
        {
            ct.ThrowIfCancellationRequested();
            while (!text.IsEmpty)
            {
                if (_length == _buffer.Length) Compact();
                var count = Math.Min(text.Length, Math.Min(SearchLimits.LiteralScanBlockChars, _buffer.Length - _length));
                text[..count].CopyTo(_buffer.AsSpan(_length));
                _length += count;
                text = text[count..];
                if (!Scan(false, consume, ct)) return false;
            }
            return true;
        }
        catch { Abort(); throw; }
    }

    public bool Complete(Func<TextRange, bool> consume, CancellationToken ct)
    {
        VerifyActive(consume);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Scan(true, consume, ct)) return false;
            AdvanceTo(_length);
            Abort();
            return true;
        }
        catch { Abort(); throw; }
    }

    public void Abort()
    {
        _buffer.AsSpan(0, _length).Clear();
        _length = _scanStart = 0;
        IsFinished = true;
    }

    public void Reset()
    {
        if (!IsFinished) throw new InvalidOperationException("未完了の走査は完了または中止してからリセットしてください。");
        _cursor = new();
        IsFinished = false;
    }

    private bool Scan(bool eof, Func<TextRange, bool> consume, CancellationToken ct)
    {
        // Keep up to one complete right-hand scalar until the next chunk or EOF.
        var end = _length - (_wholeWord && !eof ? 2 : 0);
        var lastStart = end - _query.Length;
        while (_scanStart <= lastStart)
        {
            ct.ThrowIfCancellationRequested();
            var relative = _buffer.AsSpan(_scanStart, end - _scanStart).IndexOf(_query.AsSpan(), _comparison);
            if (relative < 0) { AdvanceTo(lastStart + 1); return true; }
            var start = _scanStart + relative;
            AdvanceTo(start);
            var matchEnd = start + _query.Length;
            if (_wholeWord && (!LeftBoundary(start) || !RightBoundary(matchEnd)))
            { AdvanceTo(start + 1); continue; }
            var hit = new TextRange(_cursor.Offset, _query.Length, _cursor.Position);
            AdvanceTo(matchEnd);
            var keepGoing = consume(hit);
            ct.ThrowIfCancellationRequested();
            if (!keepGoing) { Abort(); return false; }
        }
        return true;
    }

    private bool LeftBoundary(int index) => index == 0
        || Rune.DecodeLastFromUtf16(_buffer.AsSpan(0, index), out var value, out _) != System.Buffers.OperationStatus.Done
        || !UnicodeWordBoundary.IsWord(value);

    private bool RightBoundary(int index) => index == _length
        || Rune.DecodeFromUtf16(_buffer.AsSpan(index, _length - index), out var value, out _) != System.Buffers.OperationStatus.Done
        || !UnicodeWordBoundary.IsWord(value);

    private void AdvanceTo(int index)
    {
        _cursor.Advance(_buffer.AsSpan(_scanStart, index - _scanStart));
        _scanStart = index;
    }

    private void Compact()
    {
        // Preserve two units of left scalar context; cursor already describes _scanStart.
        var discard = Math.Max(0, _scanStart - 2);
        if (discard == 0) throw new InvalidOperationException("照合バッファの走査が進んでいません。");
        _buffer.AsSpan(discard, _length - discard).CopyTo(_buffer);
        _buffer.AsSpan(_length - discard, discard).Clear();
        _length -= discard;
        _scanStart -= discard;
    }

    private void VerifyActive(Func<TextRange, bool> consume)
    {
        ArgumentNullException.ThrowIfNull(consume);
        if (IsFinished) throw new InvalidOperationException("終了した走査へ文字を追加できません。");
    }
}
