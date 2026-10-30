using CodeView.Contracts;
using CodeView.Documents;

namespace CodeView.Tests.Documents;

public sealed class DocumentDisplayNavigationTests
{
    [TestCase(2L, 4L, 8L)]
    [TestCase(2L, long.MaxValue, 9L)]
    [TestCase(long.MaxValue, 1L, 15L)]
    [TestCase(1L, 3L, 1L)]
    public void OriginalPositionRestoreClampsAndKeepsScalarBoundary(long line, long column, long expected)
    {
        using var handle = Create("a😀\r\n1234\r\nlast");
        using var display = new DocumentDisplaySession(handle);
        display.MoveToPosition(new TextPosition(line, column));
        Assert.That(display.Caret, Is.EqualTo(expected));
        Assert.Throws<ArgumentOutOfRangeException>(() => display.MoveToPosition(default));
        Assert.That(display.Caret, Is.EqualTo(expected));
    }
    [Test]
    public void HorizontalMovesKeepSurrogateAndCrLfPairsTogether()
    {
        using var handle = Create("ab😀\r\ncd");
        using var display = new DocumentDisplaySession(handle);
        foreach (var expected in new[] { 1, 2, 4, 6, 7, 8, 8 })
        {
            display.MoveHorizontal(1, extendSelection: true);
            Assert.That(display.Caret, Is.EqualTo(expected));
            Assert.That(display.Anchor, Is.Zero);
        }
        Assert.That(display.Position, Is.EqualTo(new TextPosition(2, 3)));
        foreach (var expected in new[] { 7, 6, 4, 2, 1, 0, 0 })
        {
            display.MoveHorizontal(-1, extendSelection: true);
            Assert.That(display.Caret, Is.EqualTo(expected));
        }
        Assert.That(display.SelectionLength, Is.Zero);
    }

    [TestCase(2, 8, -1, 2)]
    [TestCase(2, 8, 1, 8)]
    [TestCase(8, 2, -1, 2)]
    [TestCase(8, 2, 1, 8)]
    public void HorizontalWithoutShiftCollapsesSelectionToRequestedEdge(int anchor, int caret, int direction, int expected)
    {
        using var handle = Create("0123456789");
        using var display = new DocumentDisplaySession(handle);
        display.Select(anchor, caret);
        display.MoveHorizontal(direction);
        Assert.That(display.Caret, Is.EqualTo(expected));
        Assert.That(display.Anchor, Is.EqualTo(expected));
        Assert.That(display.SelectionLength, Is.Zero);
    }

    [Test]
    public void VerticalMovementRestoresPreferredColumnAcrossShortLinesAndClampsLineDelta()
    {
        using var handle = Create("abcdef\r\nx\r\n123456\r\n");
        using var display = new DocumentDisplaySession(handle);
        display.MoveCaret(4);
        display.MoveLines(1, true);
        Assert.That(display.Position, Is.EqualTo(new TextPosition(2, 2)));
        display.MoveLines(1, true);
        Assert.That(display.Position, Is.EqualTo(new TextPosition(3, 5)));
        Assert.That(display.Anchor, Is.EqualTo(4));
        display.MoveLines(int.MinValue, true);
        Assert.That(display.Position, Is.EqualTo(new TextPosition(1, 5)));
        display.MoveLines(int.MaxValue, true);
        Assert.That(display.Position, Is.EqualTo(new TextPosition(4, 1)));
        display.MoveLines(-1, true);
        Assert.That(display.Position, Is.EqualTo(new TextPosition(3, 5)));
        display.MoveLines(-1);
        display.MoveToLineBoundary(false);
        display.MoveLines(1);
        Assert.That(display.Position, Is.EqualTo(new TextPosition(3, 1)));
    }

    [Test]
    public void LineAndDocumentBoundariesUseOriginalTextOutsideCurrentSlice()
    {
        var text = "head\r\n" + new string('x', 10000) + "\r\nlast";
        using var handle = Create(text);
        using var display = new DocumentDisplaySession(handle);
        display.MoveCaret(5000);
        _ = display.CreateSlice(display.Caret, 1024);
        display.MoveToLineBoundary(true, true);
        Assert.That(display.Caret, Is.EqualTo(10006));
        Assert.That(display.Anchor, Is.EqualTo(5000));
        display.MoveToLineBoundary(false);
        Assert.That(display.Caret, Is.EqualTo(6));
        display.MoveToDocumentBoundary(true, true);
        Assert.That(display.Caret, Is.EqualTo(text.Length));
        Assert.That(display.Anchor, Is.EqualTo(6));
        display.MoveToDocumentBoundary(false);
        Assert.That(display.Position, Is.EqualTo(new TextPosition(1, 1)));
    }

