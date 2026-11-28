using Avalonia;
using CodeView.Presentation;

namespace CodeView.Tests.Presentation;

internal sealed class BracketGuideTests
{
    private static readonly Size Viewport = new(160, 100);

    [TestCase(20, 60, 20, 60)]
    [TestCase(-15, 60, 0, 60)]
    [TestCase(20, 140, 20, 100)]
    [TestCase(-15, 140, 0, 100)]
    public void MultilineGuideEndsInTheIndentationColumn(double top, double bottom, double expectedTop, double expectedBottom)
    {
        // A closing line can contain a long expression before the closing bracket.
        // The guide must never extend horizontally into that text or the preceding line.
        var guide = BracketColorizer.GetVisibleGuide(24, top, bottom, Viewport);

        Assert.That(guide, Is.EqualTo((new Point(24, expectedTop), new Point(24, expectedBottom))));
    }

    [TestCase(-1, 20, 60)]
    [TestCase(161, 20, 60)]
    [TestCase(24, -20, 0)]
    [TestCase(24, 100, 120)]
    [TestCase(24, 40, 40)]
    public void SkipsGuidesOutsideTheViewportOrWithoutHeight(double x, double top, double bottom) =>
        Assert.That(BracketColorizer.GetVisibleGuide(x, top, bottom, Viewport), Is.Null);
}
