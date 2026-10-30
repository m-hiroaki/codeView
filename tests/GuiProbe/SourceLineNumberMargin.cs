using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace CodeView.Presentation;

/// <summary>Draws original line labels for a bounded display, compiled only into the GUI probe.</summary>
internal sealed class SourceLineNumberMargin : LineNumberMargin
{
    private TextDocument? _mappedDocument;
    public SourceLineNumbers Numbers { get; private set; }
    public long RenderedLabels { get; private set; }
    public long? LastFirstLabel { get; private set; }
    public long? LastLastLabel { get; private set; }

    public void Apply(TextDocument document, SourceLineNumbers numbers)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!numbers.IsValid || document.LineCount != numbers.DisplayLineCount)
            throw new ArgumentException("表示文書と行番号の対応が一致しません。", nameof(numbers));
        _mappedDocument = document;
        Numbers = numbers;
        LastFirstLabel = LastLastLabel = null;
        MinWidthInDigits = numbers.WidthInDigits; // Stable across slices of the same original document.
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Clear()
    {
        _mappedDocument = null;
        Numbers = default;
        LastFirstLabel = LastLastLabel = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        if (view is not { VisualLinesValid: true } || !Numbers.IsValid ||
            !ReferenceEquals(view.Document, _mappedDocument)) return;
        LastFirstLabel = LastLastLabel = null;
        var foreground = GetValue(TextBlock.ForegroundProperty);
        foreach (var line in view.VisualLines)
        {
            var sourceLine = Numbers.GetSourceLine(line.FirstDocumentLine.LineNumber);
            var text = new FormattedText(sourceLine.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, EmSize, foreground);
            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop);
            context.DrawText(text, new Point(Bounds.Width - text.Width, y - view.VerticalOffset));
            LastFirstLabel ??= sourceLine;
            LastLastLabel = sourceLine;
            RenderedLabels++;
        }
    }
}
