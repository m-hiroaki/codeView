using CodeView.Documents;
using CodeView.Presentation;

namespace CodeView.Tests.Performance;

public sealed class RowPointerSelectionTests
{
    [TestCase("\r\n")]
    [TestCase("\r")]
    [TestCase("\n")]
    public void DragAcrossTruncatedRowsCopiesOriginalIncludingHiddenText(string newline)
    {
        var body = "first" + newline + new string('x', 8000) + newline + "last";
        using var handle = Create(body);
        using var source = new DocumentDisplaySession(handle);
        var rows = source.CreateRowProjection(3, 10);
        var pointer = new BoundedPointerSelection();
        Assert.That(pointer.BeginRows(source, rows, 0, 1, false, false), Is.True);
        Assert.That(pointer.MoveRows(source, rows, rows.Text.Length, 1), Is.True);
        using var copy = source.BeginCopy();
        Assert.That(copy.ReadText(CancellationToken.None), Is.EqualTo(body));
        Assert.That(rows.ProjectSelection(source.Anchor, source.Caret).Spans.Length, Is.EqualTo(3));
        Assert.That(source.FirstVisibleLine, Is.EqualTo(1));
    }

    [Test]
    public void WholeLineDragRetainsOriginalAnchorAcrossProjectionExchange()
    {
        using var handle = Create("one\r\ntwo\r\nthree\r\nfour");
        using var source = new DocumentDisplaySession(handle);
        source.SetViewport(3, 0);
        var pointer = new BoundedPointerSelection();
        Assert.That(pointer.BeginRows(source, source.CreateRowProjection(2, 10), 0, 7, false, true), Is.True);
        source.SetViewport(1, 0);
        Assert.That(pointer.MoveRows(source, source.CreateRowProjection(2, 10), 0, 7), Is.True);
        using var copy = source.BeginCopy();
        Assert.That(copy.ReadText(CancellationToken.None), Is.EqualTo("one\r\ntwo\r\nthree\r\n"));
        Assert.That(source.Anchor, Is.GreaterThan(source.Caret));
        Assert.That(pointer.End(8), Is.False);
        Assert.That(pointer.End(7), Is.True);
        Assert.That(pointer.IsActive, Is.False);
    }

    [Test]
    public void InvalidStaleForeignAndOtherPointerHitsDoNotChangeState()
    {
        using var handle = Create("a😀b\r\nend");
        using var source = new DocumentDisplaySession(handle);
        var stale = source.CreateRowProjection(2, 10);
        var rows = source.CreateRowProjection(2, 10);
        var pointer = new BoundedPointerSelection();
        Assert.That(pointer.BeginRows(source, stale, 0, 1, false, false), Is.False);
        Assert.That(pointer.BeginRows(source, rows, 2, 1, false, false), Is.False); // Surrogate interior.
        Assert.That(pointer.BeginRows(source, rows, -1, 1, false, true), Is.False);
        Assert.That(pointer.IsActive, Is.False);
        Assert.That(source.Caret, Is.Zero);
        Assert.That(pointer.BeginRows(source, rows, 0, 1, false, false), Is.True);
        Assert.That(pointer.MoveRows(source, rows, rows.Text.Length, 2), Is.False);
        using var other = new DocumentDisplaySession(handle);
        Assert.That(pointer.MoveRows(other, rows, rows.Text.Length, 1), Is.False);
        Assert.That(source.Caret, Is.Zero);
        pointer.Cancel();
        Assert.That(pointer.MoveRows(source, rows, 1, 1), Is.False);
    }

    [TestCase("")]
    [TestCase("a\r\n")]
    public void EmptyRowAndWholeLineShiftSelectionRemainValid(string body)
    {
        using var handle = Create(body);
        using var source = new DocumentDisplaySession(handle);
        source.SetViewport(body.Length == 0 ? 1 : 2, 0);
        var rows = source.CreateRowProjection(2, 10);
        var pointer = new BoundedPointerSelection();
        Assert.That(pointer.BeginRows(source, rows, 0, 1, false, true), Is.True);
        Assert.That(source.SelectionLength, Is.Zero);
        pointer.Cancel();
        source.SelectAll();
        Assert.That(pointer.BeginRows(source, rows, 0, 2, true, true), Is.True);
        using var copy = source.BeginCopy();
        Assert.That(copy.ReadText(CancellationToken.None), Is.EqualTo(body));
    }

    private static DocumentHandle Create(string body)
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(body.AsMemory());
        return builder.Complete();
    }
}
