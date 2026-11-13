using System.Text;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Search;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class SearchPanelTests
{
    private string _root = null!;
    private static async Task YieldAsync() => await Task.Yield();

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "codeview-search-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.cs"), "foo bar FOO\nfood");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    private async Task<AppOperations> Open(ISourceAccess source)
    {
        var operations = new AppOperations(new(source), new AvaloniaEditDocumentAdapter(), new(source));
        await operations.OpenFolderAsync(SourcePath.Create(_root, _root), SettingsSnapshot.Default);
        return operations;
    }

    [TestCase(false, false, 3)]
    [TestCase(true, false, 2)]
    [TestCase(false, true, 2)]
    public void ConditionsReachWorkerAndCompletionFollowsFullDrain(bool matchCase, bool wholeWord, int expected)
        => SingleThreadTestContext.Run(async () =>
    {
        using var operations = await Open(new SourceAccess());
        var panel = new SearchPanelViewModel(() => operations.Searches, () => operations.Workspace, YieldAsync);
        panel.RefreshWorkspace();
        panel.Query = "foo";
        panel.MatchCase = matchCase;
        panel.WholeWord = wholeWord;
        await panel.StartAsync();
        await operations.PendingSearchWork;
        Assert.That(panel.Rows.Count, Is.EqualTo(expected));
        Assert.That(panel.Rows.All(panel.CanActivate), Is.True);
        Assert.That(panel.IsRunning, Is.False);
        Assert.That(panel.Summary, Does.Contain("検索完了").And.Contain($"確定 {expected}件"));
        Assert.That(panel.Progress, Does.Contain("走査 1ファイル"));
    });

    [Test]
    public void EditedConditionsArePendingUntilSearchAndClearResetsAppliedConditions() => SingleThreadTestContext.Run(async () =>
    {
        using var operations = await Open(new SourceAccess());
        var panel = new SearchPanelViewModel(() => operations.Searches, () => operations.Workspace, YieldAsync) { Query = "foo" };
        panel.RefreshWorkspace();
        await panel.StartAsync();
        var original = panel.CurrentOperation;
        Assert.That(panel.AppliedConditions, Does.Contain("「foo」").And.Contain("大小文字を無視"));
        Assert.That(panel.HasUnappliedConditions, Is.False);

        panel.MatchCase = true;
        Assert.That(panel.HasUnappliedConditions, Is.True);
        panel.MatchCase = false;
        Assert.That(panel.HasUnappliedConditions, Is.False);
        panel.Query = "";
        Assert.That(panel.HasUnappliedConditions, Is.True);
        Assert.That(panel.CanStart, Is.False);
        Assert.That(panel.CurrentOperation, Is.SameAs(original));
        Assert.That(panel.Rows.Count, Is.EqualTo(3));

        panel.Query = "bar";
        panel.WholeWord = true;
        await panel.StartAsync();
        Assert.That(panel.HasUnappliedConditions, Is.False);
        Assert.That(panel.AppliedConditions, Does.Contain("「bar」").And.Contain("単語単位"));
        Assert.That(panel.Rows.Count, Is.EqualTo(1));
        panel.Clear();
        Assert.That(panel.AppliedConditions, Is.Empty);
        Assert.That(panel.HasUnappliedConditions, Is.False);
    });

    [Test]
    public void CancelRemovesUnadoptedFileButKeepsAdoptedCommittedRows() => SingleThreadTestContext.Run(async () =>
    {
        // A full batch is observable before final stamp verification; a short batch may stay local until EOF.
        File.WriteAllText(Path.Combine(_root, "b.cs"), string.Concat(Enumerable.Repeat("foo  ", 128)));
        using var source = new GatedSource(6);
        using var operations = await Open(source);
        var panel = new SearchPanelViewModel(() => operations.Searches, () => operations.Workspace, YieldAsync) { Query = "foo", WholeWord = true };
        panel.RefreshWorkspace();
        var work = panel.StartAsync();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => panel.Rows.Count == 130);
            Assert.That(panel.Rows.Count(panel.CanActivate), Is.EqualTo(2));
            panel.SelectedRow = panel.Rows.Last();
            panel.Cancel();
            Assert.That(panel.Rows.Count, Is.EqualTo(2));
            Assert.That(panel.SelectedRow, Is.Null);
        }
        finally { source.Release(); }
        await work;
        await panel.PendingWork;
        await operations.PendingSearchWork;
        Assert.That(panel.Rows.All(panel.CanActivate), Is.True);
        Assert.That(panel.Summary, Does.Contain("取り消しました").And.Contain("部分結果"));
    });

    [Test]
    public void HidingPanelKeepsSearchAndResultsAlive() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new GatedSource(3);
        using var operations = await Open(source);
        var panel = new SearchPanelViewModel(() => operations.Searches, () => operations.Workspace, YieldAsync) { Query = "foo" };
        panel.RefreshWorkspace();
        var work = panel.StartAsync();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            panel.IsVisible = false;
            Assert.That(panel.IsRunning, Is.True);
            Assert.That(operations.Searches.Current!.CancellationCause, Is.EqualTo(SearchCancellationCause.None));
        }
        finally { source.Release(); }
        await work;
        await operations.PendingSearchWork;
        Assert.That(panel.IsVisible, Is.False);
        Assert.That(panel.Rows.Count, Is.EqualTo(3));
        Assert.That(panel.Rows.All(panel.CanActivate), Is.True);
    });

    [Test]
    public void CancelAfterWorkerCommitDropsCommitStillWaitingForUiAdoption() => SingleThreadTestContext.Run(async () =>
    {
        File.WriteAllText(Path.Combine(_root, "b.cs"), string.Concat(Enumerable.Repeat("foo  ", 128)));
        using var source = new GatedSource(6);
        using var operations = await Open(source);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SearchPanelViewModel? panel = null;
        async Task Yield()
        {
            if (panel!.Rows.Count == 130) { held.TrySetResult(); await releaseUi.Task; }
            else await Task.Yield();
        }
        panel = new(() => operations.Searches, () => operations.Workspace, Yield) { Query = "foo", WholeWord = true };
        panel.RefreshWorkspace();
        var work = panel.StartAsync();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.Release();
            await operations.PendingSearchWork;
            var end = (await operations.Searches.Current!.Completion).Value;
            Assert.That(end.UncommittedFileScanId, Is.Null);
            Assert.That(end.CommittedHits, Is.EqualTo(130));
            panel.Cancel();
            Assert.That(panel.Rows.Count, Is.EqualTo(2));
        }
        finally { source.Release(); releaseUi.TrySetResult(); }
        await work;
        Assert.That(panel.Rows.Count, Is.EqualTo(2));
        Assert.That(panel.Rows.All(panel.CanActivate), Is.True);
        Assert.That(panel.Summary, Does.Contain("取り消しました"));
    });

    [Test]
    public void ClearAndReplacementCannotAdoptOldBatchOrCompletion() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new GatedSource(3);
        using var operations = await Open(source);
        var panel = new SearchPanelViewModel(() => operations.Searches, () => operations.Workspace, YieldAsync) { Query = "foo" };
        panel.RefreshWorkspace();
        var oldWork = panel.StartAsync();
        Task next = Task.CompletedTask;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => panel.Rows.Count > 0);
            panel.Clear();
            Assert.That(panel.Rows, Is.Empty);
            panel.Query = "bar";
            next = panel.StartAsync();
            Assert.That(source.OpenCount, Is.EqualTo(1));
        }
        finally { source.Release(); }
        await Task.WhenAll(oldWork, next);
        await operations.PendingSearchWork;
        Assert.That(panel.Rows.Count, Is.EqualTo(1));
        Assert.That(panel.Rows.Single().Match, Is.EqualTo("bar"));
        Assert.That(panel.Summary, Does.Contain("検索完了"));
    });

    [Test]
    public void ProgressCanRefreshWithoutResultEventsAndTimerStopsOnCancellation() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new GatedSource(3);
        using var operations = await Open(source);
        var panel = new SearchPanelViewModel(() => operations.Searches, () => operations.Workspace, YieldAsync) { Query = "absent" };
        panel.RefreshWorkspace();
        var progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(panel.Progress) && panel.Progress.Contains("走査")) progress.TrySetResult();
        };
        var work = panel.StartAsync();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await progress.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(panel.Rows, Is.Empty);
            Assert.That(panel.IsRunning, Is.True);
            panel.Cancel();
        }
        finally { source.Release(); }
        await work;
        await panel.PendingWork;
        await operations.PendingSearchWork;
        Assert.That(panel.IsRunning, Is.False);
    });

    [Test]
    public void MainWindowWorkspaceCloseClearsRowsAndTracksPendingSearch() => SingleThreadTestContext.Run(async () =>
    {
        var folder = Path.Combine(_root, "nested");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "body.cs"), "foo");
        using var source = new GatedSource(3);
        using var operations = new AppOperations(new(source), new AvaloniaEditDocumentAdapter(), new(source));
        var view = new MainWindowViewModel(operations, new SettingsStore(new(Path.Combine(_root, "portable.json"), Path.Combine(_root, "user.json"))), YieldAsync);
        await view.InitializeAsync([folder]);
        view.SearchPanel.Query = "foo";
        var work = view.SearchPanel.StartAsync();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => view.SearchPanel.Rows.Count == 1);
            view.CloseWorkspace();
            Assert.That(view.SearchPanel.Rows, Is.Empty);
            Assert.That(view.SearchPanel.CanStart, Is.False);
            Assert.That(view.PendingWork.IsCompleted, Is.False);
        }
        finally { source.Release(); }
        await work;
        view.Stop();
        await view.PendingWork;
        Assert.That(view.SearchPanel.Rows, Is.Empty);
        Assert.That(view.SearchPanel.Summary, Does.Contain("フォルダを開いて"));
    });

    [Test]
    public void TenThousandResultsAreIncrementalAndLastFileRemainsUncommitted() => SingleThreadTestContext.Run(async () =>
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), string.Concat(Enumerable.Repeat("foo ", 10001)));
        using var operations = await Open(new SourceAccess());
        var yields = 0;
        var maximumRowsPerSlice = 0;
        var previous = 0;
        SearchPanelViewModel? panel = null;
        async Task Yield()
        {
            var count = panel!.Rows.Count;
            maximumRowsPerSlice = Math.Max(maximumRowsPerSlice, count - previous);
            previous = count;
            yields++;
            await Task.Yield();
        }
        panel = new(() => operations.Searches, () => operations.Workspace, Yield) { Query = "foo" };
        panel.RefreshWorkspace();
        await panel.StartAsync();
        await operations.PendingSearchWork;
        Assert.That(panel.Rows.Count, Is.EqualTo(10000));
        Assert.That(panel.Rows.All(row => !panel.CanActivate(row)), Is.True);
        Assert.That(panel.Rows.SingleOrDefault(row => row.File.IsCommitted), Is.Null);
        Assert.That(panel.Rows[0].File.Status, Is.EqualTo("未完了・移動不可"));
        Assert.That(maximumRowsPerSlice, Is.LessThanOrEqualTo(256));
        Assert.That(yields, Is.GreaterThanOrEqualTo(40));
        Assert.That(panel.Summary, Does.Contain("10,000件で打ち切り"));
    });

    [TestCase(false)]
    [TestCase(true)]
    public void InvalidConditionsAndRegexPartialCoverageAreExplained(bool invalid) => SingleThreadTestContext.Run(async () =>
    {
        if (!invalid) File.WriteAllText(Path.Combine(_root, "a.cs"), new string('a', SearchLimits.RegexLineChars + 1) + "\nfoo");
        using var operations = await Open(new SourceAccess());
        var panel = new SearchPanelViewModel(() => operations.Searches, () => operations.Workspace, YieldAsync) { Query = invalid ? "[" : "foo", UseRegex = true };
        panel.RefreshWorkspace();
        await panel.StartAsync();
        await operations.PendingSearchWork;
        Assert.That(panel.Summary, invalid ? Does.Contain("検索条件が不正") : Does.Contain("長行未評価 1行").And.Contain("部分結果"));
        Assert.That(panel.Rows.Count, Is.EqualTo(invalid ? 0 : 1));
    });

    [Test]
    public void UnknownWorkerFailureIsPropagatedAndProvisionalRowsAreRemoved() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new GatedSource(3) { Fail = true };
        using var operations = await Open(source);
        var panel = new SearchPanelViewModel(() => operations.Searches, () => operations.Workspace, YieldAsync) { Query = "foo" };
        panel.RefreshWorkspace();
        var work = panel.StartAsync();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => panel.Rows.Count > 0);
        }
        finally { source.Release(); }
        var failed = false;
        try { await work; } catch (SearchWorkerException) { failed = true; }
        Assert.That(failed, Is.True);
        try { await operations.PendingSearchWork; } catch (SearchWorkerException) { }
        Assert.That(panel.Rows, Is.Empty);
        Assert.That(panel.IsRunning, Is.False);
    });

    [Test, Category("Performance")]
    public void MeasureTenThousandProjectedSearchRows() => SingleThreadTestContext.Run(async () =>
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), string.Concat(Enumerable.Repeat("foo ", 10001)));
        using var operations = await Open(new SourceAccess());
        var panel = new SearchPanelViewModel(() => operations.Searches, () => operations.Workspace, YieldAsync) { Query = "foo" };
        panel.RefreshWorkspace();
        using var memory = new CodeView.Tests.Performance.ProcessMemory();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var before = memory.ReadBytes();
        var peak = before;
        var notifications = 0;
        var maxRows = 0;
        panel.Rows.CollectionChanged += (_, change) =>
        {
            notifications++;
            maxRows = Math.Max(maxRows, change.NewItems?.Count ?? 0);
            peak = Math.Max(peak, memory.ReadBytes());
        };
        var allocated = GC.GetTotalAllocatedBytes(true);
        var pause = GC.GetTotalPauseDuration();
        var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var cpu = process.TotalProcessorTime;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await panel.StartAsync();
        await operations.PendingSearchWork;
        watch.Stop();
        peak = Math.Max(peak, memory.ReadBytes());
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        var pauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
        TestContext.Progress.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            Scenario = "search-panel-model-10000", Rows = panel.Rows.Count,
            ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds, CpuMilliseconds = cpuMs,
            AllocatedBytes = bytes, MemoryMetric = memory.Metric, MemoryBefore = before, SampledPeakMemory = peak,
            MemoryIncrease = peak - before, GcPauseMilliseconds = pauseMs,
            GcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray(),
            Notifications = notifications, MaximumAddedRowsPerNotification = maxRows,
            GuiRendering = "not-measured; serialized test context and Task.Yield, no native controls",
            MemorySampling = "at collection notifications and after completion; instantaneous peak not guaranteed"
        }));
        Assert.That(panel.Rows.Count, Is.EqualTo(10000));
        Assert.That(panel.Rows.All(row => !panel.CanActivate(row)), Is.True);
        Assert.That(maxRows, Is.InRange(1, 256));
        Assert.That(notifications, Is.GreaterThan(1));
        Assert.That(watch.Elapsed.TotalMilliseconds, Is.LessThanOrEqualTo(1000));
        Assert.That(cpuMs, Is.LessThanOrEqualTo(1000));
        Assert.That(bytes, Is.LessThanOrEqualTo(32 << 20));
        Assert.That(peak - before, Is.LessThanOrEqualTo(64 << 20));
        Assert.That(pauseMs, Is.LessThanOrEqualTo(100));
    });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (timeout.IsCompleted) throw new TimeoutException();
            await Task.Yield();
        }
    }

    private sealed class GatedSource(int stampGate) : ISourceAccess, IDisposable
    {
        private readonly SourceAccess _inner = new();
        private readonly ManualResetEventSlim _release = new();
        private int _stamps;
        public int OpenCount { get; private set; }
        public bool Fail { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.Set();
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct)
        { OpenCount++; return _inner.OpenRead(path, ct); }
        public SourceStamp GetStamp(SourcePath path)
        {
            var stamp = _inner.GetStamp(path);
            if (Interlocked.Increment(ref _stamps) != stampGate) return stamp;
            Entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            if (Fail) throw new InvalidOperationException("injected");
            return stamp;
        }
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => _inner.ResolveDirectory(path, ct);
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct) => _inner.EnumerateChildren(path, ct).OrderBy(entry => entry.Name, StringComparer.Ordinal);
        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => _inner.InspectPath(path, ct);
        public void Dispose() { _release.Set(); _release.Dispose(); }
    }
}
