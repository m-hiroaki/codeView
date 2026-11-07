using CodeView.Contracts;
using CodeView.Navigation;

namespace CodeView.Tests.Navigation;

internal sealed class CSharpLexerTests
{
    public static IEnumerable<string> Literals()
    {
        yield return "\"\"";
        yield return "\"class Fake \\\"Q\\\" {}\\n\"";
        yield return "\"\\x1\\u1234\\U00010400\"u8";
        yield return "'a'";
        yield return "'\\''";
        yield return "'\\uD800'";
        yield return "'\\x1'";
        yield return "@\"\"";
        yield return "@\"class Fake \"\"Q\"\"\r\nend\"";
        yield return "\"\"\"class Fake { }\"\"\"";
        yield return "\"\"\"\"class Fake \"\"\" { }\"\"\"\"u8";
        yield return "\"\"\"\r\nclass Fake {}\r\n\"\"\"";
        yield return "$\"class Fake {value} {{escaped}}\"";
        yield return "$\"outer {f(\"class Fake\", new[]{1,2})} end\"";
        yield return "$@\"outer {f(@\"class Fake\")} \"\"Q\"\"\"";
        yield return "@$\"class Fake {value}\"";
        yield return "$\"{(x ? \"class Fake\" : \"other\"):000}\"";
        yield return "$\"{(\n1+2\n)}\"";
        yield return "$\"{f(/* \" } class Fake */ 1)}\"";
        yield return "$\"{new Func<int>(() => { return 1; })()}\"";
        yield return "$\"{f($\"nested {1}\")}\"";
        yield return "$$\"\"\"class Fake {literal} {{ f(\"class Fake\",new[]{1,2}) }}\"\"\"";
        yield return "$$\"\"\"{{value:000}}\"\"\"";
    }

    [TestCaseSource(nameof(Literals))]
    public void LiteralBodiesAndInterpolationExpressionsNeverBecomeDeclarationTokens(string literal)
    {
        var text = "class Real { object Value = " + literal + "; }";
        for (var chunk = 1; chunk <= text.Length + 1; chunk++)
        {
            var (lexer, tokens) = Scan(text, chunk);
            Assert.That(lexer.IsComplete, Is.True, literal + " chunk=" + chunk + " error=" + lexer.Error);
            Assert.That(tokens.Where(x => x.Kind != CSharpTokenKind.Literal).Select(x => x.Text),
                Is.EqualTo(new[] { "class", "Real", "{", "object", "Value", "=", ";", "}" }));
            var token = tokens.Single(x => x.Kind == CSharpTokenKind.Literal);
            Assert.That(token.Text, Is.Empty, "literal bodies are never copied to tokens");
            Assert.That(text.Substring((int)token.Range.Offset, (int)token.Range.Length), Is.EqualTo(literal));
            VerifyPositions(text, tokens);
        }
    }

    [TestCase("// class Fake\r\nclass Real {}")]
    [TestCase("/* class Fake \" */ class Real {}")]
    [TestCase("#if false\r\nclass Real {}\r\n#endif")]
    [TestCase("#region class Fake\nclass Real {}\n#endregion")]
    [TestCase("// class Fake\u0085class Real {}")]
    public void CommentsAndDirectiveLinesAreExcludedButConditionalBranchesAreNotEvaluated(string text)
    {
        for (var chunk = 1; chunk <= text.Length; chunk++)
        {
            var (lexer, tokens) = Scan(text, chunk);
            Assert.That(lexer.IsComplete, Is.True, "chunk=" + chunk);
            Assert.That(tokens.Select(x => x.Text), Is.EqualTo(new[] { "class", "Real", "{", "}" }));
            VerifyPositions(text, tokens);
        }
    }

    [Test]
    public void OriginalUnicodeSpellingCrLfAndNumericTokensSurviveAllChunkBoundaries()
    {
        const string text = "\r\nclass @日本語<\U00010400>\r{\n int e\u0301 = 1.2e+3; int @class=0xFF; }";
        for (var chunk = 1; chunk <= text.Length; chunk++)
        {
            var (lexer, tokens) = Scan(text, chunk);
            Assert.That(lexer.IsComplete, Is.True);
            Assert.That(tokens.Select(x => x.Text), Does.Contain("@日本語").And.Contain("\U00010400").And.Contain("e\u0301"));
            Assert.That(tokens.Where(x => x.Kind == CSharpTokenKind.Number).Select(x => x.Text), Is.EqualTo(new[] { "1.2e+3", "0xFF" }));
            VerifyPositions(text, tokens);
        }
    }

