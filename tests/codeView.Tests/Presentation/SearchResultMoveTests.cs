using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class SearchResultMoveTests
{
    private static async Task YieldAsync() => await Task.Yield();

    [TestCase(false)]
    [TestCase(true)]
    public void ConfirmedResultOpensOrReusesTabAndSelectsOriginalCase(bool preopen) => WithView(preopen, async f =>
    {
        var row = f.View.SearchPanel.Rows[0];
        var before = f.View.Document;
        var opens = f.Source.OpenCount;
        (long Offset, int Length)? selection = null;
        f.View.SelectSearchRange += (offset, length) => selection = (offset, length);
        await f.View.ActivateSearchResultAsync(row);
        Assert.That(f.View.Tabs.Count, Is.EqualTo(1));
        Assert.That(selection, Is.EqualTo((4L, 3)));
        Assert.That(f.View.Position, Is.EqualTo("行 1, 列 5"));
        Assert.That(f.View.Status, Does.Contain("移動しました"));
        Assert.That(f.Source.OpenCount, Is.EqualTo(opens + (preopen ? 0 : 1)));
        if (preopen) Assert.That(f.View.Document, Is.SameAs(before));
    });

    [Test]
    public void ExternalChangeKeepsOldBodyAndSuppressesSelection() => WithView(true, async f =>
    {
        var body = f.View.Document;
        await File.WriteAllTextAsync(f.File, "externally changed body");
        var length = -1;
        f.View.SelectSearchRange += (_, count) => length = count;
        await f.View.ActivateSearchResultAsync(f.View.SearchPanel.Rows[0]);
        Assert.That(length, Is.Zero);
        Assert.That(f.View.Document, Is.SameAs(body));
        Assert.That(f.View.Document!.Text, Does.Contain("FOO"));
        Assert.That(f.View.Status, Does.Contain("検索時と異なります").And.Contain("再検索"));
    });

    [Test]
    public void SameStampDifferentTextDoesNotSelectAnUnverifiedMatch() => WithView(false, async f =>
    {
        var row = f.View.SearchPanel.Rows[0];
        var text = await File.ReadAllTextAsync(f.File);
        await File.WriteAllTextAsync(f.File, text.Replace("FOO", "BAR", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(f.File, row.Hit.Stamp.LastWriteUtc);
        Assert.That(new SourceAccess().GetStamp(row.Hit.Path), Is.EqualTo(row.Hit.Stamp));
        var length = -1;
        f.View.SelectSearchRange += (_, count) => length = count;
        await f.View.ActivateSearchResultAsync(row);
        Assert.That(length, Is.Zero);
        Assert.That(f.View.Status, Does.Contain("一致内容が変わっています"));
    });

    [Test]
    public void ChangedShorterFileClampsOldPositionAndDoesNotSelect() => WithView(false, async f =>
    {
        var row = f.View.SearchPanel.Rows[1];
        await File.WriteAllTextAsync(f.File, "x");
        (long, int)? selection = null;
        f.View.SelectSearchRange += (offset, length) => selection = (offset, length);
        await f.View.ActivateSearchResultAsync(row);
        Assert.That(selection, Is.EqualTo((1L, 0)));
        Assert.That(f.View.Position, Is.EqualTo("行 1, 列 2"));
        Assert.That(f.View.Status, Does.Contain("結果位置が古い"));
    });

    [Test]
    public void MetadataFailureDoesNotNavigateAndIsRetryable() => WithView(true, async f =>
    {
        File.Delete(f.File);
        var selected = false;
        f.View.SelectSearchRange += (_, _) => selected = true;
        await f.View.ActivateSearchResultAsync(f.View.SearchPanel.Rows[0]);
        Assert.That(selected, Is.False);
        Assert.That(f.View.Status, Does.Contain("見つかりません").And.Contain("再検索"));
    });

    [Test]
    public void LatestWaitingMoveWinsWithoutParallelMetadataReads() => WithView(true, async f =>
    {
        f.Source.Arm();
        var first = f.View.ActivateSearchResultAsync(f.View.SearchPanel.Rows[0]);
        Task middle = Task.CompletedTask, latest = Task.CompletedTask;
        var selections = new List<long>();
        f.View.SelectSearchRange += (offset, _) => selections.Add(offset);
        try
        {
            await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var calls = f.Source.StampCalls;
            middle = f.View.ActivateSearchResultAsync(f.View.SearchPanel.Rows[0]);
            latest = f.View.ActivateSearchResultAsync(f.View.SearchPanel.Rows[1]);
            await middle;
            Assert.That(f.Source.StampCalls, Is.EqualTo(calls));
            Assert.That(latest.IsCompleted, Is.False);
        }
        finally { f.Source.Release(); }
        await Task.WhenAll(first, middle, latest);
        Assert.That(selections, Is.EqualTo(new long[] { 9 }));
    });

    [TestCase("switch")]
    [TestCase("reload")]
    [TestCase("close")]
    [TestCase("clear")]
    [TestCase("root")]
    [TestCase("shutdown")]
    [TestCase("input")]
    public void LateMetadataCannotApplyAfterOwnerOrTabChanges(string action) => WithView(true, async f =>
    {
        var other = Path.Combine(f.Root, "other.txt");
        await File.WriteAllTextAsync(other, "other");
        var target = f.View.SelectedTab!;
        var selected = false;
        f.View.SelectSearchRange += (_, _) => selected = true;
        f.Source.Arm();
        var work = f.View.ActivateSearchResultAsync(f.View.SearchPanel.Rows[0]);
        try
        {
            await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            switch (action)
            {
                case "switch":
                    await f.View.OpenFilesAsync([other], OpenOrigin.Menu);
                    f.View.SelectedTab = target; // Same tab again; the activation epoch still changed.
                    break;
                case "reload": await f.View.ReloadAsync(); break;
                case "close": f.View.CloseTab(target); break;
                case "clear": f.View.SearchPanel.Clear(); break;
                case "root": f.View.CloseWorkspace(); break;
                case "shutdown": f.View.Stop(); break;
                case "input": f.View.RecordUserInteraction(); f.View.SetPosition(2, 2); break;
            }
        }
        finally { f.Source.Release(); }
        await work;
        Assert.That(selected, Is.False);
        if (action == "input") Assert.That(f.View.Position, Is.EqualTo("行 2, 列 2"));
    });

    [Test]
    public void LongRegexMatchMovesWithoutClaimingFullSelection() => WithView(false, async f =>
    {
        await File.WriteAllTextAsync(f.File, new string('a', 300));
        f.View.SearchPanel.Query = "a+";
        f.View.SearchPanel.UseRegex = true;
        await f.View.SearchPanel.StartAsync();
        var length = -1;
        f.View.SelectSearchRange += (_, count) => length = count;
        await f.View.ActivateSearchResultAsync(f.View.SearchPanel.Rows.Single());
        Assert.That(length, Is.Zero);
        Assert.That(f.View.Status, Does.Contain("長い一致"));
    });

    [Test]
    public void ClearedResultNeverCreatesATab() => WithView(false, async f =>
    {
        var row = f.View.SearchPanel.Rows[0];
        f.View.SearchPanel.Clear();
        var opens = f.Source.OpenCount;
        await f.View.ActivateSearchResultAsync(row);
        Assert.That(f.View.Tabs, Is.Empty);
        Assert.That(f.Source.OpenCount, Is.EqualTo(opens));
    });

    [Test]
    public void ProvisionalResultCannotStartAFileLoad() => WithView(false, async f =>
    {
        f.Source.Arm(3); // Prepare before/after validation, then final stamp after the first provisional hit.
        var search = f.View.SearchPanel.StartAsync();
        try
        {
            await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var deadline = Task.Delay(TimeSpan.FromSeconds(5));
            while (f.View.SearchPanel.Rows.Count == 0)
            {
                if (deadline.IsCompleted) throw new TimeoutException();
                await Task.Yield();
            }
            var row = f.View.SearchPanel.Rows[0];
            Assert.That(row.File.IsCommitted, Is.False);
            var opens = f.Source.OpenCount;
            await f.View.ActivateSearchResultAsync(row);
            Assert.That(f.View.Tabs, Is.Empty);
            Assert.That(f.Source.OpenCount, Is.EqualTo(opens));
        }
        finally { f.Source.Release(); }
        await search;
    });

    [Test]
    public void DeferredSelectionMustStillOwnSourceTabAndUserInteraction() => WithView(true, async f =>
    {
        await f.View.ActivateSearchResultAsync(f.View.SearchPanel.Rows[0]);
        var epoch = f.View.ViewEpoch;
        Assert.That(f.View.CanApplyResultSelection(epoch), Is.True);
        f.View.RecordUserInteraction();
        Assert.That(f.View.CanApplyResultSelection(epoch), Is.False);
        await f.View.ActivateSearchResultAsync(f.View.SearchPanel.Rows[0]);
        epoch = f.View.ViewEpoch;
        Assert.That(f.View.CanApplyResultSelection(epoch), Is.True);
        f.View.SearchPanel.Clear();
        Assert.That(f.View.CanApplyResultSelection(epoch), Is.False);
    });

    [Test]
    public void ClosingTargetDuringInitialLoadCannotRestoreIt() => WithView(false, async f =>
    {
        f.Source.Arm();
        var selected = false;
        f.View.SelectSearchRange += (_, _) => selected = true;
        var work = f.View.ActivateSearchResultAsync(f.View.SearchPanel.Rows[0]);
        try
        {
            await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            while (f.View.Tabs.Count == 0) await Task.Yield();
            f.View.CloseTab(f.View.Tabs.Single());
        }
        finally { f.Source.Release(); }
        await work;
        Assert.That(f.View.Tabs, Is.Empty);
        Assert.That(f.View.Document, Is.Null);
        Assert.That(selected, Is.False);
    });

    [Test]
    public void UnknownMetadataFailureStopsLatestWaiterAndIsObservedAtShutdown() => WithView(true, async f =>
    {
        f.Source.GateFailure = new InvalidOperationException("injected");
        f.Source.Arm();
        var first = f.View.ActivateSearchResultAsync(f.View.SearchPanel.Rows[0]);
        Task next = Task.CompletedTask;
        try
        {
            await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            next = f.View.ActivateSearchResultAsync(f.View.SearchPanel.Rows[1]);
        }
        finally { f.Source.Release(); }
        var failed = false;
        try { await first; } catch (InvalidOperationException error) { failed = error.Message == "injected"; }
        Assert.That(failed, Is.True);
        await next;
        failed = false;
        try { await f.View.PendingWork; } catch (InvalidOperationException error) { failed = error.Message == "injected"; }
        Assert.That(failed, Is.True);
    }, expectFailure: true);

    [Test, Category("Performance")]
    public void MeasureRepeatedMovesInAlreadyOpenDocument() => WithView(true, async f =>
    {
        var row = f.View.SearchPanel.Rows[0];
        var body = f.View.Document;
        var opens = f.Source.OpenCount;
        var stamps = f.Source.StampCalls;
        var selections = 0;
        f.View.SelectSearchRange += (_, length) => { if (length == 3) selections++; };
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var cpu = process.TotalProcessorTime;
        var pause = GC.GetTotalPauseDuration();
        var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var maximum = 0d;
        for (var index = 0; index < 1000; index++)
        {
            var start = watch.Elapsed.TotalMilliseconds;
            await f.View.ActivateSearchResultAsync(row);
            maximum = Math.Max(maximum, watch.Elapsed.TotalMilliseconds - start);
        }
        await f.View.PendingWork;
        watch.Stop();
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        var pauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
        TestContext.Progress.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            Scenario = "open-document-result-moves-1000", ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            CpuMilliseconds = cpuMs, AllocatedBytes = bytes, MaximumMoveMilliseconds = maximum,
            GcPauseMilliseconds = pauseMs, GcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray(),
            AdditionalBodyOpens = f.Source.OpenCount - opens, StampChecks = f.Source.StampCalls - stamps,
            Selections = selections, GuiRendering = "not-measured; serialized test context, native acceptance recorded separately"
        }));
        Assert.That(f.Source.OpenCount, Is.EqualTo(opens));
        Assert.That(f.Source.StampCalls - stamps, Is.EqualTo(1000));
        Assert.That(f.View.Document, Is.SameAs(body));
        Assert.That(selections, Is.EqualTo(1000));
        Assert.That(watch.Elapsed.TotalMilliseconds, Is.LessThanOrEqualTo(2000));
        Assert.That(cpuMs, Is.LessThanOrEqualTo(2000));
        Assert.That(maximum, Is.LessThanOrEqualTo(100));
        Assert.That(bytes, Is.LessThanOrEqualTo(16 << 20));
        Assert.That(pauseMs, Is.LessThanOrEqualTo(100));
    });

    private static void WithView(bool preopen, Func<Fixture, Task> run, bool expectFailure = false) => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-result-move-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "repo");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "body.cs");
        await File.WriteAllTextAsync(file, "a😀 FOO\r\nfoo");
        using var source = new ControlledSource();
        using var operations = new AppOperations(new(source), new AvaloniaEditDocumentAdapter(), new(source));
        var view = new MainWindowViewModel(operations, new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))), YieldAsync);
        try
        {
            await view.InitializeAsync(preopen ? [folder, file] : [folder]);
            view.SearchPanel.Query = "foo";
            await view.SearchPanel.StartAsync();
            await run(new(root, file, view, source));
        }
        finally
        {
            source.Release();
            view.Stop();
            try { await view.PendingWork; } catch (InvalidOperationException) when (expectFailure) { }
            Directory.Delete(root, true);
        }
    });

    private sealed record Fixture(string Root, string File, MainWindowViewModel View, ControlledSource Source);
    private sealed class ControlledSource : ISourceAccess, IDisposable
    {
        private readonly SourceAccess _inner = new();
        private readonly ManualResetEventSlim _release = new();
        private int _gateAt, _opens, _stamps;
        public int OpenCount => Volatile.Read(ref _opens);
        public int StampCalls => Volatile.Read(ref _stamps);
        public Exception? GateFailure { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm(int stampsAhead = 1) => Volatile.Write(ref _gateAt, StampCalls + stampsAhead);
        public void Release() => _release.Set();
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct)
        { Interlocked.Increment(ref _opens); return _inner.OpenRead(path, ct); }
        public SourceStamp GetStamp(SourcePath path)
        {
            var count = Interlocked.Increment(ref _stamps);
            var stamp = _inner.GetStamp(path);
            if (count == Volatile.Read(ref _gateAt))
            {
                Volatile.Write(ref _gateAt, 0);
                Entered.TrySetResult();
                if (!_release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                if (GateFailure is { } failure) throw failure;
            }
            return stamp;
        }
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => _inner.ResolveDirectory(path, ct);
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct) => _inner.EnumerateChildren(path, ct);
        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => _inner.InspectPath(path, ct);
        public void Dispose() { _release.Set(); _release.Dispose(); }
    }
}
