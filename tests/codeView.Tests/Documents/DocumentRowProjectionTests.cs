using CodeView.Documents;
using System.Runtime.CompilerServices;

namespace CodeView.Tests.Documents;

public sealed class DocumentRowProjectionTests
{
    [Test]
    public void LongMixedLinesRetainAllRequestedRowsWithoutCopyingTheirHiddenText()
    {
        var source = "before\r\n" + new string('x', 1024 * 1024) + "\r\nafter\r\n";
        using var handle = Create(source);
        using var view = new DocumentDisplaySession(handle);
        var projection = view.CreateRowProjection(4, 16);
        Assert.That(projection.Text, Is.EqualTo("before\n" + new string('x', 16) + "\nafter\n"));
        Assert.That(projection.Rows.Length, Is.EqualTo(4));
        Assert.That(projection.Rows[1].RightClipped, Is.True);
        Assert.That(projection.Rows[2].SourceLine, Is.EqualTo(3));
        Assert.That(projection.Rows[2].SourceOffset, Is.EqualTo(source.IndexOf("after", StringComparison.Ordinal)));
        Assert.That(projection.Rows[3].Length, Is.Zero);
        foreach (var row in projection.Rows)
        {
            Assert.That(projection.Text.AsSpan(row.DisplayOffset, row.Length).ToString(),
                Is.EqualTo(source.Substring((int)row.SourceOffset, row.Length)));
            for (var i = 0; i < row.Length; i++)
            {
                Assert.That(projection.TrySourceCharacter(row.DisplayOffset + i, out var original), Is.True);
                Assert.That(original, Is.EqualTo(row.SourceOffset + i));
            }
        }
    }

    [TestCase("a\r\nb\rc\n")]
    [TestCase("\r\n\r\n")]
    [TestCase("")]
    public void ArtificialSeparatorsAreNeverMappedAsSourceCharacters(string source)
    {
        using var handle = Create(source);
        using var view = new DocumentDisplaySession(handle);
        var projection = view.CreateRowProjection(8, 8);
        foreach (var row in projection.Rows)
        {
            Assert.That(projection.TrySourcePosition(row.DisplayOffset, out var original), Is.True);
            Assert.That(original, Is.EqualTo(row.SourceOffset));
            Assert.That(projection.TryDisplayPosition(row.SourceOffset, out var display), Is.True);
            Assert.That(display, Is.EqualTo(row.DisplayOffset));
            Assert.That(projection.TrySourceCharacter(row.DisplayOffset + row.Length, out _), Is.False);
        }
        Assert.That(projection.TrySourcePosition(-1, out _), Is.False);
        Assert.That(projection.TrySourcePosition(projection.Text.Length + 1, out _), Is.False);
        Assert.That(projection.TryDisplayPosition(-1, out _), Is.False);
        Assert.That(projection.TryDisplayPosition(long.MaxValue, out _), Is.False);
    }

    [Test]
    public void HorizontalClippingPreservesScalarsAndRejectsSurrogateInteriorPositions()
    {
        using var handle = Create("a😀b\r\n😀cd\r\n");
        using var view = new DocumentDisplaySession(handle);
        view.SetViewport(1, 2); // Between the first pair: retain the whole scalar.
        var projection = view.CreateRowProjection(3, 2);
        Assert.That(projection.Text, Is.EqualTo("😀\ncd\n"));
        Assert.That(projection.Rows[0].SourceColumn, Is.EqualTo(2));
        Assert.That(projection.Rows[0].LeftClipped, Is.True);
        Assert.That(projection.Rows[0].RightClipped, Is.True);
        Assert.That(projection.TrySourcePosition(1, out _), Is.False);
        Assert.That(projection.TryDisplayPosition(2, out _), Is.False);
        Assert.That(view.TryMoveFromRows(projection, 1), Is.False);
        Assert.That(view.Caret, Is.Zero);
        Assert.That(view.TryMoveFromRows(projection, 2), Is.True);
        Assert.That(view.Caret, Is.EqualTo(3));
        using var tailHandle = Create("a😀z");
        using var tail = new DocumentDisplaySession(tailHandle);
        Assert.That(tail.CreateRowProjection(1, 2).Text, Is.EqualTo("a"), "Do not end between a pair.");
    }

