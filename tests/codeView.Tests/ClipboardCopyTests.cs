using CodeView.Documents;
using CodeView.Operations;
using System.Runtime.CompilerServices;

namespace CodeView.Tests;

internal sealed class ClipboardCopyTests
{
    [Test]
    public void LatestWaiterReplacesIntermediateAndOldPreparationNeverWrites() => SingleThreadTestContext.Run(async () =>
    {
        using var handle = Create("abcdef");
        using var source = new DocumentDisplaySession(handle);
        var entered = Signal();
        var release = Signal();
        var prepares = 0;
        var writes = new List<string>();
        using var copies = new ClipboardCopyCoordinator(text => { writes.Add(text); return Task.CompletedTask; }, async (selection, ct) =>
        {
            if (++prepares == 1) { entered.SetResult(); await release.Task; }
            return selection.ReadText(ct);
        });
        source.Select(0, 2);
        var a = copies.Start(source.BeginCopy());
        await entered.Task;
        source.Select(2, 4);
        var b = copies.Start(source.BeginCopy());
        source.Select(4, 6);
        var c = copies.Start(source.BeginCopy());
        Assert.That((await b.Completion).Status, Is.EqualTo(ClipboardCopyStatus.Cancelled));
        Assert.That(prepares, Is.EqualTo(1));
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(3)); // display + active + latest
        Assert.That(copies.PendingWork.IsCompleted, Is.False);
        release.SetResult();
        Assert.That((await a.Completion).Status, Is.EqualTo(ClipboardCopyStatus.Cancelled));
        Assert.That((await c.Completion).Status, Is.EqualTo(ClipboardCopyStatus.Copied));
        await copies.PendingWork;
        Assert.That(writes, Is.EqualTo(new[] { "ef" }));
        Assert.That(prepares, Is.EqualTo(2));
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
    });

    [Test]
    public void InFlightClipboardWriteKeepsSlotUntilRealCompletion() => SingleThreadTestContext.Run(async () =>
    {
        using var handle = Create("abcdef");
        using var source = new DocumentDisplaySession(handle);
        var entered = Signal();
        var release = Signal();
        var starts = new List<string>();
        var completed = new List<string>();
        var prepares = 0;
        using var copies = new ClipboardCopyCoordinator(async text =>
        {
            starts.Add(text);
            if (starts.Count == 1) { entered.SetResult(); await release.Task; }
            completed.Add(text);
        }, (selection, ct) => { prepares++; return Task.FromResult(selection.ReadText(ct)); });
        source.Select(0, 2);
        var a = copies.Start(source.BeginCopy());
        await entered.Task;
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1)); // snapshot released before native write
        source.Select(2, 4);
        var b = copies.Start(source.BeginCopy());
        Assert.That(starts, Is.EqualTo(new[] { "ab" }));
        Assert.That(prepares, Is.EqualTo(1));
        Assert.That(a.Completion.IsCompleted, Is.False);
        release.SetResult();
        var old = await a.Completion;
        Assert.That(old.Status, Is.EqualTo(ClipboardCopyStatus.Cancelled));
        Assert.That(old.WriteStarted, Is.True); // Cancellation cannot undo the completed native write.
        Assert.That((await b.Completion).Status, Is.EqualTo(ClipboardCopyStatus.Copied));
        Assert.That(completed, Is.EqualTo(new[] { "ab", "cd" }));
        await copies.PendingWork;
    });

    [Test]
    public void SelectionChangesAndSourceClosureDoNotAlterCapturedText() => SingleThreadTestContext.Run(async () =>
    {
        var handle = Create("a😀\r\n日本語");
        var source = new DocumentDisplaySession(handle);
        var gate = Signal();
        string? written = null;
        using var copies = new ClipboardCopyCoordinator(text => { written = text; return Task.CompletedTask; }, async (selection, ct) =>
        { await gate.Task; return selection.ReadText(ct); });
        source.Select(1, 5);
        var request = copies.Start(source.BeginCopy());
        source.SelectAll();
        source.Dispose();
        handle.Dispose();
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
        gate.SetResult();
        Assert.That((await request.Completion).Status, Is.EqualTo(ClipboardCopyStatus.Copied));
        Assert.That(written, Is.EqualTo("😀\r\n"));
        await copies.PendingWork;
        Assert.That(handle.ActiveReadLeases, Is.Zero);
    });

    [TestCase(false)]
    [TestCase(true)]
    public void InvalidateOrShutdownCancelsPrepareAndWaitingAndWaitsForWorker(bool shutdown) => SingleThreadTestContext.Run(async () =>
    {
        using var handle = Create("abcd");
        using var source = new DocumentDisplaySession(handle);
        var gate = Signal();
        var writes = 0;
        using var copies = new ClipboardCopyCoordinator(_ => { writes++; return Task.CompletedTask; }, async (selection, ct) =>
        { await gate.Task; return selection.ReadText(ct); });
        source.Select(0, 2);
        var a = copies.Start(source.BeginCopy());
        source.Select(2, 4);
        var b = copies.Start(source.BeginCopy());
        if (shutdown) copies.Dispose(); else copies.Invalidate();
        Assert.That((await b.Completion).Status, Is.EqualTo(ClipboardCopyStatus.Cancelled));
        Assert.That(copies.Current, Is.Null);
        Assert.That(copies.PendingWork.IsCompleted, Is.False);
        gate.SetResult();
        Assert.That((await a.Completion).Status, Is.EqualTo(ClipboardCopyStatus.Cancelled));
        await copies.PendingWork;
        Assert.That(writes, Is.Zero);
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
    });

    [Test]
    public void EmptySelectionDoesNotReplaceOrClearExistingCopy() => SingleThreadTestContext.Run(async () =>
    {
        using var handle = Create("abcd");
        using var source = new DocumentDisplaySession(handle);
        var gate = Signal();
        var writes = new List<string>();
        using var copies = new ClipboardCopyCoordinator(text => { writes.Add(text); return Task.CompletedTask; }, async (selection, ct) =>
        { await gate.Task; return selection.ReadText(ct); });
        source.SelectAll();
        var a = copies.Start(source.BeginCopy());
        source.MoveCaret(0);
        var empty = copies.Start(source.BeginCopy());
        Assert.That((await empty.Completion).Status, Is.EqualTo(ClipboardCopyStatus.EmptySelection));
        Assert.That(copies.Current, Is.SameAs(a));
        gate.SetResult();
        Assert.That((await a.Completion).Status, Is.EqualTo(ClipboardCopyStatus.Copied));
        Assert.That(writes, Is.EqualTo(new[] { "abcd" }));
    });

    [Test]
    public void NativeUnavailableIsTypedAndLaterCopyCanRetry() => SingleThreadTestContext.Run(async () =>
    {
        using var handle = Create("abc");
        using var source = new DocumentDisplaySession(handle);
        source.SelectAll();
        var uiThread = Environment.CurrentManagedThreadId;
        var calls = 0;
        using var copies = new ClipboardCopyCoordinator(_ =>
        {
            Assert.That(Environment.CurrentManagedThreadId, Is.EqualTo(uiThread));
            return ++calls == 1 ? Task.FromException(new ClipboardUnavailableException()) : Task.CompletedTask;
        });
        var failed = copies.Start(source.BeginCopy());
        Assert.That((await failed.Completion).Status, Is.EqualTo(ClipboardCopyStatus.Unavailable));
        var retry = copies.Start(source.BeginCopy());
        Assert.That((await retry.Completion).Status, Is.EqualTo(ClipboardCopyStatus.Copied));
        await copies.PendingWork;
    });

    [TestCase(false)]
    [TestCase(true)]
    public void UnknownWriteFailureCancelsLatestWithoutStartingItsPreparation(bool nativeSeh) => SingleThreadTestContext.Run(async () =>
    {
        using var handle = Create("abcd");
        using var source = new DocumentDisplaySession(handle);
        source.SelectAll();
        var entered = Signal();
        var release = Signal();
        var prepares = 0;
        Exception fault = nativeSeh ? new System.Runtime.InteropServices.SEHException("native fault")
            : new InvalidOperationException("native write fault");
        using var copies = new ClipboardCopyCoordinator(async _ =>
        { entered.SetResult(); await release.Task; throw fault; }, (selection, ct) =>
        { prepares++; return Task.FromResult(selection.ReadText(ct)); });
        var a = copies.Start(source.BeginCopy());
        await entered.Task;
        var b = copies.Start(source.BeginCopy());
        release.SetResult();
        Exception? observed = null;
        try { await a.Completion; } catch (Exception error) { observed = error; }
        Assert.That(observed, Is.SameAs(fault));
        Assert.That(a.WriteStarted, Is.True);
        Assert.That((await b.Completion).Status, Is.EqualTo(ClipboardCopyStatus.Cancelled));
        Assert.That(prepares, Is.EqualTo(1));
        observed = null;
        try { await copies.PendingWork; } catch (Exception error) { observed = error; }
        Assert.That(observed, Is.SameAs(fault));
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
    });

    [Test]
    public void DisposeDuringNativeWriteWaitsForTheWriteBeforeFinishing() => SingleThreadTestContext.Run(async () =>
    {
        using var handle = Create("abc");
        using var source = new DocumentDisplaySession(handle);
        source.SelectAll();
        var entered = Signal();
        var release = Signal();
        using var copies = new ClipboardCopyCoordinator(async _ => { entered.SetResult(); await release.Task; });
        var request = copies.Start(source.BeginCopy());
        await entered.Task;
        copies.Dispose();
        Assert.That(copies.PendingWork.IsCompleted, Is.False);
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
        release.SetResult();
        var result = await request.Completion;
        Assert.That(result.Status, Is.EqualTo(ClipboardCopyStatus.Cancelled));
        Assert.That(result.WriteStarted, Is.True);
        await copies.PendingWork;
    });

    [TestCase(false)]
    [TestCase(true)]
    public void UnknownOrUnrelatedCancellationFaultsStopWaiterAndRemainObservable(bool unrelatedCancellation) => SingleThreadTestContext.Run(async () =>
    {
        using var handle = Create("abcd");
        using var source = new DocumentDisplaySession(handle);
        var gate = Signal();
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        Exception fault = unrelatedCancellation ? new OperationCanceledException(unrelated.Token) : new InvalidOperationException("unexpected");
        var writes = 0;
        using var copies = new ClipboardCopyCoordinator(_ => { writes++; return Task.CompletedTask; }, async (_, _) =>
        { await gate.Task; throw fault; });
        source.SelectAll();
        var a = copies.Start(source.BeginCopy());
        var b = copies.Start(source.BeginCopy());
        gate.SetResult();
        Exception? observed = null;
        try { await a.Completion; } catch (Exception error) { observed = error; }
        Assert.That(observed, Is.SameAs(fault));
        Assert.That((await b.Completion).Status, Is.EqualTo(ClipboardCopyStatus.Cancelled));
        observed = null;
        try { await copies.PendingWork; } catch (Exception error) { observed = error; }
        Assert.That(observed, Is.SameAs(fault));
        Assert.That(writes, Is.Zero);
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
        using var rejected = source.BeginCopy();
        Assert.Throws<ObjectDisposedException>(() => copies.Start(rejected));
    });

    [Test]
    public void WrongPreparedLengthNeverTouchesClipboard() => SingleThreadTestContext.Run(async () =>
    {
        using var handle = Create("abc");
        using var source = new DocumentDisplaySession(handle);
        source.SelectAll();
        var writes = 0;
        using var copies = new ClipboardCopyCoordinator(_ => { writes++; return Task.CompletedTask; }, (_, _) => Task.FromResult("wrong"));
        var request = copies.Start(source.BeginCopy());
        Exception? fault = null;
        try { await request.Completion; } catch (Exception error) { fault = error; }
        Assert.That(fault, Is.TypeOf<InvalidOperationException>());
        Assert.That(writes, Is.Zero);
    });

    [Test]
    public void RejectedOrForeignThreadStartLeavesSnapshotOwnershipWithCaller() => SingleThreadTestContext.Run(async () =>
    {
        using var handle = Create("abc");
        using var source = new DocumentDisplaySession(handle);
        source.SelectAll();
        using var copies = new ClipboardCopyCoordinator(_ => Task.CompletedTask);
        using var incoming = source.BeginCopy();
        await Task.Run(() => Assert.Throws<InvalidOperationException>(() => copies.Start(incoming)));
        copies.Dispose();
        Assert.Throws<ObjectDisposedException>(() => copies.Start(incoming));
        Assert.That(incoming.ReadText(default), Is.EqualTo("abc"));
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(2));
        copies.Dispose();
    });

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public void CompletedOwnerDoesNotRetainPreparedClipboardString() => SingleThreadTestContext.Run(() =>
    {
        var state = CreateCompletedOwner();
        using (state.Owner)
        using (state.Handle)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.That(state.Text.IsAlive, Is.False);
            GC.KeepAlive(state.Owner);
            GC.KeepAlive(state.Handle);
        }
        return Task.CompletedTask;
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ClipboardCopyCoordinator Owner, DocumentHandle Handle, WeakReference Text) CreateCompletedOwner()
    {
        var handle = Create(new string('x', 10000));
        using var source = new DocumentDisplaySession(handle);
        source.SelectAll();
        WeakReference? text = null;
        var owner = new ClipboardCopyCoordinator(value => { text = new WeakReference(value); return Task.CompletedTask; },
            (selection, ct) => Task.FromResult(selection.ReadText(ct)));
        var request = owner.Start(source.BeginCopy());
        Assert.That(request.Completion.GetAwaiter().GetResult().Status, Is.EqualTo(ClipboardCopyStatus.Copied));
        return (owner, handle, text!);
    }

    private static DocumentHandle Create(string text)
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(text.AsMemory());
        return builder.Complete();
    }
}

