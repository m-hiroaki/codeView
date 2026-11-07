using OperationStatus = System.Buffers.OperationStatus;
using System.Globalization;
using System.Text;
using CodeView.Contracts;
using CodeView.Search;

namespace CodeView.Navigation;

internal enum CSharpTokenKind { Identifier, Number, Symbol, Literal }
internal readonly record struct CSharpToken(CSharpTokenKind Kind, string Text, TextRange Range);
internal enum CSharpLexError { None, Incomplete, InvalidText, Unsupported, TokenLimit, DepthLimit }

/// <summary>
/// Serialized, bounded declaration lexer, not a compiler/parser. Literal bodies and interpolation
/// expressions are excluded from declaration tokens. All emitted tokens remain provisional until EOF.
/// </summary>
internal sealed class CSharpLexer
{
    internal const int TokenChars = 4096;
    internal const int MaximumDepth = 1024;
    private const int BlockChars = 16 * 1024;
    private readonly char[] _input = new char[BlockChars + 4];
    private readonly char[] _token = new char[TokenChars + 1];
    private readonly Stack<Frame> _frames = new();
    private TextCursor _cursor = new();
    private int _start, _count, _tokenCount, _depth;
    private bool _executing, _lineStart = true, _identifierFirst;
    private Mode _mode;
    private long _tokenOffset, _literalOffset;
    private TextPosition _tokenPosition, _literalPosition;
    private CSharpTokenKind _tokenKind;

    private enum Mode { Code, Token, LineComment, BlockComment, Directive, Suffix }
    private enum FrameKind { Regular, Verbatim, Raw, Character, Expression, Format }
    private sealed class Frame(FrameKind kind, int quotes = 1, int dollars = 0)
    {
        public FrameKind Kind = kind;
        public readonly int Quotes = quotes, Dollars = dollars;
        public int Braces, Parentheses, Brackets, CharacterUnits;
        public bool RawOpeningBlank = true, RawLineBlank = true;
        public int RawNewLines;
    }

    public bool IsFinished { get; private set; }
    public bool IsComplete { get; private set; }
    public CSharpLexError Error { get; private set; }
    public long Offset => _cursor.Offset;
    public TextPosition Position => _cursor.Position;
    internal int BufferedChars => _count + _tokenCount;
    internal int ContextDepth => _depth;

