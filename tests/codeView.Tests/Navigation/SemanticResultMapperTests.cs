using System.Text;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Navigation;
using CodeView.Operations;
using CodeView.Search;
using CodeView.Settings;

namespace CodeView.Tests.Navigation;

internal sealed class SemanticResultMapperTests
{
    [TestCase("utf-16", 3)]
    [TestCase("utf-8", 7)]
    [TestCase("utf-32", 2)]
    public void ConvertsOriginalPositionsAcrossCrLfAndSupplementaryCharacters(string encoding, int column)
    {
        const string text = "x\r\n漢😀Run\rtext";
        var source = new LspSourceText(text);
        Assert.That(source.PositionAt(6, encoding), Is.EqualTo(new LspPosition(1, column)));
        Assert.That(source.TryRange(new(1, column), new(1, column + 3), encoding, out var range), Is.True);
        Assert.That(range, Is.EqualTo(new TextRange(6, 3, new(2, 4))));
    }

    [TestCase("utf-16", 2)]
    [TestCase("utf-8", 4)]
    public void RejectsPositionsInsideEncodedCharactersAndPastTheLine(string encoding, int column)
    {
        var source = new LspSourceText("漢😀Run\r\n");
        Assert.That(source.TryRange(new(0, column), new(0, 7), encoding, out _), Is.False);
        Assert.That(source.TryRange(new(1, 0), new(1, 1), encoding, out _), Is.False);
        Assert.That(source.TryRange(new(-1, 0), new(0, 1), encoding, out _), Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.PositionAt(2, encoding));
    }

    [Test]
    public void MapsLocationLinksFromOriginalCp932AndDeduplicatesRanges()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = Directory.CreateTempSubdirectory("codeview-semantic-map-");
        try
        {
            var caller = Write(root, "caller.cs", "Run()");
            const string text = "// 日本語\r\n漢Run";
            var target = Path.Combine(root.FullName, "target.cs");
            File.WriteAllText(target, text, Encoding.GetEncoding(932));
            var relative = new Uri(target).AbsoluteUri;
            var data = JsonSerializer.SerializeToElement(new[]
            {
                new { targetUri = relative, targetSelectionRange = Span(1, 3, 6) },
                new { targetUri = relative, targetSelectionRange = Span(1, 3, 6) }
            });
            var access = new SourceAccess();
            var origin = new SemanticOrigin(caller, access.GetStamp(caller));
            var result = new SemanticResultMapper(new(access)).Map(new(data, "utf-8", "test", false),
                SourcePath.Create(root.FullName, root.FullName), new([]), origin, "Run()", DateTime.UtcNow, default);
            Assert.That(result.IsPartial, Is.False);
            var hit = result.Hits.Single();
            Assert.That(hit.Path.FullPath, Is.EqualTo(target));
            Assert.That(text.Substring((int)hit.Range.Offset, (int)hit.Range.Length), Is.EqualTo("Run"));
            Assert.That(hit.Range.Start, Is.EqualTo(new TextPosition(2, 2)));
            Assert.That(hit.Origin, Is.EqualTo(origin));
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public void RejectsVirtualExcludedInvalidAndNewerLocations()
    {
        var parent = Directory.CreateTempSubdirectory("codeview-semantic-map-");
        try
        {
            var root = parent.CreateSubdirectory("root");
            root.CreateSubdirectory("excluded");
            var caller = Write(root, "caller.cs", "Run()");
            var valid = Write(root, "valid.cs", "Run");
            var excluded = Write(root, "excluded/secret.cs", "Run");
            var outside = Write(parent, "not-in-root.cs", "Run");
            var newer = Write(root, "newer.cs", "Run");
            var started = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(newer.FullPath, started.AddMinutes(1));
            var data = JsonSerializer.SerializeToElement(new[]
            {
                new { uri = new Uri(valid.FullPath).AbsoluteUri, range = Span(0, 0, 3) },
                new { uri = new Uri(valid.FullPath).AbsoluteUri, range = Span(1, 0, 3) },
                new { uri = new Uri(excluded.FullPath).AbsoluteUri, range = Span(0, 0, 3) },
                new { uri = new Uri(outside.FullPath).AbsoluteUri, range = Span(0, 0, 3) },
                new { uri = new Uri(newer.FullPath).AbsoluteUri, range = Span(0, 0, 3) },
                new { uri = "roslyn-metadata://project/Type", range = Span(0, 0, 3) }
            });
            var access = new SourceAccess();
            var result = new SemanticResultMapper(new(access)).Map(new(data, "utf-16", "test", false),
                SourcePath.Create(root.FullName, root.FullName), new(["excluded"]), new(caller, access.GetStamp(caller)),
                "Run()", started, default);
            Assert.That(result.IsPartial, Is.True);
            Assert.That(result.Hits.Single().Path.FullPath, Is.EqualTo(valid.FullPath));
        }
        finally { parent.Delete(recursive: true); }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void NavigationRejectsUpdatedSourceOrTargetBeforeSwitchingTabs(bool updateSource, bool updateTarget) =>
        SingleThreadTestContext.Run(async () =>
    {
        var root = Directory.CreateTempSubdirectory("codeview-semantic-move-");
        try
        {
            var caller = Write(root, "caller.cs", "Run()");
            var target = Write(root, "target.cs", "Run");
            var access = new SourceAccess();
            using var operations = new AppOperations(new(access), new AvaloniaEditDocumentAdapter(), new(access));
            await operations.OpenFolderAsync(SourcePath.Create(root.FullName, root.FullName), SettingsSnapshot.Default);
            await operations.OpenFileAsync(caller, EncodingChoice.Auto);
            var search = operations.Searches.Start("Run").Value;
            Assert.That(await search.Started, Is.True);
            while (await operations.Searches.WaitForEventsAsync(search))
                while (operations.Searches.TryRead(search, out _)) { }
            await search.Completion;
            var hit = new SearchHit(1, target, new(0, 3, new(1, 1)), new("Run", 0, 3),
                access.GetStamp(target), new(caller, access.GetStamp(caller)));
            if (updateSource) File.AppendAllText(caller.FullPath, "// changed");
            if (updateTarget) File.AppendAllText(target.FullPath, "// changed");
            var result = await operations.ResultNavigator.Start(search, hit).Completion;
            if (updateSource || updateTarget)
            {
                Assert.That(result.Status, Is.EqualTo(OperationStatus.Failure));
                Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.ChangedDuringRead));
                Assert.That(operations.Tabs.ActiveTab!.Path.FullPath, Is.EqualTo(caller.FullPath));
            }
            else
            {
                Assert.That(result.Value.Move.SelectionLength, Is.EqualTo(3));
                Assert.That(operations.Tabs.ActiveTab!.Path.FullPath, Is.EqualTo(target.FullPath));
            }
        }
        finally { root.Delete(recursive: true); }
    });

    private static SourcePath Write(DirectoryInfo root, string relative, string text)
    {
        var path = SourcePath.Create(relative, root.FullName);
        File.WriteAllText(path.FullPath, text);
        return path;
    }

    private static object Span(int line, int start, int end) =>
        new { start = new { line, character = start }, end = new { line, character = end } };
}
