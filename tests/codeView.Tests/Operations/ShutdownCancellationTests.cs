using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Settings;

namespace CodeView.Tests.Operations;

internal sealed class ShutdownCancellationTests
{
    [Test]
    public void TabCancellationFailureClosesAllTabsAndWaitsForWorker() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new GatedSource();
        using var operations = source.CreateOperations();
        var tabs = operations.Tabs;
        var ready = (await tabs.OpenAsync(source.Path)).Value;
        var readyHandle = ready.File!.Document;
        source.HoldRead = true;
        var loading = tabs.ReloadAsync(ready);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = tabs.OpenAsync(SourcePath.Create("waiting.cs", source.Root));
        var waitingTab = tabs.ActiveTab!;
        try
        {
            var failure = Assert.Throws<AggregateException>(operations.Dispose)!;
            Assert.That(failure.Flatten().InnerExceptions, Does.Contain(source.CallbackFailure));
            Assert.That(tabs.Tabs, Is.Empty);
            Assert.That(readyHandle.IsReleased, Is.True);
            Assert.That(ready.Status, Is.EqualTo(TabStatus.Closed));
            Assert.That(waitingTab.Status, Is.EqualTo(TabStatus.Closed));
            Assert.That((await waiting).Status, Is.EqualTo(OperationStatus.Cancelled));
            Assert.That(operations.PendingTabWork.IsCompleted, Is.False);
            operations.Dispose();
        }
        finally { source.Release.Set(); }
        Assert.That((await loading.WaitAsync(TimeSpan.FromSeconds(5))).Status, Is.EqualTo(OperationStatus.Cancelled));
        await operations.PendingTabWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(source.OpenCount, Is.EqualTo(2), "The queued file must never start after disposal.");
    });

    [TestCase(false)]
    [TestCase(true)]
    public void SearchCancellationFailureCannotSkipTabsWorkspaceOrPendingWork(bool workspaceFailure) => SingleThreadTestContext.Run(async () =>
    {
        using var source = new GatedSource();
        using var operations = source.CreateOperations();
        var file = (await operations.OpenFileAsync(source.Path, EncodingChoice.Auto)).Value;
        var workspace = (await operations.OpenFolderAsync(SourcePath.Create(source.Root, source.Root), SettingsSnapshot.Default)).Value;
        var workspaceFault = new InvalidOperationException("workspace cancellation callback");
        using var registration = workspace.Token.Register(() => { if (workspaceFailure) throw workspaceFault; });
        source.HoldRead = true;
        var searches = operations.Searches;
        var search = searches.Start("original").Value;
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var failure = Assert.Throws<AggregateException>(operations.Dispose)!;
            var errors = failure.Flatten().InnerExceptions;
            Assert.That(errors, Does.Contain(source.CallbackFailure));
            if (workspaceFailure) Assert.That(errors, Does.Contain(workspaceFault));
            Assert.That(file.Document.IsReleased, Is.True);
            Assert.That(operations.Workspace, Is.Null);
            Assert.That(workspace.Token.IsCancellationRequested, Is.True);
            Assert.That(workspace.Tree!.Root.IsAttached, Is.False);
            Assert.That(operations.PendingSearchWork.IsCompleted, Is.False);
            Assert.That(searches.Current, Is.Null);
            Assert.That(operations.Startup.State, Is.EqualTo(StartupState.Stopping));
            operations.Dispose();
        }
        finally { source.Release.Set(); }
        await operations.PendingSearchWork.WaitAsync(TimeSpan.FromSeconds(5));
        await operations.PendingTreeWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(search.Token.IsCancellationRequested, Is.True);
        Assert.That((await operations.OpenFileAsync(source.Path, EncodingChoice.Auto)).Status, Is.EqualTo(OperationStatus.Cancelled));
    });

    [TestCase(false)]
    [TestCase(true)]
    public void TreeCancellationFailureStillDetachesNodesAndRetiresActualWork(bool workspaceFailure) => SingleThreadTestContext.Run(async () =>
    {
        using var source = new GatedSource();
        Directory.CreateDirectory(System.IO.Path.Combine(source.Root, "first"));
        Directory.CreateDirectory(System.IO.Path.Combine(source.Root, "second"));
        using var operations = source.CreateOperations();
        var workspace = (await operations.OpenFolderAsync(SourcePath.Create(source.Root, source.Root), SettingsSnapshot.Default)).Value;
        var tree = workspace.Tree!;
        var children = tree.Root.Children.Where(node => node.CanExpand).OrderBy(node => node.Entry.Name).ToArray();
        var workspaceFault = new InvalidOperationException("workspace callback after tree callback");
        using var registration = workspace.Token.Register(() => { if (workspaceFailure) throw workspaceFault; });
        source.HoldListing = true;
        var loading = tree.ExpandAsync(children[0]);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = tree.ExpandAsync(children[1]);
        try
        {
            var failure = Assert.Throws<AggregateException>(operations.Dispose)!;
            Assert.That(failure.Flatten().InnerExceptions, Does.Contain(source.CallbackFailure));
            if (workspaceFailure) Assert.That(failure.Flatten().InnerExceptions, Does.Contain(workspaceFault));
            Assert.That(operations.Workspace, Is.Null);
            Assert.That(tree.Root.IsAttached, Is.False);
            Assert.That(children.All(child => !child.IsAttached), Is.True);
            Assert.That(workspace.Token.IsCancellationRequested, Is.True);
            Assert.That(operations.PendingTreeWork.IsCompleted, Is.False);
            Assert.That((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Status, Is.EqualTo(OperationStatus.Cancelled));
            tree.Dispose();
            operations.Dispose();
        }
        finally { source.Release.Set(); }
        Assert.That((await loading.WaitAsync(TimeSpan.FromSeconds(5))).Status, Is.EqualTo(OperationStatus.Cancelled));
        await operations.PendingTreeWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That((await tree.ExpandAsync(children[0])).Status, Is.EqualTo(OperationStatus.Cancelled));
    });

    private sealed class GatedSource : ISourceAccess, IDisposable
    {
        private readonly SourceAccess _inner = new();
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codeview-cancel-" + Guid.NewGuid().ToString("N"));
        public SourcePath Path => SourcePath.Create("body.cs", Root);
        public bool HoldRead;
        public bool HoldListing;
        public int OpenCount;
        public readonly InvalidOperationException CallbackFailure = new("I/O cancellation callback");
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();

        public GatedSource()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.FullPath, "// original");
        }

        public AppOperations CreateOperations() => new(new FileReader(this), new AvaloniaEditDocumentAdapter(), new DirectoryReader(this));

        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct)
        {
            Interlocked.Increment(ref OpenCount);
            if (HoldRead)
            {
                using var registration = ct.Register(() => throw CallbackFailure);
                Entered.TrySetResult();
                Release.Wait(); // Model non-cancellable I/O even after its callback throws.
            }
            return _inner.OpenRead(path, ct);
        }

        public SourceStamp GetStamp(SourcePath path) => _inner.GetStamp(path);
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => _inner.ResolveDirectory(path, ct);
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct)
        {
            if (HoldListing)
            {
                using var registration = ct.Register(() => throw CallbackFailure);
                Entered.TrySetResult();
                Release.Wait();
            }
            foreach (var entry in _inner.EnumerateChildren(path, ct)) yield return entry;
        }
        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => _inner.InspectPath(path, ct);
        public void Dispose() { Release.Set(); Release.Dispose(); Directory.Delete(Root, true); }
    }
}
