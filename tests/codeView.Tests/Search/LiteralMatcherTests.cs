using System.Globalization;
using System.Text;
using CodeView.Contracts;
using CodeView.Search;

namespace CodeView.Tests.Search;

internal sealed class LiteralMatcherTests
{
    private static List<TextRange> Scan(string text, string query, SearchOptions options, int chunk)
    {
        var result = LiteralMatcher.Create(query, options);
        Assert.That(result.Status, Is.EqualTo(OperationStatus.Success));
        var matcher = result.Value;
        var hits = new List<TextRange>();
        bool Consume(TextRange hit) { hits.Add(hit); return true; }
        for (var index = 0; index < text.Length; index += chunk)
            Assert.That(matcher.Push(text.AsSpan(index, Math.Min(chunk, text.Length - index)), Consume, CancellationToken.None), Is.True);
        Assert.That(matcher.Complete(Consume, CancellationToken.None), Is.True);
        Assert.That(matcher.IsFinished, Is.True);
        return hits;
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(7)]
    [TestCase(16_384)]
    public void WordSearchPositionsAreIndependentOfChunkBoundaries(int chunk)
    {
        var hits = Scan("foo food Foo\r\nfoo", "foo", new(WholeWord: true), chunk);
        Assert.That(hits.Select(hit => hit.Offset), Is.EqualTo(new long[] { 0, 9, 14 }));
        Assert.That(hits.Select(hit => hit.Start), Is.EqualTo(new[] { new TextPosition(1, 1), new TextPosition(1, 10), new TextPosition(2, 1) }));
        Assert.That(Scan("foo food Foo\r\nfoo", "foo", new(MatchCase: true, WholeWord: true), chunk).Select(hit => hit.Offset), Is.EqualTo(new long[] { 0, 14 }));
    }

    [TestCase("aaaaa", "aa", new long[] { 0, 2 })]
    [TestCase("ababa", "aba", new long[] { 0 })]
    [TestCase("aabaaaab", "aab", new long[] { 0, 5 })]
    [TestCase(" a  a ", " ", new long[] { 0, 2, 3, 5 })]
    [TestCase("x\r\nx\rx\nx", "x", new long[] { 0, 3, 5, 7 })]
    [TestCase("a\r\nb", "ab", new long[] { })]
    [TestCase("", "x", new long[] { })]
    public void NonOverlappingAndWhitespaceQueriesMatchExpectedOffsets(string text, string query, long[] offsets)
    {
        Assert.That(Scan(text, query, default, 1).Select(hit => hit.Offset), Is.EqualTo(offsets));
    }

    [Test]
    public void CrLfCrAndLfKeepOriginalUtf16Coordinates()
    {
        var hits = Scan("a😀b\r\nが\nか\u3099\rが", "が", default, 1);
        Assert.That(hits.Select(hit => hit.Offset), Is.EqualTo(new long[] { 6, 11 }));
        Assert.That(hits.Select(hit => hit.Start), Is.EqualTo(new[] { new TextPosition(2, 1), new TextPosition(4, 1) }));
        var decomposed = Scan("a😀b\r\nが\nか\u3099", "か\u3099", default, 1).Single();
        Assert.That(decomposed, Is.EqualTo(new TextRange(8, 2, new(3, 1))));
        Assert.That(Scan("か\u3099", "が", default, 1), Is.Empty);
        Assert.That(Scan("a😀b", "b", default, 1).Single().Start, Is.EqualTo(new TextPosition(1, 4)));
    }

    [TestCase("a", false)]
    [TestCase("9", false)]
    [TestCase("_", false)]
    [TestCase("日", false)]
    [TestCase("\u2163", false)]
    [TestCase("\u0301", false)]
    [TestCase("\u0903", false)]
    [TestCase("\u200D", false)]
    [TestCase("\U00010400", false)]
    [TestCase("@", true)]
    [TestCase("-", true)]
    [TestCase(" ", true)]
    [TestCase("😀", true)]
    [TestCase("\u00B2", true)]
    public void WholeWordUsesTheSpecifiedUnicodeScalarCategories(string neighbor, bool boundary)
    {
        foreach (var text in new[] { neighbor + "foo", "foo" + neighbor, neighbor + "foo" + neighbor })
            Assert.That(Scan(text, "foo", new(WholeWord: true), 1).Count, Is.EqualTo(boundary ? 1 : 0), text);
    }

    [Test]
    public void SupplementaryCaseAndCultureFollowOrdinalIgnoreCase()
    {
        Assert.That(Scan("\U00010400 \U00010428", "\U00010400", default, 1).Select(hit => hit.Offset), Is.EqualTo(new long[] { 0, 3 }));
        Assert.That(Scan("\U00010400 \U00010428", "\U00010400", new(MatchCase: true), 1).Count, Is.EqualTo(1));
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            const string text = "FILE file FıLE fİle \u017F s K k";
            foreach (var query in new[] { "file", "s", "k" })
                Assert.That(Scan(text, query, default, 1).Select(hit => hit.Offset), Is.EqualTo(Oracle(text, query, StringComparison.OrdinalIgnoreCase)));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("x\ny")]
    [TestCase("\r")]
    public void InvalidQueryIsRejectedBeforeScanning(string? query)
        => Assert.That(LiteralMatcher.Create(query).Error!.Code, Is.EqualTo(ErrorCode.InvalidPattern));

