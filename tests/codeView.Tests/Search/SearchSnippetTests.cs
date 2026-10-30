using System.Text;
using CodeView.Contracts;
using CodeView.Search;

namespace CodeView.Tests.Search;

internal sealed class SearchSnippetTests
{
    [Test]
    public void CaptureKeepsActualSourceCaseAndOnlyCurrentLine()
    {
        var matcher = LiteralMatcher.Create("foo").Value;
        SearchSnippet? snippet = null;
        matcher.Push("previous\r\nleft FOO right\nnext", _ => { snippet = matcher.CaptureSnippet(); return true; }, default);
        Assert.That(snippet, Is.EqualTo(new SearchSnippet("left FOO right", 5, 3)));
        Assert.Throws<InvalidOperationException>(() => matcher.CaptureSnippet());
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(16384)]
    public void LiteralSnippetRemainsAlignedAcrossChunksAndCompaction(int chunk)
    {
        var text = new string('x', 40000) + "😀FoO😀 tail";
        var matcher = LiteralMatcher.Create("😀foo😀").Value;
        var count = 0;
        bool Consume(TextRange hit)
        {
            var snippet = matcher.CaptureSnippet();
            Assert.That(hit.Offset, Is.EqualTo(40000));
            Assert.That(snippet.Text.Substring(snippet.MatchStart, snippet.MatchLength), Is.EqualTo("😀FoO😀"));
            AssertValid(snippet);
            count++;
            return true;
        }
        for (var index = 0; index < text.Length; index += chunk)
            matcher.Push(text.AsSpan(index, Math.Min(chunk, text.Length - index)), Consume, default);
        matcher.Complete(Consume, default);
        Assert.That(count, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LongMatchesCopyOnlyBoundedActualSourcePrefix(bool regex)
    {
        var text = string.Concat(Enumerable.Repeat("😀", 5000));
        SearchSnippet snippet = default;
        if (regex)
        {
            var matcher = RegexLineMatcher.Create(".+", new(UseRegex: true)).Value;
            matcher.Push(text, _ => throw new AssertionException("行末前の通知"), default);
            matcher.Complete(_ => { snippet = matcher.CaptureSnippet(); return true; }, default);
        }
        else
        {
            var matcher = LiteralMatcher.Create(text).Value;
            matcher.Push(text, _ => { snippet = matcher.CaptureSnippet(); return true; }, default);
        }
        Assert.That(snippet.Text, Is.EqualTo(text[..200]));
        Assert.That(snippet.MatchLength, Is.EqualTo(200));
        Assert.That(snippet.MatchStart, Is.Zero);
        AssertValid(snippet);
    }

    [Test]
    public void RegexCanCaptureEarlyMatchesWhenWholeLineIsAvailable()
    {
        var matcher = RegexLineMatcher.Create("foo", new(UseRegex: true)).Value;
        var snippets = new List<SearchSnippet>();
        matcher.Push("FOO " + new string('x', 900000) + " FOO\r", _ =>
        { snippets.Add(matcher.CaptureSnippet()); return true; }, default);
        Assert.That(snippets.Count, Is.EqualTo(2));
        foreach (var snippet in snippets)
        {
            Assert.That(snippet.Text.Substring(snippet.MatchStart, snippet.MatchLength), Is.EqualTo("FOO"));
            AssertValid(snippet);
        }
        Assert.Throws<InvalidOperationException>(() => matcher.CaptureSnippet());
    }

    [TestCase("")]
    [TestCase("😀")]
    public void ZeroLengthAtEndHasValidEmptyHighlight(string text)
    {
        var matcher = RegexLineMatcher.Create("$", new(UseRegex: true)).Value;
        SearchSnippet snippet = default;
        matcher.Push(text, _ => true, default);
        matcher.Complete(_ => { snippet = matcher.CaptureSnippet(); return true; }, default);
        Assert.That(snippet, Is.EqualTo(new SearchSnippet(text, text.Length, 0)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CallbackFailureRevokesBorrowedStorageAccess(bool regex)
    {
        if (regex)
        {
            var matcher = RegexLineMatcher.Create("x", new(UseRegex: true)).Value;
            Assert.Throws<FormatException>(() => matcher.Push("x\n", _ => throw new FormatException(), default));
            Assert.Throws<InvalidOperationException>(() => matcher.CaptureSnippet());
        }
        else
        {
            var matcher = LiteralMatcher.Create("x").Value;
            Assert.Throws<FormatException>(() => matcher.Push("x", _ => throw new FormatException(), default));
            Assert.Throws<InvalidOperationException>(() => matcher.CaptureSnippet());
        }
    }

    [Test]
    public void BothClippedEdgesAvoidHalfSurrogates()
    {
        var text = "😀" + new string('a', 59) + "x" + new string('b', 137) + "😀";
        var snippet = SearchSnippet.Capture(text, 61, 1);
        Assert.That(snippet.MatchStart, Is.EqualTo(59));
        Assert.That(snippet.Text[snippet.MatchStart], Is.EqualTo('x'));
        AssertValid(snippet);
        var right = SearchSnippet.Capture(new string('x', 199) + "😀", 0, 1);
        Assert.That(right.Text.Length, Is.EqualTo(199));
        AssertValid(right);
    }

    [TestCase(-1, 0)]
    [TestCase(0, -1)]
    [TestCase(2, 0)]
    [TestCase(1, 1)]
    public void InvalidRangeIsRejected(int start, int length)
        => Assert.Throws<ArgumentOutOfRangeException>(() => SearchSnippet.Capture("x", start, length));

    private static void AssertValid(SearchSnippet snippet)
    {
        Assert.That(snippet.Text.Length, Is.LessThanOrEqualTo(SearchSnippet.MaxChars));
        Assert.That(snippet.MatchStart, Is.InRange(0, snippet.Text.Length));
        Assert.That(snippet.MatchLength, Is.InRange(0, snippet.Text.Length - snippet.MatchStart));
        Assert.DoesNotThrow(() => new UTF8Encoding(false, true).GetBytes(snippet.Text));
        Assert.That(snippet.Text, Does.Not.Contain("\r").And.Not.Contain("\n"));
    }
}
