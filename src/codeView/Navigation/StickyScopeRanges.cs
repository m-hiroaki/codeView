using CodeView.Documents;

namespace CodeView.Navigation;

internal readonly record struct StickyScopeRange(DeclarationKind Kind, string Name,
    long StartLine, long HeaderEndLine, long EndLine, int Parent);

internal sealed class StickyScopeRanges
{
    internal const int MaximumChars = 2 * 1024 * 1024;
    internal const int MaximumRanges = 8192;
    private readonly StickyScopeRange[] _ranges;

    internal StickyScopeRanges(StickyScopeRange[] ranges) => _ranges = ranges;
    internal IReadOnlyList<StickyScopeRange> Ranges => _ranges;

    internal static StickyScopeRanges? ReadCSharp(DocumentReadLease source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (source.Length > MaximumChars) return null;
        var ranges = new StickyScopeBuilder();
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

}

internal sealed class StickyScopeBuilder
{
    private readonly List<StickyScopeRange> _ranges = [];
    private int _current = -1;
    private bool _hasUncertainScopes;
    internal int Count => _ranges.Count;
    internal int Current => _current;
    internal string? CurrentTypeName => _current >= 0 && _ranges[_current].Kind == DeclarationKind.Type
        ? _ranges[_current].Name : null;

    internal bool Begin(DeclarationKind kind, CSharpToken name, CSharpToken start, CSharpToken headerEnd)
        => Begin(kind, name.Text, start.Range.Start.Line, headerEnd.Range.Start.Line);

    internal bool Begin(DeclarationKind kind, string name, long start, long headerEnd)
    {
        if (_ranges.Count == StickyScopeRanges.MaximumRanges) return false;
        _ranges.Add(new(kind, name, start, headerEnd, 0, _current));
        _current = _ranges.Count - 1;
        return true;
    }

    internal void End(CSharpToken end)
        => End(end.Range.Start.Line);

    internal void End(long line)
    {
        var scope = _ranges[_current];
        if (scope.EndLine >= 0) _ranges[_current] = scope with { EndLine = line };
        _current = scope.Parent;
    }

    internal void DiscardBranch(int count, int enclosing)
    {
        _ranges.RemoveRange(count, _ranges.Count - count);
        _current = enclosing;
        InvalidateCurrent();
    }

    internal void InvalidateCurrent()
    {
        if (_current < 0) return;
        _ranges[_current] = _ranges[_current] with { EndLine = -1 };
        _hasUncertainScopes = true;
    }

    internal StickyScopeRanges Build()
    {
        if (!_hasUncertainScopes) return new(_ranges.ToArray());
        var valid = new List<StickyScopeRange>(_ranges.Count);
        var remap = new int[_ranges.Count];
        for (var i = 0; i < _ranges.Count; i++)
        {
            var range = _ranges[i];
            var parent = range.Parent < 0 ? -1 : remap[range.Parent];
            if (range.EndLine < 0 || parent < -1)
            {
                remap[i] = -2;
                continue;
            }
            remap[i] = valid.Count;
            valid.Add(range with { Parent = parent });
        }
        return new(valid.ToArray());
    }
}
