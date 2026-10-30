using System.Collections.Specialized;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;

namespace CodeView.Tests.Presentation;

internal sealed class FileTreeViewModelTests
{
    private static SourcePath PathOf(string path) => SourcePath.Create(path, Path.GetTempPath());
    private static SourceEntry Folder(string name) => new(PathOf(name), name, SourceEntryKind.Directory, false);
    private static TreeSession Create(Source source, SemaphoreSlim slot, int roots = 2, int queue = 64) =>
        new(new DirectoryReader(source), slot, new ExclusionPolicy([]), CancellationToken.None,
            new(PathOf("root"), Enumerable.Range(0, roots).Select(i => Folder($"root-{i:D4}")).ToArray(), null), queue);
    private static async Task YieldAsync() => await Task.Yield();

    [Test]
    public void RootProjectionIsBatchedAndOldBindingCannotReappear() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source();
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot, roots: 600);
        using var next = Create(source, slot, roots: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new FileTreeViewModel(() => gate.Task);
        var added = new List<int>();
        view.Rows.CollectionChanged += (_, args) =>
        { if (args.Action == NotifyCollectionChangedAction.Add) added.Add(args.NewItems!.Count); };
        var old = view.BindAsync(tree);
        Assert.That(view.Rows.Count, Is.InRange(1, 256));
        await view.BindAsync(next);
        var current = view.Rows[0];
        gate.SetResult();
        await old;
        Assert.That(view.Rows, Has.Count.EqualTo(1));
        Assert.That(view.Rows[0], Is.SameAs(current));
        Assert.That(added.All(count => count <= 256), Is.True);
        Assert.That(source.Calls, Is.Zero);
    });

    [Test]
    public void InterleavedBranchesKeepOrderAndSiblingSelection() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source { ChildCount = 600 };
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = false;
        var view = new FileTreeViewModel(async () =>
        {
            if (!once) { once = true; entered.SetResult(); await release.Task; }
            else await Task.Yield();
        });
        await view.BindAsync(tree);
        var a = view.Rows[0];
        var b = view.Rows[1];
        var first = view.ExpandAsync(a);
        await entered.Task;
        Assert.That(view.ExpandAsync(a), Is.SameAs(first));
        await view.ExpandAsync(b);
        view.SelectedRow = b;
        release.SetResult();
        await first;
        Assert.That(view.Rows, Has.Count.EqualTo(1202));
        Assert.That(view.Rows[601], Is.SameAs(b));
        Assert.That(view.Rows.Skip(1).Take(600).All(row => ReferenceEquals(row.Parent, a)), Is.True);
        Assert.That(view.Rows.Skip(602).All(row => ReferenceEquals(row.Parent, b)), Is.True);
        view.Collapse(a);
        Assert.That(view.SelectedRow, Is.SameAs(b));
        Assert.That(view.Rows[1], Is.SameAs(b));
        view.SelectedRow = view.Rows[2];
        view.Collapse(b);
        Assert.That(view.SelectedRow, Is.SameAs(b));
        Assert.That(view.Rows, Has.Count.EqualTo(2));
    });

    [Test]
    public void CollapseDuringDrainDoesNotResurrectRowsAndReloadRejectsOldRows() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source { ChildCount = 600 };
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new FileTreeViewModel(async () => { entered.TrySetResult(); await release.Task; await Task.Yield(); });
        await view.BindAsync(tree);
        var a = view.Rows[0];
        var load = view.ExpandAsync(a);
        await entered.Task;
        var stale = view.Rows[1];
        view.Collapse(a);
        release.SetResult();
        await load;
        Assert.That(view.Rows, Has.Count.EqualTo(2));
        Assert.That(view.IsCurrentRow(stale), Is.False);
        await view.ExpandAsync(a);
        Assert.That(view.Rows, Has.Count.EqualTo(602));
        var old = view.Rows[1];
        await view.ExpandAsync(a, reload: true);
        Assert.That(view.IsCurrentRow(old), Is.False);
        Assert.That(view.Rows, Has.Count.EqualTo(602));
        view.Clear();
        await view.ExpandAsync(a);
        Assert.That(view.HasRows, Is.False);
        Assert.That(view.SelectedRow, Is.Null);
    });

    [Test]
    public void EmptyFailureAndQueueFullHaveDistinctPresentation() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source { ChildCount = 0 };
        using var slot = new SemaphoreSlim(0, 1);
        using var tree = Create(source, slot, queue: 1);
        var view = new FileTreeViewModel(YieldAsync);
        await view.BindAsync(tree);
        var a = view.Rows[0];
        var b = view.Rows[1];
        var waiting = view.ExpandAsync(a);
        Assert.That(a.Detail, Is.EqualTo("読込中…"));
        await view.ExpandAsync(b);
        Assert.That(view.Notice, Does.Contain("上限"));
        Assert.That(b.Node.IsExpanded, Is.False);
        slot.Release();
        await waiting;
        Assert.That(a.Detail, Is.EqualTo("空"));
        source.ChildCount = 2;
        source.Fail = true;
        await view.ExpandAsync(a, reload: true);
        Assert.That(a.Detail, Does.Contain("部分一覧"));
        Assert.That(view.Rows, Has.Count.EqualTo(3));
        source.Fail = false;
        await view.ExpandAsync(a, reload: true);
        Assert.That(a.Detail, Is.Empty);
        Assert.That(view.Rows, Has.Count.EqualTo(4));
    });

    [Test]
    public void RootDrainAllowsChildExpansionWithoutReordering() => SingleThreadTestContext.Run(async () =>
    {
        using var source = new Source { ChildCount = 2 };
        using var slot = new SemaphoreSlim(1);
        using var tree = Create(source, slot, roots: 600);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new FileTreeViewModel(() => gate.Task);
        var bind = view.BindAsync(tree);
        var a = view.Rows[0];
        await view.ExpandAsync(a);
        gate.SetResult();
        await bind;
        Assert.That(view.Rows, Has.Count.EqualTo(602));
        Assert.That(view.Rows[1].Parent, Is.SameAs(a));
        Assert.That(view.Rows[2].Parent, Is.SameAs(a));
        Assert.That(view.Rows[3].Depth, Is.Zero);
        Assert.That(view.Rows[3].Name, Is.EqualTo("root-0001"));
    });

    private sealed class Source : ISourceAccess, IDisposable
    {
        public int ChildCount { get; set; } = 2;
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct)
        {
            Calls++;
            for (var index = 0; index < ChildCount; index++)
            {
                yield return new(SourcePath.Create($"child-{index:D4}.cs", path.FullPath), $"child-{index:D4}.cs", SourceEntryKind.FileCandidate, false);
                if (Fail) throw new UnauthorizedAccessException();
            }
        }
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => throw new NotSupportedException();
        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => throw new NotSupportedException();
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct) => throw new NotSupportedException();
        public SourceStamp GetStamp(SourcePath path) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
