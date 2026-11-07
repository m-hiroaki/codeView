using AvaloniaEdit.Document;
using CodeView.Documents;

namespace CodeView.Tests.Documents;

public sealed class SyntaxHighlightingTests
{
    [TestCase("sample.cs", "class Widget { string text = \"abc\"; }")]
    [TestCase("sample.c", "#define SIZE 42\nint size = SIZE;")]
    [TestCase("sample.cpp", "template<class T> struct Box {};")]
    [TestCase("c-header.h", "#define SIZE 42\nint size = SIZE;")]
    [TestCase("cpp-header.h", "template<class T> struct Box {};")]
    [TestCase("sample.json", "{\"size\": true}")]
    [TestCase("sample.xml", "<Item Size=\"42\"/>")]
    [TestCase("sample.xaml", "<Window Title=\"Sample\"/>")]
    [TestCase("sample.md", "# Heading\n\n**bold**")]
    public void ThemeProducesSourceSpansForEachLanguage(string path, string content)
    {
        using var document = new DocumentHandle(new TextDocument(content), null);
        using var lease = document.AcquireReadView();
        var (highlighting, status) = SyntaxHighlighting.Read(lease, BundledGrammars.ScopeForPath(path)!, new BundledGrammars(), default);

        Assert.That(highlighting, Is.Not.Null, status);
        Assert.That(highlighting!.Spans, Is.Not.Empty);
        Assert.That(highlighting.Spans.All(span => span.Length > 0 && span.Offset >= 0
            && span.Offset + span.Length <= content.Length && highlighting.Palette.ContainsKey(span.Foreground)), Is.True);
        Assert.That(lease.ReadRange(0, content.Length), Is.EqualTo(content));
    }

    [Test]
    public void MultilineCommentRetainsColorAcrossChunkBoundaryAndMixedNewlines()
    {
        const string line = "// 😀";
        var padding = string.Concat(Enumerable.Repeat(" \n", DocumentReadLease.ChunkChars / 2 - 3));
        var content = padding + "/* 😀\r\ncontinued */\rint value;\n" + line;
        using var document = new DocumentHandle(new TextDocument(content), null);
        using var lease = document.AcquireReadView();
        var (highlighting, status) = SyntaxHighlighting.Read(lease, "source.c", new BundledGrammars(), default);

        Assert.That(highlighting, Is.Not.Null, status);
        var spans = highlighting!.Spans;
        var comment = spans.First(span => span.Offset == padding.Length);
        Assert.That(spans.First(span => span.Offset == content.IndexOf("continued", StringComparison.Ordinal)).Foreground,
            Is.EqualTo(comment.Foreground));
        Assert.That(spans.First(span => span.Offset == content.IndexOf("int", StringComparison.Ordinal)).Foreground,
            Is.Not.EqualTo(comment.Foreground));
        Assert.That(spans.All(span => !char.IsLowSurrogate(content[span.Offset])
            && (span.Offset + span.Length == content.Length || !char.IsLowSurrogate(content[span.Offset + span.Length]))), Is.True);
    }

    [Test]
    public void OversizedDocumentStaysPlainWithoutInitializingGrammars()
    {
        using var document = new DocumentHandle(new TextDocument(new string(' ', SyntaxHighlighting.MaximumChars + 1)), null);
        using var lease = document.AcquireReadView();
        var grammars = new BundledGrammars();
        var result = SyntaxHighlighting.Read(lease, "source.cs", grammars, default);

        Assert.That(result.Highlighting, Is.Null);
        Assert.That(result.Status, Does.Contain("本文上限"));
        Assert.That(grammars.IsInitialized, Is.False);
    }

    [TestCase(0, true)]
    [TestCase(1, false)]
    public void LongLineBoundaryDoesNotReturnPartialColors(int extra, bool expectedColor)
    {
        using var document = new DocumentHandle(new TextDocument("int value;\n" + new string(' ', SyntaxHighlighting.MaximumLineChars + extra)), null);
        using var lease = document.AcquireReadView();
        var result = SyntaxHighlighting.Read(lease, "source.c", new BundledGrammars(), default);
        Assert.That(result.Highlighting is not null, Is.EqualTo(expectedColor), result.Status);
        if (!expectedColor) Assert.That(result.Status, Does.Contain("長行"));
    }

    [Test]
    public void MissingGrammarAndCancellationHaveSeparateOutcomes()
    {
        using var document = new DocumentHandle(new TextDocument("int value;"), null);
        using var lease = document.AcquireReadView();
        var grammars = new BundledGrammars();
        var result = SyntaxHighlighting.Read(lease, "source.codeview-missing", grammars, default);
        Assert.That(result.Highlighting, Is.Null);
        Assert.That(result.Status, Does.Contain("文法"));
        Assert.That(() => SyntaxHighlighting.Read(lease, "source.cs", grammars, new CancellationToken(true)),
            Throws.TypeOf<OperationCanceledException>());
    }
}
