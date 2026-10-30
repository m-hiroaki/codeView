using CodeView.Documents;

namespace CodeView.Tests.Documents;

public sealed class DocumentAdapterTests
{
    [Test]
    public async Task WorkerBuiltDocumentCanBeTransferredWithoutUndoHistory()
    {
        using var handle = await Task.Run(() =>
        {
            using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
            builder.Append("a😀b\r".AsMemory());
            builder.Append("\n日本語".AsMemory());
            return builder.Complete();
        });
        var document = handle.ClaimForDisplay();
        Assert.That(document.Text, Is.EqualTo("a😀b\r\n日本語"));
        Assert.That(document.LineCount, Is.EqualTo(2));
        Assert.That(document.UndoStack.CanUndo, Is.False);
    }

    [Test]
    public async Task SnapshotCanBeReadOnWorkerAndOutliveClosedDocument()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append("original".AsMemory());
        var handle = builder.Complete();
        var lease = handle.AcquireReadView();
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
        handle.Dispose();
        handle.Dispose();
        Assert.That(await Task.Run(() => lease.ReadRange(0, 8)), Is.EqualTo("original"));
        Assert.Throws<ObjectDisposedException>(() => handle.AcquireReadView());
        lease.Dispose();
        lease.Dispose();
        Assert.That(handle.ActiveReadLeases, Is.Zero);
        Assert.Throws<ObjectDisposedException>(() => lease.ReadRange(0, 1));
    }

    [Test]
    public void EmptyDocumentAndRangeBoundariesAreSupported()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        Assert.That(handle.Length, Is.Zero);
        Assert.That(lease.ReadRange(0, 0), Is.Empty);
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.ReadRange(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.ReadRange(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.ReadRange(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.ReadRange(long.MaxValue, 0));
        Assert.Throws<ObjectDisposedException>(() => builder.Complete());
        Assert.Throws<ObjectDisposedException>(() => builder.Append("late".AsMemory()));
    }

    [Test]
    public async Task BuilderCannotChangeItsOwningThread()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        await Task.Run(() => Assert.Throws<InvalidOperationException>(() => builder.Append("wrong thread".AsMemory())));
    }
}