    [TestCase("/* unfinished", CSharpLexError.Incomplete)]
    [TestCase("\"unfinished", CSharpLexError.Incomplete)]
    [TestCase("\"unfinished\\", CSharpLexError.Incomplete)]
    [TestCase("'x", CSharpLexError.Incomplete)]
    [TestCase("@\"unfinished", CSharpLexError.Incomplete)]
    [TestCase("\"\"\"unfinished", CSharpLexError.Incomplete)]
    [TestCase("$\"{value", CSharpLexError.Incomplete)]
    [TestCase("$\"{(value}\"", CSharpLexError.InvalidText)]
    [TestCase("''", CSharpLexError.InvalidText)]
    [TestCase("'ab'", CSharpLexError.InvalidText)]
    [TestCase("'😀'", CSharpLexError.InvalidText)]
    [TestCase("'\\U0001F600'", CSharpLexError.InvalidText)]
    [TestCase("\"\\q\"", CSharpLexError.InvalidText)]
    [TestCase("\"\\u12\"", CSharpLexError.InvalidText)]
    [TestCase("\"line\nbreak\"", CSharpLexError.InvalidText)]
    [TestCase("\"\"\"inline\nclass Fake\n\"\"\"", CSharpLexError.InvalidText)]
    [TestCase("\"\"\"\nclass Fake\"\"\"", CSharpLexError.InvalidText)]
    [TestCase("\"\"\"\n\"\"\"", CSharpLexError.InvalidText)]
    [TestCase("$$\"not raw\"", CSharpLexError.InvalidText)]
    [TestCase("$\"unmatched }\"", CSharpLexError.InvalidText)]
    [TestCase("$\"{x:{nested}}\"", CSharpLexError.Unsupported)]
    [TestCase("\\u0043lass Fake {}", CSharpLexError.Unsupported)]
    [TestCase("na\\u006de", CSharpLexError.Unsupported)]
    public void UnsafeOrUnfinishedInputCannotBeCommitted(string tail, CSharpLexError error)
    {
        var text = "class Provisional {} " + tail;
        for (var chunk = 1; chunk <= text.Length; chunk++)
        {
            var (lexer, tokens) = Scan(text, chunk);
            Assert.That(lexer.IsComplete, Is.False, "chunk=" + chunk);
            Assert.That(lexer.IsFinished, Is.True);
            Assert.That(lexer.Error, Is.EqualTo(error), tail + " chunk=" + chunk);
            Assert.That(tokens.Select(x => x.Text), Does.Contain("Provisional"), "earlier tokens remain provisional until EOF");
            Assert.That(lexer.BufferedChars, Is.Zero);
            Assert.That(lexer.ContextDepth, Is.Zero);
        }
    }

    [TestCase(0xd800)]
    [TestCase(0xdc00)]
    public void InvalidUtf16IsRejectedEvenInsideSkippedContent(int codeUnit)
    {
        // Attribute strings are UTF-8 metadata and cannot carry unpaired surrogates.
        foreach (var prefix in new[] { "class A {} ", "// ", "\"", "/*" })
        {
            var (lexer, _) = Scan(prefix + (char)codeUnit, 1);
            Assert.That(lexer.IsComplete, Is.False);
            Assert.That(lexer.Error, Is.EqualTo(CSharpLexError.InvalidText));
        }
    }

    [TestCase("")]
    [TestCase("// end")]
    [TestCase("#endif")]
    [TestCase("/", "/")]
    [TestCase("\"\"", "")]
    [TestCase("@\"\"", "")]
    [TestCase("\"text\"u", "", "u")]
    [TestCase("\"text\"u8", "")]
    [TestCase("'a'u8", "", "u8")]
    public void EndOfInputFlushesOnlyCompleteTokens(string text, params string[] expected)
    {
        var (lexer, tokens) = Scan(text, 1);
        Assert.That(lexer.IsComplete, Is.True);
        Assert.That(tokens.Select(x => x.Text), Is.EqualTo(expected));
        Assert.That(lexer.Offset, Is.EqualTo(text.Length));
        VerifyPositions(text, tokens);
    }

    [Test]
    public void TokenAndDelimiterBudgetsRejectRatherThanTruncate()
    {
        foreach (var prefix in new[] { "", "@" })
        {
            var name = prefix + new string('x', CSharpLexer.TokenChars);
            var (valid, tokens) = Scan(name, 97);
            Assert.That(valid.IsComplete, Is.True);
            Assert.That(tokens.Single().Text, Is.EqualTo(name));
            var (invalid, _) = Scan(name + "x", 97);
            Assert.That(invalid.Error, Is.EqualTo(CSharpLexError.TokenLimit));
        }
        var quotes = new string('"', CSharpLexer.TokenChars);
        Assert.That(Scan(quotes + "x" + quotes, 97).Lexer.IsComplete, Is.True);
        Assert.That(Scan(quotes + "\"x", 97).Lexer.Error, Is.EqualTo(CSharpLexError.TokenLimit));
        Assert.That(Scan(new string('$', CSharpLexer.TokenChars + 1) + "\"\"\"x\"\"\"", 97).Lexer.Error, Is.EqualTo(CSharpLexError.TokenLimit));
    }

