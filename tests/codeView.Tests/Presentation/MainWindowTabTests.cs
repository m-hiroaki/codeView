using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class MainWindowTabTests
{
    [Test]
    public void MultipleFilesKeepBodiesAndReopenSelectsWithoutRereading() => WithView(async fixture =>
    {
        var view = fixture.View;
        await view.InitializeAsync([fixture.First, fixture.Second]);
        Assert.That(view.Tabs, Has.Count.EqualTo(2));
        var secondBody = view.Document;
        var first = view.Tabs[0];
        view.SelectedTab = first;
        var firstBody = view.Document;
        Assert.That(firstBody!.Text, Is.EqualTo("first\n日本語末尾"));
        await File.WriteAllTextAsync(fixture.First, "external change");
        await view.OpenFilesAsync([fixture.First], OpenOrigin.Menu);
        Assert.That(view.Tabs, Has.Count.EqualTo(2));
        Assert.That(view.Document, Is.SameAs(firstBody));
        Assert.That(view.Document!.Text, Does.StartWith("first"));
        view.CycleTab(1);
        Assert.That(view.Document, Is.SameAs(secondBody));
        view.CycleTab(1);
        Assert.That(view.Document, Is.SameAs(firstBody));
        view.CycleTab(-1);
        Assert.That(view.Document, Is.SameAs(secondBody));
    });

    [Test]
    public void SwitchingRestoresIndependentPositionViewportAndGuardsBindingResets() => WithView(async fixture =>
    {
        var view = fixture.View;
        await view.InitializeAsync([fixture.First, fixture.Second]);
        var first = view.Tabs[0];
        var second = view.Tabs[1];
        view.SetPosition(1, 3);
        view.SetViewport(12.5, 250.25);
        view.SelectedTab = first;
        view.SetPosition(2, 4);
        view.SetViewport(20, 500);
        (int Line, int Column) caret = default;
        (double X, double Y, long Epoch) viewport = default;
        view.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(view.Document)) return;
            view.SetPosition(1, 1);
            view.SetViewport(0, 0);
        };
        view.RestoreCaret += (line, column) => caret = (line, column);
        view.RestoreViewport += (x, y, epoch) => viewport = (x, y, epoch);
        var epoch = view.ViewEpoch;
        view.SelectedTab = second;
        Assert.That(caret, Is.EqualTo((1, 3)));
        Assert.That((viewport.X, viewport.Y), Is.EqualTo((12.5, 250.25)));
        Assert.That(viewport.Epoch, Is.GreaterThan(epoch));
        view.SelectedTab = first;
        Assert.That(caret, Is.EqualTo((2, 4)));
        Assert.That((viewport.X, viewport.Y), Is.EqualTo((20d, 500d)));
        Assert.That(view.Position, Is.EqualTo("行 2, 列 4"));
        Assert.That(first.State.Position, Is.EqualTo(new TextPosition(2, 4)));
    });

    [Test]
    public void ClosingBackgroundAndLastTabsReleasesOnlyTheirDocuments() => WithView(async fixture =>
    {
        var view = fixture.View;
        await view.InitializeAsync([fixture.First, fixture.Second]);
        var first = view.Tabs[0];
        var firstHandle = first.State.File!.Document;
        var secondHandle = view.SelectedTab!.State.File!.Document;
        var body = view.Document;
        var epoch = view.ViewEpoch;
        view.CloseTab(first);
        Assert.That(firstHandle.IsReleased, Is.True);
        Assert.That(view.Document, Is.SameAs(body));
        Assert.That(view.ViewEpoch, Is.EqualTo(epoch));
        view.CloseFile();
        Assert.That(secondHandle.IsReleased, Is.True);
        Assert.That(view.HasTabs, Is.False);
        Assert.That(view.Document, Is.Null);
        Assert.That(view.FilePath, Is.Empty);
        Assert.That(view.CanReload, Is.False);
        Assert.That(view.CanReopenEncoding, Is.False);
        view.CycleTab(1);
        Assert.That(view.SelectedTab, Is.Null);
    });

    [Test]
    public void FailedTabCanRetryEncodingAndReloadClampsPositionWithoutLosingOtherTab() => WithView(async fixture =>
    {
        var view = fixture.View;
        await view.InitializeAsync([fixture.First]);
        var firstBody = view.Document;
        var bad = Path.Combine(fixture.Root, "utf16.txt");
        // ASCII in BOM-less UTF-16 supplies NUL bytes, making auto-detection reject it.
        await File.WriteAllBytesAsync(bad, System.Text.Encoding.Unicode.GetBytes("日本語a"));
        await view.OpenFilesAsync([bad], OpenOrigin.Menu);
        Assert.That(view.Tabs, Has.Count.EqualTo(2));
        Assert.That(view.SelectedTab!.State.Status, Is.EqualTo(TabStatus.Failed));
        Assert.That(view.Document, Is.Null);
        Assert.That(view.CanReopenEncoding, Is.True);
        Assert.That(view.CanReload, Is.True);
        await view.ReopenEncodingAsync(EncodingChoice.Utf16Le);
        Assert.That(view.Document!.Text, Is.EqualTo("日本語a"));
        view.SetPosition(1, 4);
        var old = view.Document;
        await File.WriteAllBytesAsync(bad, [0x61]);
        await view.ReloadAsync();
        Assert.That(view.Document, Is.SameAs(old));
        Assert.That(view.Position, Is.EqualTo("行 1, 列 4"));
        await File.WriteAllBytesAsync(bad, System.Text.Encoding.Unicode.GetBytes("短"));
        await view.ReloadAsync();
        Assert.That(view.Position, Is.EqualTo("行 1, 列 2"));
        Assert.That(view.EncodingName, Is.EqualTo("UTF-16 LE"));
        view.SelectedTab = view.Tabs[0];
        Assert.That(view.Document, Is.SameAs(firstBody));
    });

    [Test]
    public void SameNameTabsShowParentPathsAndFolderSwitchKeepsEveryTab() => WithView(async fixture =>
    {
        var other = Path.Combine(fixture.Root, "nested");
        Directory.CreateDirectory(other);
        var sameName = Path.Combine(other, "first.cs");
        await File.WriteAllTextAsync(sameName, "same name");
        var view = fixture.View;
        await view.InitializeAsync([fixture.First, sameName]);
        Assert.That(view.Tabs.Select(tab => tab.Label).Distinct().Count(), Is.EqualTo(2));
        Assert.That(view.Tabs.All(tab => tab.Label.Contains(Path.GetDirectoryName(tab.FullPath)!, StringComparison.Ordinal)), Is.True);
        var body = view.Document;
        await view.OpenFilesAsync([other], OpenOrigin.Menu);
        view.CloseWorkspace();
        Assert.That(view.Tabs, Has.Count.EqualTo(2));
        Assert.That(view.Document, Is.SameAs(body));
    });

    [TestCase(false)]
    [TestCase(true)]
    public void BackgroundCompletionOrCloseCannotReplaceSelectedBody(bool closeLoading) => WithView(async fixture =>
    {
        var view = fixture.View;
        await view.InitializeAsync([fixture.First]);
        var first = view.Tabs[0];
        var body = view.Document;
        fixture.Source.DelaySecond = true;
        var open = view.OpenFilesAsync([fixture.Second], OpenOrigin.Menu);
        await fixture.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = view.Tabs[1];
        view.SelectedTab = first;
        try
        {
            if (closeLoading) view.CloseTab(second);
            Assert.That(view.Document, Is.SameAs(body));
        }
        finally { fixture.Source.Release.Set(); }
        await open;
        Assert.That(view.SelectedTab, Is.SameAs(first));
        Assert.That(view.Document, Is.SameAs(body));
        Assert.That(view.Tabs.Count, Is.EqualTo(closeLoading ? 1 : 2));
        if (!closeLoading)
        {
            Assert.That(second.StatusText, Is.Empty);
            view.SelectedTab = second;
            Assert.That(view.Document!.Text, Is.EqualTo("second body"));
        }
    });

    [TestCase(false)]
    [TestCase(true)]
    public void SyntaxNotificationFailureReachesShutdown(bool statusNotificationFails) => SingleThreadTestContext.Run(async () =>
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.First, "class First {}");
        var view = fixture.View;
        var syntaxError = new InvalidOperationException("Could not apply syntax colors.");
        var statusError = new InvalidOperationException("Could not notify status.");
        var fatal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.SyntaxChanged += highlighting =>
        {
            if (highlighting is not null) throw syntaxError;
        };
        view.PropertyChanged += (_, args) =>
        {
            if (statusNotificationFails && args.PropertyName == nameof(view.Status)
                && view.Status.Contains("構文色分けエラー", StringComparison.Ordinal)) throw statusError;
        };
        view.FatalFailure += () => fatal.TrySetResult();
        await view.InitializeAsync([fixture.First]);
        await fatal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var failure = view.PendingWork.Exception!.Flatten().InnerExceptions;
        Assert.That(failure, Does.Contain(syntaxError));
        if (statusNotificationFails) Assert.That(failure, Does.Contain(statusError));
        else Assert.That(failure, Has.Count.EqualTo(1));
        var failures = 0;
        await MainWindow.StopAndWaitAsync(view.Stop, () => view.PendingWork, () => failures++);
        Assert.That(failures, Is.EqualTo(1));
        Assert.That(view.HasDocument, Is.False);
    });

    [Test]
    public void CopyCapturesOriginalRangeAndReleasesItsLeaseAfterClosing() => WithView(async fixture =>
    {
        const string text = "A\r\n日本語\r😀\nZ";
        File.WriteAllText(fixture.First, text);
        var view = fixture.View;
        await view.InitializeAsync([fixture.First]);
        var handle = view.SelectedTab!.State.File!.Document;
        Assert.That(view.BeginCopy(1, 0), Is.Null);
        Assert.That(view.BeginCopy(1, text.Length), Is.Null);
        using var selection = view.BeginCopy(1, text.Length - 2);
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
        view.CloseFile();
        Assert.That(view.BeginCopy(1, 1), Is.Null);
        Assert.That(handle.IsReleased, Is.True);
        Assert.That(await Task.Run(() => selection!.ReadText(default)), Is.EqualTo(text[1..^1]));
        selection!.Dispose();
        Assert.That(handle.ActiveReadLeases, Is.Zero);
    });

    private static void WithView(Func<Fixture, Task> run) => SingleThreadTestContext.Run(async () =>
    {
        using var fixture = new Fixture();
        try { await run(fixture); }
        finally { fixture.Source.Release.Set(); fixture.View.Stop(); await fixture.View.PendingWork; }
    });

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codeview-tab-ui-" + Guid.NewGuid().ToString("N"));
        public string First => Path.Combine(Root, "first.cs");
        public string Second => Path.Combine(Root, "second.cs");
        public DelaySource Source { get; } = new();
        public MainWindowViewModel View { get; }
        private readonly AppOperations _operations;
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(First, "first\n日本語末尾");
            File.WriteAllText(Second, "second body");
            _operations = new(new FileReader(Source), new AvaloniaEditDocumentAdapter(), new DirectoryReader(Source));
            View = new(_operations, new SettingsStore(new(Path.Combine(Root, "portable.json"), Path.Combine(Root, "user.json"))), YieldAsync);
        }
        private static async Task YieldAsync() => await Task.Yield();
        public void Dispose() { _operations.Dispose(); Source.Release.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class DelaySource : ISourceAccess
    {
        private readonly SourceAccess _source = new();
        public bool DelaySecond { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct)
        {
            if (DelaySecond && Path.GetFileName(path.FullPath) == "second.cs")
            { Entered.TrySetResult(); Release.Wait(ct); }
            return _source.OpenRead(path, ct);
        }
        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => _source.InspectPath(path, ct);
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => _source.ResolveDirectory(path, ct);
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct) => _source.EnumerateChildren(path, ct);
        public SourceStamp GetStamp(SourcePath path) => _source.GetStamp(path);
    }
}
