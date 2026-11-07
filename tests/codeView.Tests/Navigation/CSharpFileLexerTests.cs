using System.Text;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Navigation;

namespace CodeView.Tests.Navigation;

internal sealed class CSharpFileLexerTests
{
    [TestCase(EncodingChoice.Utf8)]
    [TestCase(EncodingChoice.Utf16Le)]
    [TestCase(EncodingChoice.Cp932)]
    public void StrictDecodingAndFileStampAllowOriginalPositionTokensToCommit(EncodingChoice choice)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = choice == EncodingChoice.Cp932 ? Encoding.GetEncoding(932) : choice == EncodingChoice.Utf16Le ? Encoding.Unicode : Encoding.UTF8;
        var prefix = "/*" + new string('字', FileReader.ChunkChars + 5) + "*/\r\nclass ";
        WithSource(prefix + "日本語{}", (files, source, _) =>
        {
            var tokens = new List<CSharpToken>();
            var result = new CSharpFileLexer(files).Read(source, token => { tokens.Add(token); return true; }, default);
            Assert.That(result.CanCommit, Is.True);
            Assert.That(result.LexError, Is.EqualTo(CSharpLexError.None));
            Assert.That(result.ReadError, Is.Null);
            Assert.That(tokens.Select(x => x.Text), Is.EqualTo(new[] { "class", "日本語", "{", "}" }));
            Assert.That(tokens[1].Range, Is.EqualTo(new TextRange(prefix.Length, 3, new(2, 7))));
            Assert.That(source.Stream.CanRead, Is.True, "ownership remains with the caller");
        }, encoding, choice, "source.CS");
    }

    [TestCase("/* unfinished", CSharpLexError.Incomplete)]
    [TestCase("\\u0043lass Hidden {}", CSharpLexError.Unsupported)]
    public void EarlierProvisionalTokensAreNotCommittableAfterLexicalFailure(string tail, CSharpLexError error) =>
        WithSource("class Seen {} " + tail, (files, source, _) =>
        {
            var tokens = new List<CSharpToken>();
            var result = new CSharpFileLexer(files).Read(source, token => { tokens.Add(token); return true; }, default);
            Assert.That(tokens.Select(x => x.Text), Does.Contain("Seen"));
            Assert.That(result, Is.EqualTo(new CSharpFileLexResult(false, error, null)));
        });

    [Test]
    public void TokenLimitInvalidatesTheFileRatherThanCommittingEarlierTokens() =>
        WithSource("class Seen {} " + new string('x', CSharpLexer.TokenChars + 1), (files, source, _) =>
        {
            var tokens = new List<CSharpToken>();
            var result = new CSharpFileLexer(files).Read(source, token => { tokens.Add(token); return true; }, default);
            Assert.That(tokens.Select(x => x.Text), Does.Contain("Seen").And.Not.Contain(new string('x', CSharpLexer.TokenChars)));
            Assert.That(result, Is.EqualTo(new CSharpFileLexResult(false, CSharpLexError.TokenLimit, null)));
        });

    [Test]
    public void ChangedStampInvalidatesTheFileWithoutFinalizingAnEofToken() =>
        WithSource("class Seen {} Tail", (files, source, access) =>
        {
            var tokens = new List<CSharpToken>();
            var result = new CSharpFileLexer(files).Read(source, token => { tokens.Add(token); access.Changed = true; return true; }, default);
            Assert.That(result.CanCommit, Is.False);
            Assert.That(result.ReadError!.Code, Is.EqualTo(ErrorCode.ChangedDuringRead));
            Assert.That(tokens.Select(x => x.Text), Does.Contain("Seen").And.Not.Contain("Tail"));
        });

    [Test]
    public void CancellationAndConsumerFaultsAreObservedAndWorkerCanReadAgain() =>
        WithSource("class Seen {}", (files, source, _) =>
        {
            var lexer = new CSharpFileLexer(files);
            Assert.That(lexer.Read(source, _ => false, default).CanCommit, Is.False);
            var fault = new IOException("not a file read failure");
            Assert.That(Assert.Throws<IOException>(() => lexer.Read(source, _ => throw fault, default)), Is.SameAs(fault));
            using var cancelled = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => lexer.Read(source, _ => { cancelled.Cancel(); return true; }, cancelled.Token));
            Assert.Throws<InvalidOperationException>(() => lexer.Read(source, _ => lexer.Read(source, _ => true, default).CanCommit, default));
            Assert.That(lexer.Read(source, _ => true, default).CanCommit, Is.True);
        });

    [Test]
    public void NonCSharpSourceIsNotScanned() => WithSource("class Seen {}", (files, source, _) =>
    {
        var result = new CSharpFileLexer(files).Read(source, _ => throw new AssertionException("must not scan"), default);
        Assert.That(result, Is.EqualTo(new CSharpFileLexResult(false, CSharpLexError.Unsupported, null)));
    }, name: "source.txt");

    private static void WithSource(string text, Action<FileReader, PreparedText, TestAccess> run,
        Encoding? encoding = null, EncodingChoice choice = EncodingChoice.Utf8, string name = "source.cs")
    {
        var root = Directory.CreateTempSubdirectory("codeview-lexer-");
        try
        {
            var path = Path.Combine(root.FullName, name);
            File.WriteAllText(path, text, encoding ?? Encoding.UTF8);
            var original = File.ReadAllBytes(path);
            var access = new TestAccess();
            var files = new FileReader(access);
            using var source = files.Prepare(SourcePath.Create(path, path), choice, default).Value;
            run(files, source, access);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class TestAccess : ISourceAccess
    {
        private readonly SourceAccess _source = new();
        public bool Changed { get; set; }
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct) => _source.OpenRead(path, ct);
        public SourceStamp GetStamp(SourcePath path)
        {
            var stamp = _source.GetStamp(path);
            return Changed ? stamp with { LastWriteUtc = stamp.LastWriteUtc.AddSeconds(1) } : stamp;
        }
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => _source.ResolveDirectory(path, ct);
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct) => _source.EnumerateChildren(path, ct);
        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => _source.InspectPath(path, ct);
    }
}
