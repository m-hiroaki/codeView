using AvaloniaEdit.Document;

namespace CodeView.Documents;

/// <summary>Experimental immutable, bounded rows; synthetic LF separators are not source characters.</summary>
internal sealed class DocumentRowProjection
{
    private readonly DocumentProjectionRow[] _rows;
    public string Text { get; }
    public int EffectiveColumns { get; }
    public ReadOnlySpan<DocumentProjectionRow> Rows => _rows;

    private DocumentRowProjection(string text, DocumentProjectionRow[] rows, int columns) =>
        (Text, _rows, EffectiveColumns) = (text, rows, columns);

    internal static DocumentRowProjection Create(TextDocument source, DocumentReadLease lease, long firstLine,
        long horizontalOffset, int visibleLines, int columns, int maxChars, DocumentGraphemeIndex? graphemes = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstLine, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstLine, source.LineCount);
        ArgumentOutOfRangeException.ThrowIfNegative(horizontalOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(horizontalOffset, lease.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(visibleLines, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(visibleLines, 512);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, DocumentDisplaySession.MaximumSliceChars);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxChars, DocumentDisplaySession.MaximumSliceChars);
        var count = (int)Math.Min(visibleLines, source.LineCount - firstLine + 1);
        var effective = Math.Min(columns, (maxChars - count + 1) / count);
        if (effective < 2) throw new ArgumentException("表示行数と文字予算ではスカラーを表示できません。", nameof(maxChars));
        var rows = new DocumentProjectionRow[count];
        var total = 0;
        for (var index = 0; index < count; index++)
        {
            var line = source.GetLineByNumber(checked((int)firstLine + index));
            var start = line.Offset + (int)Math.Min(horizontalOffset, line.Length);
            var end = start;
            var unavailable = false;
            if (graphemes is null)
            {
                if (SplitsScalar(source, start, line.Offset, line.EndOffset)) start--;
                end = (int)Math.Min(line.EndOffset, (long)start + effective);
                if (SplitsScalar(source, end, line.Offset, line.EndOffset)) end--;
            }
            else if (start < line.EndOffset)
            {
                if (graphemes.TryGetBounds(lease, start, out var before, out _))
                {
                    start = checked((int)before);
                    var limit = Math.Min(line.EndOffset, (long)start + effective);
                    if (graphemes.TryGetBounds(lease, limit, out var boundary, out _))
                        end = checked((int)boundary);
                    else end = start;
                    unavailable = end == start;
                }
                else unavailable = true;
            }
            rows[index] = new(line.LineNumber, start, end - start, start - line.Offset + 1,
                total, start > line.Offset, end < line.EndOffset, horizontalOffset > line.Length, unavailable);
            total = checked(total + end - start + (index + 1 < count ? 1 : 0));
        }
        var text = string.Create(total, (Rows: rows, Lease: lease), static (target, state) =>
        {
            for (var i = 0; i < state.Rows.Length; i++)
            {
                var row = state.Rows[i];
                state.Lease.CopyRangeInto(row.SourceOffset, target.Slice(row.DisplayOffset, row.Length));
                if (i + 1 < state.Rows.Length) target[row.DisplayOffset + row.Length] = '\n';
            }
        });
        return new(text, rows, effective);
    }

    public bool TrySourcePosition(int displayOffset, out long sourceOffset)
    {
        sourceOffset = 0;
        if (displayOffset < 0 || displayOffset > Text.Length || SplitsDisplayedScalar(displayOffset)) return false;
        var index = FindDisplayRow(displayOffset);
        var row = _rows[index];
        if (row.IsOutsideHorizontalRange || row.IsGraphemeRangeUnavailable) return false;
        var relative = displayOffset - row.DisplayOffset;
        if (relative > row.Length) return false;
        sourceOffset = row.SourceOffset + relative;
        return true;
    }

    public bool TrySourceCharacter(int displayOffset, out long sourceOffset)
    {
        sourceOffset = 0;
        if (displayOffset < 0 || displayOffset >= Text.Length) return false;
        var row = _rows[FindDisplayRow(displayOffset)];
        var relative = displayOffset - row.DisplayOffset;
        if (relative >= row.Length) return false; // Synthetic separator or empty row.
        sourceOffset = row.SourceOffset + relative;
        return true;
    }

    public bool TryDisplayPosition(long sourceOffset, out int displayOffset)
    {
        displayOffset = 0;
        var low = 0;
        var high = _rows.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_rows[middle].SourceOffset <= sourceOffset) low = middle + 1;
            else high = middle;
        }
        if (low == 0) return false;
        var row = _rows[low - 1];
        if (row.IsOutsideHorizontalRange || row.IsGraphemeRangeUnavailable) return false;
        var relative = sourceOffset - row.SourceOffset;
        if (relative > row.Length) return false;
        displayOffset = row.DisplayOffset + (int)relative;
        if (SplitsDisplayedScalar(displayOffset)) { displayOffset = 0; return false; }
        return true;
    }

    public DocumentRowSelection ProjectSelection(long anchor, long caret)
    {
        var first = Math.Min(anchor, caret);
        var last = Math.Max(anchor, caret);
        var spans = new List<DocumentProjectionSelection>();
        foreach (var row in _rows)
        {
            var start = Math.Max(first, row.SourceOffset);
            var end = Math.Min(last, row.SourceOffset + row.Length);
            if (end > start) spans.Add(new(row.DisplayOffset + (int)(start - row.SourceOffset), (int)(end - start)));
        }
        return new(spans.ToArray(), TryDisplayPosition(caret, out var position) ? position : null, anchor > caret);
    }

    private int FindDisplayRow(int offset)
    {
        var low = 0;
        var high = _rows.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_rows[middle].DisplayOffset <= offset) low = middle + 1;
            else high = middle;
        }
        return low - 1;
    }

    private static bool SplitsScalar(TextDocument source, int offset, int start, int end) =>
        offset > start && offset < end && char.IsHighSurrogate(source.GetCharAt(offset - 1)) && char.IsLowSurrogate(source.GetCharAt(offset));

    private bool SplitsDisplayedScalar(int offset) => offset > 0 && offset < Text.Length
        && char.IsHighSurrogate(Text[offset - 1]) && char.IsLowSurrogate(Text[offset]);
}

internal readonly record struct DocumentProjectionRow(long SourceLine, long SourceOffset, int Length, long SourceColumn,
    int DisplayOffset, bool LeftClipped, bool RightClipped, bool IsOutsideHorizontalRange, bool IsGraphemeRangeUnavailable = false);
internal readonly record struct DocumentProjectionSelection(int Offset, int Length);
internal sealed class DocumentRowSelection(DocumentProjectionSelection[] spans, int? caretOffset, bool reversed)
{
    public ReadOnlySpan<DocumentProjectionSelection> Spans => spans;
    public int? CaretOffset { get; } = caretOffset;
    public bool IsReversed { get; } = reversed;
}
