using System.Globalization;
using System.Text.RegularExpressions;
using CodeView.Contracts;
using CodeView.Search;

namespace CodeView.Tests.Search;

internal sealed class RegexLineMatcherTests
{
    private static (List<TextRange> Hits, RegexLineMatcher Matcher) Scan(string text, string pattern, int chunk = 1,
        bool matchCase = false, bool wholeWord = false, int lineLimit = 128)
    {
        var matcher = RegexLineMatcher.Create(pattern, new(matchCase, wholeWord, UseRegex: true), lineLimit).Value;
        var hits = new List<TextRange>();
        Func<TextRange, bool> consume = hit => { hits.Add(hit); return true; };
        for (var index = 0; index < text.Length; index += chunk)
            Assert.That(matcher.Push(text.AsSpan(index, Math.Min(chunk, text.Length - index)), consume, CancellationToken.None), Is.True);
        Assert.That(matcher.Complete(consume, CancellationToken.None), Is.True);
        Assert.That(matcher.Error, Is.Null);
        return (hits, matcher);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(16)]
    [TestCase(16_384)]
    public void NewlinesAndUtf16PositionsAreIndependentOfChunkBoundaries(int chunk)
    {
        var result = Scan("a😀b\r\nが\nか\u3099\rが", "b|が|か\u3099", chunk);
        Assert.That(result.Hits.Select(hit => hit.Offset), Is.EqualTo(new long[] { 3, 6, 8, 11 }));
        Assert.That(result.Hits.Select(hit => hit.Start), Is.EqualTo(new[] { new TextPosition(1, 4), new TextPosition(2, 1), new TextPosition(3, 1), new TextPosition(4, 1) }));
        Assert.That(result.Matcher.EvaluatedLines, Is.EqualTo(4));
        Assert.That(Scan("a\r\nb", @"a\s+b", chunk).Hits, Is.Empty);
    }

    [TestCase("", new long[] { 0 })]
    [TestCase("\r\n", new long[] { 0, 2 })]
    [TestCase("\r\r\n", new long[] { 0, 1, 3 })]
    [TestCase("\n\r", new long[] { 0, 1, 2 })]
    public void EmptyAndFinalEmptyLinesAreEvaluatedOnce(string text, long[] offsets)
    {
        var result = Scan(text, "^$");
        Assert.That(result.Hits.Select(hit => hit.Offset), Is.EqualTo(offsets));
        Assert.That(result.Hits.All(hit => hit.Length == 0), Is.True);
        Assert.That(result.Matcher.EvaluatedLines, Is.EqualTo(offsets.Length));
    }

    [Test]
    public void AnchorsLookbehindAndBackreferencesUseTheWholeOriginalLine()
    {
        Assert.That(Scan("x\r\nx\ry\n", "$", 1).Hits.Select(hit => hit.Offset), Is.EqualTo(new long[] { 1, 4, 6, 7 }));
        Assert.That(Scan("ab ab ab", "(?<=a)b").Hits.Select(hit => hit.Offset), Is.EqualTo(new long[] { 1, 4, 7 }));
        Assert.That(Scan("ab ab ab", @"(?<n>ab)\s+\k<n>").Hits.Single(), Is.EqualTo(new TextRange(0, 5, new(1, 1))));
        const string pattern = @"\G(?=.)";
        Assert.That(Scan("abc", pattern).Hits.Select(hit => hit.Offset),
            Is.EqualTo(new Regex(pattern, RegexOptions.CultureInvariant).Matches("abc").Select(match => (long)match.Index)));
    }

    [Test]
    public void ZeroLengthProgressSkipsSurrogateInteriorAndPreservesEndMatch()
    {
        var result = Scan("a😀b", "(?=)");
        Assert.That(result.Hits.Select(hit => hit.Offset), Is.EqualTo(new long[] { 0, 1, 3, 4 }));
        Assert.That(result.Matcher.SkippedScalarMatches, Is.Zero);
        Assert.That(Scan("😀", "$|(?=😀)").Hits.Select(hit => hit.Offset), Is.EqualTo(new long[] { 0, 2 }));
    }

