using CodeView.Contracts;

namespace CodeView.Navigation;

internal readonly record struct CppToken(string Text, TextRange Range, bool Word = false, bool Macro = false);

// The buffer retains only enough lookahead for a raw-string delimiter across read boundaries.
internal delegate bool CppStructuralWord(ReadOnlySpan<char> text);

internal sealed class CppLexer(Func<CppToken, bool> consume, Func<bool> structuralOnly,
    Func<string, bool>? directive = null, CppStructuralWord? structuralWord = null, Action? macroBrace = null)
{
    private const int Lookahead = 20;
    private readonly char[] _input = new char[16 * 1024 + Lookahead];
    private readonly char[] _word = new char[IdentifierExtractor.MaximumChars];
    private readonly char[] _rawEnd = new char[18];
    private TextCursor _cursor = new();
    private int _index, _end, _wordLength, _rawEndLength;
    private long _offset;
    private TextPosition _position;
    private bool _lineStart = true;
    private char _quote, _numberEnd;
    private Mode _mode;
    private Directive _directive;
    private bool _macroBody;
    private enum Mode { Code, Word, Number, LineComment, BlockComment, Quoted, Raw }
    private enum Directive { None, Name, MacroName, Body }

    public void Reset()
    {
        _index = _end = 0;
        _mode = Mode.Code;
        _directive = Directive.None;
        _macroBody = false;
        _lineStart = true;
        _cursor = new();
    }

    public bool Push(ReadOnlySpan<char> chunk, CancellationToken ct)
    {
        while (!chunk.IsEmpty)
        {
            var remainder = _end - _index;
            _input.AsSpan(_index, remainder).CopyTo(_input);
            var size = Math.Min(chunk.Length, _input.Length - remainder);
            chunk[..size].CopyTo(_input.AsSpan(remainder));
            _index = 0;
            _end = remainder + size;
            if (!Process(false, ct)) return false;
            chunk = chunk[size..];
        }
        return true;
    }

    public bool Complete(CancellationToken ct) =>
        Process(true, ct) && (_mode != Mode.Word || FinishWord()) &&
        _mode is Mode.Code or Mode.Number or Mode.LineComment;

    private bool Process(bool eof, CancellationToken ct)
    {
        while (_index < _end && (eof || _end - _index > Lookahead))
        {
            if ((_index & 1023) == 0) ct.ThrowIfCancellationRequested();
            var value = _input[_index];
            var next = _index + 1 < _end ? _input[_index + 1] : '\0';
            // Preserve original source offsets when skipping a physical line continuation.
            if (_mode != Mode.Raw && value == '\\' && next is '\n' or '\r')
            {
                var length = next == '\r' && _index + 2 < _end && _input[_index + 2] == '\n' ? 3 : 2;
                Advance(length);
                continue;
            }
            switch (_mode)
            {
                case Mode.Word:
                    if (IsWord(value))
                    {
                        if (_wordLength == _word.Length) return false;
                        _word[_wordLength++] = value;
                        Advance();
                    }
                    else if (value == '"' && IsRawPrefix(_word.AsSpan(0, _wordLength)))
                    {
                        if (!StartRaw()) return false;
                    }
                    else if ((value is '"' or '\'') && _word.AsSpan(0, _wordLength) is "u8" or "u" or "U" or "L")
                    {
                        if (!StartQuoted(value)) return false;
                    }
                    else if (!FinishWord()) return false;
                    break;
                case Mode.Number:
                    if (IsWord(value) || value == '.' || (value == '\'' && IsWord(next))
                        || ((value is '+' or '-') && _numberEnd is 'e' or 'E' or 'p' or 'P'))
                    {
                        _numberEnd = value;
                        Advance();
                    }
                    else _mode = Mode.Code;
                    break;
                case Mode.LineComment:
                    if (value is '\r' or '\n') { _mode = Mode.Code; NewLine(); }
                    Advance();
                    break;
                case Mode.BlockComment:
                    if (value == '*' && next == '/') { Advance(2); _mode = Mode.Code; }
                    else { if (value is '\r' or '\n') NewLine(); Advance(); }
                    break;
                case Mode.Quoted:
                    if (value is '\r' or '\n') return false;
                    if (value == '\\')
                    {
                        if (_end - _index < 2) return false;
                        Advance(2);
                    }
                    else
                    {
                        Advance();
                        if (value == _quote) _mode = Mode.Code;
                    }
                    break;
                case Mode.Raw:
                    if (value == ')' && _input.AsSpan(_index, _end - _index).StartsWith(_rawEnd.AsSpan(0, _rawEndLength)))
                    {
                        Advance(_rawEndLength);
                        _mode = Mode.Code;
                    }
                    else Advance();
                    break;
                default:
                    if (value is '\r' or '\n') { NewLine(); Advance(); break; }
                    if (char.IsWhiteSpace(value)) { Advance(); break; }
                    if (value == '/' && next is '/' or '*')
                    {
                        _mode = next == '/' ? Mode.LineComment : Mode.BlockComment;
                        Advance(2);
                        break;
                    }
                    if (_lineStart && value == '#')
                    {
                        _lineStart = false;
                        _directive = Directive.Name;
                        Advance();
                        break;
                    }
                    _lineStart = false;
                    _offset = _cursor.Offset;
                    _position = _cursor.Position;
                    if (char.IsAsciiDigit(value) || (value == '.' && char.IsAsciiDigit(next)))
                    {
                        if (!Emit("<number>", 1)) return false;
                        _numberEnd = value;
                        Advance();
                        _mode = Mode.Number;
                    }
                    else if (IsWord(value))
                    {
                        _wordLength = 0;
                        _mode = Mode.Word;
                    }
                    else if (value is '"' or '\'')
                    {
                        if (!StartQuoted(value)) return false;
                    }
                    else
                    {
                        if (_macroBody && value is '{' or '}') macroBrace?.Invoke();
                        if (value == '\\' && _directive == Directive.None) return false;
                        var symbol = (value, next) switch
                        {
                            (':', ':') => "::", ('-', '>') => "->", ('&', '&') => "&&",
                            ('(', _) => "(", (')', _) => ")", ('[', _) => "[", (']', _) => "]",
                            ('{', _) => "{", ('}', _) => "}",
                            _ => ""
                        };
                        if ((value is '(' or ')' or '[' or ']' or '{' or '}' || !structuralOnly())
                            && (_directive == Directive.None)
                            && !Emit(symbol.Length == 0 ? value.ToString() : symbol, Math.Max(1, symbol.Length))) return false;
                        Advance(Math.Max(1, symbol.Length));
                    }
                    break;
            }
        }
        return true;
    }

    private bool FinishWord()
    {
        _mode = Mode.Code;
        var spelling = _word.AsSpan(0, _wordLength);
        if (_directive == Directive.Name)
        {
            _directive = spelling is "define" ? Directive.MacroName : Directive.Body;
            if (spelling is "error" or "warning") _mode = Mode.LineComment;
            if (directive is not null && spelling is "if" or "ifdef" or "ifndef" or "elif" or "elifdef" or "elifndef" or "else" or "endif")
                return directive(spelling.ToString());
            return true;
        }
        if (_directive == Directive.Body) return true;
        var length = _cursor.Offset - _offset;
        if (_directive == Directive.MacroName)
        {
            _directive = Directive.Body;
            _macroBody = macroBrace is not null;
            return consume(new(spelling.ToString(), new(_offset, length, _position), Word: true, Macro: true));
        }
        return structuralOnly() ? structuralWord?.Invoke(spelling) ?? true : Emit(spelling.ToString(), length, word: true);
    }

    private bool StartQuoted(char quote)
    {
        _quote = quote;
        _mode = Mode.Quoted;
        Advance();
        return Emit("<literal>", 1);
    }

    private bool StartRaw()
    {
        _rawEnd[0] = ')';
        var length = 1;
        for (var index = _index + 1; index < _end; index++)
        {
            var value = _input[index];
            if (value == '(')
            {
                _rawEnd[length] = '"';
                _rawEndLength = length + 1;
                Advance(length + 1);
                _mode = Mode.Raw;
                return Emit("<literal>", 1);
            }
            if (length > 16 || value is ')' or '\\' || char.IsWhiteSpace(value)) return false;
            _rawEnd[length++] = value;
        }
        return false;
    }

    private bool Emit(string text, long length, bool word = false) =>
        _directive != Directive.None || consume(new(text, new(_offset, length, _position), word));

    private void Advance(int length = 1)
    {
        _cursor.Advance(_input.AsSpan(_index, length));
        _index += length;
    }

    private void NewLine()
    {
        _lineStart = true;
        _directive = Directive.None;
        _macroBody = false;
    }

    private static bool IsRawPrefix(ReadOnlySpan<char> word) => word is "R" or "u8R" or "uR" or "UR" or "LR";
    private static bool IsWord(char value) => value == '_' || char.IsLetterOrDigit(value) ||
        value >= 128 && (char.IsSurrogate(value) || char.GetUnicodeCategory(value) is
            System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark);
}
