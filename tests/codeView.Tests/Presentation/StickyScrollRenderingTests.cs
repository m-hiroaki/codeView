using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.Document;
using CodeView.Documents;
using CodeView.Navigation;
using CodeView.Presentation;

namespace CodeView.Tests.Presentation;

internal sealed class StickyScrollRenderingTests
{
    [Test]
    public void ShowsTheOriginalDeclarationWithItsBodyPalette()
    {
        var document = new TextDocument("    void Read(\n        int count)\n    {");
        var second = document.GetLineByNumber(2).Offset;
        var colors = new SyntaxColorizer(document, new SyntaxHighlighting(
            [new(4, 4, 1), new(second + 8, 3, 1)], new Dictionary<int, string> { [1] = "#80BBEE" }));
        var label = new TextBlock { Foreground = Brushes.Gold };
        MainWindow.SetStickyDeclaration(label, document, new(DeclarationKind.Method, "Read", 1, 2, 3, 0), colors);
        var runs = label.Inlines!.OfType<Run>().ToArray();

        Assert.That(string.Concat(runs.Select(run => run.Text)), Is.EqualTo("    void Read( int count)"));
        Assert.That(runs.Where(run => run.Foreground is ISolidColorBrush brush && brush.Color == Color.Parse("#80BBEE"))
            .Select(run => run.Text), Is.EqualTo(new[] { "void", "int" }));
    }

    [Test]
    public void TruncatesLongDeclarationsWithoutSplittingUnicodeAndUsesUncoloredFallback()
    {
        var document = new TextDocument(new string('a', 511) + "😀z");
        var label = new TextBlock { Foreground = Brushes.Gold };
        MainWindow.SetStickyDeclaration(label, document, new(DeclarationKind.Type, "name", 1, 1, 1, -1), null);
        var runs = label.Inlines!.OfType<Run>().ToArray();

        Assert.That(string.Concat(runs.Select(run => run.Text)), Is.EqualTo(new string('a', 511) + "…"));
        Assert.That(runs.All(run => run.Foreground is ISolidColorBrush brush && brush.Color == Colors.Gold), Is.True);
    }
}
