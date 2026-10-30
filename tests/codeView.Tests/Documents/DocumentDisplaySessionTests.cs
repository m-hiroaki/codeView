using System.Text;
using System.Runtime.CompilerServices;
using CodeView.Contracts;
using CodeView.Documents;

namespace CodeView.Tests.Documents;

public sealed class DocumentDisplaySessionTests
{
    [Test]
    public void BoundaryNormalizationMatchesSourceWithAndWithoutCachedDisplayText()
    {
        const string text = "a😀\r\nbc😀d\r\n";
        using var handle = Create(text);
        using var sourceOnly = new DocumentDisplaySession(handle);
        using var sliced = new DocumentDisplaySession(handle);
        for (var target = 0; target <= text.Length; target++)
        {
            sliced.CreateSlice(target, 4);
            for (var offset = 0; offset <= text.Length; offset++)
            {
                sourceOnly.MoveCaret(offset);
                sliced.MoveCaret(offset);
                Assert.That(sliced.Caret, Is.EqualTo(sourceOnly.Caret), $"target={target}, offset={offset}");
            }
        }
    }

    [Test]
    public void SelectionAndCopyUseOriginalCoordinatesAcrossDisplaySlices()
    {
        var original = "first\r\n" + new string('x', 10000) + "😀\r\nlast";
        using var handle = Create(original);
        using var display = new DocumentDisplaySession(handle);
        var first = display.CreateSlice(7, 1024);
        Assert.That(display.TryMoveFromDisplay(first, 7), Is.True);
        var last = display.CreateSlice(original.Length, 1024);
        Assert.That(display.TryMoveFromDisplay(last, last.Text.Length, extendSelection: true), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(display.Anchor, Is.EqualTo(7));
            Assert.That(display.Caret, Is.EqualTo(original.Length));
            Assert.That(display.Position, Is.EqualTo(new TextPosition(3, 5)));
            Assert.That(last.SourceStart.Line, Is.EqualTo(2));
            Assert.That(last.SourceStart.Column, Is.EqualTo(last.SourceOffset - 7 + 1));
            Assert.That(last.TargetDisplayOffset, Is.EqualTo(last.Text.Length));
        });
        using var copy = display.BeginCopy();
        using var writer = new StringWriter();
        copy.WriteTo(writer, CancellationToken.None);
        Assert.That(writer.ToString(), Is.EqualTo(original[7..]));
    }

    [Test]
    public void ReversedSelectionAndCopySnapshotSurviveLaterSelectionAndClosure()
    {
        var handle = Create("0123456789");
        var display = new DocumentDisplaySession(handle);
        display.Select(8, 2);
        using var copy = display.BeginCopy();
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(2));
        display.SelectAll();
        display.Dispose();
        display.Dispose();
        handle.Dispose();
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
        using var writer = new StringWriter();
        copy.WriteTo(writer, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(copy.Offset, Is.EqualTo(2));
            Assert.That(copy.Length, Is.EqualTo(6));
            Assert.That(writer.ToString(), Is.EqualTo("234567"));
        });
        copy.Dispose();
        copy.Dispose();
        Assert.That(handle.ActiveReadLeases, Is.Zero);
        Assert.Throws<ObjectDisposedException>(() => copy.WriteTo(writer, CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => display.CreateSlice(0, 1024));
    }

    [Test]
    public void OldEqualAndForeignSliceEventsCannotChangeSelection()
    {
        using var handle = Create("0123456789");
        using var display = new DocumentDisplaySession(handle);
        using var other = new DocumentDisplaySession(handle);
        var old = display.CreateSlice(5, 4);
        var current = display.CreateSlice(5, 4);
        var foreign = other.CreateSlice(5, 4);
        display.Select(1, 9);
        Assert.Multiple(() =>
        {
            Assert.That(current, Is.EqualTo(old));
            Assert.That(display.TryMoveFromDisplay(old, 0), Is.False);
            Assert.That(display.TryMoveFromDisplay(foreign, 0), Is.False);
            Assert.That(display.TryMoveFromDisplay(current with { }, 0), Is.False);
            Assert.That(display.Anchor, Is.EqualTo(1));
            Assert.That(display.Caret, Is.EqualTo(9));
        });
        Assert.That(display.TryMoveFromDisplay(current, 1), Is.True);
        Assert.That(display.Caret, Is.EqualTo(current.SourceOffset + 1));
        Assert.That(display.SelectionLength, Is.Zero);
    }

    [TestCase("a😀bcdef\r\nlast", 2, 1)]
    [TestCase("\r\n😀\r\n", 1, 0)]
    public void EveryTargetAndWindowBoundaryKeepsScalarAndCrLfTogether(string original, int anchor, int expectedAnchor)
    {
        using var handle = Create(original);
        using var display = new DocumentDisplaySession(handle);
        for (var offset = 0; offset <= original.Length; offset++)
        {
            var slice = display.CreateSlice(offset, 4);
            var normalized = offset > 0 && offset < original.Length &&
                ((char.IsHighSurrogate(original[offset - 1]) && char.IsLowSurrogate(original[offset])) ||
                 (original[offset - 1] == '\r' && original[offset] == '\n')) ? offset - 1 : offset;
            Assert.Multiple(() =>
            {
                Assert.That(slice.Text.Length, Is.LessThanOrEqualTo(4));
                Assert.That(slice.TargetOffset, Is.EqualTo(normalized));
                Assert.That(slice.Text, Is.EqualTo(original.Substring((int)slice.SourceOffset, slice.Text.Length)));
                Assert.That(char.IsLowSurrogate(slice.Text[0]), Is.False);
                Assert.That(char.IsHighSurrogate(slice.Text[^1]), Is.False);
                Assert.That(slice.Text[0], Is.Not.EqualTo('\n'));
                Assert.That(slice.Text[^1], Is.Not.EqualTo('\r'));
            });
            display.MoveCaret(offset);
            Assert.That(display.Caret, Is.EqualTo(normalized));
        }
        display.Select(anchor, original.Length);
        Assert.That(display.Anchor, Is.EqualTo(expectedAnchor));
    }

    [Test]
    public void EmptyDocumentAndEmptySelectionHaveValidEofAndCopyNoCharacters()
    {
        using var handle = Create("");
        using var display = new DocumentDisplaySession(handle);
        display.SelectAll();
        var slice = display.CreateSlice(0, 4);
        Assert.Multiple(() =>
        {
            Assert.That(display.Position, Is.EqualTo(new TextPosition(1, 1)));
            Assert.That(slice.Text, Is.Empty);
            Assert.That(slice.TargetDisplayOffset, Is.Zero);
            Assert.That(display.TryMoveFromDisplay(slice, 0, true), Is.True);
            Assert.That(display.SelectionLength, Is.Zero);
        });
        using var copy = display.BeginCopy();
        using var writer = new StringWriter();
        copy.WriteTo(writer, CancellationToken.None);
        Assert.That(writer.ToString(), Is.Empty);
    }

    [Test]
    public void InvalidRequestsLeaveSelectionAndCurrentSliceUnchanged()
    {
        using var handle = Create("abc");
        using var display = new DocumentDisplaySession(handle);
        display.Select(1, 2);
        var slice = display.CreateSlice(1, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => display.Select(0, long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => display.Select(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => display.CreateSlice(long.MaxValue, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => display.CreateSlice(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => display.CreateSlice(0, 16385));
        Assert.Throws<ArgumentOutOfRangeException>(() => display.TryMoveFromDisplay(slice, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => slice.ToSourceOffset(-1));
        Assert.That((display.Anchor, display.Caret), Is.EqualTo((1, 2)));
        Assert.That(display.TryMoveFromDisplay(slice, 0), Is.True);
    }

    [Test]
    public void ReleasedHandleCannotStartDisplayOrNewCopy()
    {
        var handle = Create("abc");
        using var display = new DocumentDisplaySession(handle);
        handle.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new DocumentDisplaySession(handle));
        Assert.Throws<ObjectDisposedException>(() => display.BeginCopy());
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
        display.Dispose();
        Assert.That(handle.ActiveReadLeases, Is.Zero);
    }

    [Test]
    public void DisplayIsOwnedByCreatingThreadWhileCopyCanRunOnWorker()
    {
        using var handle = Create("a😀\r\n日本語");
        using var display = new DocumentDisplaySession(handle);
        display.SelectAll();
        using var copy = display.BeginCopy();
        Task.Run(() =>
        {
            Assert.Throws<InvalidOperationException>(() => display.MoveCaret(0));
            using var writer = new StringWriter();
            copy.WriteTo(writer, CancellationToken.None);
            Assert.That(writer.ToString(), Is.EqualTo("a😀\r\n日本語"));
        }).GetAwaiter().GetResult();
    }

    [Test]
    public void RetainingDisposedDisplayDoesNotRetainItsOriginalHandle()
    {
        var (display, weakHandle) = CreateDisposedDisplay();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.That(weakHandle.IsAlive, Is.False);
        GC.KeepAlive(display);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DocumentDisplaySession Display, WeakReference Handle) CreateDisposedDisplay()
    {
        var handle = Create("source");
        var display = new DocumentDisplaySession(handle);
        var weakHandle = new WeakReference(handle);
        display.Dispose();
        handle.Dispose();
        return (display, weakHandle);
    }

    private static DocumentHandle Create(string text)
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(text.AsMemory());
        return builder.Complete();
    }
}

public sealed class DocumentRangeCopyTests
{
    [Test]
    public void LargeRangeStreamsInBoundedWritesWithoutOwningOrClosingDestination()
    {
        var text = "prefix" + string.Concat(Enumerable.Repeat("日本語😀\r\n", 20000)) + "suffix";
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(text.AsMemory());
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        using var writer = new RecordingWriter();
        lease.CopyRangeTo(6, text.Length - 12, writer, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(writer.Content.ToString(), Is.EqualTo(text[6..^6]));
            Assert.That(writer.Writes, Is.GreaterThan(1));
            Assert.That(writer.MaxWrite, Is.LessThanOrEqualTo(DocumentReadLease.ChunkChars));
            Assert.That(writer.WasDisposed, Is.False);
        });
    }

    [Test]
    public void InvalidCancelledAndFaultedWritesPropagateWithoutLosingLease()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(new string('a', 100000).AsMemory());
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        using var writer = new RecordingWriter();
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.CopyRangeTo(-1, 1, writer, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.CopyRangeTo(0, -1, writer, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.CopyRangeTo(1, long.MaxValue, writer, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.CopyRangeTo(long.MaxValue, 0, writer, default));
        Assert.Throws<ArgumentNullException>(() => lease.CopyRangeTo(0, 0, null!, default));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => lease.CopyRangeTo(0, 0, writer, cts.Token));
        Assert.That(writer.Writes, Is.Zero);
        var fault = new IOException("destination failed");
        writer.OnWrite = () => throw fault;
        Assert.That(Assert.Throws<IOException>(() => lease.CopyRangeTo(0, 100000, writer, default)), Is.SameAs(fault));
        writer.OnWrite = null;
        lease.CopyRangeTo(99999, 1, writer, default);
        Assert.That(writer.Content.ToString(), Is.EqualTo("a"));
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.CopyRangeTo(0, 0, writer, default));
    }

    [Test]
    public void CancellationDuringWriteStopsBeforeNextChunk()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(new string('a', 100000).AsMemory());
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        using var cts = new CancellationTokenSource();
        using var writer = new RecordingWriter { OnWrite = cts.Cancel };
        Assert.Throws<OperationCanceledException>(() => lease.CopyRangeTo(0, lease.Length, writer, cts.Token));
        Assert.That(writer.Writes, Is.EqualTo(1));
        Assert.That(writer.Content.Length, Is.LessThanOrEqualTo(DocumentReadLease.ChunkChars));
    }

    private sealed class RecordingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public StringBuilder Content { get; } = new();
        public int Writes { get; private set; }
        public int MaxWrite { get; private set; }
        public bool WasDisposed { get; private set; }
        public Action? OnWrite { get; set; }
        public override void Write(char[] buffer, int index, int count)
        {
            Writes++;
            MaxWrite = Math.Max(MaxWrite, count);
            OnWrite?.Invoke();
            Content.Append(buffer, index, count);
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
