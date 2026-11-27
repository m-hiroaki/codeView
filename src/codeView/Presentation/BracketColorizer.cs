using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CodeView.Documents;

namespace CodeView.Presentation;

internal sealed class BracketColorizer(TextDocument document, BracketHighlighting brackets)
    : DocumentColorizingTransformer, IBackgroundRenderer
{
    internal static readonly ImmutableSolidColorBrush[] Colors =
    [
        new ImmutableSolidColorBrush(Color.Parse("#FFD700")),
        new ImmutableSolidColorBrush(Color.Parse("#DA70D6")),
        new ImmutableSolidColorBrush(Color.Parse("#87CEFA"))
    ];
    private static readonly IPen[] Pens = Colors.Select(brush => (IPen)new ImmutablePen(brush, 0.5)).ToArray();
    public KnownLayer Layer => KnownLayer.Background;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!ReferenceEquals(CurrentContext.Document, document)) return;
        brackets.Visit(line.Offset, line.EndOffset, pair =>
        {
            ColorBracket(pair.Open, pair.Depth);
            ColorBracket(pair.Close, pair.Depth);
        });
        void ColorBracket(int offset, int depth)
        {
            if (offset >= line.Offset && offset < line.EndOffset)
                ChangeLinePart(offset, offset + 1, element => element.TextRunProperties.SetForegroundBrush(Colors[depth % Colors.Length]));
        }
    }

    public void Draw(TextView view, DrawingContext context)
    {
        if (!ReferenceEquals(view.Document, document) || !view.VisualLinesValid || view.VisualLines.Count == 0) return;
        var first = view.VisualLines[0].FirstDocumentLine;
        var last = view.VisualLines[^1].LastDocumentLine;
        using var clip = context.PushClip(new Rect(view.Bounds.Size));
        brackets.Visit(first.Offset, last.EndOffset, pair =>
        {
            if (pair.OpenLine == pair.CloseLine) return;
            // Word wrapping is disabled by the source viewer. Coordinates use visible visual lines only.
            var top = pair.OpenLine < first.LineNumber ? 0 : Y(pair.OpenLine, true);
            var bottom = pair.CloseLine > last.LineNumber ? view.Bounds.Height : Y(pair.CloseLine, false);
            var x = pair.Indent * view.WideSpaceWidth - view.HorizontalOffset + 1;
            if (x < 0 || x > view.Bounds.Width || bottom <= top) return;
            context.DrawLine(Pens[pair.Depth % Colors.Length], new Point(x, top), new Point(x, bottom));
            if (pair.CloseLine <= last.LineNumber)
            {
                var line = view.VisualLines.FirstOrDefault(line => line.FirstDocumentLine.LineNumber == pair.CloseLine);
                if (line is null) return;
                var column = line.GetVisualColumn(pair.Close - line.FirstDocumentLine.Offset);
                var end = line.GetVisualPosition(column, VisualYPosition.LineTop).X - view.HorizontalOffset;
                if (end > x) context.DrawLine(Pens[pair.Depth % Colors.Length], new Point(x, bottom), new Point(end, bottom));
            }
        });
        double Y(int number, bool below)
        {
            var line = view.VisualLines.First(line => line.FirstDocumentLine.LineNumber == number);
            return line.VisualTop - view.VerticalOffset + (below ? line.Height : 0);
        }
    }
}