    public bool Push(ReadOnlySpan<char> text, Func<CSharpToken, bool> consume, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consume);
        Enter();
        try
        {
            ct.ThrowIfCancellationRequested();
            while (!text.IsEmpty)
            {
                if (_start > 0)
                {
                    _input.AsSpan(_start, _count).CopyTo(_input);
                    _input.AsSpan(_count, _start).Clear();
                    _start = 0;
                }
                var size = Math.Min(text.Length, Math.Min(BlockChars, _input.Length - _count));
                if (size == 0) throw new InvalidOperationException("字句走査の先読み予算を超えました。");
                text[..size].CopyTo(_input.AsSpan(_count));
                _count += size;
                text = text[size..];
                if (!Process(eof: false, consume, ct)) return false;
            }
            return true;
        }
        catch { Stop(); throw; }
        finally { _executing = false; }
    }

    public bool Complete(Func<CSharpToken, bool> consume, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consume);
        Enter();
        try
        {
            if (!Process(eof: true, consume, ct)) return false;
            if (_count != 0 || _frames.Count != 0 || _mode != Mode.Code)
                return Fail(CSharpLexError.Incomplete);
            IsComplete = true;
            Stop();
            return true;
        }
        catch { Stop(); throw; }
        finally { _executing = false; }
    }

    public void Abort()
    {
        if (_executing) throw new InvalidOperationException("通知中に字句走査を操作できません。");
        Stop();
    }

    public void Reset()
    {
        if (_executing || !IsFinished) throw new InvalidOperationException("終了後だけ字句走査を再利用できます。");
        _cursor = new();
        _lineStart = true;
        _previousCarriageReturn = false;
        IsFinished = IsComplete = false;
        Error = CSharpLexError.None;
    }

    private bool Process(bool eof, Func<CSharpToken, bool> consume, CancellationToken ct)
    {
        while (!IsFinished)
        {
            ct.ThrowIfCancellationRequested();
            if (_mode == Mode.Suffix)
            {
                if (_count == 0 && !eof) return true;
                if (_allowUtf8 && _count > 0 && At(0) == 'u')
                {
                    if (_count < 2 && !eof) return true;
                    if (_count >= 2 && At(1) == '8') Consume(2);
                }
                _mode = Mode.Code;
                if (!Emit(CSharpTokenKind.Literal, "", _literalOffset, _literalPosition, consume)) return false;
                continue;
            }
            if (_count == 0)
            {
                if (!eof) return true;
                if (_mode == Mode.Token)
                {
                    if (_identifierFirst) return Fail(CSharpLexError.InvalidText);
                    if (!EmitToken(consume)) return false;
                    continue;
                }
                if (_mode is Mode.LineComment or Mode.Directive) _mode = Mode.Code;
                return _mode == Mode.Code && _frames.Count == 0 || Fail(CSharpLexError.Incomplete);
            }
            var decoded = Rune.DecodeFromUtf16(_input.AsSpan(_start, _count), out var rune, out var size);
            if (decoded == OperationStatus.NeedMoreData && !eof) return true;
            if (decoded != OperationStatus.Done) return Fail(CSharpLexError.InvalidText);
            var c = rune.Value;
            if (_mode == Mode.Token)
            {
                var part = _identifierFirst ? IsIdentifierStart(rune) : UnicodeWordBoundary.IsWord(rune);
                if (_tokenKind == CSharpTokenKind.Number)
                    part |= c == '.' || ((c is '+' or '-') && _tokenCount > 0 && _token[_tokenCount - 1] is 'e' or 'E');
                if (part)
                {
                    var maximum = TokenChars + (_tokenCount > 0 && _token[0] == '@' ? 1 : 0);
                    if (_tokenCount + size > maximum) return Fail(CSharpLexError.TokenLimit);
                    _input.AsSpan(_start, size).CopyTo(_token.AsSpan(_tokenCount));
                    _tokenCount += size;
                    _identifierFirst = false;
                    Consume(size);
                }
                else
                {
                    if (_identifierFirst) return Fail(CSharpLexError.InvalidText);
                    if (c == '\\') return Fail(CSharpLexError.Unsupported); // Escaped identifiers are not silently renamed.
                    if (!EmitToken(consume)) return false;
                }
                continue;
            }
            if (_mode is Mode.LineComment or Mode.Directive)
            {
                if (IsNewLine(c)) _mode = Mode.Code;
                Consume(size);
                continue;
            }
            if (_mode == Mode.BlockComment)
            {
                if (c == '*')
                {
                    if (_count == 1 && !eof) return true;
                    if (_count > 1 && At(1) == '/') { Consume(2); _mode = Mode.Code; continue; }
                }
                Consume(size);
                continue;
            }
            var frame = _frames.Count > 0 ? _frames.Peek() : null;
            if (frame is not null && frame.Kind is not FrameKind.Expression)
            {
                if (frame.Kind == FrameKind.Format)
                {
                    if (c == '}')
                    {
                        if (_count < frame.Dollars && !eof) return true;
                        if (!HasRun('}', frame.Dollars)) return Fail(CSharpLexError.InvalidText);
                        Consume(frame.Dollars); Pop(); continue;
                    }
                    if (c == '{') return Fail(CSharpLexError.Unsupported);
                    if (IsNewLine(c) || c == '"') return Fail(CSharpLexError.Unsupported);
                    Consume(size); continue;
                }
                if (frame.Kind == FrameKind.Raw)
                {
                    if (c == '"')
                    {
                        var run = Run('"');
                        if (run > TokenChars) return Fail(CSharpLexError.TokenLimit);
                        if (run == _count && !eof) return true;
                        if (run > frame.Quotes) return Fail(CSharpLexError.InvalidText);
                        if (run == frame.Quotes && frame.RawNewLines > 0 && (!frame.RawLineBlank || frame.RawNewLines < 2))
                            return Fail(CSharpLexError.InvalidText);
                        Consume(run);
                        if (run == frame.Quotes) CloseLiteral();
                        else { frame.RawOpeningBlank = false; frame.RawLineBlank = false; }
                        continue;
                    }
                    if (frame.Dollars > 0 && (c is '{' or '}'))
                    {
                        var run = Run((char)c);
                        if (run > 2 * frame.Dollars) return Fail(CSharpLexError.InvalidText);
                        if (run == _count && !eof) return true;
                        if (run >= 2 * frame.Dollars || (c == '}' && run >= frame.Dollars)) return Fail(CSharpLexError.InvalidText);
                        frame.RawOpeningBlank = false; frame.RawLineBlank = false;
                        Consume(run);
                        if (run >= frame.Dollars && !PushFrame(new(FrameKind.Expression, dollars: frame.Dollars))) return false;
                        continue;
                    }
                    if (IsNewLine(c))
                    {
                        if (frame.RawNewLines == 0 && !frame.RawOpeningBlank) return Fail(CSharpLexError.InvalidText);
                        // CRLF is one physical line ending, including across input chunks.
                        if (c != '\n' || !_previousCarriageReturn) frame.RawNewLines = Math.Min(2, frame.RawNewLines + 1);
                        frame.RawLineBlank = true;
                    }
                    else if (!Rune.IsWhiteSpace(rune))
                    { frame.RawOpeningBlank = false; frame.RawLineBlank = false; }
                    Consume(size); continue;
                }
                var quote = frame.Kind == FrameKind.Character ? '\'' : '"';
                if (c == quote)
                {
                    if (frame.Kind == FrameKind.Verbatim)
                    {
                        if (_count == 1 && !eof) return true;
                        if (_count > 1 && At(1) == '"') { Consume(2); continue; }
                    }
                    if (frame.Kind == FrameKind.Character && frame.CharacterUnits != 1) return Fail(CSharpLexError.InvalidText);
                    Consume(1); CloseLiteral(); continue;
                }
                if (c == '\\' && frame.Kind != FrameKind.Verbatim)
                {
                    var escaped = EscapeLength(eof, out var units);
                    if (escaped == 0) return true;
                    if (escaped < 0) return Fail(escaped == -2 ? CSharpLexError.Incomplete : CSharpLexError.InvalidText);
                    if (frame.Kind == FrameKind.Character) frame.CharacterUnits += units;
                    Consume(escaped); continue;
                }
                if (IsNewLine(c) && frame.Kind != FrameKind.Verbatim) return Fail(CSharpLexError.InvalidText);
                if (frame.Dollars > 0 && (c is '{' or '}'))
                {
                    if (_count == 1 && !eof) return true;
                    if (_count > 1 && At(1) == c) { Consume(2); continue; }
                    if (c == '}') return Fail(CSharpLexError.InvalidText);
                    Consume(1);
                    if (!PushFrame(new(FrameKind.Expression, dollars: 1))) return false;
                    continue;
                }
                if (frame.Kind == FrameKind.Character)
                {
                    frame.CharacterUnits += size;
                    if (frame.CharacterUnits > 1) return Fail(CSharpLexError.InvalidText);
                }
                Consume(size); continue;
            }
            // Code, including interpolation expressions. Expressions never emit declaration tokens.
            if (Rune.IsWhiteSpace(rune)) { Consume(size); continue; }
            if (c == '#')
            {
                if (!_lineStart) return Fail(CSharpLexError.Unsupported);
                _mode = Mode.Directive; Consume(1); continue;
            }
            if (c == '/')
            {
                if (_count == 1 && !eof) return true;
                if (_count > 1 && At(1) is '/' or '*')
                { _mode = At(1) == '/' ? Mode.LineComment : Mode.BlockComment; Consume(2); continue; }
            }
            if (c is '"' or '$' or '@')
            {
                var literal = StartLiteral(eof);
                if (literal == 0) return true;
                if (literal < 0) return false;
                if (literal == 1) continue;
                // '@' before an identifier, rather than a string.
            }
            if (c == '\'')
            {
                BeginLiteral(); Consume(1);
                if (!PushFrame(new(FrameKind.Character))) return false;
                continue;
            }
            if (IsIdentifierStart(rune) || c is >= '0' and <= '9' || c == '@')
            {
                _tokenKind = c is >= '0' and <= '9' ? CSharpTokenKind.Number : CSharpTokenKind.Identifier;
                _tokenOffset = _cursor.Offset; _tokenPosition = _cursor.Position;
                _mode = Mode.Token;
                _identifierFirst = c == '@';
                _input.AsSpan(_start, size).CopyTo(_token);
                _tokenCount = size; Consume(size); continue;
            }
            if (c > 127 || !"{}()[];,.<>:=+-*%&|^!?~/".Contains((char)c)) return Fail(CSharpLexError.Unsupported);
            if (frame is not null)
            {
                if (c == '}' && frame.Braces == 0)
                {
                    if (frame.Parentheses != 0 || frame.Brackets != 0) return Fail(CSharpLexError.InvalidText);
                    if (_count < frame.Dollars && !eof) return true;
                    if (!HasRun('}', frame.Dollars)) return Fail(CSharpLexError.InvalidText);
                    Consume(frame.Dollars); Pop(); continue;
                }
                if (c is '{' or '(' or '[')
                {
                    if (!GrowDepth()) return false;
                    if (c == '{') frame.Braces++; else if (c == '(') frame.Parentheses++; else frame.Brackets++;
                }
                if (c is '}' or ')' or ']')
                {
                    if (c == '}') { if (frame.Braces == 0) return Fail(CSharpLexError.InvalidText); frame.Braces--; }
                    else if (c == ')') { if (frame.Parentheses == 0) return Fail(CSharpLexError.InvalidText); frame.Parentheses--; }
                    else { if (frame.Brackets == 0) return Fail(CSharpLexError.InvalidText); frame.Brackets--; }
                    _depth--;
                }
                // A top-level colon starts a format; a conditional expression needs parentheses.
                if (c == ':' && frame.Braces == 0 && frame.Parentheses == 0 && frame.Brackets == 0)
                {
                    if (_count == 1 && !eof) return true;
                    if (_count > 1 && At(1) == ':') { Consume(2); continue; }
                    frame.Kind = FrameKind.Format;
                }
                Consume(size); continue;
            }
            var offset = _cursor.Offset; var position = _cursor.Position;
            Consume(size);
            if (!Emit(CSharpTokenKind.Symbol, rune.ToString(), offset, position, consume)) return false;
        }
        return false;
    }

    // 0=needs lookahead, 1=started literal, 2=identifier prefix, -1=failed.
    private int StartLiteral(bool eof)
    {
        var index = 0; var dollars = 0; var verbatim = false;
        if (At(0) == '@')
        {
            if (_count == 1) return eof ? 2 : 0;
            if (At(1) == '$') { index = 2; dollars = 1; verbatim = true; }
            else if (At(1) == '"') { index = 1; verbatim = true; }
            else return 2;
        }
        else if (At(0) == '$')
        {
            dollars = Run('$'); index = dollars;
            if (dollars > TokenChars) { Fail(CSharpLexError.TokenLimit); return -1; }
            if (index < _count && At(index) == '@') { verbatim = true; index++; }
        }
        if (index == _count)
        {
            if (!eof) return 0;
            Fail(CSharpLexError.Incomplete); return -1;
        }
        if (At(index) != '"' || (verbatim && dollars > 1)) { Fail(CSharpLexError.Unsupported); return -1; }
        var quotes = 1;
        if (!verbatim)
        {
            quotes = 0;
            while (index + quotes < _count && At(index + quotes) == '"') quotes++;
            if (quotes > TokenChars) { Fail(CSharpLexError.TokenLimit); return -1; }
            if (index + quotes == _count && !eof) return 0;
            if (dollars > 1 && quotes < 3) { Fail(CSharpLexError.InvalidText); return -1; }
        }
        BeginLiteral();
        Consume(index + (verbatim ? 1 : quotes));
        var kind = verbatim ? FrameKind.Verbatim : quotes >= 3 ? FrameKind.Raw : FrameKind.Regular;
        if (!PushFrame(new(kind, quotes, dollars))) return -1;
        if (!verbatim && quotes == 2) CloseLiteral();
        return 1;
    }

    private void BeginLiteral()
    {
        if (_frames.Count != 0) return;
        _literalOffset = _cursor.Offset; _literalPosition = _cursor.Position;
    }

    private void CloseLiteral()
    {
        var character = _frames.Peek().Kind == FrameKind.Character;
        Pop();
        if (_frames.Count == 0)
        {
            // Character literals do not have a UTF-8 suffix; Suffix is still used to emit once.
            _mode = Mode.Suffix;
            _allowUtf8 = !character;
        }
    }
    private bool _allowUtf8, _previousCarriageReturn;

    private int EscapeLength(bool eof, out int units)
    {
        units = 1;
        if (_count < 2) return eof ? -2 : 0;
        var c = At(1);
        if ("'\"\\0abefnrtv".Contains(c)) return 2;
        if (c is not ('u' or 'U' or 'x')) return -1;
        var minimum = c == 'x' ? 1 : c == 'u' ? 4 : 8;
        var maximum = c == 'U' ? 8 : 4;
        var digits = 0; uint value = 0;
        while (digits < maximum && digits + 2 < _count && Hex(At(digits + 2)) is var digit && digit >= 0)
        { value = (value << 4) | (uint)digit; digits++; }
        if (digits + 2 == _count && !eof && digits < maximum) return 0;
        if (digits < minimum) return digits + 2 == _count && eof ? -2 : -1;
        if (c == 'U' && value > 0x10ffff) return -1;
        if (c == 'U' && value > 0xffff) units = 2;
        return digits + 2;
    }

    private static int Hex(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
    private static bool IsIdentifierStart(Rune rune) => rune.Value == '_' || Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
    private static bool IsNewLine(int c) => c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';
    private char At(int index) => _input[_start + index];
    private int Run(char c) { var n = 0; while (n < _count && At(n) == c) n++; return n; }
    private bool HasRun(char c, int length) => _count >= length && _input.AsSpan(_start, length).IndexOfAnyExcept(c) < 0;

    private void Consume(int length)
    {
        var text = _input.AsSpan(_start, length);
        foreach (var c in text)
        {
            if (IsNewLine(c)) _lineStart = true;
            else if (!char.IsWhiteSpace(c)) _lineStart = false;
            _previousCarriageReturn = c == '\r';
        }
        _cursor.Advance(text);
        _start += length; _count -= length;
    }

    private bool EmitToken(Func<CSharpToken, bool> consume)
    {
        var text = _frames.Count == 0 ? new string(_token, 0, _tokenCount) : "";
        _token.AsSpan(0, _tokenCount).Clear(); _tokenCount = 0; _mode = Mode.Code;
        return Emit(_tokenKind, text, _tokenOffset, _tokenPosition, consume);
    }

    private bool Emit(CSharpTokenKind kind, string text, long offset, TextPosition position, Func<CSharpToken, bool> consume)
    {
        if (_frames.Count != 0) return true;
        if (consume(new(kind, text, new(offset, _cursor.Offset - offset, position)))) return true;
        Stop(); return false;
    }

    private bool GrowDepth() => ++_depth <= MaximumDepth || Fail(CSharpLexError.DepthLimit);
    private bool PushFrame(Frame frame) { if (!GrowDepth()) return false; _frames.Push(frame); return true; }
    private void Pop() { _frames.Pop(); _depth--; }
    private bool Fail(CSharpLexError error) { Error = error; Stop(); return false; }
    private void Enter()
    {
        if (_executing || IsFinished) throw new InvalidOperationException("終了済み、または通知中の字句走査を操作できません。");
        _executing = true;
    }
    private void Stop()
    {
        IsFinished = true;
        Array.Clear(_input); Array.Clear(_token); _frames.Clear();
        _start = _count = _tokenCount = _depth = 0; _mode = Mode.Code;
    }
}
