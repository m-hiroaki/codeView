using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Settings;

namespace CodeView.Tests.Operations;

public sealed class WorkspaceTests
{
    private string _root = null!;
    private SourcePath PathOf(string name) => SourcePath.Create(name, _root);
    private static AppOperations Create(ISourceAccess source) =>
        new(new FileReader(source), new AvaloniaEditDocumentAdapter(), new DirectoryReader(source));

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "codeview-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "first", "skip"));
        Directory.CreateDirectory(Path.Combine(_root, "second"));
        File.WriteAllText(Path.Combine(_root, "first", "body.cs"), "class Body { }");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    [Test]
    public async Task SettingsAreFixedAndSwitchOrFailureKeepsTheOpenDocument()
    {
        using var operations = Create(new SourceAccess());
        var file = (await operations.OpenFileAsync(PathOf("first/body.cs"), EncodingChoice.Auto)).Value;
        var names = new[] { "skip" };
        var settings = new SettingsSnapshot(names);
        names[0] = "body.cs";
        var first = (await operations.OpenFolderAsync(PathOf("first"), settings)).Value;
        Assert.That(first.Settings, Is.SameAs(settings));
        Assert.That(first.CanSearch, Is.True);
        Assert.That(first.RootListing!.Entries.Select(e => e.Name), Is.EqualTo(new[] { "body.cs" }));
        var epoch = operations.WorkspaceSearchEpoch;
        var failure = await operations.OpenFolderAsync(PathOf("missing"), settings);
        Assert.That(failure.Error!.Code, Is.EqualTo(ErrorCode.NotFound));
        Assert.That(first.Token.IsCancellationRequested, Is.True);
        Assert.That(operations.WorkspaceSearchEpoch, Is.GreaterThan(epoch));
        Assert.That(operations.Workspace!.Status, Is.EqualTo(WorkspaceStatus.Failed));
        Assert.That(operations.Workspace.CanSearch, Is.False);
        Assert.That(operations.Workspace.RequestedPath.FullPath, Is.EqualTo(PathOf("missing").FullPath));
        Assert.That(operations.CurrentFile, Is.SameAs(file));
        Assert.That(file.Document.IsReleased, Is.False);
        operations.CloseWorkspace();
        Assert.That(operations.Workspace, Is.Null);
        Assert.That(operations.CurrentFile, Is.SameAs(file));
    }

