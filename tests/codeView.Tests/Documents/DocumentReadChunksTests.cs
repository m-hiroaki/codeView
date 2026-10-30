using System.Text;
using CodeView.Contracts;
using CodeView.Documents;

namespace CodeView.Tests.Documents;

public sealed class DocumentReadChunksTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(16383)]
    [TestCase(16384)]
    [TestCase(16385)]
    [TestCase(65539)]
    public void ReadsEntireSnapshotWithBoundedChunks(int length)
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        var original = new string('x', length);
        builder.Append(original.AsMemory());
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        var text = new StringBuilder();
        var calls = 0;
        var delivered = lease.VisitChunks(chunk =>
        {
            calls++;
            Assert.That(chunk.Length, Is.InRange(1, DocumentReadLease.ChunkChars));
            text.Append(chunk.Span);
            return true;
        }, CancellationToken.None);
        Assert.That(delivered, Is.EqualTo(length));
        Assert.That(text.ToString(), Is.EqualTo(original));
        Assert.That(calls == 0, Is.EqualTo(length == 0));
        Assert.That(lease.ReadRange(0, length), Is.EqualTo(original));
    }

    [Test]
    public async Task WorkerReadsOriginalUtf16AndCrLfAcrossChunkBoundariesAfterClose()
    {
        var text = new string('a', 16383) + "😀" + new string('b', 16382) + "\r\n日本語\u3099\nend";
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(text.AsMemory());
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        handle.Dispose();
        var expected = new TextCursor();
        expected.Advance(text.AsSpan());
        var actual = new TextCursor();
        var delivered = await Task.Run(() => lease.VisitChunks(chunk =>
        {
            Assert.That(chunk.Span.SequenceEqual(text.AsSpan((int)actual.Offset, chunk.Length)), Is.True);
            actual.Advance(chunk.Span);
            return true;
        }, CancellationToken.None));
        Assert.That(delivered, Is.EqualTo(text.Length));
        Assert.That(actual.Offset, Is.EqualTo(expected.Offset));
        Assert.That(actual.Position, Is.EqualTo(expected.Position));
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
        lease.Dispose();
        Assert.That(handle.ActiveReadLeases, Is.Zero);
    }

    [Test]
    public void SnapshotScanDoesNotObserveLaterLiveDocumentChanges()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append("original\r\ncontent".AsMemory());
        using var handle = builder.Complete();
        var document = handle.ClaimForDisplay();
        using var lease = handle.AcquireReadView();
        document.Text = "replacement";
        var captured = new StringBuilder();
        lease.VisitChunks(chunk => { captured.Append(chunk.Span); return true; }, CancellationToken.None);
        Assert.That(captured.ToString(), Is.EqualTo("original\r\ncontent"));
        Assert.That(document.Text, Is.EqualTo("replacement"));
    }

    [Test]
    public void EarlyStopAndRepeatedReadsStartAtBeginning()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(("start" + new string('x', DocumentReadLease.ChunkChars * 3)).AsMemory());
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var calls = 0;
            var delivered = lease.VisitChunks(chunk =>
            {
                calls++;
                Assert.That(chunk.Span.StartsWith("start"), Is.True);
                return false;
            }, CancellationToken.None);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(delivered, Is.InRange(5, DocumentReadLease.ChunkChars));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PreCancelledScanDoesNotDeliverEvenForEmptyDocument(bool empty)
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        if (!empty) builder.Append("content".AsMemory());
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        Assert.Throws<OperationCanceledException>(() => lease.VisitChunks(_ => { calls++; return true; }, cancellation.Token));
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public void CancellationAndConsumerFailuresDoNotPoisonLaterScans()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(new string('x', DocumentReadLease.ChunkChars * 3).AsMemory());
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        Assert.Throws<OperationCanceledException>(() => lease.VisitChunks(_ =>
        {
            calls++;
            cancellation.Cancel();
            return false;
        }, cancellation.Token));
        Assert.That(calls, Is.EqualTo(1));
        var failure = new InvalidOperationException("consumer failed");
        Assert.That(Assert.Throws<InvalidOperationException>(() => lease.VisitChunks(_ => throw failure, CancellationToken.None)),
            Is.SameAs(failure));
        Assert.That(lease.VisitChunks(_ => true, CancellationToken.None), Is.EqualTo(lease.Length));
    }

    [Test]
    public void DisposedLeaseRejectsNewScansAndNullConsumerFails()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        Assert.Throws<ArgumentNullException>(() => lease.VisitChunks(null!, CancellationToken.None));
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.VisitChunks(_ => true, CancellationToken.None));
    }
}
