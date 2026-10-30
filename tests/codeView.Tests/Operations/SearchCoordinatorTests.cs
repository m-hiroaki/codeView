using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Search;
using CodeView.Settings;

namespace CodeView.Tests.Operations;

internal sealed class SearchCoordinatorTests
{
    private string _root = null!;
    private SourcePath PathOf(string name) => SourcePath.Create(name, _root);
    private static AppOperations Create(ISourceAccess source) => new(new(source), new AvaloniaEditDocumentAdapter(), new(source));

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "codeview-search-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "one"));
        Directory.CreateDirectory(Path.Combine(_root, "two"));
        File.WriteAllText(Path.Combine(_root, "one", "body.cs"), "foo bar baz");
        File.WriteAllText(Path.Combine(_root, "two", "body.cs"), "foo");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    private static async Task<List<SearchEvent<SearchHit>>> Drain(SearchCoordinator searches, WorkspaceSearchOperation operation)
    {
        var events = new List<SearchEvent<SearchHit>>();
        if (!await operation.Started) return events;
        while (await searches.WaitForEventsAsync(operation))
            while (searches.TryRead(operation, out var item)) events.Add(item!);
        return events;
    }

    [Test]
    public void RootMustBeReadyAndRequestUsesFixedWorkspaceSettings() => SingleThreadTestContext.Run(async () =>
    {
        using var operations = Create(new SourceAccess());
        var searches = operations.Searches;
        Assert.That(searches.CanStart, Is.False);
        Assert.That(searches.Start("foo").Status, Is.EqualTo(OperationStatus.Cancelled));
        var openId = operations.Startup.Submit(["one/body.cs"], OpenOrigin.Menu).Value.Id;
        var owner = (await operations.OpenFolderAsync(PathOf("one"), new SettingsSnapshot(["skip"]))).Value;
        var epoch = operations.WorkspaceSearchEpoch;
        var operation = searches.Start("foo", new(WholeWord: true), EncodingChoice.Utf8).Value;
        Assert.That(operation.Request.Root, Is.SameAs(owner.ResolvedPath));
        Assert.That(operation.Request.Exclusions, Is.SameAs(owner.Policy));
        Assert.That(operation.Request.Encoding, Is.EqualTo(EncodingChoice.Utf8));
        Assert.That(operation.Request.WorkspaceEpoch, Is.EqualTo(owner.Epoch));
        Assert.That(operation.Request.SearchEpoch, Is.GreaterThan(epoch));
        Assert.That(operation.Request.SearchEpoch, Is.EqualTo(operations.WorkspaceSearchEpoch));
        Assert.That(operation.Request.RequestId, Is.GreaterThan(openId));
        Assert.That(operations.Startup.Submit(["two/body.cs"], OpenOrigin.Menu).Value.Id, Is.GreaterThan(operation.Request.RequestId));
        var events = await Drain(searches, operation);
        await operation.Completion;
        await operations.PendingSearchWork;
        Assert.That(events.OfType<SearchEvent<SearchHit>.Hits>().Single().Items.Single().Range.Offset, Is.Zero);
        Assert.That(searches.TryFinalize(operation, out var result), Is.True);
        Assert.That(result!.Value.CommittedHits, Is.EqualTo(1));
    });

