using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using CodeView.Documents;
using System.Globalization;

namespace CodeView.Presentation;

/// <summary>Uses the existing row editor for viewport geometry and input ownership.</summary>
internal sealed class ContextualRowLayer(TextEditor editor) : Control
{
    private readonly Dictionary<int, ContextualRowControl> _rows = [];
    private TextDocument? _display;

    internal long DrawnRows { get; private set; }
    internal long ResolvedHits { get; private set; }

    internal bool TryGetHorizontalOffset(double delta, DocumentDisplaySession source, out long column)
    {
        column = 0;
        return _display is not null && ReferenceEquals(editor.Document, _display) &&
            _rows.TryGetValue(0, out var first) && first.TryGetHorizontalOffset(delta, source, out column);
    }

    internal bool TryGetCaretRectangle(int displayOffset, long sourceOffset, out Rect? rectangle)
    {
        rectangle = null;
        if (_display is null || !ReferenceEquals(editor.Document, _display)) return false;
        var line = _display.GetLineByOffset(displayOffset);
        if (!_rows.TryGetValue(line.LineNumber - 1, out var control)) return false;
        rectangle = control.GetCaretRectangle(sourceOffset);
        return true;
    }

    internal bool RowHasForeground(int row, Color color) =>
        _rows.TryGetValue(row, out var control) && control.HasForeground(color);

    internal void Apply(TextDocument display, DocumentDisplaySession source, DocumentRowProjection projection,
        SyntaxHighlighting? highlighting)
    {
        Clear();
        _display = display;
        var view = editor.TextArea.TextView;
        var budget = DocumentDisplaySession.MaximumSliceChars;
        var spanIndex = 0;
        if (highlighting is not null)
        {
            var high = highlighting.Spans.Count;
            var low = 0;
            var start = projection.Rows[0].SourceOffset - projection.Rows[0].SourceColumn + 1;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (highlighting.Spans[middle].Offset < start) low = middle + 1;
                else high = middle;
            }
            spanIndex = low;
        }
        for (var index = 0; index < projection.Rows.Length; index++)
        {
            if (projection.GetContext(index) is not { VisibleLength: > 0 } range || range.Length > budget
                || source.ReadRowContext(projection, index) is not { } text) continue;
            var control = new ContextualRowControl { FlowDirection = editor.FlowDirection };
            List<(int Start, int End, IBrush Brush)>? colors = null;
            if (highlighting is not null)
            {
                int[]? boundaries = null;
                while (spanIndex < highlighting.Spans.Count && highlighting.Spans[spanIndex].Offset < range.SourceOffset + range.Length)
                {
                    var span = highlighting.Spans[spanIndex++];
                    if (span.Offset < range.SourceOffset || span.Offset + span.Length > range.SourceOffset + range.Length ||
                        !highlighting.Palette.TryGetValue(span.Foreground, out var value) || !Color.TryParse(value, out var color))
                        continue;
                    var start = (int)(span.Offset - range.SourceOffset);
                    var end = start + span.Length;
                    boundaries ??= StringInfo.ParseCombiningCharacters(text);
                    if (Array.BinarySearch(boundaries, start) < 0 ||
                        (end != text.Length && Array.BinarySearch(boundaries, end) < 0)) continue;
                    (colors ??= []).Add((start, end, new SolidColorBrush(color)));
                }
            }
            control.SetRow(text, range, new Typeface(editor.FontFamily), editor.FontSize, editor.Foreground,
                editor.Options.IndentationSize * view.WideSpaceWidth, colors);
            _rows.Add(index, control);
            budget -= range.Length;
        }
        InvalidateVisual();
    }

    internal void SetSelection(long anchor, long caret)
    {
        foreach (var row in _rows.Values) row.SetSelection(anchor, caret);
        InvalidateVisual();
    }

    internal bool TryHit(int row, Point point, DocumentDisplaySession source, out long? offset)
    {
        offset = null;
        if (!ReferenceEquals(editor.Document, _display) || !_rows.TryGetValue(row, out var control)) return false;
        if (control.TryHitSource(point, out var hit, clamp: true) &&
            source.TryGetGraphemeBounds(hit, out var before, out var after))
        {
            offset = control.NearestBoundary(point, before, after);
            ResolvedHits++;
        }
        return true;
    }

    public override void Render(DrawingContext context)
    {
        var view = editor.TextArea.TextView;
        if (!view.VisualLinesValid || !ReferenceEquals(view.Document, _display)) return;
        using var viewport = context.PushClip(new Rect(view.Bounds.Size));
        foreach (var line in view.VisualLines)
        {
            if (!_rows.TryGetValue(line.FirstDocumentLine.LineNumber - 1, out var row)) continue;
            var y = line.VisualTop - view.VerticalOffset;
            var rect = new Rect(0, y, view.Bounds.Width, line.Height);
            if (editor.Background is { } background) context.FillRectangle(background, rect);
            using var clip = context.PushClip(rect);
            using var transform = context.PushTransform(Matrix.CreateTranslation(0, y));
            row.Render(context);
            DrawnRows++;
        }
    }

    internal void Clear()
    {
        foreach (var row in _rows.Values) row.Dispose();
        _rows.Clear();
        _display = null;
        InvalidateVisual();
    }
}
