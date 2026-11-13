using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Navigation;

internal enum DeclarationKind { Type, Delegate, Method, Property, Field }
internal enum DeclarationScanError { None, Incomplete, DeclarationLimit, DepthLimit }
internal readonly record struct DeclarationMatch(DeclarationKind Kind, TextRange Range);
internal readonly record struct DeclarationScanResult(CSharpFileLexResult Lexical, DeclarationScanError Error)
{
    public bool CanCommit => Lexical.CanCommit && Error == DeclarationScanError.None;
}

internal sealed class CSharpDeclarationScanner(FileReader files)
{
    private readonly CSharpFileLexer _lexer = new(files);

    // Consumers must treat matches as provisional until both source and declaration checks pass.
    public DeclarationScanResult Read(PreparedText source, string name, Func<DeclarationMatch, bool> consume, CancellationToken ct)
    {
        var declarations = new CSharpDeclarations(name, consume);
        var lexical = _lexer.Read(source, declarations.Push, ct);
        if (lexical.CanCommit) declarations.Complete();
        return new(lexical, declarations.Error);
    }
}

internal sealed class CSharpDeclarations(string name, Func<DeclarationMatch, bool> consume)
{
    internal const int MaximumTokens = 256;
    internal const int MaximumChars = 16 * 1024;
    private readonly List<CSharpToken> _header = [];
    private readonly Stack<Scope> _scopes = new();
    private readonly Stack<string> _groups = new();
    private bool _body, _hasAccessor;
    private CSharpToken? _property;
    public DeclarationScanError Error { get; private set; }

    private enum Scope { Namespace, Type }
    private bool InType => _scopes.TryPeek(out var scope) && scope == Scope.Type;

    public bool Push(CSharpToken token)
    {
        if (_body)
        {
            if (_property.HasValue && _groups.Count == 1 && token.Text is "get" or "set" or "init")
                _hasAccessor = true;
            if (!TrackGroup(token.Text)) return false;
            if (_groups.Count != 0) return true;
            _body = false;
            return !_hasAccessor || _property is not { } property || Emit(DeclarationKind.Property, property);
        }

        if (_groups.Count != 0)
            return Append(token) && TrackGroup(token.Text);

        switch (token.Text)
        {
            case "}":
                if (_header.Count != 0 || !_scopes.TryPop(out _)) return Fail(DeclarationScanError.Incomplete);
                return true;
            case "{":
                return StartBody(token);
            case ";":
                if (!WithinBudget(token) || !FinishHeader()) return false;
                _header.Clear();
                return true;
            default:
                return Append(token) && TrackGroup(token.Text);
        }
    }

    public void Complete()
    {
        if (_header.Count != 0 || _groups.Count != 0 || _scopes.Count != 0)
            Fail(DeclarationScanError.Incomplete);
    }

    private bool StartBody(CSharpToken token)
    {
        if (!WithinBudget(token)) return false;
        var header = new Header(_header);
        if (header.IsNamespace)
        {
            _header.Clear();
            return PushScope(Scope.Namespace);
        }
        if (header.TryType(out var type, out var keyword))
        {
            if (!Emit(DeclarationKind.Type, type)) return false;
            _header.Clear();
            if (keyword != "enum") return PushScope(Scope.Type);
            return SkipBody(token, null);
        }
        if (InType && header.TryMember(out var member, out var tail, out var method))
        {
            if (tail == _header.Count)
            {
                if (method && !Emit(DeclarationKind.Method, member)) return false;
                return SkipBody(token, method ? null : member);
            }
            // Object/collection initializers, lambdas, switch expressions and accessor initializers.
            if (header.At(tail) == "=") return Append(token) && TrackGroup(token.Text);
        }
        if (header.HasTopLevelAssignment()) return Append(token) && TrackGroup(token.Text);
        return SkipBody(token, null);
    }

    private bool SkipBody(CSharpToken token, CSharpToken? property)
    {
        _header.Clear();
        _body = true;
        _property = property;
        _hasAccessor = false;
        return TrackGroup(token.Text);
    }

