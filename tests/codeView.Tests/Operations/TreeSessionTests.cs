using CodeView.Contracts;
using CodeView.Files;
using CodeView.Operations;

namespace CodeView.Tests.Operations;

internal sealed class TreeSessionTests
{
    private static SourcePath PathOf(string path) => SourcePath.Create(path, Path.GetTempPath());
    private static SourceEntry Folder(string name) => new(PathOf(name), name, SourceEntryKind.Directory, false);
    private static SourceEntry FileEntry(string name) => new(PathOf(name), name, SourceEntryKind.FileCandidate, false);
    private static TreeSession Create(Source source, SemaphoreSlim slot, int queue = 64, int cache = 10_000) =>
        new(new DirectoryReader(source), slot, new ExclusionPolicy(["skip"]), CancellationToken.None,
            new(PathOf("root"), new[] { Folder("a"), Folder("b"), Folder("c"), FileEntry("file"), Folder("link") with { IsLink = true } }, null), queue, cache);

    [Test]
    public void ExpansionIsLazyCoalescedAndLoadedChildrenAreReused() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source { DelayName = "a" };
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot);
        var a = tree.Root.Children[0];
        Assert.That(source.Read, Is.Empty);
        var first = tree.ExpandAsync(a);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.That(tree.ExpandAsync(a), Is.SameAs(first));
            Assert.That(a.Status, Is.EqualTo(TreeLoadStatus.Loading));
            Assert.That(source.Read, Is.EqualTo(new[] { "a" }));
        }
        finally { source.Release.Set(); }
        Assert.That((await first).Status, Is.EqualTo(OperationStatus.Success));
        Assert.That(a.Children.Select(n => n.Entry.Name), Is.EqualTo(new[] { "child", "a.cs" }));
        Assert.That(a.Children[0].Status, Is.EqualTo(TreeLoadStatus.NotLoaded));
        var children = a.Children;
        tree.Collapse(a);
        Assert.That(a.IsExpanded, Is.False);
        Assert.That(tree.CachedNodeCount, Is.EqualTo(2));
        await tree.ExpandAsync(a);
        Assert.That(a.Children, Is.SameAs(children));
        Assert.That(tree.CachedNodeCount, Is.Zero);
        Assert.That(source.Read, Has.Count.EqualTo(1));
        Assert.That((await tree.ExpandAsync(tree.Root.Children[3])).Error!.Code, Is.EqualTo(ErrorCode.NotRegularFile));
        Assert.That((await tree.ExpandAsync(tree.Root.Children[4])).Error!.Code, Is.EqualTo(ErrorCode.NotRegularFile));
    });

    [Test]
    public void CollapseCancelsLateResultAndReexpandUsesNewGeneration() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source { DelayName = "a" };
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot);
        var a = tree.Root.Children[0];
        var first = tree.ExpandAsync(a);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tree.Collapse(a);
        var second = tree.ExpandAsync(a);
        try { Assert.That(a.Children, Is.Empty); }
        finally { source.Release.Set(); }
        Assert.That((await first).Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That((await second).Status, Is.EqualTo(OperationStatus.Success));
        Assert.That(a.IsExpanded, Is.True);
        Assert.That(source.MaxActive, Is.EqualTo(1));
        Assert.That(source.Read, Has.Count.EqualTo(2));
    });

    [Test]
    public void WaitingLimitRejectsNewNodesButStillCoalescesAndCancelledWaiterDoesNotRead() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source { DelayName = "a" };
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot, queue: 1);
        var a = tree.ExpandAsync(tree.Root.Children[0]);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var bNode = tree.Root.Children[1];
        var b = tree.ExpandAsync(bNode);
        try
        {
            Assert.That(tree.ExpandAsync(bNode), Is.SameAs(b));
            var rejected = await tree.ExpandAsync(tree.Root.Children[2]);
            Assert.That(rejected.Error!.Code, Is.EqualTo(ErrorCode.QueueFull));
            Assert.That(tree.Root.Children[2].Status, Is.EqualTo(TreeLoadStatus.NotLoaded));
            tree.Collapse(bNode);
            Assert.That((await b).Status, Is.EqualTo(OperationStatus.Cancelled));
            Assert.That(source.Read, Is.EqualTo(new[] { "a" }));
        }
        finally { source.Release.Set(); }
        await a;
        Assert.That((await tree.ExpandAsync(tree.Root.Children[2])).Status, Is.EqualTo(OperationStatus.Success));
    });

    [TestCase(0, 0)]
    [TestCase(1, 0)]
    [TestCase(2, 2)]
    [TestCase(3, 2)]
    [TestCase(4, 4)]
    public void CollapsedCacheHonorsBoundaryAndEvictsOldest(int limit, int retained) => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source();
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot, cache: limit);
        var a = tree.Root.Children[0];
        var b = tree.Root.Children[1];
        await tree.ExpandAsync(a);
        tree.Collapse(a);
        await tree.ExpandAsync(b);
        tree.Collapse(b);
        Assert.That(tree.CachedNodeCount, Is.EqualTo(retained));
        Assert.That(a.Children, limit >= 4 ? Has.Count.EqualTo(2) : Is.Empty);
        Assert.That(a.Status, Is.EqualTo(limit >= 4 ? TreeLoadStatus.Loaded : TreeLoadStatus.NotLoaded));
        Assert.That(tree.CachedNodeCount, Is.LessThanOrEqualTo(limit));
    });

    [Test]
    public void ParentCollapseDiscardsHiddenDescendantsAndDetachedNodesCannotRead() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source();
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot);
        var a = tree.Root.Children[0];
        await tree.ExpandAsync(a);
        var child = a.Children[0];
        await tree.ExpandAsync(child);
        var detached = child.Children[0];
        tree.Collapse(child);
        Assert.That(tree.CachedNodeCount, Is.EqualTo(2));
        tree.Collapse(a);
        Assert.That(tree.CachedNodeCount, Is.EqualTo(2));
        Assert.That(child.Children, Is.Empty);
        Assert.That((await tree.ExpandAsync(detached)).Status, Is.EqualTo(OperationStatus.Cancelled));
        await tree.ExpandAsync(a);
        await tree.ExpandAsync(child);
        Assert.That(source.Read.Count(name => name == "child"), Is.EqualTo(2));
    });

    [TestCase(9_999)]
    [TestCase(10_000)]
    [TestCase(10_001)]
    public void DefaultCacheLimitHandlesWideDirectoryBoundary(int count) => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source { DirectFileCount = count };
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot);
        var a = tree.Root.Children[0];
        await tree.ExpandAsync(a);
        Assert.That(a.Children, Has.Count.EqualTo(count));
        tree.Collapse(a);
        Assert.That(tree.CachedNodeCount, Is.EqualTo(count <= 10_000 ? count : 0));
        Assert.That(a.Children.Count, Is.EqualTo(tree.CachedNodeCount));
    });

    [Test]
    public void PartialFailureRetriesAndEmptyFolderIsLoaded() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source { PartialFailure = true };
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot);
        var a = tree.Root.Children[0];
        Assert.That((await tree.ExpandAsync(a)).Error!.Code, Is.EqualTo(ErrorCode.AccessDenied));
        Assert.That(a.Status, Is.EqualTo(TreeLoadStatus.Failed));
        Assert.That(a.Children, Has.Count.EqualTo(1));
        source.PartialFailure = false;
        Assert.That((await tree.ExpandAsync(a)).Status, Is.EqualTo(OperationStatus.Success));
        await tree.ExpandAsync(tree.Root.Children[2]);
        Assert.That(tree.Root.Children[2].Status, Is.EqualTo(TreeLoadStatus.Loaded));
        Assert.That(tree.Root.Children[2].Children, Is.Empty);
    });

    [Test]
    public void DisposalTracksNativeWaitAndRejectsFutureExpansion() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source { DelayName = "a" };
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot);
        var node = tree.Root.Children[0];
        var load = tree.ExpandAsync(node);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tree.Dispose();
        var pending = tree.PendingWork;
        try
        {
            Assert.That(pending.IsCompleted, Is.False);
            Assert.That(tree.Root.Children, Is.Empty);
            Assert.That((await tree.ExpandAsync(node)).Status, Is.EqualTo(OperationStatus.Cancelled));
        }
        finally { source.Release.Set(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That((await load).Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That(node.Children, Is.Empty);
        Assert.That(slot.CurrentCount, Is.EqualTo(1));
    });

    [Test]
    public void ReloadReplacesChildrenAndUnexpectedErrorsPropagate() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source();
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot);
        var a = tree.Root.Children[0];
        await tree.ExpandAsync(a);
        var oldChild = a.Children[0];
        await tree.ExpandAsync(a, reload: true);
        Assert.That(a.Children[0], Is.Not.SameAs(oldChild));
        Assert.That((await tree.ExpandAsync(oldChild)).Status, Is.EqualTo(OperationStatus.Cancelled));
        source.UnexpectedFailure = true;
        try
        {
            await tree.ExpandAsync(a, reload: true);
            Assert.Fail("Unexpected exceptions must propagate.");
        }
        catch (InvalidOperationException) { }
        Assert.That(a.Status, Is.EqualTo(TreeLoadStatus.Failed));
        Assert.That(a.Error!.Code, Is.EqualTo(ErrorCode.Unexpected));
        Assert.That(slot.CurrentCount, Is.EqualTo(1));
        using var other = Create(source, slot);
        Assert.Throws<ArgumentException>(() => other.ExpandAsync(a));
        Assert.Throws<InvalidOperationException>(() => tree.ExpandAsync(tree.Root, reload: true));
    });

    private sealed class Source : ISourceAccess, IDisposable
    {
        public string? DelayName { get; init; }
        public bool PartialFailure { get; set; }
        public bool UnexpectedFailure { get; set; }
        public int? DirectFileCount { get; init; }
        public List<string> Read { get; } = [];
        public int MaxActive { get; private set; }
        private int _active;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct)
        {
            var name = Path.GetFileName(path.FullPath);
            MaxActive = Math.Max(MaxActive, Interlocked.Increment(ref _active));
            Read.Add(name);
            try
            {
                if (UnexpectedFailure) throw new InvalidOperationException("test failure");
                if (name == DelayName) { Entered.TrySetResult(); Release.Wait(); }
                if (DirectFileCount is { } count)
                {
                    for (var index = 0; index < count; index++) yield return FileEntry($"file-{index:D5}.cs");
                    yield break;
                }
                if (name == "c") yield break;
                yield return Folder("child");
                if (PartialFailure) throw new UnauthorizedAccessException();
                yield return FileEntry("a.cs");
                yield return Folder("skip");
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => throw new NotSupportedException();
        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => throw new NotSupportedException();
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct) => throw new NotSupportedException();
        public SourceStamp GetStamp(SourcePath path) => throw new NotSupportedException();
        public void Dispose() => Release.Dispose();
    }
}
