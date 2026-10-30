using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;

namespace CodeView.Tests.Operations;

internal sealed class TabManagerTests
{
    private static SourcePath PathOf(string name) => SourcePath.Create(name, Path.GetTempPath());

    [Test]
    public void ReservedPathsCoalesceAndDifferentTabsLoadFifoWithoutStealingSelection() => SingleThreadTestContext.Run(async () =>
    {
        using var load = new Loader { HoldFirst = true };
        using var slot = new SemaphoreSlim(1);
        using var tabs = new TabManager(load.Load, slot);
        var first = tabs.OpenAsync(PathOf("a.cs"));
        await load.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var a = tabs.Tabs[0];
        var second = tabs.OpenAsync(PathOf("b.cs"));
        var b = tabs.Tabs[1];
        var third = tabs.OpenAsync(PathOf("c.cs"));
        var c = tabs.Tabs[2];
        try
        {
            Assert.That(tabs.OpenAsync(PathOf("a.cs")), Is.SameAs(first));
            Assert.That(tabs.ActiveTab, Is.SameAs(a));
            tabs.Select(c);
            Assert.That(tabs.Tabs.Select(tab => tab.Id).Distinct().Count(), Is.EqualTo(3));
            Assert.That(a.Status, Is.EqualTo(TabStatus.Loading));
        }
        finally { load.Release.Set(); await Task.WhenAll(first, second, third); }
        Assert.That(load.Calls, Is.EqualTo(new[] { "a.cs", "b.cs", "c.cs" }));
        Assert.That(load.MaxActive, Is.EqualTo(1));
        Assert.That(tabs.ActiveTab, Is.SameAs(c));
        Assert.That(tabs.Tabs.All(tab => tab.Status == TabStatus.Ready && tab.DocumentVersion == 1), Is.True);
        await tabs.OpenAsync(PathOf("b.cs"), EncodingChoice.Cp932);
        Assert.That(tabs.ActiveTab, Is.SameAs(b));
        Assert.That(b.EncodingChoice, Is.EqualTo(EncodingChoice.Auto));
        Assert.That(load.Calls, Has.Count.EqualTo(3));
        Assert.That(a.File!.Document.IsReleased, Is.False);
    });