    [Test]
    public void BrowsingDoesNotMoveSelectionAndRevealIsExplicit()
    {
        using var handle = Create("0123456789\r\na\r\nb\r\nc\r\nd");
        using var view = new DocumentDisplaySession(handle);
        view.Select(8, 2);
        view.SetViewport(4, 9);
        var projection = view.CreateRowProjection(2, 4);
        Assert.That((view.Anchor, view.Caret), Is.EqualTo((8L, 2L)));
        Assert.That(view.TryGetRowSelection(projection, out var selection), Is.True);
        Assert.That(selection!.CaretOffset, Is.Null);
        Assert.That(selection.Spans.Length, Is.Zero);
        Assert.That(selection.IsReversed, Is.True);
        view.MoveCaret(3, extendSelection: true);
        Assert.That((view.FirstVisibleLine, view.HorizontalSourceOffset), Is.EqualTo((4L, 9L)));
        view.RevealCaret(2, 4);
        Assert.That((view.FirstVisibleLine, view.HorizontalSourceOffset), Is.EqualTo((1L, 3L)));
        Assert.That((view.Anchor, view.Caret), Is.EqualTo((8L, 3L)));
    }

    [TestCase("e\u0301")]
    [TestCase("👩‍💻")]
    [TestCase("🇯🇵")]
    [TestCase("각")]
    public void IndexedProjectionClipsOnlyAtWholeGraphemes(string cluster)
    {
        var text = $"a{cluster}z\r\nlong";
        using var handle = Create(text, collectGraphemeIndex: true);
        using var view = new DocumentDisplaySession(handle);
        view.Select(text.Length, 0);
        Assert.That(view.CreateRowProjection(1, cluster.Length).Text, Is.EqualTo("a"));
        view.SetViewport(1, 2);
        var projection = view.CreateRowProjection(1, cluster.Length);
        Assert.That(projection.Text, Is.EqualTo(cluster));
        Assert.That(projection.Rows[0].SourceOffset, Is.EqualTo(1));
        Assert.That(projection.TrySourcePosition(cluster.Length, out var source), Is.True);
        Assert.That(source, Is.EqualTo(cluster.Length + 1));
        using var copy = view.BeginCopy();
        Assert.That(copy.ReadText(default), Is.EqualTo(text));
    }

    [TestCase(0, 0)]
    [TestCase(DocumentGraphemeIndex.WindowChars, 1)]
    public void UnavailableGraphemeRangeDoesNotProduceMappablePartialText(int extraMarks, int horizontal)
    {
        var text = "e\u0301\u0301" + new string('\u0301', extraMarks);
        using var handle = Create(text, collectGraphemeIndex: true);
        using var view = new DocumentDisplaySession(handle);
        view.SetViewport(1, horizontal);
        var projection = view.CreateRowProjection(1, 2);
        Assert.That(projection.Text, Is.Empty);
        Assert.That(projection.Rows[0].IsGraphemeRangeUnavailable, Is.True);
        Assert.That(projection.TrySourcePosition(0, out _), Is.False);
        Assert.That(projection.TryDisplayPosition(projection.Rows[0].SourceOffset, out _), Is.False);
    }

    [Test]
    public void SelectionUsesSeparateTextSpansAndCopyRetainsOriginalDelimitersAfterClosure()
    {
        const string source = "abc\r\ndef\rghi\n";
        var handle = Create(source);
        var view = new DocumentDisplaySession(handle);
        view.Select(source.Length, 0);
        var projection = view.CreateRowProjection(4, 2);
        Assert.That(view.TryGetRowSelection(projection, out var selection), Is.True);
        Assert.That(selection!.Spans.ToArray(), Is.EqualTo(new[]
        { new DocumentProjectionSelection(0, 2), new DocumentProjectionSelection(3, 2), new DocumentProjectionSelection(6, 2) }));
        Assert.That(selection.CaretOffset, Is.Zero);
        using var copy = view.BeginCopy();
        view.ScrollViewport(long.MaxValue, long.MaxValue);
        Assert.That((view.Anchor, view.Caret), Is.EqualTo(((long)source.Length, 0L)));
        view.Dispose(); handle.Dispose();
        Assert.That(copy.ReadText(CancellationToken.None), Is.EqualTo(source));
        Assert.Throws<ObjectDisposedException>(() => view.TryMoveFromRows(projection, 0));
    }