    [Test]
    public void NonScalarResultsAreReportedAndNeverExpandedIntoValidSelections()
    {
        var dot = Scan("a😀b", ".");
        Assert.That(dot.Hits.Select(hit => hit.Offset), Is.EqualTo(new long[] { 0, 3 }));
        Assert.That(dot.Matcher.SkippedScalarMatches, Is.EqualTo(2));
        Assert.That(Scan("😀", "😀").Hits.Single(), Is.EqualTo(new TextRange(0, 2, new(1, 1))));
        Assert.That(Scan("😀", @"[\uD800-\uDFFF]").Matcher.SkippedScalarMatches, Is.EqualTo(2));
        Assert.That(Scan("😀", @"(?<=\uD83D)").Matcher.SkippedScalarMatches, Is.EqualTo(1));
        var unpairedPattern = new string('\uD83D', 1);
        Assert.That(Scan("😀", unpairedPattern).Hits, Is.Empty);
    }

    [Test]
    public void WholeWordUsesTheSameOuterScalarCategoriesAsLiteralSearch()
    {
        foreach (var neighbor in new[] { "a", "_", "\u0301", "\u0903", "\u200D", "\U00010400" })
            Assert.That(Scan(neighbor + "foo foo" + neighbor, "foo", wholeWord: true).Hits, Is.Empty, neighbor);
        Assert.That(Scan("@foo foo😀", "foo", wholeWord: true).Hits.Count, Is.EqualTo(2));
        Assert.That(Scan("foo food Foo\r\nfoo", "foo", wholeWord: true).Hits.Select(hit => hit.Offset), Is.EqualTo(new long[] { 0, 9, 14 }));
        Assert.That(Scan("foo food Foo\r\nfoo", "foo", matchCase: true, wholeWord: true).Hits.Count, Is.EqualTo(2));
    }

