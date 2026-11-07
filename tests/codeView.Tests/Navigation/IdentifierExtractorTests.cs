using CodeView.Navigation;

namespace CodeView.Tests.Navigation;

public sealed class IdentifierExtractorTests
{
    [TestCase("value", 0, "value", 0, 5)]
    [TestCase("value", 3, "value", 0, 5)]
    [TestCase("value", 5, "value", 0, 5)]
    [TestCase("value;", 5, "value", 0, 5)]
    [TestCase("obj.Method()", 6, "Method", 4, 6)]
    [TestCase("obj.Method()", 4, "Method", 4, 6)]
    [TestCase("@class", 0, "class", 0, 6)]
    [TestCase("@class", 1, "class", 0, 6)]
    [TestCase("@class", 6, "class", 0, 6)]
    [TestCase("@name;", 3, "name", 0, 5)]
    [TestCase("日本語", 2, "日本語", 0, 3)]
    [TestCase("e\u0301", 2, "e\u0301", 0, 2)]
    [TestCase("\U00010400name", 2, "\U00010400name", 0, 6)]
    [TestCase("_name9", 6, "_name9", 0, 6)]
    [TestCase("var", 1, "var", 0, 3)]
    [TestCase("record", 2, "record", 0, 6)]
    [TestCase("a\r\nname", 4, "name", 3, 4)]
    public void CaretExtractsOriginalRangeAndExactName(string text, int caret, string expected, int start, int length)
    {
        Assert.That(IdentifierExtractor.TryExtract(text, caret, caret, 0, true, out var name, out var offset, out var count), Is.True);
        Assert.That((name, offset, count), Is.EqualTo((expected, start, length)));
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("123name")]
    [TestCase("class")]
    [TestCase("null")]
    [TestCase("@")]
    [TestCase("@@name")]
    [TestCase("two words")]
    [TestCase("obj.Method")]
    [TestCase("a\nb")]
    [TestCase("\\u0061")]
    [TestCase("\ud800")]
    [TestCase("a\udc00")]
    [TestCase("\u0301a")]
    [TestCase("\u200Da")]
    public void InvalidSelectionDoesNotProduceAPartialName(string text)
    {
        Assert.That(IdentifierExtractor.TryExtract(text, 0, 0, text.Length, true, out var name, out _, out _), Is.False);
        Assert.That(name, Is.Empty);
    }

    [Test]
    public void SelectionWinsOverCaretAndEscapedNameKeepsSourceRange()
    {
        const string text = "other @class tail";
        Assert.That(IdentifierExtractor.TryExtract(text, text.Length, 6, 6, true, out var name, out var start, out var length), Is.True);
        Assert.That((name, start, length), Is.EqualTo(("class", 6, 6)));
        Assert.That(IdentifierExtractor.TryExtract("class", 0, 0, 5, false, out name, out _, out _), Is.True);
        Assert.That(name, Is.EqualTo("class"));
    }

    [Test]
    public void LengthLimitIsExactAndDoesNotSilentlyTruncate()
    {
        var allowed = new string('x', IdentifierExtractor.MaximumChars);
        foreach (var text in new[] { allowed, "@" + allowed })
        {
            Assert.That(IdentifierExtractor.TryExtract(text, text.Length, 0, text.Length, true, out _, out _, out _), Is.True);
            Assert.That(IdentifierExtractor.TryExtract(text, text.Length / 2, 0, 0, true, out _, out _, out _), Is.True);
            Assert.That(IdentifierExtractor.TryExtract(text + "x", 0, 0, text.Length + 1, true, out _, out _, out _), Is.False);
            Assert.That(IdentifierExtractor.TryExtract(text + "x", text.Length / 2, 0, 0, true, out _, out _, out _), Is.False);
        }
    }

    [Test]
    public void InvalidRangesAndSplitScalarsAreRejected()
    {
        foreach (var (caret, start, length) in new[] { (-1, 0, 0), (4, 0, 0), (0, -1, 1), (0, 0, -1), (0, 2, 2), (1, 0, 0), (0, 1, 1), (0, 0, 1) })
            Assert.That(IdentifierExtractor.TryExtract("\U00010400x", caret, start, length, true, out _, out _, out _), Is.False);
    }
}