    [Test]
    public void InterpolationNestingHasAnExactSharedDepthLimit()
    {
        foreach (var nesting in new[] { CSharpLexer.MaximumDepth / 2, CSharpLexer.MaximumDepth / 2 + 1 })
        {
            var text = string.Concat(Enumerable.Repeat("$\"{", nesting)) + "1"
                + string.Concat(Enumerable.Repeat("}\"", nesting));
            var (lexer, _) = Scan(text, 13);
            Assert.That(lexer.IsComplete, Is.EqualTo(nesting == CSharpLexer.MaximumDepth / 2));
            Assert.That(lexer.Error, Is.EqualTo(nesting == CSharpLexer.MaximumDepth / 2 ? CSharpLexError.None : CSharpLexError.DepthLimit));
        }
        var allowed = "$\"{" + new string('(', CSharpLexer.MaximumDepth - 2) + "1"
            + new string(')', CSharpLexer.MaximumDepth - 2) + "}\"";
        Assert.That(Scan(allowed, 11).Lexer.IsComplete, Is.True);
        Assert.That(Scan(allowed.Replace("1", "(1)"), 11).Lexer.Error, Is.EqualTo(CSharpLexError.DepthLimit));
    }

    [Test]
    public void LargeCommentAndLiteralBodiesAreStreamedWithoutPayloadRetention()
    {
        var body = new string('x', 2 * 1024 * 1024);
        var text = "/*" + body + "*/ \"" + body + "\" class Last {}";
        var (lexer, tokens) = Scan(text, 701);
        Assert.That(lexer.IsComplete, Is.True);
        Assert.That(lexer.Offset, Is.EqualTo(text.Length));
        Assert.That(tokens.Where(x => x.Kind == CSharpTokenKind.Literal).Single().Text, Is.Empty);
        Assert.That(tokens.Select(x => x.Text), Does.Contain("Last"));
        Assert.That(lexer.BufferedChars, Is.Zero);
        Assert.That(lexer.ContextDepth, Is.Zero);
    }

    [Test]
    public void StopAbortResetCancellationAndConsumerFailureKeepTerminalStatesDistinct()
    {
        var lexer = new CSharpLexer();
        Assert.Throws<InvalidOperationException>(() => lexer.Reset());
        Assert.That(lexer.Push("class C", _ => false), Is.False);
        Assert.That(lexer.IsFinished && !lexer.IsComplete, Is.True);
        Assert.Throws<InvalidOperationException>(() => lexer.Complete(_ => true));
        lexer.Reset();
        lexer.Push("/*partial", _ => true);
        lexer.Abort();
        Assert.That(lexer.IsComplete, Is.False);
        Assert.That(lexer.BufferedChars, Is.Zero);
        lexer.Reset();
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => lexer.Push("a b", _ => { cancellation.Cancel(); return true; }, cancellation.Token));
        Assert.That(lexer.IsFinished && !lexer.IsComplete, Is.True);
        lexer.Reset();
        var failure = new IOException("consumer failed");
        Assert.That(Assert.Throws<IOException>(() => lexer.Push("name ", _ => throw failure)), Is.SameAs(failure));
        lexer.Reset();
        Assert.Throws<InvalidOperationException>(() => lexer.Push("name ", _ => { lexer.Abort(); return true; }));
        lexer.Reset();
        var tokens = new List<CSharpToken>();
        lexer.Push("class End {}", t => { tokens.Add(t); return true; });
        Assert.That(lexer.Complete(t => { tokens.Add(t); return true; }), Is.True);
        Assert.That(tokens[0].Range.Start, Is.EqualTo(new TextPosition(1, 1)));
        Assert.That(tokens[0].Range.Offset, Is.Zero);
    }

    private static (CSharpLexer Lexer, List<CSharpToken> Tokens) Scan(string text, int chunk)
    {
        var lexer = new CSharpLexer(); var tokens = new List<CSharpToken>();
        bool Consume(CSharpToken token) { tokens.Add(token); return true; }
        for (var start = 0; start < text.Length && !lexer.IsFinished; start += chunk)
            lexer.Push(text.AsSpan(start, Math.Min(chunk, text.Length - start)), Consume);
        if (!lexer.IsFinished) lexer.Complete(Consume);
        return (lexer, tokens);
    }

    private static void VerifyPositions(string source, IEnumerable<CSharpToken> tokens)
    {
        foreach (var token in tokens)
        {
            var cursor = new TextCursor();
            cursor.Advance(source.AsSpan(0, (int)token.Range.Offset));
            Assert.That(token.Range.Start, Is.EqualTo(cursor.Position));
            if (token.Kind != CSharpTokenKind.Literal)
                Assert.That(source.Substring((int)token.Range.Offset, (int)token.Range.Length), Is.EqualTo(token.Text));
        }
    }
}
