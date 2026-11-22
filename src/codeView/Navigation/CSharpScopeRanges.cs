using CodeView.Documents;

namespace CodeView.Navigation;

internal readonly record struct CSharpScopeRange(DeclarationKind Kind, string Name,
    long StartLine, long HeaderEndLine, long EndLine, int Parent);

internal sealed class CSharpScopeRanges
{
    internal const int MaximumChars = 2 * 1024 * 1024;
    internal const int MaximumRanges = 8192;
    private readonly CSharpScopeRange[] _ranges;

    internal CSharpScopeRanges(CSharpScopeRange[] ranges) => _ranges = ranges;
    internal IReadOnlyList<CSharpScopeRange> Ranges => _ranges;

    internal static CSharpScopeRanges? Read(DocumentReadLease source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (source.Length > MaximumChars) return null;
        var ranges = new CSharpScopeBuilder();
        var declarations = new CSharpDeclarations("", static _ => true, ranges);
        var lexer = new CSharpLexer(rejectConditionalDirectives: true);
        Func<CSharpToken, bool> push = declarations.Push;
        if (source.VisitChunks(chunk => lexer.Push(chunk.Span, push, ct), ct) != source.Length
            || lexer.IsFinished || !lexer.Complete(push, ct)) return null;
        declarations.Complete();
        return declarations.Error == DeclarationScanError.None ? ranges.Build() : null;
    }

    // The declarations are added in source order, and nested ranges close before their parents.
    internal int Find(long line)
    {
        var left = 0;
        var right = _ranges.Length;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (_ranges[middle].HeaderEndLine < line) left = middle + 1;
            else right = middle;
        }
        for (var index = left - 1; index >= 0; index = _ranges[index].Parent)
            if (line <= _ranges[index].EndLine) return index;
        return -1;
    }

    internal string Format(int index)
    {
        var names = new Stack<string>();
        string? method = null;
        for (; index >= 0; index = _ranges[index].Parent)
        {
            var range = _ranges[index];
            if (range.Kind == DeclarationKind.Method) method = range.Name;
            else names.Push(range.Name);
        }
        var types = string.Join(".", names);
        return method is null ? types : types + " › " + method;
    }
}

internal sealed class CSharpScopeBuilder
{
    private readonly List<CSharpScopeRange> _ranges = [];
    private int _current = -1;
    internal string? CurrentTypeName => _current >= 0 && _ranges[_current].Kind == DeclarationKind.Type
        ? _ranges[_current].Name : null;

    internal bool Begin(DeclarationKind kind, CSharpToken name, CSharpToken start, CSharpToken headerEnd)
    {
        if (_ranges.Count == CSharpScopeRanges.MaximumRanges) return false;
        _ranges.Add(new(kind, name.Text, start.Range.Start.Line, headerEnd.Range.Start.Line, 0, _current));
        _current = _ranges.Count - 1;
        return true;
    }

    internal void End(CSharpToken end)
    {
        var scope = _ranges[_current];
        _ranges[_current] = scope with { EndLine = end.Range.Start.Line };
        _current = scope.Parent;
    }

    internal CSharpScopeRanges Build() => new(_ranges.ToArray());
}
