using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Search;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class InFileSearchTests
{
    [Test]
    public void FindsNextPreviousAndWrapsUsingOriginalOffsets() => WithView("foo\r\nfood Foo foo", async (view, _, _) =>
    {
        var search = view.InFileSearch;
        search.IsVisible = true;
        search.WholeWord = true;
        search.Query = "foo";
        await search.PendingWork;
        Assert.That(search.Matches.Select(match => match.Offset), Is.EqualTo(new long[] { 0, 10, 14 }));
        Assert.That((await search.MoveAsync(0, 0, 3, false))?.Offset, Is.EqualTo(10));
        Assert.That((await search.MoveAsync(0, 0, 3, true))?.Offset, Is.EqualTo(14));
        Assert.That(search.Summary, Does.Contain("折り返し"));
        Assert.That((await search.MoveAsync(14, 14, 3, false))?.Offset, Is.Zero);
        search.MatchCase = true;
        await search.PendingWork;
        Assert.That(search.Matches.Select(match => match.Offset), Is.EqualTo(new long[] { 0, 14 }));
    });

    [Test]
    public void ZeroLengthMatchesAdvanceAndWrap() => WithView("a\na", async (view, _, _) =>
    {
        var search = view.InFileSearch;
        search.IsVisible = true;
        search.UseRegex = true;
        search.Query = "^";
        var first = await search.MoveAsync(0, 0, 0, false);
        Assert.That(first?.Length, Is.Zero);
        Assert.That(first?.Offset, Is.Zero);
        Assert.That((await search.MoveAsync(0, 0, 0, false))?.Offset, Is.EqualTo(2));
        Assert.That((await search.MoveAsync(2, 2, 0, false))?.Offset, Is.Zero);
        Assert.That(search.Summary, Does.Contain("折り返し"));
    });

    [Test]
    public void MovesBeyondStoredLimitInBothDirections() =>
        WithView(string.Concat(Enumerable.Repeat("x ", SearchLimits.ResultLimit + 2)), async (view, _, _) =>
        {
            var search = view.InFileSearch;
            search.IsVisible = true;
            search.Query = "x";
            await search.PendingWork;
            Assert.That(search.Matches.Count, Is.EqualTo(SearchLimits.ResultLimit));
            Assert.That(search.Summary, Does.Contain("少なくとも"));
            var boundary = 2L * SearchLimits.ResultLimit;
            Assert.That((await search.MoveAsync(boundary, boundary, 0, false))?.Offset, Is.EqualTo(boundary));
            Assert.That((await search.MoveAsync(boundary + 2, boundary + 2, 1, true))?.Offset, Is.EqualTo(boundary));
            Assert.That((await search.MoveAsync(0, 0, 1, true))?.Offset, Is.EqualTo(boundary + 2));
            Assert.That(search.Matches.Count, Is.EqualTo(SearchLimits.ResultLimit));
        });

    [Test]
    public void PreservesTabConditionsAndRefreshesMatchesAfterReload() => WithView("foo foo", async (view, first, second) =>
    {
        var search = view.InFileSearch;
        search.IsVisible = true;
        search.Query = "foo";
        await search.PendingWork;
        var cached = search.Matches;
        view.SelectedTab = view.Tabs.Single(tab => tab.FullPath == second);
        Assert.That(search.Query, Is.Empty);
        search.Query = "bar";
        search.MatchCase = true;
        view.SelectedTab = view.Tabs.Single(tab => tab.FullPath == first);
        await search.PendingWork;
        Assert.That(search.Query, Is.EqualTo("foo"));
        Assert.That(search.MatchCase, Is.False);
        Assert.That(search.Matches, Is.SameAs(cached));
        Assert.That(search.Matches.Count, Is.EqualTo(2));
        await File.WriteAllTextAsync(first, "a foo");
        await view.ReloadAsync();
        await search.PendingWork;
        Assert.That(search.Query, Is.EqualTo("foo"));
        Assert.That(search.Matches.Single().Offset, Is.EqualTo(2));
    });

    [Test]
    public void InputReplacementAndClosingRejectOldMatches() => WithView("one two one", async (view, _, _) =>
    {
        var search = view.InFileSearch;
        search.IsVisible = true;
        search.Query = "one";
        search.Query = "two";
        await search.PendingWork;
        Assert.That(search.Matches.Single().Offset, Is.EqualTo(4));
        search.Query = "one";
        search.IsVisible = false;
        await search.PendingWork;
        Assert.That(search.Matches, Is.Empty);
        search.IsVisible = true;
        var match = search.MoveAsync(0, 0, 0, false);
        view.Stop();
        await search.PendingWork;
        Assert.That(await match, Is.Null);
        Assert.That(search.Matches, Is.Empty);
    });

    [Test]
    public void ClosingDuringBatchedScanReleasesReadLease() =>
        WithView(string.Concat(Enumerable.Repeat("x ", SearchLimits.ResultLimit * 2)), async (view, first, _) =>
        {
            var search = view.InFileSearch;
            var handle = view.Tabs.Single(tab => tab.FullPath == first).State.File!.Document;
            var sawBatch = false;
            search.MatchesChanged += () =>
            {
                if (sawBatch || search.Matches.Count == 0) return;
                sawBatch = true;
                Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
                search.IsVisible = false;
            };
            search.IsVisible = true;
            search.Query = "x";
            await search.PendingWork;
            Assert.That(sawBatch, Is.True);
            Assert.That(handle.ActiveReadLeases, Is.Zero);
            Assert.That(search.Matches, Is.Empty);
        });

    [Test]
    public void RegexErrorsAndSkippedLongLinesAreReportedSeparately() =>
        WithView(new string('x', SearchLimits.RegexLineChars + 1) + "\nfoo", async (view, _, _) =>
        {
            var search = view.InFileSearch;
            search.IsVisible = true;
            search.UseRegex = true;
            search.Query = "[";
            await search.PendingWork;
            Assert.That(search.Summary, Does.Contain("不正"));
            search.Query = "^foo$";
            await search.PendingWork;
            Assert.That(search.Matches.Single().Offset, Is.EqualTo(SearchLimits.RegexLineChars + 2));
            Assert.That(search.Summary, Does.Contain("未評価"));
        });

    private static void WithView(string text, Func<MainWindowViewModel, string, string, Task> run)
        => SingleThreadTestContext.Run(async () =>
    {
        var root = Directory.CreateTempSubdirectory("codeview-in-file-");
        var first = Path.Combine(root.FullName, "first.txt");
        var second = Path.Combine(root.FullName, "second.txt");
        await File.WriteAllTextAsync(first, text);
        await File.WriteAllTextAsync(second, "bar");
        var source = new SourceAccess();
        using var operations = new AppOperations(new(source), new AvaloniaEditDocumentAdapter(), new(source));
        var view = new MainWindowViewModel(operations, new SettingsStore(new(
            Path.Combine(root.FullName, "portable.json"), Path.Combine(root.FullName, "user.json"))), async () => await Task.Yield());
        try
        {
            await view.InitializeAsync([second, first]);
            Assert.That(view.FilePath, Is.EqualTo(first));
            await run(view, first, second);
        }
        finally
        {
            view.Stop();
            await view.PendingWork;
            root.Delete(true);
        }
    });
}
