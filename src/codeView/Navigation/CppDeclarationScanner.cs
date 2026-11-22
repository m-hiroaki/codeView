using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;

namespace CodeView.Navigation;

internal readonly record struct CppDefinition(string Name, DeclarationKind Kind, string? Qualifier, TextRange Range,
    bool HasBody);

internal sealed class CppDeclarationScanner
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".h", ".i", ".ii", ".cc", ".cp", ".cpp", ".cxx", ".c++", ".hh", ".hpp", ".hxx", ".h++",
        ".ipp", ".inl", ".tpp", ".ixx", ".cppm"
    };
    private static readonly HashSet<string> NonNames = new(StringComparer.Ordinal)
    {
        "alignas", "auto", "bool", "char", "char8_t", "char16_t", "char32_t", "class", "const", "consteval",
        "constexpr", "constinit", "decltype", "double", "enum", "explicit", "export", "extern", "float", "friend",
        "inline", "int", "long", "mutable", "namespace", "register", "restrict", "short", "signed", "static",
        "struct", "template", "thread_local", "typename", "union", "unsigned", "virtual", "void", "volatile", "wchar_t",
        "_Atomic", "_Bool", "_Complex", "_Thread_local", "_Alignas"
    };
    private readonly List<CppToken> _header = [];
    private readonly Stack<char> _groups = new();
    private readonly Stack<Scope> _scopes = new();
    private readonly CppLexer _lexer;
    private readonly string _name;
    private readonly StickyScopeBuilder? _ranges;
    private readonly Stack<ConditionalBranch>? _branches;
    private readonly HashSet<string>? _macros;
    private readonly HashSet<string>? _structuralMacros;
    private string? _macroName;
    private bool _tooManyRanges;
    private Func<DeclarationMatch, bool> _consume = null!;
    private Func<CppDefinition, bool>? _collect;
    private readonly Stack<(int Depth, bool HasElse)>? _definitionBranches;
    private int _groupTokens;
    private enum ScopeKind { Namespace, Type, Enum, Function, Initializer }
    private readonly record struct Scope(ScopeKind Kind, string? Resume, string? TypeName = null, bool Tracked = false,
        string? Qualifier = null);

    private sealed class ConditionalBranch(int depth, int rangeCount, int enclosing, bool suspended)
    {
        internal int Depth { get; } = depth;
        internal int RangeCount { get; set; } = rangeCount;
        internal int Enclosing { get; } = enclosing;
        internal bool Suspended { get; } = suspended;
        internal bool Invalid { get; set; }
        internal bool HasElse { get; set; }
    }

    internal CppDeclarationScanner(string name) : this(name, null) { }

    private CppDeclarationScanner(string name, StickyScopeBuilder? ranges, bool collect = false)
    {
        _name = name;
        _ranges = ranges;
        if (ranges is not null)
        {
            _branches = new();
            _macros = new(StringComparer.Ordinal);
            _structuralMacros = new(StringComparer.Ordinal);
        }
        if (collect) _definitionBranches = new();
        Func<string, bool>? directive = ranges is not null ? ConditionalDirective : collect ? DefinitionDirective : null;
        _lexer = new(ranges is null ? Push : PushStickyToken,
            () => _scopes.TryPeek(out var scope) && scope.Kind is ScopeKind.Function or ScopeKind.Initializer,
            directive,
            ranges is null ? null : ObserveStructuralWord,
            ranges is null ? null : RememberMacroBrace);
    }

    public static bool IncludesPath(string path) => Extensions.Contains(Path.GetExtension(path));

    internal static CppDeclarationScanner CreateDefinitionCollector() => new("", null, collect: true);

    // A file's candidates are provisional until the reader confirms decoding and completeness.
    internal OperationError? CollectDefinitions(FileReader files, PreparedText source,
        Func<CppDefinition, bool> collect, CancellationToken ct)
    {
        _collect = collect;
        return ReadCore(files, source, ct);
    }

    internal static StickyScopeRanges? ReadScopes(DocumentReadLease source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (source.Length > StickyScopeRanges.MaximumChars) return null;
        var ranges = new StickyScopeBuilder();
        var scanner = new CppDeclarationScanner("", ranges);
        var accepted = true;
        source.VisitChunks(chunk => accepted = scanner._lexer.Push(chunk.Span, ct), ct);
        return accepted && scanner._lexer.Complete(ct) && !scanner._tooManyRanges && scanner._branches!.Count == 0
            && scanner._groups.Count == 0 && scanner._scopes.Count == 0 && scanner._header.Count == 0
            ? ranges.Build() : null;
    }

    internal OperationError? Read(FileReader files, PreparedText source, Func<DeclarationMatch, bool> consume, CancellationToken ct)
    {
        _consume = consume;
        return ReadCore(files, source, ct);
    }

    private OperationError? ReadCore(FileReader files, PreparedText source, CancellationToken ct)
    {
        var unsupported = new OperationError(ErrorCode.UnsupportedDeclaration, source.Path.DisplayPath);
        try
        {
            var read = files.ReadChunks(source, chunk =>
            {
                if (!_lexer.Push(chunk.Span, ct)) throw new StopScan();
            }, ct);
            if (read.Status != OperationStatus.Success)
            {
                ct.ThrowIfCancellationRequested();
                return read.Error ?? unsupported;
            }
            return _lexer.Complete(ct) && _groups.Count == 0 && _scopes.Count == 0 && _header.Count == 0
                && (_definitionBranches is null || _definitionBranches.Count == 0)
                ? null : unsupported;
        }
        catch (StopScan) { return unsupported; }
        finally
        {
            _lexer.Reset();
            _header.Clear();
            _groups.Clear();
            _scopes.Clear();
            _definitionBranches?.Clear();
            _collect = null;
            _consume = null!;
        }
    }

    private sealed class StopScan : Exception;

    private bool DefinitionDirective(string directive)
    {
        var branches = _definitionBranches!;
        // Declarators that cross conditional boundaries cannot be collected reliably.
        if (_groups.Count != 0 || _header.Count != 0) return false;
        if (directive is "if" or "ifdef" or "ifndef")
        {
            if (branches.Count >= CSharpLexer.MaximumDepth) return false;
            branches.Push((_scopes.Count, false));
            return true;
        }
        if (!branches.TryPop(out var branch) || _scopes.Count != branch.Depth || branch.HasElse && directive != "endif")
            return false;
        if (directive != "endif") branches.Push((branch.Depth, directive == "else"));
        return true;
    }

    private bool PushStickyToken(CppToken token)
    {
        if (token.Macro)
        {
            _macroName = token.Text;
            if (_macros!.Count == StickyScopeRanges.MaximumRanges) return _macros.Contains(token.Text);
            _macros.Add(token.Text);
            return true;
        }
        if (!_branches!.TryPeek(out var branch)) return Push(token);
        if (branch.Suspended || branch.Invalid) return true;
        // Branches may add temporary scopes but may not close a scope opened before the directive.
        if ((token.Text != "}" || _groups.Count > 0 || _scopes.Count > branch.Depth) && Push(token)) return true;
        branch.Invalid = true;
        return !_tooManyRanges;
    }

    private bool ConditionalDirective(string directive)
    {
        var branches = _branches!;
        if (directive is "if" or "ifdef" or "ifndef")
        {
            var suspended = branches.TryPeek(out var outer) && (outer.Suspended || outer.Invalid);
            if (branches.Count >= CSharpLexer.MaximumDepth || !suspended && (_header.Count != 0 || _groups.Count != 0))
                return false;
            branches.Push(new(_scopes.Count, _ranges!.Count, _ranges.Current, suspended));
            return true;
        }
        if (!branches.TryPeek(out var branch) || directive != "endif" && branch.HasElse) return false;
        if (!branch.Suspended)
        {
            if (branch.Invalid || _scopes.Count != branch.Depth || _groups.Count != 0 || _header.Count != 0)
            {
                while (_scopes.Count > branch.Depth) _scopes.Pop();
                _groups.Clear();
                _header.Clear();
                _ranges!.DiscardBranch(branch.RangeCount, branch.Enclosing);
            }
            branch.RangeCount = _ranges!.Count;
            branch.Invalid = false;
        }
        if (directive == "endif") branches.Pop();
        else branch.HasElse = directive == "else";
        return true;
    }

    private void RememberMacroBrace()
    {
        if (_macroName is { } name) _structuralMacros!.Add(name);
    }

    private bool ObserveStructuralWord(ReadOnlySpan<char> word)
    {
        if (_structuralMacros!.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word))
            _ranges!.InvalidateCurrent();
        return true;
    }

    private bool Push(CppToken token)
    {
        if (token.Macro) return Emit(token, DeclarationKind.Macro);
        var scope = _scopes.TryPeek(out var parent) ? parent.Kind : ScopeKind.Namespace;
        if (_groups.Count > 0)
        {
            if (_groupTokens < 2 && _groups.Count == 1 && _groups.Peek() == ')'
                && scope is not (ScopeKind.Function or ScopeKind.Initializer))
            {
                // Retain the small portion of a parenthesized pointer declarator that can name a symbol.
                var position = _groupTokens++;
                if (position == 0 && token.Text is "*" or "&" or "&&" or "<number>" or "<literal>"
                    || position == 1 && IsPointer(_header.Count - 1) && token.Word)
                    return Append(token);
            }
            if (_groups.Count == 1 && token.Text.Length == 1 && token.Text[0] == _groups.Peek())
            {
                if (scope == ScopeKind.Initializer)
                {
                    _scopes.Pop();
                    _groups.Pop();
                    return Append(token with { Text = "<initializer>" });
                }
                if (scope != ScopeKind.Function && !Append(token)) return false;
            }
            return TrackGroup(token.Text);
        }
        switch (token.Text)
        {
            case "}":
                if (_definitionBranches is not null && _definitionBranches.TryPeek(out var branch)
                    && _scopes.Count <= branch.Depth) return false;
                if (!_scopes.TryPop(out var closed)) return false;
                if (closed.Kind == ScopeKind.Enum && !FinishEnumerator()) return false;
                if (closed.Tracked) _ranges!.End(token.Range.Start.Line);
                _header.Clear();
                return closed.Resume is not { } resume || Append(token with { Text = resume });
            case "{":
                return StartBody(scope);
            case "," when scope == ScopeKind.Enum:
                if (!FinishEnumerator()) return false;
                _header.Clear();
                return true;
            case ";":
                if (!FinishStatement(scope)) return false;
                _header.Clear();
                return true;
            case ":" when scope == ScopeKind.Type && _header.Count == 1
                && _header[0].Text is "public" or "protected" or "private":
                _header.Clear();
                return true;
            default:
                return (scope == ScopeKind.Function || Append(token)) && TrackGroup(token.Text);
        }
    }

    private bool StartBody(ScopeKind parent)
    {
        if (_scopes.Count >= CSharpLexer.MaximumDepth) return false;
        if (parent == ScopeKind.Function)
        {
            _scopes.Push(new(ScopeKind.Function, null));
            return true;
        }

        var start = DeclarationStart();
        var function = FunctionName(start, out var tail);
        if (function >= 0)
        {
            // In a constructor initializer list, a brace after a member name starts its initializer.
            var constructorList = tail < _header.Count && _header[tail].Text == ":";
            if (constructorList && (_header[^1].Word || _header[^1].Text == ">"))
            {
                _scopes.Push(new(ScopeKind.Initializer, null));
                return TrackGroup("{");
            }
            if (!Emit(function, DeclarationKind.Function, hasBody: true)) return false;
            if (!StartStickyRange(DeclarationKind.Function, function, out var tracked)) return false;
            _header.Clear();
            _scopes.Push(new(ScopeKind.Function, null, Tracked: tracked));
            return true;
        }
        if (At(start, "namespace") || At(start, "inline") && At(start + 1, "namespace")
            || At(start, "extern") && At(start + 1, "<literal>") || _header.Count == 1 && At(0, "export"))
        {
            var qualifier = _collect is null ? null : NamespaceQualifier(At(start, "inline") ? start + 2 : start + 1);
            _header.Clear();
            _scopes.Push(new(ScopeKind.Namespace, null, Qualifier: qualifier));
            return true;
        }
        var tag = Tag(start, out var tagName);
        if (tag >= 0)
        {
            if (tagName >= 0 && !Emit(tagName, DeclarationKind.Type)) return false;
            var placeholder = At(start, "typedef") ? "<typedef>" : "<type>";
            var kind = At(tag, "enum") ? ScopeKind.Enum : ScopeKind.Type;
            var typeName = tagName >= 0 ? _header[tagName].Text : null;
            var qualifier = _collect is null ? null : typeName is null ? "" : JoinQualifier(CurrentQualifier(), typeName);
            if (!StartStickyRange(DeclarationKind.Type, tagName, out var tracked)) return false;
            _header.Clear();
            _scopes.Push(new(kind, placeholder, typeName, tracked, qualifier));
            return true;
        }
        // Keep the declarator until ';', but skip arbitrary nested initializer contents.
        _scopes.Push(new(ScopeKind.Initializer, null));
        return TrackGroup("{");
    }

    private bool StartStickyRange(DeclarationKind kind, int name, out bool tracked)
    {
        tracked = false;
        if (_ranges is null || name < 0) return true;
        tracked = _ranges.Begin(kind, _header[name].Text, _header[0].Range.Start.Line, _header[^1].Range.Start.Line);
        _tooManyRanges = !tracked;
        if (tracked && (_macros!.Contains(_header[name].Text) || _header.Any(token => _structuralMacros!.Contains(token.Text))))
            _ranges.InvalidateCurrent();
        return tracked;
    }

    private bool FinishStatement(ScopeKind scope)
    {
        if (_header.Count == 0 || scope is ScopeKind.Function or ScopeKind.Enum) return true;
        var start = DeclarationStart();
        if (At(start, "using"))
            return !At(start + 2, "=") || !IsName(start + 1) || Emit(start + 1, DeclarationKind.Type);

        var function = FunctionName(start, out var tail);
        if (function >= 0)
        {
            if (At(start, "typedef")) return Emit(function, DeclarationKind.Type);
            return !(At(tail, "=") && (At(tail + 1, "default") || At(tail + 1, "delete")))
                || Emit(function, DeclarationKind.Function);
        }

        if (At(start, "extern") && !_header.Exists(token => token.Text is "=" or "<initializer>")) return true;
        if (At(start, "namespace") || At(start, "return") || At(start, "static_assert")
            || At(start, "module") || At(start, "import") || At(start, "concept")) return true;
        var isAlias = At(start, "typedef") || At(start, "<typedef>");
        var kind = isAlias ? DeclarationKind.Type : scope == ScopeKind.Type ? DeclarationKind.Field : DeclarationKind.Variable;
        var first = start;
        for (var index = start; index < _header.Count;)
        {
            var end = index;
            var declarator = -1;
            // Groups were reduced to their delimiters. Stop at the first initializer in this declarator.
            while (end < _header.Count && !At(end, ","))
            {
                if (At(end, "<") && SkipAngles(end) is var after && after > end)
                { end = after; continue; }
                if (At(end, "=") || At(end, "<initializer>") || At(end, ":") || At(end, "[") && !At(end + 1, "]"))
                    break;
                if (At(end, "("))
                {
                    if (At(end + 1, ")") && (At(end - 1, "decltype") || At(end - 1, "_Atomic") || At(end - 1, "alignas")))
                    { end += 2; continue; }
                    if (IsName(end + 2) && IsPointer(end + 1) && At(end + 3, ")"))
                        declarator = end + 2;
                    break;
                }
                if (IsName(end)) declarator = end;
                end++;
            }
            var tagOnly = At(first, "struct") || At(first, "class") || At(first, "union") || At(first, "enum");
            var minimum = index == first ? first + (tagOnly || At(first, "typedef") ? 2 : 1) : index;
            if (declarator >= minimum && !Emit(declarator, kind)) return false;
            while (end < _header.Count && !At(end, ","))
            {
                var after = At(end, "<") ? SkipAngles(end) : end;
                end = after > end ? after : end + 1;
            }
            index = end + 1;
        }
        return true;
    }

    private int FunctionName(int start, out int tail)
    {
        tail = _header.Count;
        for (var index = start; index < _header.Count; index++)
        {
            if (At(index, "=") || At(index, ",") || At(index, "<initializer>")) break;
            if (At(index, "<") && SkipAngles(index) is var after && after > index)
            { index = after - 1; continue; }
            if (!At(index, "(") || !At(index + 1, ")")) continue;
            var candidate = index - 1;
            if (!IsName(candidate) || At(candidate, "static_assert")) continue;
            var prefix = At(candidate - 1, "~") ? candidate - 1 : candidate;
            if (prefix <= start && (!_scopes.TryPeek(out var scope) || scope.TypeName != _header[candidate].Text))
                continue;
            tail = index + 2;
            if (At(tail, ",") || At(tail, "<initializer>")) return -1;
            return candidate;
        }
        return -1;
    }

    private int Tag(int start, out int nameIndex)
    {
        nameIndex = -1;
        if (At(start, "typedef") || At(start, "friend")) start++;
        if (!At(start, "struct") && !At(start, "class") && !At(start, "union") && !At(start, "enum")) return -1;
        var index = start + 1;
        if (At(start, "enum") && (At(index, "class") || At(index, "struct"))) index++;
        while (At(index, "[") && At(index + 1, "]")) index += 2;
        if (IsName(index)) nameIndex = index++;
        if (At(index, "<")) index = SkipAngles(index);
        if (At(index, "final")) index++;
        return index == _header.Count || At(index, ":") ? start : -1;
    }

    private int DeclarationStart()
    {
        var index = 0;
        while (index < _header.Count)
        {
            if (At(index, "export")) index++;
            else if (At(index, "[") && At(index + 1, "]")) index += 2;
            else if (At(index, "template") && At(index + 1, "<") && SkipAngles(index + 1) is var after && after > index + 1)
                index = after;
            else break;
        }
        return index;
    }

    private int SkipAngles(int start)
    {
        var depth = 0;
        for (var index = start; index < _header.Count; index++)
        {
            if (At(index, "<")) depth++;
            else if (At(index, ">") && --depth == 0) return index + 1;
        }
        return start;
    }

    private bool FinishEnumerator() => _header.Count == 0 || !_header[0].Word || Emit(0, DeclarationKind.Enumerator);
    private bool At(int index, string text) => (uint)index < _header.Count && _header[index].Text == text;
    private bool IsPointer(int index) => (uint)index < _header.Count && _header[index].Text is "*" or "&" or "&&";
    private bool IsName(int index) => (uint)index < _header.Count && _header[index].Word && !NonNames.Contains(_header[index].Text);
    private bool Emit(int index, DeclarationKind kind, bool hasBody = false)
    {
        var token = _header[index];
        var qualifier = _collect is null ? null : DeclarationQualifier(index);
        return Emit(token, kind, qualifier is { Length: > 0 } ? qualifier : null, hasBody);
    }

    private bool Emit(CppToken token, DeclarationKind kind, string? qualifier = null, bool hasBody = false) =>
        _collect is not null ? token.Range.Length != token.Text.Length
            || _collect(new(token.Text, kind, qualifier, token.Range, hasBody))
        : !string.Equals(_name, token.Text, StringComparison.Ordinal)
            || token.Range.Length == _name.Length && _consume(new(kind, token.Range));

    private string? CurrentQualifier() => _scopes.TryPeek(out var scope) ? scope.Qualifier : null;

    private string? NamespaceQualifier(int start)
    {
        var qualifier = CurrentQualifier();
        for (var index = start; IsName(index); index += 2)
        {
            qualifier = JoinQualifier(qualifier, _header[index].Text);
            if (qualifier.Length == 0 || !At(index + 1, "::")) break;
        }
        return qualifier;
    }

    private string? DeclarationQualifier(int index)
    {
        var separator = (At(index - 1, "~") ? index - 1 : index) - 1;
        if (!At(separator, "::")) return CurrentQualifier();
        var components = new Stack<string>();
        var absolute = false;
        while (At(separator, "::"))
        {
            var part = separator - 1;
            if (At(part, ">"))
            {
                var depth = 1;
                while (--part >= 0 && depth > 0)
                {
                    if (At(part, ">")) depth++;
                    else if (At(part, "<")) depth--;
                    if (depth == 0) { part--; break; }
                }
            }
            if (!IsName(part))
            {
                absolute = true;
                break;
            }
            components.Push(_header[part].Text);
            separator = part - 1;
        }
        var qualifier = absolute ? null : CurrentQualifier();
        foreach (var component in components)
        {
            qualifier = JoinQualifier(qualifier, component);
            if (qualifier.Length == 0) break;
        }
        return qualifier;
    }

    private static string JoinQualifier(string? prefix, string name) =>
        prefix == "" || (prefix?.Length + 2 ?? 0) + name.Length > IdentifierExtractor.MaximumChars
            ? "" : prefix is null ? name : prefix + "::" + name;

    private bool Append(CppToken token)
    {
        if (_header.Count == CSharpDeclarations.MaximumTokens || _header.Count > 0
            && token.Range.Offset + token.Range.Length - _header[0].Range.Offset > CSharpDeclarations.MaximumChars) return false;
        _header.Add(token);
        return true;
    }

    private bool TrackGroup(string text)
    {
        if (text.Length != 1) return true;
        var closing = text[0] switch { '(' => ')', '[' => ']', '{' => '}', _ => '\0' };
        if (closing != '\0')
        {
            if (_groups.Count + _scopes.Count >= CSharpLexer.MaximumDepth) return false;
            if (_groups.Count == 0) _groupTokens = 0;
            _groups.Push(closing);
            return true;
        }
        return text[0] is not (')' or ']' or '}') || _groups.TryPop(out var expected) && expected == text[0];
    }
}