    [Test]
    public void InlineCaseAndInvariantCultureAreRetainedWithoutUnicodeNormalization()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.That(Scan("FILE file", "file").Hits.Count, Is.EqualTo(2));
            Assert.That(Scan("FOO foo", "(?-i)foo").Hits.Count, Is.EqualTo(1));
            Assert.That(Scan("FOO foo", "(?i)foo", matchCase: true).Hits.Count, Is.EqualTo(2));
            Assert.That(Scan("か\u3099", "が").Hits, Is.Empty);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Test]
    public void WholeWordFiltersOriginalMatchesWithoutChangingAlternationPriority()
    {
        Assert.That(Scan("foobar", "foo|foobar", wholeWord: true).Hits, Is.Empty);
        Assert.That(Scan("foobar", "foobar|foo", wholeWord: true).Hits.Single().Length, Is.EqualTo(6));
    }

    [TestCase(3, 1, 0)]
    [TestCase(4, 1, 0)]
    [TestCase(5, 0, 1)]
    public void LineLimitNeverSearchesOnlyThePrefix(int length, int hits, int skipped)
    {
        var result = Scan(new string('a', length), "^a+", lineLimit: 4);
        Assert.That(result.Hits.Count, Is.EqualTo(hits));
        Assert.That(result.Matcher.SkippedLongLines, Is.EqualTo(skipped));
        Assert.That(result.Matcher.BufferedCharacters, Is.Zero);
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(1)]
    public void ActualOneMiLimitIsBoundedAndNextLinePositionsStayCorrect(int difference)
    {
        var count = SearchLimits.RegexLineChars + difference;
        var result = Scan(new string('a', count) + "\r\nx", "^a+$|x", 16_384, lineLimit: SearchLimits.RegexLineChars);
        Assert.That(result.Matcher.SkippedLongLines, Is.EqualTo(difference > 0 ? 1 : 0));
        Assert.That(result.Hits.Count, Is.EqualTo(difference > 0 ? 1 : 2));
        Assert.That(result.Hits[^1], Is.EqualTo(new TextRange(count + 2, 1, new(2, 1))));
        Assert.That(result.Matcher.BufferCapacity, Is.EqualTo(SearchLimits.RegexLineChars));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("[")]
    [TestCase("(?<bad")]
    public void InvalidPatternFailsBeforeInputIsRead(string? pattern)
        => Assert.That(RegexLineMatcher.Create(pattern, new(UseRegex: true)).Error!.Code, Is.EqualTo(ErrorCode.InvalidPattern));

    [Test]
    public void QueryAndTimeoutConfigurationHaveFiniteBounds()
    {
        Assert.That(RegexLineMatcher.Create(new string('x', SearchLimits.QueryChars), new(UseRegex: true), 4).Status, Is.EqualTo(OperationStatus.Success));
        Assert.That(RegexLineMatcher.Create(new string('x', SearchLimits.QueryChars + 1), new(UseRegex: true)).Error!.Code, Is.EqualTo(ErrorCode.InvalidPattern));
        Assert.That(RegexLineMatcher.Create("x", default).Status, Is.EqualTo(OperationStatus.Failure));
        Assert.That(RegexLineMatcher.Create("x", new(UseRegex: true), 4).Value.MatchTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(100)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RegexLineMatcher.Create("x", new(UseRegex: true), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RegexLineMatcher.Create("x", new(UseRegex: true), timeout: Regex.InfiniteMatchTimeout));
        Assert.Throws<ArgumentOutOfRangeException>(() => RegexLineMatcher.Create("x", new(UseRegex: true), timeout: TimeSpan.Zero));
    }

    [Test]
    public void TimeoutStopsTheSessionAndCannotBeResetToContinueOtherFiles()
    {
        var matcher = RegexLineMatcher.Create("^(a+)+$", new(UseRegex: true), timeout: TimeSpan.FromMilliseconds(1)).Value;
        matcher.Push(new string('a', 30_000) + "!", _ => true, CancellationToken.None);
        Assert.That(matcher.Complete(_ => true, CancellationToken.None), Is.False);
        Assert.That(matcher.Error!.Code, Is.EqualTo(ErrorCode.RegexTimedOut));
        Assert.That(matcher.IsFinished, Is.True);
        Assert.That(matcher.BufferedCharacters, Is.Zero);
        Assert.Throws<InvalidOperationException>(matcher.Reset);
    }

    [Test]
    public void AbortResetStopAndConsumerFailureHaveDistinctOutcomes()
    {
        var matcher = RegexLineMatcher.Create("foo", new(UseRegex: true), 4).Value;
        var hits = new List<TextRange>();
        Func<TextRange, bool> consume = hit => { hits.Add(hit); return true; };
        matcher.Push("foo", consume, CancellationToken.None);
        matcher.Abort();
        Assert.That(hits, Is.Empty);
        matcher.Reset();
        matcher.Push("foo\r", consume, CancellationToken.None);
        matcher.Complete(consume, CancellationToken.None);
        matcher.Reset();
        matcher.Push("foo", consume, CancellationToken.None);
        matcher.Complete(consume, CancellationToken.None);
        Assert.That(hits.All(hit => hit.Start == new TextPosition(1, 1) && hit.Offset == 0), Is.True);
        matcher.Reset();
        Assert.That(matcher.Push("foo\nfoo", _ => false, CancellationToken.None), Is.False);
        Assert.That(matcher.Error, Is.Null);
        matcher.Reset();
        var expected = new RegexMatchTimeoutException("consumer failed");
        Assert.That(Assert.Throws<RegexMatchTimeoutException>(() => matcher.Push("foo\n", _ => throw expected, CancellationToken.None)), Is.SameAs(expected));
        Assert.That(matcher.Error, Is.Null, "Consumer faults must not be converted to engine timeouts.");
    }

    [Test]
    public void CancellationIsObservedBeforeInputAndAfterEachResult()
    {
        var matcher = RegexLineMatcher.Create("foo", new(UseRegex: true), 16).Value;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => matcher.Push("foo", _ => true, cancelled.Token));
        matcher.Reset();
        using var during = new CancellationTokenSource();
        var count = 0;
        Assert.Throws<OperationCanceledException>(() => matcher.Push("foo foo\n", _ => { count++; during.Cancel(); return true; }, during.Token));
        Assert.That(count, Is.EqualTo(1));
        Assert.That(matcher.IsFinished, Is.True);
    }

    [Test]
    public void ResetDoesNotJoinCrLfOrCarrySkippedLineStateAcrossFiles()
    {
        var matcher = RegexLineMatcher.Create("^$|^x", new(UseRegex: true), 4).Value;
        matcher.Push("aaaaa\r", _ => true, CancellationToken.None);
        matcher.Complete(_ => true, CancellationToken.None);
        Assert.That(matcher.SkippedLongLines, Is.EqualTo(1));
        matcher.Reset();
        var hits = new List<TextRange>();
        Func<TextRange, bool> consume = hit => { hits.Add(hit); return true; };
        matcher.Push("\nx", consume, CancellationToken.None);
        matcher.Complete(consume, CancellationToken.None);
        Assert.That(hits.Select(hit => hit.Offset), Is.EqualTo(new long[] { 0, 1 }));
        Assert.That(hits.Select(hit => hit.Start), Is.EqualTo(new[] { new TextPosition(1, 1), new TextPosition(2, 1) }));
        Assert.That(matcher.SkippedLongLines, Is.Zero);
    }
}
