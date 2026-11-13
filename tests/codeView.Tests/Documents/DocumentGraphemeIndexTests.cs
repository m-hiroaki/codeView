using System.Globalization;
using CodeView.Documents;

namespace CodeView.Tests.Documents;

public sealed class DocumentGraphemeIndexTests
{
    [TestCase("")]
    [TestCase("a\r\nb\rc\n\t日本語")]
    [TestCase("Ae\u0301B👩‍💻🇯🇵각क्‍ष\u0600a سلام אבג")]
    [TestCase("\ud800a\udc00\ud800\ud800\udc00")]
    public void EveryOffsetMatchesWholeStringOracle(string text) => Verify(text, everyOffset: true);

    [TestCase("e\u0301")]
    [TestCase("👩‍💻")]
    [TestCase("🇯🇵🇺🇸🇨")]
    [TestCase("각क्‍ष\u0600a")]
    [TestCase("\r\n")]
    public void RestartsAndWindowSeamsMatchOracle(string element)
    {
        var repeated = string.Concat(Enumerable.Repeat(element, 18000));
        Verify(new string('x', DocumentGraphemeIndex.WindowChars - 3) + repeated, everyOffset: false);
    }

    [TestCase(DocumentGraphemeIndex.WindowChars - 1, true)]
    [TestCase(DocumentGraphemeIndex.WindowChars, true)]
    [TestCase(DocumentGraphemeIndex.WindowChars + 1, false)]
    public void HugeClusterHasAnExplicitUnresolvedSuffix(int clusterLength, bool complete)
    {
        var text = "prefix" + "a" + new string('\u0301', clusterLength - 1);
        using var builder = new DocumentBuilder();
        builder.Append(text.AsMemory());
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        var index = DocumentGraphemeIndex.Build(lease, default);
        Assert.That(index.IsComplete, Is.EqualTo(complete));
        Assert.That(index.TryGetBounds(lease, text.Length, out _, out _), Is.EqualTo(complete));
        if (!complete)
        {
            Assert.That(index.IndexedLength, Is.EqualTo(6));
            Assert.That(index.TryGetBounds(lease, 6, out var left, out var right), Is.True);
            Assert.That((left, right), Is.EqualTo((6L, 6L)));
            Assert.That(index.TryGetBounds(lease, 7, out _, out _), Is.False);
        }
    }

