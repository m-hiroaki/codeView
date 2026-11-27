using AvaloniaEdit.Document;
using CodeView.Documents;

namespace CodeView.Tests.Documents;

internal sealed class BracketHighlightingTests
{
    [Test]
    public void MatchesMixedNestedBracketsAcrossMixedNewlinesAndUnicode()
    {
        const string text = "😀 {\r\n\tcall([1]);\r}\n";
        var brackets = Read(text);
        Assert.That(brackets.Pairs.Select(p => (text[p.Open], text[p.Close], p.Depth)),
            Is.EqualTo(new[] { ('{', '}', 0), ('(', ')', 1), ('[', ']', 2) }));
        Assert.That((brackets.Pairs[0].OpenLine, brackets.Pairs[0].CloseLine), Is.EqualTo((1, 3)));
        Assert.That(brackets.Pairs[1].Indent, Is.EqualTo(4));
        var visible = new List<BracketHighlighting.Pair>();
        brackets.Visit(text.IndexOf('1'), text.IndexOf('1'), visible.Add);
        Assert.That(visible, Has.Count.EqualTo(3));
    }

    [TestCase("source.cs", "class C { string s = \"[}]\"; /* } */ char c = '('; // }\n}")]
    [TestCase("source.c", "void f() { char *s = \"}\"; /* [ */ // }\n}")]
    [TestCase("source.cpp", "void f() { auto s = R\"tag([}])tag\"; /* { */ }")]
    [TestCase("source.json", "{\"brackets\": \"[}]\", \"array\": []}")]
    public void IgnoresCommentsAndStringsUsingGrammarScopes(string scope, string text)
    {
        var brackets = Read(text, scope);
        Assert.That(brackets.Pairs.Count(p => text[p.Open] == '{'), Is.EqualTo(1));
        Assert.That(brackets.Pairs.Single(p => text[p.Open] == '{').Close, Is.EqualTo(text.LastIndexOf('}')));
    }

    [TestCase("")]
    [TestCase("{([)]}")]
    [TestCase("((")]
    [TestCase("]})")]
    public void EmptyOrUnmatchedSourceDoesNotInventPairs(string text) =>
        Assert.That(Parse(text).Pairs, Is.Empty);

    [TestCase(0, true)]
    [TestCase(1, false)]
    public void DepthLimitDiscardsPartialResults(int extra, bool enabled)
    {
        var depth = BracketHighlighting.MaximumDepth + extra;
        var brackets = Parse(new string('(', depth) + new string(')', depth));
        Assert.That(brackets.Pairs.Count, Is.EqualTo(enabled ? depth : 0));
    }

    [TestCase(0, true)]
    [TestCase(1, false)]
    public void PairLimitDiscardsPartialResults(int extra, bool enabled)
    {
        var count = BracketHighlighting.MaximumPairs + extra;
        var brackets = Parse(string.Concat(Enumerable.Repeat("()", count)));
        Assert.That(brackets.Pairs.Count, Is.EqualTo(enabled ? count : 0));
    }

    [Test]
    public void BracketBudgetPreservesNormalSyntaxColors()
    {
        var text = "void Read" + new string('(', BracketHighlighting.MaximumDepth + 1)
            + new string(')', BracketHighlighting.MaximumDepth + 1) + " {}";
        using var document = new DocumentHandle(new TextDocument(text), null);
        using var lease = document.AcquireReadView();
        var (highlighting, status) = SyntaxHighlighting.Read(lease, "source.cs", new BundledGrammars(), default);
        Assert.That(highlighting, Is.Not.Null, status);
        Assert.That(highlighting!.Spans, Is.Not.Empty);
        Assert.That(highlighting.Brackets.Pairs, Is.Empty);
    }

    [Test]
    public void IntervalIndexSkipsSiblingsButRetainsEnclosingPair()
    {
        var text = "{" + string.Concat(Enumerable.Repeat("()", 20_000)) + "}";
        var brackets = Parse(text);
        var found = new List<BracketHighlighting.Pair>();
        brackets.Visit(30_001, 30_002, found.Add);
        Assert.That(found.Select(p => p.Open), Is.EqualTo(new[] { 0, 30_001 }));
        found.Clear();
        brackets.Visit(text.Length, text.Length + 10, found.Add);
        Assert.That(found, Is.Empty);
    }

    [Test]
    public void MaximumPairIndexHasBoundedAllocationAndViewportQueries()
    {
        var text = "{" + string.Concat(Enumerable.Repeat("()", BracketHighlighting.MaximumPairs - 1)) + "}";
        Parse("{}"); // Warm up JIT before measuring the additional analysis.
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var brackets = Parse(text);
        var buildMs = watch.Elapsed.TotalMilliseconds;
        var buildBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var count = 0;
        Action<BracketHighlighting.Pair> visit = _ => count++;
        watch.Restart();
        for (var i = 0; i < 10_000; i++) brackets.Visit(30_001, 30_002, visit);
        TestContext.Out.WriteLine($"bracket index: build={buildMs:F3} ms, allocation={buildBytes} bytes, "
            + $"10000 viewport queries={watch.Elapsed.TotalMilliseconds:F3} ms, CPU={(System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds:F3} ms");
        Assert.That(count, Is.EqualTo(20_000));
        Assert.That(buildBytes, Is.LessThan(8 * 1024 * 1024));
    }

    private static BracketHighlighting Parse(string text)
    {
        var result = new BracketHighlighting();
        result.ReadToken(text, 0, text.Length, 0, 1);
        result.Complete();
        return result;
    }

    private static BracketHighlighting Read(string text, string scope = "source.cs")
    {
        using var document = new DocumentHandle(new TextDocument(text), null);
        using var lease = document.AcquireReadView();
        var (highlighting, status) = SyntaxHighlighting.Read(lease, scope, new BundledGrammars(), default);
        Assert.That(highlighting, Is.Not.Null, status);
        return highlighting!.Brackets;
    }
}