    [Test]
    public void UnpairedSurrogatesAreRejectedWithoutAttributeEncodingReplacement()
    {
        foreach (var value in new[] { '\uD83D', '\uDE00' })
            Assert.That(LiteralMatcher.Create(new string(value, 1)).Error!.Code, Is.EqualTo(ErrorCode.InvalidPattern));
    }

    [Test]
    public void QueryLimitIsExactAndRegexIsNotSentToLiteralMatcher()
    {
        Assert.That(LiteralMatcher.Create(new('x', SearchLimits.QueryChars - 1)).Status, Is.EqualTo(OperationStatus.Success));
        Assert.That(LiteralMatcher.Create(new('x', SearchLimits.QueryChars)).Status, Is.EqualTo(OperationStatus.Success));
        Assert.That(LiteralMatcher.Create(new('x', SearchLimits.QueryChars + 1)).Error!.Code, Is.EqualTo(ErrorCode.InvalidPattern));
        Assert.That(LiteralMatcher.Create("x", new(UseRegex: true)).Status, Is.EqualTo(OperationStatus.Failure));
    }

    [TestCase(1)]
    [TestCase(256)]
    [TestCase(16_384)]
    [TestCase(16_385)]
    public void MaximumQueryAndCompactionKeepAllMatchesAndSmallStorage(int chunk)
    {
        var query = new string('a', SearchLimits.QueryChars - 1) + "b";
        var text = new string('x', 17_000) + query + "\r\n" + query;
        Assert.That(Scan(text, query, default, chunk).Select(hit => hit.Offset), Is.EqualTo(new long[] { 17_000, 17_000 + query.Length + 2 }));
        var matcher = LiteralMatcher.Create(query).Value;
        Assert.That(matcher.BufferCapacity, Is.LessThanOrEqualTo(2 * SearchLimits.QueryChars + 4));
    }

    [Test]
    public void WholeWordSurrogateContextSurvivesCompaction()
    {
        var prefix = new string(' ', SearchLimits.LiteralScanBlockChars - 1);
        var text = prefix + "\U00010400foo foo😀 foo\U00010400 @foo";
        var expected = new long[] { prefix.Length + 6, prefix.Length + 19 };
        Assert.That(Scan(text, "foo", new(WholeWord: true), 1).Select(hit => hit.Offset), Is.EqualTo(expected));
        Assert.That(Scan(text, "😀", new(WholeWord: false), 1).Single().Length, Is.EqualTo(2));
    }

    [Test]
    public void WordAtTailWaitsForContextAndAbortDoesNotTreatAnIoFailureAsEof()
    {
        var matcher = LiteralMatcher.Create("foo", new(WholeWord: true)).Value;
        var hits = new List<TextRange>();
        bool Consume(TextRange hit) { hits.Add(hit); return true; }
        matcher.Push("foo", Consume, CancellationToken.None);
        Assert.That(hits, Is.Empty);
        Assert.Throws<InvalidOperationException>(matcher.Reset);
        matcher.Abort();
        Assert.That(hits, Is.Empty);
        matcher.Reset();
        matcher.Push("foo", Consume, CancellationToken.None);
        matcher.Complete(Consume, CancellationToken.None);
        Assert.That(hits.Single(), Is.EqualTo(new TextRange(0, 3, new(1, 1))));
        Assert.Throws<InvalidOperationException>(() => matcher.Push("foo", Consume, CancellationToken.None));
    }

    [Test]
    public void StopCancellationAndConsumerFaultAreTerminalAndResetClearsAllPositionState()
    {
        var matcher = LiteralMatcher.Create("foo").Value;
        var calls = 0;
        Assert.That(matcher.Push("foofoo", _ => { calls++; return false; }, CancellationToken.None), Is.False);
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(matcher.IsFinished, Is.True);
        matcher.Reset();
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => matcher.Push("foo foo", _ => { cancellation.Cancel(); return true; }, cancellation.Token));
        Assert.That(matcher.IsFinished, Is.True);
        matcher.Reset();
        var expected = new IOException("consumer failed");
        Assert.That(Assert.Throws<IOException>(() => matcher.Push("foo", _ => throw expected, CancellationToken.None)), Is.SameAs(expected));
        matcher.Reset();
        matcher.Push("foo\r", _ => true, CancellationToken.None);
        matcher.Complete(_ => true, CancellationToken.None);
        matcher.Reset();
        TextRange final = default;
        matcher.Push("foo", hit => { final = hit; return true; }, CancellationToken.None);
        matcher.Complete(_ => true, CancellationToken.None);
        Assert.That(final, Is.EqualTo(new TextRange(0, 3, new(1, 1))));
    }

    [Test]
    public void SeededInputsAgreeWithIndependentWholeStringComparison()
    {
        var random = new Random(20261031);
        var alphabet = new[] { "a", "b", "A", " ", "日", "😀", "\r", "\n", "\u0301", "\U00010400", "\U00010428" };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var text = string.Concat(Enumerable.Range(0, 100).Select(_ => alphabet[random.Next(alphabet.Length)]));
            var query = attempt % 2 == 0 ? "a" : "\U00010400";
            foreach (var matchCase in new[] { false, true })
                Assert.That(Scan(text, query, new(MatchCase: matchCase), random.Next(1, 17)).Select(hit => hit.Offset),
                    Is.EqualTo(Oracle(text, query, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static List<long> Oracle(string text, string query, StringComparison comparison)
    {
        var offsets = new List<long>();
        var index = 0;
        while (index <= text.Length - query.Length)
        {
            index = text.IndexOf(query, index, comparison);
            if (index < 0) break;
            offsets.Add(index);
            index += query.Length;
        }
        return offsets;
    }
}