    [Test]
    public void IdentityDisposalCancellationAndRangesAreChecked()
    {
        using var builder = new DocumentBuilder();
        builder.Append("text".AsMemory());
        using var handle = builder.Complete();
        using var source = handle.AcquireReadView();
        using var other = handle.AcquireReadView();
        var index = DocumentGraphemeIndex.Build(source, default);
        Assert.That(index.TryGetBounds(other, 0, out _, out _), Is.True);
        using var foreign = new DocumentReadLease(new AvaloniaEdit.Document.StringTextSource("text"), static () => { });
        Assert.Throws<ArgumentException>(() => index.TryGetBounds(foreign, 0, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.TryGetBounds(source, -1, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.TryGetBounds(source, 5, out _, out _));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DocumentGraphemeIndex.Build(source, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => index.TryGetBounds(source, 0, out _, out _, cancellation.Token));
        handle.Dispose();
        Assert.That(index.TryGetBounds(source, 2, out _, out _), Is.True, "existing immutable lease survives close");
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => index.TryGetBounds(source, 0, out _, out _));
        Assert.Throws<ObjectDisposedException>(() => DocumentGraphemeIndex.Build(source, default));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DisplaySessionQueriesTheIndexBuiltOnTheWorker(bool collect)
    {
        using var handle = await Task.Run(() =>
        {
            using var builder = new DocumentBuilder(collectGraphemeIndex: collect);
            builder.Append("e\u0301x".AsMemory());
            return builder.Complete();
        });
        using var session = new DocumentDisplaySession(handle);
        Assert.That(session.TryGetGraphemeBounds(1, out var before, out var after), Is.EqualTo(collect));
        if (collect) Assert.That((before, after), Is.EqualTo((0L, 2L)));
        session.Dispose();
        Assert.That(handle.ActiveReadLeases, Is.Zero);
        Assert.Throws<ObjectDisposedException>(() => session.TryGetGraphemeBounds(1, out _, out _));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BuilderCollectsOnlyWhenEnabledAndIndexRejectsNewVersions(bool collect)
    {
        using var builder = new AvaloniaEditDocumentAdapter(collectGraphemeIndex: collect).CreateBuilder();
        builder.Append("e\u0301x".AsMemory());
        using var handle = builder.Complete();
        Assert.That(handle.ActiveReadLeases, Is.Zero);
        if (!collect)
        {
            Assert.That(handle.GraphemeIndex, Is.Null);
            return;
        }

        var index = handle.GraphemeIndex!;
        using var original = handle.AcquireReadView();
        using var otherBuilder = new DocumentBuilder();
        otherBuilder.Append("e\u0301x".AsMemory());
        using var otherHandle = otherBuilder.Complete();
        using var other = otherHandle.AcquireReadView();
        Assert.Throws<ArgumentException>(() => index.TryGetBounds(other, 1, out _, out _));
        handle.ClaimForDisplay().Replace(0, 2, "ab");
        using var newer = handle.AcquireReadView();
        Assert.Throws<ArgumentException>(() => index.TryGetBounds(newer, 1, out _, out _));
        handle.Dispose();
        Assert.That(handle.GraphemeIndex, Is.Null);
        Assert.That(index.TryGetBounds(original, 1, out var before, out var after), Is.True);
        Assert.That((before, after), Is.EqualTo((0L, 2L)));
    }

    [Test]
    public void CancelledBuildDoesNotPublishAndCanCompleteOnTheSameWorker()
    {
        using var builder = new DocumentBuilder(collectGraphemeIndex: true);
        builder.Append("e\u0301x".AsMemory());
        Assert.Throws<OperationCanceledException>(() => builder.Complete(new CancellationToken(true)));
        using var handle = builder.Complete();
        Assert.That(handle.GraphemeIndex?.IsComplete, Is.True);
        Assert.That(handle.ActiveReadLeases, Is.Zero);
    }

    private static void Verify(string text, bool everyOffset)
    {
        using var builder = new DocumentBuilder();
        for (var start = 0; start < text.Length; start += 701)
            builder.Append(text.AsMemory(start, Math.Min(701, text.Length - start)));
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        var index = DocumentGraphemeIndex.Build(lease, default);
        Assert.That(index.IsComplete, Is.True);
        Assert.That(index.StorageBytes, Is.LessThanOrEqualTo(DocumentGraphemeIndex.MaximumStorageBytes));
        var boundaries = StringInfo.ParseCombiningCharacters(text).Select(x => (long)x).Append(text.Length).ToArray();
        var offsets = everyOffset ? Enumerable.Range(0, text.Length + 1).Select(x => (long)x) :
            Enumerable.Range(0, text.Length / 193 + 1).Select(x => (long)x * 193)
                .Concat(boundaries.Where(x => x % 4096 < 20).SelectMany(x => new[] { x - 1, x, x + 1 }))
                .Append(text.Length).Where(x => x >= 0 && x <= text.Length).Distinct();
        foreach (var offset in offsets)
        {
            var found = Array.BinarySearch(boundaries, offset);
            var before = boundaries[found >= 0 ? found : ~found - 1];
            var after = boundaries[found >= 0 ? found : ~found];
            Assert.That(index.TryGetBounds(lease, offset, out var actualBefore, out var actualAfter), Is.True, "offset=" + offset);
            Assert.That((actualBefore, actualAfter), Is.EqualTo((before, after)), "offset=" + offset);
        }
        Assert.That(lease.ReadRange(0, text.Length), Is.EqualTo(text));
    }
}