    [Test]
    public void ClosingActiveLoadDisposesLateDocumentAndReopenGetsANewIdentity() => SingleThreadTestContext.Run(async () =>
    {
        using var load = new Loader { HoldFirst = true };
        using var slot = new SemaphoreSlim(1);
        using var tabs = new TabManager(load.Load, slot);
        var old = tabs.OpenAsync(PathOf("a.cs"));
        await load.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var tab = tabs.ActiveTab!;
        var built = load.LastBuilt!;
        using var lease = built.Document.AcquireReadView();
        tabs.Close(tab);
        Assert.That(tab.Status, Is.EqualTo(TabStatus.Closed));
        Assert.That(tabs.ActiveTab, Is.Null);
        Assert.That(tabs.PendingWork.IsCompleted, Is.False);
        var reopened = tabs.OpenAsync(PathOf("a.cs"));
        Assert.That(tabs.ActiveTab!.Id, Is.Not.EqualTo(tab.Id));
        load.Release.Set();
        Assert.That((await old).Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That((await reopened).Status, Is.EqualTo(OperationStatus.Success));
        Assert.That(built.Document.IsReleased, Is.True);
        Assert.That(lease.ReadRange(0, (int)lease.Length), Is.EqualTo("a.cs"));
        Assert.That(tabs.Tabs, Has.Count.EqualTo(1));
    });

    [Test]
    public void WaitingLimitAndQueuedReplacementOrCloseAreAtomic() => SingleThreadTestContext.Run(async () =>
    {
        using var load = new Loader { HoldFirst = true };
        using var slot = new SemaphoreSlim(1);
        using var tabs = new TabManager(load.Load, slot, maxWaiting: 1);
        var first = tabs.OpenAsync(PathOf("a.cs"));
        await load.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var old = tabs.OpenAsync(PathOf("b.cs"));
        var b = tabs.ActiveTab!;
        var replacement = tabs.ReloadAsync(b, EncodingChoice.Cp932);
        Assert.That((await old).Status, Is.EqualTo(OperationStatus.Cancelled));
        var rejected = await tabs.OpenAsync(PathOf("c.cs"));
        Assert.That(rejected.Error!.Code, Is.EqualTo(ErrorCode.QueueFull));
        Assert.That(tabs.Tabs, Has.Count.EqualTo(2));
        Assert.That(tabs.ActiveTab, Is.SameAs(b));
        tabs.Close(b);
        Assert.That((await replacement).Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That(tabs.ActiveTab!.Path.FullPath, Is.EqualTo(PathOf("a.cs").FullPath));
        var accepted = tabs.OpenAsync(PathOf("c.cs"));
        load.Release.Set();
        await Task.WhenAll(first, accepted);
        Assert.That(load.Calls, Is.EqualTo(new[] { "a.cs", "c.cs" }));
    });

    [Test]
    public void ViewStateAndCloseSelectionAreIndependentPerTab() => SingleThreadTestContext.Run(async () =>
    {
        using var load = new Loader();
        using var slot = new SemaphoreSlim(1);
        using var tabs = new TabManager(load.Load, slot);
        var a = (await tabs.OpenAsync(PathOf("a.cs"))).Value;
        var b = (await tabs.OpenAsync(PathOf("b.cs"))).Value;
        tabs.SaveView(a, new(2, 3), 1.5, 200.25);
        tabs.SaveView(b, new(4, 5), 100, 300);
        tabs.Select(a);
        Assert.That(a.Position, Is.EqualTo(new TextPosition(2, 3)));
        Assert.That(a.HorizontalOffset, Is.EqualTo(1.5));
        Assert.That(a.VerticalOffset, Is.EqualTo(200.25));
        Assert.That(b.Position, Is.EqualTo(new TextPosition(4, 5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => tabs.SaveView(a, default, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tabs.SaveView(a, new(1, 1), double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tabs.SaveView(a, new(1, 1), 0, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => tabs.SaveView(a, new(1, 1), -1, 0));
        var first = a.File!.Document;
        tabs.Close(a);
        Assert.That(first.IsReleased, Is.True);
        Assert.That(tabs.ActiveTab, Is.SameAs(b));
        Assert.That(tabs.Select(a), Is.False);
        Assert.That(tabs.Close(a), Is.False);
        tabs.Close(b);
        Assert.That(tabs.ActiveTab, Is.Null);
        Assert.That(tabs.Tabs, Is.Empty);
    });

    [Test]
    public void ConsecutiveReloadsDiscardStaleDocumentAndKeepViewAndSelection() => SingleThreadTestContext.Run(async () =>
    {
        using var load = new Loader();
        using var slot = new SemaphoreSlim(1);
        using var tabs = new TabManager(load.Load, slot);
        var a = (await tabs.OpenAsync(PathOf("a.cs"))).Value;
        var original = a.File!;
        var b = (await tabs.OpenAsync(PathOf("b.cs"))).Value;
        tabs.SaveView(a, new(3, 4), 10, 20);
        load.HoldFirst = true;
        var oldReload = tabs.ReloadAsync(a, EncodingChoice.Utf8);
        await load.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stale = load.LastBuilt!;
        Assert.That(a.Status, Is.EqualTo(TabStatus.Reloading));
        Assert.That(a.File, Is.SameAs(original));
        var latest = tabs.ReloadAsync(a, EncodingChoice.Cp932);
        load.Release.Set();
        Assert.That((await oldReload).Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That((await latest).Status, Is.EqualTo(OperationStatus.Success));
        Assert.That(stale.Document.IsReleased, Is.True);
        Assert.That(original.Document.IsReleased, Is.True);
        Assert.That(a.EncodingChoice, Is.EqualTo(EncodingChoice.Cp932));
        Assert.That(a.DocumentVersion, Is.EqualTo(2));
        Assert.That(a.Position, Is.EqualTo(new TextPosition(3, 4)));
        Assert.That(tabs.ActiveTab, Is.SameAs(b));
    });

    [Test]
    public void UnexpectedLoadFaultIsObservedByRequestAndEarlierShutdownWait() => SingleThreadTestContext.Run(async () =>
    {
        using var load = new Loader { HoldFirst = true, ThrowUnexpected = true };
        using var slot = new SemaphoreSlim(1);
        using var tabs = new TabManager(load.Load, slot);
        var first = tabs.OpenAsync(PathOf("a.cs"));
        await load.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = tabs.OpenAsync(PathOf("b.cs"));
        var pending = tabs.PendingWork;
        load.Release.Set();
        try { await first; Assert.Fail("Load fault must propagate."); }
        catch (InvalidOperationException) { }
        try { await pending; Assert.Fail("Shutdown must observe the fault."); }
        catch (InvalidOperationException) { }
        Assert.That((await second).Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That((await tabs.OpenAsync(PathOf("c.cs"))).Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That(tabs.Tabs[0].Error!.Code, Is.EqualTo(ErrorCode.Unexpected));
        Assert.That(slot.CurrentCount, Is.EqualTo(1));
    });

    [Test]
    public void RealReadFailureKeepsVersionEncodingAndLeaseAndRetryCanRecover() => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-tabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        var first = Path.Combine(root, "a", "source.cs");
        var second = Path.Combine(root, "b", "source.cs");
        await File.WriteAllTextAsync(first, "original");
        await File.WriteAllTextAsync(second, "second");
        var source = new SourceAccess();
        using var operations = new AppOperations(new FileReader(source), new AvaloniaEditDocumentAdapter(), new DirectoryReader(source));
        var tabs = operations.Tabs;
        try
        {
            var a = (await tabs.OpenAsync(SourcePath.Create(first, root), EncodingChoice.Utf8)).Value;
            var b = (await tabs.OpenAsync(SourcePath.Create(second, root))).Value;
            Assert.That(tabs.Tabs, Has.Count.EqualTo(2));
            Assert.That(a.File!.Document.IsReleased, Is.False);
            var file = a.File;
            using var lease = file.Document.AcquireReadView();
            File.Delete(first);
            Assert.That((await tabs.ReloadAsync(a, EncodingChoice.Cp932)).Error!.Code, Is.EqualTo(ErrorCode.NotFound));
            Assert.That(a.Status, Is.EqualTo(TabStatus.Ready));
            Assert.That(a.File, Is.SameAs(file));
            Assert.That(a.EncodingChoice, Is.EqualTo(EncodingChoice.Utf8));
            Assert.That(a.DocumentVersion, Is.EqualTo(1));
            await File.WriteAllTextAsync(first, "replacement");
            Assert.That((await tabs.ReloadAsync(a)).Status, Is.EqualTo(OperationStatus.Success));
            Assert.That(a.DocumentVersion, Is.EqualTo(2));
            Assert.That(a.EncodingChoice, Is.EqualTo(EncodingChoice.Utf8));
            Assert.That(file.Document.IsReleased, Is.True);
            Assert.That(lease.ReadRange(0, (int)lease.Length), Is.EqualTo("original"));
            var missing = SourcePath.Create("missing.cs", root);
            var failed = await tabs.OpenAsync(missing);
            Assert.That(failed.Status, Is.EqualTo(OperationStatus.Failure));
            var failedTab = tabs.ActiveTab!;
            Assert.That(failedTab.Status, Is.EqualTo(TabStatus.Failed));
            await File.WriteAllTextAsync(missing.FullPath, "recovered");
            Assert.That((await tabs.OpenAsync(missing)).Status, Is.EqualTo(OperationStatus.Failure));
            Assert.That((await tabs.ReloadAsync(failedTab)).Status, Is.EqualTo(OperationStatus.Success));
            tabs.Select(b);
            operations.Dispose();
            await operations.PendingTabWork;
            Assert.That(tabs.Tabs, Is.Empty);
            Assert.That((await tabs.OpenAsync(missing)).Status, Is.EqualTo(OperationStatus.Cancelled));
        }
        finally { operations.Dispose(); await operations.PendingTabWork; Directory.Delete(root, true); }
    });

    private sealed class Loader : IDisposable
    {
        public bool HoldFirst { get; set; }
        public bool ThrowUnexpected { get; init; }
        public List<string> Calls { get; } = [];
        public LoadedFile? LastBuilt { get; private set; }
        public int MaxActive { get; private set; }
        private int _active;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public OperationResult<LoadedFile> Load(SourcePath path, EncodingChoice choice, CancellationToken ct)
        {
            MaxActive = Math.Max(MaxActive, Interlocked.Increment(ref _active));
            var name = Path.GetFileName(path.FullPath);
            Calls.Add(name);
            try
            {
                if (!ThrowUnexpected)
                {
                    using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
                    builder.Append(name.AsMemory());
                    LastBuilt = new(path, new(name.Length, DateTime.UnixEpoch), "utf-8", builder.Complete());
                }
                if (HoldFirst && name == "a.cs") { Entered.TrySetResult(); Release.Wait(); }
                if (ThrowUnexpected) throw new InvalidOperationException("test load fault");
                return OperationResult<LoadedFile>.Success(LastBuilt!);
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public void Dispose() => Release.Dispose();
    }
}
