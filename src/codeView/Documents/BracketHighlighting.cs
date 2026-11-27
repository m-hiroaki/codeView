namespace CodeView.Documents;

// Built during the existing bounded TextMate pass; no second document read.
internal sealed class BracketHighlighting
{
    internal const int MaximumPairs = 32_768;
    internal const int MaximumDepth = 256;
    internal readonly record struct Pair(int Open, int Close, int Depth, int OpenLine, int CloseLine, int Indent);
    private readonly List<(char Symbol, int Offset, int Line, int Indent)> _stack = [];
    private readonly List<Pair> _pairs = [];
    private Pair[] _ordered = [];
    private int[] _maximumEnd = [];
    private bool _disabled;
    private int _lineOffset = -1;
    private int _lineIndent;
    internal IReadOnlyList<Pair> Pairs => _ordered;

    internal void ReadToken(string text, int start, int end, int lineOffset, int lineNumber)
    {
        if (_disabled) return;
        if (_lineOffset != lineOffset)
        {
            _lineOffset = lineOffset;
            _lineIndent = 0;
            for (var j = 0; j < text.Length && text[j] is ' ' or '\t'; j++)
                _lineIndent += text[j] == '\t' ? 4 - _lineIndent % 4 : 1;
        }
        for (var i = start; i < end; i++)
        {
            var ch = text[i];
            if (ch is '(' or '[' or '{')
            {
                if (_stack.Count == MaximumDepth) { Disable(); return; }
                _stack.Add((ch, lineOffset + i, lineNumber, _lineIndent));
            }
            else if (ch is ')' or ']' or '}')
            {
                if (_stack.Count == 0) continue;
                var open = _stack[^1];
                if ((open.Symbol, ch) is not (('(', ')') or ('[', ']') or ('{', '}')))
                {
                    // Do not invent crossing pairs in malformed source.
                    _stack.Clear();
                    continue;
                }
                _stack.RemoveAt(_stack.Count - 1);
                if (_pairs.Count == MaximumPairs) { Disable(); return; }
                _pairs.Add(new(open.Offset, lineOffset + i, _stack.Count, open.Line, lineNumber, open.Indent));
            }
        }
    }

    private void Disable()
    {
        _disabled = true;
        _stack.Clear();
        _pairs.Clear();
    }

    internal void Complete()
    {
        _ordered = _pairs.OrderBy(pair => pair.Open).ToArray();
        _pairs.Clear();
        _stack.Clear();
        _pairs.TrimExcess();
        _maximumEnd = new int[_ordered.Length * 4];
        if (_ordered.Length > 0) Build(1, 0, _ordered.Length);
    }

    private int Build(int node, int start, int end)
    {
        if (end - start == 1) return _maximumEnd[node] = _ordered[start].Close;
        var mid = (start + end) / 2;
        return _maximumEnd[node] = Math.Max(Build(node * 2, start, mid), Build(node * 2 + 1, mid, end));
    }

    // Interval index prunes off-screen siblings, including when a large outer pair encloses them.
    internal void Visit(int first, int last, Action<Pair> visit)
    {
        if (_ordered.Length > 0) Query(1, 0, _ordered.Length);
        void Query(int node, int start, int end)
        {
            if (_ordered[start].Open > last || _maximumEnd[node] < first) return;
            if (end - start == 1) { visit(_ordered[start]); return; }
            var mid = (start + end) / 2;
            Query(node * 2, start, mid);
            Query(node * 2 + 1, mid, end);
        }
    }
}