    [TestCase(0, 20, 0, 8, null, false, true)]
    [TestCase(20, 0, 0, 8, null, true, true)]
    [TestCase(0, 10, 0, 2, 2, false, true)]
    [TestCase(9, 12, 1, 3, 4, false, false)]
    [TestCase(12, 9, 1, 3, 1, true, false)]
    [TestCase(0, 7, 0, 0, null, false, true)]
    [TestCase(17, 20, 8, 0, null, false, true)]
    [TestCase(5, 5, 0, 0, null, false, false)]
    [TestCase(16, 16, 8, 0, 8, false, false)]
    public void VisibleSelectionIsClippedWhileOriginalSelectionRemainsUnchanged(
        int anchor, int caret, int start, int length, int? visibleCaret, bool reverse, bool clipped)
    {
        using var handle = Create("0123456789ABCDEFGHIJ");
        using var display = new DocumentDisplaySession(handle);
        var slice = display.CreateSlice(10, 8);
        display.Select(anchor, caret);
        Assert.That(display.TryGetDisplaySelection(slice, out var selection), Is.True);
        Assert.That(selection, Is.EqualTo(new DocumentDisplaySelection(start, length, visibleCaret, reverse, clipped)));
        Assert.That((display.Anchor, display.Caret), Is.EqualTo((anchor, caret)));
    }

    [Test]
    public void ShiftMovementCrossesWindowWithoutResettingOriginalAnchor()
    {
        using var handle = Create(new string('a', 4096));
        using var display = new DocumentDisplaySession(handle);
        var old = display.CreateSlice(0, 1024);
        for (var i = 0; i < 1100; i++) display.MoveHorizontal(1, true);
        Assert.That(display.TryGetDisplaySelection(old, out var offscreen), Is.True);
        Assert.That(offscreen.CaretOffset, Is.Null);
        var current = display.CreateSlice(display.Caret, 1024);
        Assert.That(display.TryGetDisplaySelection(old, out _), Is.False);
        Assert.That(display.TryGetDisplaySelection(current, out var visible), Is.True);
        Assert.That(visible.CaretOffset, Is.EqualTo(256));
        Assert.That(visible.Length, Is.EqualTo(256));
        Assert.That(visible.IsClipped, Is.True);
        Assert.That(display.Anchor, Is.Zero);
        Assert.That(display.SelectionLength, Is.EqualTo(1100));
        using var copy = display.BeginCopy();
        using var writer = new StringWriter();
        copy.WriteTo(writer, default);
        Assert.That(writer.ToString(), Is.EqualTo(new string('a', 1100)));
    }

    [Test]
    public void EmptyInvalidOldAndDisposedNavigationIsDefined()
    {
        using var handle = Create("");
        var display = new DocumentDisplaySession(handle);
        display.MoveHorizontal(1);
        display.MoveHorizontal(-1);
        display.MoveLines(int.MaxValue);
        display.MoveToLineBoundary(true);
        display.MoveToDocumentBoundary(false);
        var old = display.CreateSlice(0, 4);
        var current = display.CreateSlice(0, 4);
        Assert.That(display.TryGetDisplaySelection(old, out var rejected), Is.False);
        Assert.That(rejected, Is.EqualTo(default(DocumentDisplaySelection)));
        Assert.That(display.TryGetDisplaySelection(current, out var empty), Is.True);
        Assert.That(empty, Is.EqualTo(new DocumentDisplaySelection(0, 0, 0, false, false)));
        Assert.Throws<ArgumentOutOfRangeException>(() => display.MoveHorizontal(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => display.MoveHorizontal(int.MinValue));
        Assert.Throws<ArgumentNullException>(() => display.TryGetDisplaySelection(null!, out _));
        display.Dispose();
        Assert.Throws<ObjectDisposedException>(() => display.MoveLines(1));
        Assert.Throws<ObjectDisposedException>(() => display.MoveToLineBoundary(false));
        Assert.Throws<ObjectDisposedException>(() => display.TryGetDisplaySelection(current, out _));
    }

    private static DocumentHandle Create(string text)
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(text.AsMemory());
        return builder.Complete();
    }
}
