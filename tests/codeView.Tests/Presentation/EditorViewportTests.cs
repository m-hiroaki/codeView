using Avalonia;
using CodeView.Presentation;

namespace CodeView.Tests.Presentation;

internal sealed class EditorViewportTests
{
    [TestCase(0, 0)]
    [TestCase(20, 20)]
    [TestCase(80, 80)]
    [TestCase(81, 80)]
    public void OffsetIsConstrainedToCurrentDocument(double requested, double expected)
        => Assert.That(EditorViewport.Constrain(requested, requested, new(100, 100), new(20, 20)), Is.EqualTo(new Vector(expected, expected)));

    [Test]
    public void ShorterDocumentAndUnmeasuredExtentReturnOrigin()
    {
        Assert.That(EditorViewport.Constrain(100, 100, new(10, 10), new(20, 20)), Is.EqualTo(default(Vector)));
        Assert.That(EditorViewport.Constrain(100, 100, Size.Infinity, new(20, 20)), Is.EqualTo(default(Vector)));
    }

    [TestCase(-1)]
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    public void InvalidSavedOffsetIsRejected(double value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => EditorViewport.Constrain(value, 0, new(100, 100), new(20, 20)));
}