    [Test]
    public async Task EmptyRootIsReadyAndFileCannotBecomeRoot()
    {
        using var operations = Create(new SourceAccess());
        var empty = await operations.OpenFolderAsync(PathOf("second"), SettingsSnapshot.Default);
        Assert.That(empty.Value.CanSearch, Is.True);
        Assert.That(empty.Value.RootListing!.Entries, Is.Empty);
        var file = await operations.OpenFolderAsync(PathOf("first/body.cs"), SettingsSnapshot.Default);
        Assert.That(file.Error!.Code, Is.EqualTo(ErrorCode.NotRegularFile));
        Assert.That(operations.Workspace!.CanSearch, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LateNonCancellableResolutionCannotRestoreClosedOrReplacedRoot(bool replace)
    {
        using var source = new ControlledSource { DelayResolution = true };
        using var operations = Create(source);
        var old = operations.OpenFolderAsync(PathOf("first"), SettingsSnapshot.Default);
        Task<OperationResult<WorkspaceState>>? next = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var state = operations.Workspace!;
            Assert.That(state.Status, Is.EqualTo(WorkspaceStatus.Loading));
            Assert.That(state.CanSearch, Is.False);
            if (replace) next = operations.OpenFolderAsync(PathOf("second"), SettingsSnapshot.Default);
            else operations.CloseWorkspace();
            Assert.That(state.Token.IsCancellationRequested, Is.True);
            Assert.That(source.Listed, Is.Zero);
        }
        finally { source.Release.Set(); }
        Assert.That((await old.WaitAsync(TimeSpan.FromSeconds(5))).Status, Is.EqualTo(OperationStatus.Cancelled));
        if (next is not null)
        {
            Assert.That((await next.WaitAsync(TimeSpan.FromSeconds(5))).Status, Is.EqualTo(OperationStatus.Success));
            Assert.That(operations.Workspace!.RequestedPath.FullPath, Is.EqualTo(PathOf("second").FullPath));
            Assert.That(source.Listed, Is.EqualTo(1));
        }
        else Assert.That(operations.Workspace, Is.Null);
        Assert.That(source.MaxActive, Is.EqualTo(1));
    }

    [Test]
    public async Task PartialRootFailureRetainsEntriesButDisablesSearchAndRefreshCanRecover()
    {
        using var source = new ControlledSource { ListingFailure = true };
        using var operations = Create(source);
        var failed = await operations.OpenFolderAsync(PathOf("first"), SettingsSnapshot.Default);
        Assert.That(failed.Error!.Code, Is.EqualTo(ErrorCode.AccessDenied));
        Assert.That(operations.Workspace!.RootListing!.Entries, Has.Count.EqualTo(1));
        Assert.That(operations.Workspace.CanSearch, Is.False);
        source.ListingFailure = false;
        Assert.That((await operations.OpenFolderAsync(PathOf("first"), SettingsSnapshot.Default)).Value.CanSearch, Is.True);
    }

    [Test]
    public async Task UnexpectedFailurePropagatesAndStopRejectsFutureWork()
    {
        using var source = new ControlledSource { UnexpectedFailure = true };
        using var operations = Create(source);
        Assert.ThrowsAsync<InvalidOperationException>(() => operations.OpenFolderAsync(PathOf("first"), SettingsSnapshot.Default));
        Assert.That(operations.Workspace!.Error!.Code, Is.EqualTo(ErrorCode.Unexpected));
        operations.Dispose();
        Assert.That((await operations.OpenFolderAsync(PathOf("second"), SettingsSnapshot.Default)).Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That((await operations.InspectPathAsync(PathOf("first"), CancellationToken.None)).Status, Is.EqualTo(OperationStatus.Cancelled));
    }

    [Test]
    public void PathInspectionClassifiesWithoutOpeningBodyAndHonorsCancellation()
    {
        var source = new SourceAccess();
        Assert.That(source.InspectPath(PathOf("first"), CancellationToken.None).Value.Kind, Is.EqualTo(SourceEntryKind.Directory));
        Assert.That(source.InspectPath(PathOf("first/body.cs"), CancellationToken.None).Value.Kind, Is.EqualTo(SourceEntryKind.FileCandidate));
        Assert.That(source.InspectPath(PathOf("missing"), CancellationToken.None).Error!.Code, Is.EqualTo(ErrorCode.NotFound));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => source.InspectPath(PathOf("first"), cancelled.Token));
    }

    internal sealed class ControlledSource : ISourceAccess, IDisposable
    {
        private readonly SourceAccess _source = new();
        private int _active;
        public bool DelayResolution { get; set; }
        public bool ListingFailure { get; set; }
        public bool UnexpectedFailure { get; init; }
        public int MaxActive { get; private set; }
        public int Listed { get; private set; }
        public ManualResetEventSlim Release { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct)
        {
            MaxActive = Math.Max(MaxActive, Interlocked.Increment(ref _active));
            try
            {
                if (UnexpectedFailure) throw new InvalidOperationException("test failure");
                if (DelayResolution && path.FullPath.EndsWith("first", StringComparison.Ordinal))
                {
                    Entered.TrySetResult();
                    Release.Wait(); // Simulate native I/O that cannot observe cancellation.
                    return _source.ResolveDirectory(path, CancellationToken.None);
                }
                return _source.ResolveDirectory(path, ct);
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct)
        {
            Listed++;
            foreach (var entry in _source.EnumerateChildren(path, ct))
            {
                yield return entry;
                if (ListingFailure) throw new UnauthorizedAccessException();
            }
        }

        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => _source.InspectPath(path, ct);
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct) => _source.OpenRead(path, ct);
        public SourceStamp GetStamp(SourcePath path) => _source.GetStamp(path);
        public void Dispose() => Release.Dispose();
    }
}
