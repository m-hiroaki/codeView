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
        Assert.Throws<ArgumentException>(() => index.TryGetBounds(other, 0, out _, out _));
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