    private bool FinishHeader()
    {
        var header = new Header(_header);
        if (header.TryType(out var type, out _))
            return Emit(DeclarationKind.Type, type);
        if (header.TryDelegate(out var delegated))
            return Emit(DeclarationKind.Delegate, delegated);
        if (!InType) return true;
        var fixedBuffer = header.FixedBufferStart;
        if (fixedBuffer >= 0) return FinishFixedBuffers(header, fixedBuffer);
        if (!header.TryMember(out var member, out var tail, out var method)) return true;
        if (header.IsArrow(tail))
        {
            if (tail + 2 == _header.Count) return Fail(DeclarationScanError.Incomplete);
            return Emit(method ? DeclarationKind.Method : DeclarationKind.Property, member);
        }
        if (method)
            return tail != _header.Count || Emit(DeclarationKind.Method, member);
        if (tail == _header.Count) return Emit(DeclarationKind.Field, member);
        if (header.At(tail) is not ("=" or ",")) return true;

        // Stage matching declarators until the entire field list has been recognized.
        var matching = new List<CSharpToken>();
        while (true)
        {
            if (Matches(member)) matching.Add(member);
            if (header.At(tail) == "=")
            {
                var end = header.SkipInitializer(tail + 1);
                if (end == tail + 1) return Fail(DeclarationScanError.Incomplete);
                tail = end;
            }
            if (tail == _header.Count) break;
            if (header.At(tail++) != "," || !header.IsName(tail)) return Fail(DeclarationScanError.Incomplete);
            member = _header[tail++];
        }
        foreach (var field in matching)
            if (!Emit(DeclarationKind.Field, field)) return false;
        return true;
    }

    private bool FinishFixedBuffers(Header header, int index)
    {
        if (!header.ReadType(ref index)) return Fail(DeclarationScanError.Incomplete);
        var matching = new List<CSharpToken>();
        while (true)
        {
            if (!header.IsName(index)) return Fail(DeclarationScanError.Incomplete);
            var field = _header[index++];
            if (header.At(index) != "[" || header.At(index + 1) == "]")
                return Fail(DeclarationScanError.Incomplete);
            index = header.SkipGroup(index);
            if (Matches(field)) matching.Add(field);
            if (index == _header.Count) break;
            if (header.At(index++) != ",") return Fail(DeclarationScanError.Incomplete);
        }
        foreach (var field in matching)
            if (!Emit(DeclarationKind.Field, field)) return false;
        return true;
    }

    private bool Matches(CSharpToken token) => token.Text.AsSpan(token.Text.StartsWith('@') ? 1 : 0).SequenceEqual(name);
    private bool Emit(DeclarationKind kind, CSharpToken token) =>
        !Matches(token) || consume(new(kind, token.Range));

    private bool WithinBudget(CSharpToken token) =>
        (_header.Count < MaximumTokens && (_header.Count == 0 || token.Range.End - _header[0].Range.Offset <= MaximumChars))
        || Fail(DeclarationScanError.DeclarationLimit);

    private bool Append(CSharpToken token)
    {
        if (!WithinBudget(token)) return false;
        _header.Add(token);
        return true;
    }

    private bool PushScope(Scope scope)
    {
        if (_scopes.Count >= CSharpLexer.MaximumDepth) return Fail(DeclarationScanError.DepthLimit);
        _scopes.Push(scope);
        return true;
    }

    private bool TrackGroup(string text)
    {
        var close = text switch { "(" => ")", "[" => "]", "{" => "}", _ => null };
        if (close != null)
        {
            if (_scopes.Count + _groups.Count >= CSharpLexer.MaximumDepth) return Fail(DeclarationScanError.DepthLimit);
            _groups.Push(close);
        }
        else if (text is ")" or "]" or "}")
        {
            if (!_groups.TryPop(out var expected) || expected != text) return Fail(DeclarationScanError.Incomplete);
        }
        return true;
    }

    private bool Fail(DeclarationScanError error)
    {
        Error = error;
        return false;
    }