    [Test]
    public void StaleOtherAndModeSwitchedProjectionsCannotChangeTheSourceState()
    {
        using var handle = Create("abc\r\ndef");
        using var first = new DocumentDisplaySession(handle);
        using var second = new DocumentDisplaySession(handle);
        var old = first.CreateRowProjection(2, 2);
        var other = second.CreateRowProjection(2, 2);
        var current = first.CreateRowProjection(2, 2);
        Assert.That(first.TryMoveFromRows(old, 1), Is.False);
        Assert.That(first.TryMoveFromRows(other, 1), Is.False);
        Assert.That(first.TryMoveFromRows(current, 1), Is.True);
        var slice = first.CreateViewportSlice(0, 8, 2);
        Assert.That(first.TryMoveFromRows(current, 0), Is.False);
        current = first.CreateRowProjection(2, 2);
        Assert.That(first.TryMoveFromDisplay(slice, 0), Is.False);
        first.ScrollViewport(1, 0);
        Assert.That(first.TryMoveFromRows(current, 0), Is.False);
        Assert.That(first.Caret, Is.EqualTo(1));
    }

    [Test]
    public void RowBudgetIsSharedAcrossVisibleLinesAndNeverScalesWithSourceLength()
    {
        using var handle = Create(string.Join('\n', Enumerable.Repeat(new string('x', 100), 600)));
        using var view = new DocumentDisplaySession(handle);
        var projection = view.CreateRowProjection(512, 16384);
        Assert.That(projection.Rows.Length, Is.EqualTo(512));
        Assert.That(projection.Text.Length, Is.LessThanOrEqualTo(16384));
        Assert.That(projection.EffectiveColumns, Is.EqualTo(31));
        Assert.That(projection.Rows[^1].SourceLine, Is.EqualTo(512));
    }

    [Test]
    public void InvalidViewportAndProjectionRequestsAreAtomicAndOverflowSafe()
    {
        using var handle = Create("abc\r\ndef");
        using var view = new DocumentDisplaySession(handle);
        var current = view.CreateRowProjection(2, 2);
        foreach (var (line, column) in new[] { (0L, 0L), (3L, 0L), (1L, -1L), (1L, long.MaxValue) })
            Assert.Throws<ArgumentOutOfRangeException>(() => view.SetViewport(line, column));
        foreach (var (rows, columns, budget) in new[] { (0, 2, 16), (513, 2, 16), (2, 1, 16), (2, 16385, 16), (2, 2, 3), (2, 2, 16385) })
            Assert.Throws<ArgumentOutOfRangeException>(() => view.CreateRowProjection(rows, columns, budget));
        Assert.Throws<ArgumentException>(() => view.CreateRowProjection(2, 2, 4));
        Assert.That(view.TryGetRowSelection(current, out _), Is.True);
        view.ScrollViewport(long.MaxValue, long.MaxValue);
        Assert.That((view.FirstVisibleLine, view.HorizontalSourceOffset), Is.EqualTo((2L, view.Length)));
        view.ScrollViewport(long.MinValue, long.MinValue);
        Assert.That((view.FirstVisibleLine, view.HorizontalSourceOffset), Is.EqualTo((1L, 0L)));
    }

    [Test]
    public void EntirelyLeftClippedLineDoesNotPretendItsCaretIsVisible()
    {
        using var handle = Create("a\r\n");
        using var view = new DocumentDisplaySession(handle);
        view.MoveCaret(1);
        view.SetViewport(1, 3);
        var projection = view.CreateRowProjection(2, 4);
        Assert.That(projection.Rows[0].IsOutsideHorizontalRange, Is.True);
        Assert.That(projection.TryDisplayPosition(1, out _), Is.False);
        Assert.That(projection.TrySourcePosition(0, out _), Is.False);
        Assert.That(view.TryGetRowSelection(projection, out var selection), Is.True);
        Assert.That(selection!.CaretOffset, Is.Null);
        view.RevealCaret(2, 4);
        projection = view.CreateRowProjection(2, 4);
        Assert.That(projection.TryDisplayPosition(1, out _), Is.True);
    }