    [Test]
    public void LatestWaitingRequestReplacesIntermediateWithoutStartingAnotherWorker() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new ControlledSource();
        using var operations = Create(source);
        await operations.OpenFolderAsync(PathOf("one"), SettingsSnapshot.Default);
        var searches = operations.Searches;
        var first = searches.Start("foo").Value;
        WorkspaceSearchOperation? latest = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var middle = searches.Start("bar").Value;
            latest = searches.Start("baz").Value;
            Assert.That(await middle.Started, Is.False);
            Assert.That((await middle.Completion).Status, Is.EqualTo(OperationStatus.Cancelled));
            Assert.That(middle.CancellationCause, Is.EqualTo(SearchCancellationCause.Replaced));
            Assert.That(latest.Started.IsCompleted, Is.False);
            Assert.That(source.OpenCount, Is.EqualTo(1));
            Assert.That(searches.CanAdoptEvents(first), Is.False);
            Assert.That(searches.CanAdoptTerminal(first), Is.False);
            Assert.That(searches.TryRead(first, out _), Is.False);
        }
        finally { source.Release(); }
        var events = await Drain(searches, latest!);
        var end = (await latest!.Completion).Value;
        await operations.PendingSearchWork;
        Assert.That(source.OpenCount, Is.EqualTo(2));
        Assert.That(searches.Current, Is.SameAs(latest));
        Assert.That(end.CommittedHits, Is.EqualTo(1));
        Assert.That(events.OfType<SearchEvent<SearchHit>.Hits>().Single().Items.Single().Range.Offset, Is.EqualTo(8));
        Assert.That(first.Session!.Events.Count, Is.Zero);
        Assert.That((await first.Completion).Value.Reason, Is.EqualTo(SearchEndReason.Cancelled));
    });

    [TestCase(false)]
    [TestCase(true)]
    public void RootSwitchOrCloseRejectsLateEventsAndCompletion(bool replace) => SingleThreadTestContext.Run(async () =>
    {
        using var source = new ControlledSource();
        using var operations = Create(source);
        await operations.OpenFolderAsync(PathOf("one"), SettingsSnapshot.Default);
        var searches = operations.Searches;
        var old = searches.Start("foo").Value;
        WorkspaceSearchOperation? next = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (replace)
            {
                await operations.OpenFolderAsync(PathOf("two"), SettingsSnapshot.Default);
                next = searches.Start("foo").Value;
                Assert.That(next.Started.IsCompleted, Is.False);
            }
            else operations.CloseWorkspace();
            Assert.That(old.CancellationCause, Is.EqualTo(SearchCancellationCause.WorkspaceChanged));
            Assert.That(searches.CanAdoptTerminal(old), Is.False);
            Assert.That(source.OpenCount, Is.EqualTo(1));
        }
        finally { source.Release(); }
        if (next is not null)
        {
            var events = await Drain(searches, next);
            await next.Completion;
            var hit = events.OfType<SearchEvent<SearchHit>.Hits>().Single().Items.Single();
            Assert.That(hit.Path.FullPath, Is.EqualTo(PathOf("two/body.cs").FullPath));
        }
        await old.Completion;
        await operations.PendingSearchWork;
        Assert.That(old.Session!.Events.Count, Is.Zero);
        Assert.That(searches.TryFinalize(old, out _), Is.False);
    });

    [Test]
    public void ExplicitCancelRetainsCurrentIdentityButDropsQueuedEvents() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new ControlledSource();
        using var operations = Create(source);
        await operations.OpenFolderAsync(PathOf("one"), SettingsSnapshot.Default);
        var searches = operations.Searches;
        var operation = searches.Start("foo").Value;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The first hit is queued before final stamp verification; model UI adoption.
            Assert.That(searches.TryRead(operation, out var item), Is.True);
            Assert.That(item, Is.TypeOf<SearchEvent<SearchHit>.Hits>());
            searches.Cancel();
            Assert.That(searches.Current, Is.SameAs(operation));
            Assert.That(operation.CancellationCause, Is.EqualTo(SearchCancellationCause.User));
            Assert.That(searches.CanAdoptEvents(operation), Is.False);
            Assert.That(searches.CanAdoptTerminal(operation), Is.True);
            Assert.That(searches.TryFinalize(operation, out _), Is.False);
        }
        finally { source.Release(); }
        var end = (await operation.Completion).Value;
        await operations.PendingSearchWork;
        Assert.That(end.UncommittedFileScanId, Is.EqualTo(1));
        Assert.That(searches.TryFinalize(operation, out var result), Is.True);
        Assert.That(result!.Value.Reason, Is.EqualTo(SearchEndReason.Cancelled));
    });

    [Test]
    public void ClearInvalidatesAnAlreadyCompletedButUndrainedSearch() => SingleThreadTestContext.Run(async () =>
    {
        using var operations = Create(new SourceAccess());
        await operations.OpenFolderAsync(PathOf("one"), SettingsSnapshot.Default);
        var searches = operations.Searches;
        var operation = searches.Start("foo").Value;
        await operation.Completion;
        await operations.PendingSearchWork;
        Assert.That(searches.TryFinalize(operation, out _), Is.False);
        var epoch = operations.WorkspaceSearchEpoch;
        searches.Clear();
        Assert.That(searches.Current, Is.Null);
        Assert.That(operations.WorkspaceSearchEpoch, Is.GreaterThan(epoch));
        Assert.That(operation.CancellationCause, Is.EqualTo(SearchCancellationCause.Cleared));
        Assert.That(operation.Session!.Events.Count, Is.Zero);
        Assert.That(searches.TryRead(operation, out _), Is.False);
        Assert.That(searches.TryFinalize(operation, out _), Is.False);
    });

    [Test]
    public void CancellingWaitingRequestNeverStartsItAndDoesNotReleaseActiveWork() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new ControlledSource();
        using var operations = Create(source);
        await operations.OpenFolderAsync(PathOf("one"), SettingsSnapshot.Default);
        var searches = operations.Searches;
        var active = searches.Start("foo").Value;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waiting = searches.Start("bar").Value;
            searches.Cancel();
            Assert.That(await waiting.Started, Is.False);
            Assert.That((await waiting.Completion).Status, Is.EqualTo(OperationStatus.Cancelled));
            Assert.That(waiting.CancellationCause, Is.EqualTo(SearchCancellationCause.User));
            Assert.That(searches.TryFinalize(waiting, out var end), Is.True);
            Assert.That(end!.Status, Is.EqualTo(OperationStatus.Cancelled));
            Assert.That(operations.PendingSearchWork.IsCompleted, Is.False);
            Assert.That(source.OpenCount, Is.EqualTo(1));
        }
        finally { source.Release(); }
        await active.Completion;
        await operations.PendingSearchWork;
        Assert.That(source.OpenCount, Is.EqualTo(1));
    });

    [Test]
    public void UserCancelAfterWorkerEndDropsUndrainedEventsButKeepsStopCause() => SingleThreadTestContext.Run(async () =>
    {
        using var operations = Create(new SourceAccess());
        await operations.OpenFolderAsync(PathOf("one"), SettingsSnapshot.Default);
        var searches = operations.Searches;
        var operation = searches.Start("foo").Value;
        await operation.Completion;
        await operations.PendingSearchWork;
        Assert.That(searches.TryFinalize(operation, out _), Is.False);
        searches.Cancel();
        Assert.That(searches.TryFinalize(operation, out var end), Is.True);
        Assert.That(end!.Value.Reason, Is.EqualTo(SearchEndReason.Completed));
        Assert.That(operation.CancellationCause, Is.EqualTo(SearchCancellationCause.User));
        Assert.That(searches.CanAdoptEvents(operation), Is.False);
    });

    [Test]
    public void FailedRootCannotStartSearchUsingPreviousRoot() => SingleThreadTestContext.Run(async () =>
    {
        using var operations = Create(new SourceAccess());
        await operations.OpenFolderAsync(PathOf("one"), SettingsSnapshot.Default);
        var searches = operations.Searches;
        var previous = searches.Start("foo").Value;
        await Drain(searches, previous);
        await previous.Completion;
        await operations.OpenFolderAsync(PathOf("missing"), SettingsSnapshot.Default);
        Assert.That(searches.CanStart, Is.False);
        Assert.That(searches.Current, Is.Null);
        Assert.That(searches.Start("foo").Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That(searches.CanAdoptTerminal(previous), Is.False);
        await operations.PendingSearchWork;
    });

    [Test, Category("Performance")]
    public void ThousandReplacementsKeepOnlyLatestWaitingRequest() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new ControlledSource();
        using var operations = Create(source);
        await operations.OpenFolderAsync(PathOf("one"), SettingsSnapshot.Default);
        var searches = operations.Searches;
        var active = searches.Start("foo").Value;
        WorkspaceSearchOperation? latest = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var index = 0; index < 1000; index++)
            {
                var previous = latest;
                latest = searches.Start("baz").Value;
                if (previous is not null && (!previous.Started.IsCompletedSuccessfully || previous.Started.Result
                    || !previous.Completion.IsCompletedSuccessfully || previous.Completion.Result.Status != OperationStatus.Cancelled))
                    throw new AssertionException("置換済みの待機要求が残っています。");
            }
            watch.Stop();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            TestContext.Progress.WriteLine($"replacementCount=1000; elapsedMs={watch.Elapsed.TotalMilliseconds:F3}; cpuMs={cpuMs:F3}; allocatedBytes={bytes}; openedFiles={source.OpenCount}");
            Assert.That(watch.Elapsed.TotalMilliseconds, Is.LessThanOrEqualTo(100));
            Assert.That(cpuMs, Is.LessThanOrEqualTo(100));
            Assert.That(bytes, Is.LessThanOrEqualTo(2 << 20));
            Assert.That(source.OpenCount, Is.EqualTo(1));
            Assert.That(latest!.Started.IsCompleted, Is.False);
        }
        finally { source.Release(); }
        var events = await Drain(searches, latest!);
        await latest!.Completion;
        await active.Completion;
        await operations.PendingSearchWork;
        Assert.That(events.OfType<SearchEvent<SearchHit>.Hits>().Single().Items.Single().Range.Offset, Is.EqualTo(8));
        Assert.That(source.OpenCount, Is.EqualTo(2));
    });

    [Test]
    public void ShutdownCancelsActiveAndWaitingButWaitsForPhysicalWorker() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new ControlledSource();
        using var operations = Create(source);
        await operations.OpenFolderAsync(PathOf("one"), SettingsSnapshot.Default);
        var searches = operations.Searches;
        var active = searches.Start("foo").Value;
        Task pending;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waiting = searches.Start("bar").Value;
            operations.Dispose();
            Assert.That(await waiting.Started, Is.False);
            Assert.That((await waiting.Completion).Status, Is.EqualTo(OperationStatus.Cancelled));
            pending = operations.PendingSearchWork;
            Assert.That(pending.IsCompleted, Is.False);
            Assert.That(searches.CanStart, Is.False);
            Assert.That(searches.Start("baz").Status, Is.EqualTo(OperationStatus.Cancelled));
        }
        finally { source.Release(); }
        await pending;
        await active.Completion;
        Assert.That(active.Session!.Events.Count, Is.Zero);
    });

    [Test]
    public void UnknownFailureInRetiredWorkerStopsWaitingAndRemainsObservable() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new ControlledSource { ThrowOnRelease = true };
        using var operations = Create(source);
        await operations.OpenFolderAsync(PathOf("one"), SettingsSnapshot.Default);
        var searches = operations.Searches;
        var first = searches.Start("foo").Value;
        WorkspaceSearchOperation? next = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            next = searches.Start("bar").Value;
        }
        finally { source.Release(); }
        SearchWorkerException? failure = null;
        try { await first.Completion; } catch (SearchWorkerException error) { failure = error; }
        Assert.That(failure, Is.Not.Null);
        Assert.That(await next!.Started, Is.False);
        Assert.That((await next.Completion).Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That(next.CancellationCause, Is.EqualTo(SearchCancellationCause.Fatal));
        Assert.That(searches.CanStart, Is.False);
        var observed = false;
        try { await operations.PendingSearchWork; }
        catch (SearchWorkerException error) { observed = ReferenceEquals(error, failure); }
        Assert.That(observed, Is.True);
        Assert.That(source.OpenCount, Is.EqualTo(1));
    });

    private sealed class ControlledSource : ISourceAccess, IDisposable
    {
        private readonly SourceAccess _inner = new();
        private readonly ManualResetEventSlim _release = new();
        private int _stamps;
        public int OpenCount { get; private set; }
        public bool ThrowOnRelease { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.Set();
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct)
        { OpenCount++; return _inner.OpenRead(path, ct); }
        public SourceStamp GetStamp(SourcePath path)
        {
            var stamp = _inner.GetStamp(path);
            if (Interlocked.Increment(ref _stamps) != 3) return stamp;
            Entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            if (ThrowOnRelease) throw new InvalidOperationException("injected");
            return stamp;
        }
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => _inner.ResolveDirectory(path, ct);
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct) => _inner.EnumerateChildren(path, ct);
        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => _inner.InspectPath(path, ct);
        public void Dispose() { _release.Set(); _release.Dispose(); }
    }
}
