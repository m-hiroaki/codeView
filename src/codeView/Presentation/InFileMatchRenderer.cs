using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CodeView.Contracts;
using CodeView.Search;

namespace CodeView.Presentation;

internal sealed class InFileMatchRenderer(InFileSearchViewModel search) : IBackgroundRenderer
{
    internal static readonly IBrush Highlight = new SolidColorBrush(Color.FromArgb(110, 219, 165, 49));
    private static readonly IBrush IdentifierHighlight = new SolidColorBrush(Color.FromArgb(82, 145, 170, 255));
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView view, DrawingContext context)
    {
        if (!view.VisualLinesValid || view.VisualLines.Count == 0) return;
        using var clip = context.PushClip(new Rect(view.Bounds.Size));
        DrawIdentifiers(view, context);
        DrawMatches(search.Matches, Highlight, view, context, view.VisualLines[0].FirstDocumentLine.Offset,
            view.VisualLines[^1].LastDocumentLine.EndOffset);
    }

    private void DrawIdentifiers(TextView view, DrawingContext context)
    {
        var matches = search.IdentifierMatches;
        if (matches.Count == 0) return;
        foreach (var line in view.VisualLines)
        {
            var start = line.FirstDocumentLine.Offset;
            var end = line.LastDocumentLine.EndOffset;
            var left = view.ScrollOffset.X;
            var right = left + view.Bounds.Width;
            // Only use the two horizontal edges to bound document offsets when visual and logical order agree.
            if (line.TextLines.Count == 1 && line.TextLines[0] is { } textLine
                && (left > 0 || right < textLine.WidthIncludingTrailingWhitespace)
                && textLine.TextRuns.All(run => run is ShapedTextRun { BidiLevel: 0 } or TextEndOfParagraph))
            {
                if (left > textLine.WidthIncludingTrailingWhitespace) continue;
                if (left > 0) start += line.GetRelativeOffset(textLine.GetCharacterHitFromDistance(left - 2).FirstCharacterIndex);
                if (right < textLine.WidthIncludingTrailingWhitespace)
                    end = line.FirstDocumentLine.Offset
                        + line.GetRelativeOffset(textLine.GetCharacterHitFromDistance(right + 2).FirstCharacterIndex);
            }
            DrawMatches(matches, IdentifierHighlight, view, context, start, end);
        }
    }

    private static void DrawMatches(IReadOnlyList<TextRange> matches, IBrush brush,
        TextView view, DrawingContext context, int first, int end)
    {
        if (matches.Count == 0) return;
        var index = InFileSearch.LowerBound(matches, first);
        if (index > 0 && matches[index - 1].End > first) index--;
        for (; index < matches.Count && matches[index].Offset <= end; index++)
        {
            var match = matches[index];
            var segment = new SimpleSegment(checked((int)match.Offset), checked((int)match.Length));
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(view, segment))
                context.DrawRectangle(brush, null, new Rect(rect.X, rect.Y, Math.Max(2, rect.Width), rect.Height));
        }
    }
}
