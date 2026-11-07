using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CodeView.Documents;

namespace CodeView.Presentation;

internal sealed class SyntaxColorizer(TextDocument document, SyntaxHighlighting highlighting) : DocumentColorizingTransformer
{
    private readonly Dictionary<int, ImmutableSolidColorBrush> _brushes = highlighting.Palette
        .Where(entry => Color.TryParse(entry.Value, out _))
        .ToDictionary(entry => entry.Key, entry => new ImmutableSolidColorBrush(Color.Parse(entry.Value)));

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!ReferenceEquals(CurrentContext.Document, document)) return;
        var spans = highlighting.Spans;
        var low = 0;
        var high = spans.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (spans[middle].Offset < line.Offset) low = middle + 1;
            else high = middle;
        }

        for (var index = low; index < spans.Count; index++)
        {
            var span = spans[index];
            if (span.Offset >= line.EndOffset) break;
            if (_brushes.TryGetValue(span.Foreground, out var brush))
                ChangeLinePart(span.Offset, span.Offset + span.Length, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}
