using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CodeView.Search;

namespace CodeView.Presentation;

internal sealed class InFileMatchRenderer(InFileSearchViewModel search) : IBackgroundRenderer
{
    private static readonly IBrush Highlight = new SolidColorBrush(Color.FromArgb(110, 219, 165, 49));
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView view, DrawingContext context)
    {
        var matches = search.Matches;
        if (matches.Count == 0 || !view.VisualLinesValid || view.VisualLines.Count == 0) return;
        var first = view.VisualLines[0].FirstDocumentLine.Offset;
        var end = view.VisualLines[^1].LastDocumentLine.EndOffset;
        var index = InFileSearch.LowerBound(matches, first);
        if (index > 0 && matches[index - 1].End > first) index--;
        using var clip = context.PushClip(new Rect(view.Bounds.Size));
        for (; index < matches.Count && matches[index].Offset <= end; index++)
        {
            var match = matches[index];
            var segment = new SimpleSegment(checked((int)match.Offset), checked((int)match.Length));
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(view, segment))
                context.DrawRectangle(Highlight, null, new Rect(rect.X, rect.Y, Math.Max(2, rect.Width), rect.Height));
        }
    }
}
