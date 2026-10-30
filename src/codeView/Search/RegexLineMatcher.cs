using System.Text.RegularExpressions;
using CodeView.Contracts;

namespace CodeView.Search;

/// <summary>One reusable bounded line buffer and one non-compiled Regex per serialized session.</summary>
internal sealed class RegexLineMatcher
{
    private readonly Regex _regex;
    private readonly bool _wholeWord;
    private readonly char[] _line;
    private TextCursor _cursor = new();
    private int _length;
    private bool _overlong;
    private bool _previousCr;
    private long _lineOffset;
    private long _lineNumber = 1;
    private (int Start, int Length)? _hit;
    public bool IsFinished { get; private set; }
    public OperationError? Error { get; private set; }
    public long EvaluatedLines { get; private set; }
    public long SkippedLongLines { get; private set; }
    public long SkippedScalarMatches { get; private set; }
    public int BufferCapacity => _line.Length;
    public int BufferedCharacters => _length;
    public TimeSpan MatchTimeout => _regex.MatchTimeout;

    public SearchSnippet CaptureSnippet() => _hit is { } hit
        ? SearchSnippet.Capture(_line.AsSpan(0, _length), hit.Start, hit.Length)
        : throw new InvalidOperationException("抜粋は一致通知中だけ取得できます。");

    private RegexLineMatcher(Regex regex, bool wholeWord, int limit)
        => (_regex, _wholeWord, _line) = (regex, wholeWord, new char[limit]);

    public static OperationResult<RegexLineMatcher> Create(string? pattern, SearchOptions options,
        int lineLimit = SearchLimits.RegexLineChars, TimeSpan? timeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lineLimit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lineLimit, SearchLimits.RegexLineChars);
        var finiteTimeout = timeout ?? SearchLimits.RegexTimeout;
        if (finiteTimeout <= TimeSpan.Zero || finiteTimeout > SearchLimits.RegexTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (string.IsNullOrEmpty(pattern) || pattern.Length > SearchLimits.QueryChars || !options.UseRegex) return Invalid();
        try
        {
            var flags = RegexOptions.CultureInvariant | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            return OperationResult<RegexLineMatcher>.Success(new(new(pattern, flags, finiteTimeout), options.WholeWord, lineLimit));
        }
        catch (RegexParseException) { return Invalid(); }

        static OperationResult<RegexLineMatcher> Invalid() => OperationResult<RegexLineMatcher>.Failure(new(ErrorCode.InvalidPattern));
    }

    public bool Push(ReadOnlySpan<char> text, Func<TextRange, bool> consume, CancellationToken ct)
    {
        VerifyActive(consume);
        try
        {
            ct.ThrowIfCancellationRequested();
            for (var index = 0; index < text.Length; index++)
            {
                if ((index & 4095) == 0) ct.ThrowIfCancellationRequested();
                var c = text[index];
                if (c is '\r' or '\n')
                {
                    if (!(c == '\n' && _previousCr) && !ScanLine(consume, ct)) return false;
                    _cursor.Advance(text.Slice(index, 1));
                    _lineOffset = _cursor.Offset;
                    _lineNumber = _cursor.Position.Line;
                    _previousCr = c == '\r';
                }
                else
                {
                    _previousCr = false;
                    if (!_overlong)
                    {
                        if (_length == _line.Length) { ClearLine(); _overlong = true; }
                        else _line[_length++] = c;
                    }
                    _cursor.Advance(text.Slice(index, 1));
                }
            }
            ct.ThrowIfCancellationRequested();
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
            if (!ScanLine(consume, ct)) return false;
            Abort();
            return true;
        }
        catch { Abort(); throw; }
    }

    private bool ScanLine(Func<TextRange, bool> consume, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_overlong)
        {
            SkippedLongLines = checked(SkippedLongLines + 1);
            _overlong = false;
            return true;
        }
        var text = _line.AsSpan(0, _length);
        var matches = _regex.EnumerateMatches(text);
        var minimumZeroPosition = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool found;
            try { found = matches.MoveNext(); }
            catch (RegexMatchTimeoutException) { return TimedOut(); }
            if (!found) break;
            var match = matches.Current;
            if (match.Length == 0 && match.Index < minimumZeroPosition) continue;
            var end = checked(match.Index + match.Length);
            if (!UnicodeWordBoundary.IsScalarBoundary(text, match.Index) || !UnicodeWordBoundary.IsScalarBoundary(text, end))
            {
                SkippedScalarMatches = checked(SkippedScalarMatches + 1);
                continue;
            }
            if (match.Length == 0 && match.Index < text.Length)
                minimumZeroPosition = UnicodeWordBoundary.NextScalarBoundary(text, match.Index);
            if (!_wholeWord || (UnicodeWordBoundary.Before(text, match.Index) && UnicodeWordBoundary.After(text, end)))
            {
                var hit = new TextRange(checked(_lineOffset + match.Index), match.Length, new(_lineNumber, checked((long)match.Index + 1)));
                bool keepGoing;
                _hit = (match.Index, match.Length);
                try { keepGoing = consume(hit); }
                finally { _hit = null; }
                ct.ThrowIfCancellationRequested();
                if (!keepGoing) { Abort(); return false; }
            }
            if (match.Length == 0 && match.Index == text.Length) break;
        }
        EvaluatedLines = checked(EvaluatedLines + 1);
        ClearLine();
        return true;
    }

    private bool TimedOut()
    {
        Error = new(ErrorCode.RegexTimedOut);
        Abort();
        return false;
    }

    public void Abort()
    {
        ClearLine();
        _overlong = false;
        IsFinished = true;
    }

    public void Reset()
    {
        if (!IsFinished || Error is not null) throw new InvalidOperationException("未完了または失敗した正規表現走査は再利用できません。");
        _cursor = new();
        _previousCr = false;
        _lineOffset = 0;
        _lineNumber = 1;
        EvaluatedLines = SkippedLongLines = SkippedScalarMatches = 0;
        IsFinished = false;
    }

    private void ClearLine()
    {
        _line.AsSpan(0, _length).Clear();
        _length = 0;
    }

    private void VerifyActive(Func<TextRange, bool> consume)
    {
        ArgumentNullException.ThrowIfNull(consume);
        if (IsFinished) throw new InvalidOperationException("終了した正規表現走査へ文字を追加できません。");
    }
}
