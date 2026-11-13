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

    [TestCase("replace")]
    [TestCase("switch")]
    [TestCase("reload")]
    [TestCase("close")]
    public void AlreadyPublishingOldScanCannotAppendAfterStateChange(string change)
    {
        const string prefix = "middle final ";
        WithView(prefix + string.Concat(Enumerable.Repeat("old ", SearchLimits.ResultLimit * 2)),
            async (view, first, second) =>
            {
                var search = view.InFileSearch;
                var firstTab = view.Tabs.Single(tab => tab.FullPath == first);
                var secondTab = view.Tabs.Single(tab => tab.FullPath == second);
                var handle = firstTab.State.File!.Document;
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task reload = Task.CompletedTask;
                var observedLeases = 0;
                var changed = false;
                search.MatchesChanged += () =>
                {
                    if (changed || search.Matches.Count == 0) return;
                    changed = true;
                    observedLeases = handle.ActiveReadLeases;
                    switch (change)
                    {
                        case "replace":
                            search.Query = "middle";
                            search.Query = "final";
                            break;
                        case "switch":
                            view.SelectedTab = secondTab;
                            search.Query = "bar";
                            break;
                        case "reload":
                            File.WriteAllText(first, "new old");
                            reload = view.ReloadAsync();
                            break;
                        case "close":
                            view.CloseTab(firstTab);
                            search.Query = "bar";
                            break;
                    }
                    entered.SetResult();
                };

                search.IsVisible = true;
                search.Query = "old";
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await reload;
                await search.PendingWork;

                var expected = change switch { "replace" => 7, "reload" => 4, _ => 0 };
                Assert.That(observedLeases, Is.EqualTo(1));
                Assert.That(handle.ActiveReadLeases, Is.Zero);
                Assert.That(search.Matches.Select(match => match.Offset), Is.EqualTo(new long[] { expected }));
                Assert.That(view.FilePath, Is.EqualTo(change is "switch" or "close" ? second : first));
            });
    }

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
            var revision = search.Revision;
            Assert.That(await search.MoveAsync(0, 0, 0, false), Is.Null);
            Assert.That(search.Revision, Is.EqualTo(revision));
            search.Query = "^foo$";
            await search.PendingWork;
            Assert.That(search.Matches.Single().Offset, Is.EqualTo(SearchLimits.RegexLineChars + 2));
            Assert.That(search.Summary, Does.Contain("長行未評価 1行"));
        });

    [Test]
    public void RegexResultsDescribeSkippedSurrogatesBeforeAndAfterNavigation() =>
        WithView("😀x", async (view, _, _) =>
        {
            var search = view.InFileSearch;
            search.IsVisible = true;
            search.UseRegex = true;
            search.Query = ".";
            await search.PendingWork;
            Assert.That(search.Matches.Single().Offset, Is.EqualTo(2));
            Assert.That(search.Summary, Does.Contain("文字の途中での一致を除外 2件"));
            Assert.That((await search.MoveAsync(3, 3, 0, false))?.Offset, Is.EqualTo(2));
            Assert.That(search.Summary, Does.Contain("折り返しました").And.Contain("文字の途中での一致を除外 2件"));
        });

    [TestCase(1)]
    [TestCase(4096)]
    public void BackgroundNotificationFaultSurvivesTaskCleanupAndReleasesLease(int occurrences) =>
        WithView(string.Concat(Enumerable.Repeat("needle ", occurrences)), async (view, _, _) =>
        {
            var search = view.InFileSearch;
            var handle = view.SelectedTab!.State.File!.Document;
            var failure = new InvalidOperationException("search result notification failed");
            var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notifications = 0;
            search.FatalFailure += () =>
            {
                notifications++;
                signalled.TrySetResult();
            };
            search.MatchesChanged += () =>
            {
                if (search.Matches.Count > 0 && notifications == 0) throw failure;
            };
            search.IsVisible = true;
            search.Query = "needle";
            await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(search.PendingWork.Exception!.Flatten().InnerExceptions, Does.Contain(failure));
            var failures = 0;
            await MainWindow.StopAndWaitAsync(view.Stop, () => view.PendingWork, () => failures++);
            Assert.That(failures, Is.EqualTo(1));
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(handle.IsReleased, Is.True);
            Assert.That(handle.ActiveReadLeases, Is.Zero);
        }, expectPendingFailure: true);

    private static void WithView(string text, Func<MainWindowViewModel, string, string, Task> run, bool expectPendingFailure = false)
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
            try { await view.PendingWork; }
            catch (InvalidOperationException) when (expectPendingFailure) { }
            finally { root.Delete(true); }
        }
    });
}