internal sealed class ClipboardStringTests
{
    [TestCase("")]
    [TestCase("abc\r\n日本語😀")]
    [TestCase("e\u0301é\t\n")]
    public void FinalStringPreservesOriginalSelectionAfterClosure(string text)
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(("prefix" + text + "suffix").AsMemory());
        var handle = builder.Complete();
        var source = new DocumentDisplaySession(handle);
        source.Select(6, 6 + text.Length);
        using var copy = source.BeginCopy();
        source.Dispose();
        handle.Dispose();
        Assert.That(copy.ReadText(default), Is.EqualTo(text));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => copy.ReadText(cts.Token));
        copy.Dispose();
        Assert.Throws<ObjectDisposedException>(() => copy.ReadText(default));
        Assert.That(handle.ActiveReadLeases, Is.Zero);
    }

    [Test]
    public void FinalStringValidatesRangeAndCopiesMultipleChunks()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        var text = string.Concat(Enumerable.Repeat("日本語😀\r\n", 10000));
        builder.Append(text.AsMemory());
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        Assert.That(lease.ReadRangeAsString(0, text.Length, default), Is.EqualTo(text));
        Assert.That(lease.ReadRangeAsString(text.Length, 0, default), Is.Empty);
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.ReadRangeAsString(-1, 0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.ReadRangeAsString(0, -1, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.ReadRangeAsString(long.MaxValue, 0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.ReadRangeAsString(1, int.MaxValue, default));
    }
}