    [Test]
    public void BoundedRangeFillPreservesExactTextAndRejectsInvalidRangesBeforeWriting()
    {
        using var handle = Create("a😀\r\nbc");
        var lease = handle.AcquireReadView();
        var target = new char[5];
        lease.CopyRangeInto(1, target);
        Assert.That(new string(target), Is.EqualTo("😀\r\nb"));
        lease.CopyRangeInto(lease.Length, Span<char>.Empty);
        var unchanged = new string(target);
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.CopyRangeInto(-1, target));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.CopyRangeInto(long.MaxValue, target));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.CopyRangeInto(lease.Length - 1, target));
        Assert.That(new string(target), Is.EqualTo(unchanged));
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.CopyRangeInto(0, target));
    }

    [Test]
    public void BoundedRangeFillAlsoWorksForNonRopeSnapshots()
    {
        var released = false;
        using (var lease = new DocumentReadLease(new AvaloniaEdit.Document.StringTextSource("a😀b"), () => released = true))
        {
            var target = new char[2];
            lease.CopyRangeInto(1, target);
            Assert.That(new string(target), Is.EqualTo("😀"));
            Assert.That(lease.ReadRange(0, 4), Is.EqualTo("a😀b"));
        }
        Assert.That(released, Is.True);
        using var handle = Create(new string('x', DocumentReadLease.ChunkChars * 2));
        using var large = handle.AcquireReadView();
        var tooLarge = new char[DocumentReadLease.ChunkChars + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => large.CopyRangeInto(0, tooLarge));
        Assert.That(tooLarge.All(c => c == '\0'), Is.True);
    }

    [Test]
    public void AllVisiblePositionsRoundTripAcrossMixedDelimitersUnicodeAndHorizontalWindows()
    {
        const string source = "a😀b\r\n\r\n12\t中\rcdef\nlast";
        using var handle = Create(source);
        using var view = new DocumentDisplaySession(handle);
        for (var line = 1; line <= 5; line++)
        for (var horizontal = 0; horizontal <= source.Length; horizontal++)
        foreach (var columns in new[] { 2, 3, 4, 7 })
        {
            view.SetViewport(line, horizontal);
            var projection = view.CreateRowProjection(3, columns, 32);
            for (var display = 0; display <= projection.Text.Length; display++)
                if (projection.TrySourcePosition(display, out var original))
                {
                    Assert.That(projection.TryDisplayPosition(original, out var roundTrip), Is.True);
                    Assert.That(roundTrip, Is.EqualTo(display));
                }
            for (var original = 0; original <= source.Length; original++)
                if (projection.TryDisplayPosition(original, out var display))
                {
                    Assert.That(projection.TrySourcePosition(display, out var roundTrip), Is.True);
                    Assert.That(roundTrip, Is.EqualTo(original));
                }
            for (var display = 0; display < projection.Text.Length; display++)
                if (projection.TrySourceCharacter(display, out var original))
                    Assert.That(projection.Text[display], Is.EqualTo(source[(int)original]));
        }
    }

    [Test]
    public void ProjectionRetainsOnlyItsBoundedTextAfterSourceClosure()
    {
        var (projection, document) = CreateClosedProjection();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.That(document.IsAlive, Is.False);
        Assert.That(projection.Text.Length, Is.LessThanOrEqualTo(16));
        var rows = projection.Rows.ToArray();
        rows[0] = rows[0] with { SourceOffset = long.MaxValue };
        Assert.That(projection.Rows[0].SourceOffset, Is.Zero);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DocumentRowProjection, WeakReference) CreateClosedProjection()
    {
        using var handle = Create(new string('x', 1024 * 1024));
        using var view = new DocumentDisplaySession(handle);
        return (view.CreateRowProjection(1, 16), new WeakReference(handle.ClaimForDisplay()));
    }

    [Test]
    public void RowProjectionRequiresTheExistingOwnerThread()
    {
        using var handle = Create("abc");
        using var view = new DocumentDisplaySession(handle);
        Task.Run(() => Assert.Throws<InvalidOperationException>(() => view.CreateRowProjection(1, 2))).GetAwaiter().GetResult();
    }

    private static DocumentHandle Create(string text, bool collectGraphemeIndex = false)
    {
        using var builder = new AvaloniaEditDocumentAdapter(collectGraphemeIndex: collectGraphemeIndex).CreateBuilder();
        builder.Append(text.AsMemory());
        return builder.Complete();
    }
}
