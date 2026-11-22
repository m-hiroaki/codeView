using CodeView.Documents;
using TextMateSharp.Registry;

namespace CodeView.Tests.Documents;

public sealed class BundledGrammarsTests
{
    [TestCase("code.cs", "class Widget { string value = \"text\"; } // comment")]
    [TestCase("code.c", "int answer = 42; char* text = \"text\"; // comment")]
    [TestCase("code.cpp", "template<class T> struct Box { const char* text = \"text\"; }; // comment")]
    [TestCase("c-header.h", "int answer = 42; char* text = \"text\"; // comment")]
    [TestCase("cpp-header.h", "template<class T> struct Box { const char* text = \"text\"; }; // comment")]
    public void NativeTokenizerDistinguishesCommentsStringsAndKeywords(string path, string line)
    {
        var registry = new Registry(new BundledGrammars());
        var grammar = registry.LoadGrammar(BundledGrammars.ScopeForPath(path));
        var result = grammar.TokenizeLine(line, null, TimeSpan.FromSeconds(1));
        var scopes = result.Tokens.SelectMany(token => token.Scopes).ToArray();
        Assert.That(scopes.Any(scope => scope.StartsWith("comment.", StringComparison.Ordinal)), Is.True);
        Assert.That(scopes.Any(scope => scope.StartsWith("string.", StringComparison.Ordinal)), Is.True);
        Assert.That(scopes.Any(scope => scope.StartsWith("storage.type", StringComparison.Ordinal)
            || scope.StartsWith("keyword.", StringComparison.Ordinal)), Is.True);
    }

    [TestCase("data.json", "{\"value\": true}", "string.")]
    [TestCase("file.xml", "<Item Value=\"text\"/>", "entity.name.tag")]
    [TestCase("file.xaml", "<Window Title=\"text\"/>", "entity.name.tag")]
    [TestCase("file.md", "# Heading", "markup.heading")]
    public void StructuredTextHasExpectedTokenCategories(string path, string line, string category)
    {
        var registry = new Registry(new BundledGrammars());
        var grammar = registry.LoadGrammar(BundledGrammars.ScopeForPath(path));
        var result = grammar.TokenizeLine(line, null, TimeSpan.FromSeconds(1));
        Assert.That(result.Tokens.SelectMany(token => token.Scopes)
            .Any(scope => scope.StartsWith(category, StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void CommentStateContinuesAcrossLines()
    {
        var grammar = new Registry(new BundledGrammars()).LoadGrammar("source.cpp");
        var first = grammar.TokenizeLine("/* comment", null, TimeSpan.FromSeconds(1));
        var second = grammar.TokenizeLine("continued */ int value;", first.RuleStack, TimeSpan.FromSeconds(1));
        Assert.That(second.Tokens[0].Scopes.Any(scope => scope.StartsWith("comment.", StringComparison.Ordinal)), Is.True);
        Assert.That(second.Tokens.SelectMany(token => token.Scopes)
            .Any(scope => scope.StartsWith("storage.type", StringComparison.Ordinal)), Is.True);
    }

    [TestCase("file.cs", "source.cs")]
    [TestCase("file.c", "source.c")]
    [TestCase("file.i", "source.c")]
    [TestCase("file.CPP", "source.cpp")]
    [TestCase("file.cc", "source.cpp")]
    [TestCase("file.cp", "source.cpp")]
    [TestCase("file.cxx", "source.cpp")]
    [TestCase("file.c++", "source.cpp")]
    [TestCase("file.ii", "source.cpp")]
    [TestCase("file.hpp", "source.cpp")]
    [TestCase("file.hh", "source.cpp")]
    [TestCase("file.hxx", "source.cpp")]
    [TestCase("file.h++", "source.cpp")]
    [TestCase("file.ipp", "source.cpp")]
    [TestCase("file.inl", "source.cpp")]
    [TestCase("file.tpp", "source.cpp")]
    [TestCase("file.ixx", "source.cpp")]
    [TestCase("file.cppm", "source.cpp")]
    [TestCase("c-header.h", "source.cpp")]
    [TestCase("cpp-header.H", "source.cpp")]
    [TestCase("file.json", "source.json")]
    [TestCase("file.xml", "text.xml")]
    [TestCase("file.config", "text.xml")]
    [TestCase("file.csproj", "text.xml")]
    [TestCase("file.props", "text.xml")]
    [TestCase("file.targets", "text.xml")]
    [TestCase("file.xaml", "text.xml")]
    [TestCase("file.md", "text.html.markdown")]
    [TestCase("file.markdown", "text.html.markdown")]
    public void SupportedMappingsHaveOfflineGrammarAssets(string path, string scope)
    {
        var catalog = new BundledGrammars();
        Assert.That(catalog.IsInitialized, Is.False);
        Assert.That(BundledGrammars.ScopeForPath(path), Is.EqualTo(scope));
        Assert.That(catalog.IsInitialized, Is.False);
        Assert.That(catalog.GetGrammar(scope)?.GetScopeName(), Is.EqualTo(scope));
        Assert.That(catalog.IsInitialized, Is.True);
        Assert.That(catalog.GetDefaultTheme(), Is.Not.Null);
    }

    [TestCase("no-extension")]
    [TestCase("file.unknown")]
    [TestCase("README")]
    public void UnknownPathsRemainPlainAndDoNotInitializeAssets(string path)
    {
        var catalog = new BundledGrammars();
        Assert.That(BundledGrammars.ScopeForPath(path), Is.Null);
        Assert.That(catalog.IsInitialized, Is.False);
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase(null)]
    public void InvalidPathFails(string? path) =>
        Assert.That(() => BundledGrammars.ScopeForPath(path!), Throws.InstanceOf<ArgumentException>());

    [Test]
    public void MissingGrammarReturnsNoGrammar() =>
        Assert.That(new BundledGrammars().GetGrammar("source.codeview-missing"), Is.Null);
}