    private readonly struct Header(List<CSharpToken> tokens)
    {
        public string? At(int i) => i < tokens.Count ? tokens[i].Text : null;
        public bool IsName(int i) => i < tokens.Count && tokens[i].Kind == CSharpTokenKind.Identifier
            && !IdentifierExtractor.IsReserved(tokens[i].Text);
        public bool IsArrow(int i) => At(i) == "=" && At(i + 1) == ">";
        public bool IsNamespace => At(0) == "namespace" && IsName(1);
        public int FixedBufferStart
        {
            get
            {
                var prefix = Prefix();
                return At(prefix) == "fixed" ? prefix + 1 : -1;
            }
        }

        public bool HasTopLevelAssignment()
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (At(i) == "=") return true;
                if (At(i) is "(" or "[") i = SkipGroup(i) - 1;
            }
            return false;
        }

        private int Prefix() => Prefix(out _);

        private int Prefix(out int firstContextual)
        {
            firstContextual = -1;
            var i = 0;
            while (At(i) == "[") i = SkipGroup(i);
            while (At(i) is "public" or "private" or "protected" or "internal" or "static" or "abstract"
                or "sealed" or "partial" or "readonly" or "ref" or "unsafe" or "new" or "virtual" or "override"
                or "extern" or "async" or "volatile" or "const" or "required" or "file")
            {
                if (firstContextual < 0 && IsContextualModifier(At(i))) firstContextual = i;
                i++;
            }
            return i;
        }

        private static bool IsContextualModifier(string? text) => text is "partial" or "async" or "required" or "file";

        public bool TryType(out CSharpToken name, out string? keyword)
        {
            name = default;
            var i = Prefix();
            keyword = At(i++);
            if (keyword is not ("class" or "struct" or "interface" or "enum" or "record")) return false;
            if (keyword == "record" && At(i) is "class" or "struct") i++;
            if (!IsName(i)) return false;
            name = tokens[i++];
            if (!SkipAngles(ref i)) return false;
            if (At(i) == "(" && keyword is "record" or "class" or "struct") i = SkipGroup(i);
            return At(i) is null or ":" or "where";
        }

        public bool TryDelegate(out CSharpToken name)
        {
            name = default;
            var i = Prefix();
            if (At(i++) != "delegate" || !ReadType(ref i) || !IsName(i)) return false;
            name = tokens[i++];
            return SkipAngles(ref i) && At(i) == "(" && EndMethod(SkipGroup(i)) == tokens.Count;
        }

        public bool TryMember(out CSharpToken name, out int tail, out bool method)
        {
            var prefix = Prefix(out var firstContextual);
            if (TryMemberAt(prefix, out name, out tail, out method)) return true;

            // A contextual modifier may instead begin the member's type.
            for (var i = prefix - 1; firstContextual >= 0 && i >= firstContextual; i--)
                if (IsContextualModifier(At(i)) && TryMemberAt(i, out name, out tail, out method)) return true;
            return false;
        }

        private bool TryMemberAt(int start, out CSharpToken name, out int tail, out bool method)
        {
            name = default;
            tail = start;
            method = false;
            if (!ReadType(ref tail) || !IsName(tail)) return false;
            name = tokens[tail++];
            var generic = At(tail) == "<";
            if (!SkipAngles(ref tail)) return false;
            method = At(tail) == "(";
            if (generic && !method) return false;
            if (method) tail = EndMethod(SkipGroup(tail));
            return true;
        }

        private int EndMethod(int i)
        {
            while (StartsConstraintClause(i))
            {
                i += 3;
                while (At(i) is not (null or "=") && !StartsConstraintClause(i))
                    i = At(i) is "(" or "[" ? SkipGroup(i) : i + 1;
            }
            return i;
        }

        private bool StartsConstraintClause(int i) => At(i) == "where" && IsName(i + 1) && At(i + 2) == ":";

        public bool ReadType(ref int i)
        {
            while (At(i) is "ref" or "readonly") i++;
            if (At(i) == "(") i = SkipGroup(i); // Tuple type.
            else
            {
                while (true)
                {
                    if (!IsName(i) && At(i) is not ("void" or "bool" or "byte" or "sbyte" or "char" or "decimal"
                        or "double" or "float" or "int" or "uint" or "long" or "ulong" or "object" or "short"
                        or "ushort" or "string")) return false;
                    i++;
                    if (!ReadTypeArguments(ref i)) return false;
                    if (At(i) == ".") i++;
                    else if (At(i) == ":" && At(i + 1) == ":") i += 2;
                    else break;
                }
            }
            while (At(i) is "?" or "*" or "[")
                i = At(i) == "[" ? SkipGroup(i) : i + 1;
            return true;
        }

        private bool ReadTypeArguments(ref int i)
        {
            if (At(i) != "<") return true;
            do { i++; if (!ReadType(ref i)) return false; }
            while (At(i) == ",");
            if (At(i) != ">") return false;
            i++;
            return true;
        }

        public int SkipGroup(int i)
        {
            var depth = 0;
            for (; i < tokens.Count; i++)
            {
                if (At(i) is "(" or "[" or "{") depth++;
                else if ((At(i) is ")" or "]" or "}") && --depth == 0) return i + 1;
            }
            return i;
        }

        private bool SkipAngles(ref int i)
        {
            if (At(i) != "<") return true;
            var depth = 0;
            for (; i < tokens.Count; i++)
            {
                if (At(i) == "<") depth++;
                else if (At(i) == ">" && --depth == 0) { i++; return true; }
                else if (At(i) is "(" or "[") i = SkipGroup(i) - 1;
            }
            return false;
        }

        public int SkipInitializer(int i)
        {
            for (; At(i) is not (null or ","); i++)
            {
                if (At(i) is "(" or "[" or "{") i = SkipGroup(i) - 1;
                else if (At(i) == "<")
                {
                    var end = i;
                    // A complete type argument list cannot include a field initializer's assignment.
                    if (ReadTypeArguments(ref end)) i = end - 1;
                }
            }
            return i;
        }
    }
}
