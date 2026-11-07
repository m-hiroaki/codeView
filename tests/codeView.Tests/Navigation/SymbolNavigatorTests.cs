using CodeView.Contracts;
using CodeView.Files;
using CodeView.Navigation;
using CodeView.Search;

namespace CodeView.Tests.Navigation;

public sealed class SymbolNavigatorTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task BothOperationsPublishOriginalPositionsWithSharedFileCommitRules(bool definitions)
    {
        var directory = Directory.CreateTempSubdirectory("codeview-symbol-");
        const string source = "class Target {}\r\n// Target target TargetSuffix\r\nclass C { string x = \"Target\"; }";
        var path = Path.Combine(directory.FullName, "sample.cs");
        try
        {
            await File.WriteAllTextAsync(path, source);
            var (navigator, request) = Create(directory.FullName, definitions);
            var session = definitions ? navigator.FindDefinitions(request, default) : navigator.FindReferences(request, default);
            var events = new List<SearchEvent<SearchHit>>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var item in session.Events.ReadAllAsync(deadline.Token)) events.Add(item);

            var hits = events.OfType<SearchEvent<SearchHit>.Hits>().SelectMany(batch => batch.Items).ToArray();
            Assert.That(hits.Select(hit => hit.Range.Start.Line), Is.EqualTo(definitions ? new long[] { 1 } : [1L, 2L, 3L]));
            Assert.That(hits.All(hit => source.Substring((int)hit.Range.Offset, (int)hit.Range.Length) == "Target"), Is.True);
            Assert.That(events.Last(), Is.TypeOf<SearchEvent<SearchHit>.FileCommitted>());
            var end = await session.Completion;
            Assert.That(end.IsPartial, Is.False);
            Assert.That(end.CommittedHits, Is.EqualTo(hits.Length));
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(source));
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AlreadyCancelledRequestsCloseBothChannelsWithoutPublishingResults(bool definitions)
    {
        var (navigator, request) = Create(Path.GetTempPath(), definitions);
        var cancellation = new CancellationToken(true);
        var session = definitions ? navigator.FindDefinitions(request, cancellation) : navigator.FindReferences(request, cancellation);
        Assert.That(await session.Events.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        Assert.That((await session.Completion).Reason, Is.EqualTo(SearchEndReason.Cancelled));
    }

    private static (ISymbolNavigator Navigator, WorkspaceSearchRequest Request) Create(string root, bool definitions)
    {
        var source = new SourceAccess();
        var files = new FileReader(source);
        return (new SimpleSymbolNavigator(new TextSearch(new DirectoryReader(source), files), files),
            new WorkspaceSearchRequest(SourcePath.Create(root, root), new ExclusionPolicy([]),
                EncodingChoice.Auto, "Target", new(UseRegex: !definitions), 1, 1, 1, definitions));
    }
}
