using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class StartupSettingsIntegrationTests
{
    [Test]
    public void CliMenuAndDropRemainFifoUntilSettingsCompleteExactlyOnce() => SingleThreadTestContext.Run(async () =>
    {
        await using var fixture = new Fixture();
        var startup = fixture.View.InitializeAsync([fixture.Cli]);
        await fixture.Access.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.View.OpenFilesAsync([fixture.Menu], OpenOrigin.Menu);
        await fixture.View.OpenDropAsync(FileDropInputTests.Files(fixture.Drop));
        Assert.That(fixture.Operations.Startup.State, Is.EqualTo(StartupState.WaitingSettings));
        Assert.That(fixture.Operations.Startup.OutstandingCount, Is.EqualTo(3));
        Assert.That(fixture.View.Tabs, Is.Empty);
        fixture.Access.Release.Set();
        await startup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.View.Tabs.Select(t => t.FullPath), Is.EqualTo(new[] { fixture.Cli, fixture.Menu, fixture.Drop }));
        Assert.That(fixture.Operations.Startup.Settings!.Snapshot.ExcludedDirectoryNames, Is.EqualTo(new[] { "late-excluded" }));
        Assert.That(fixture.Operations.Startup.OutstandingCount, Is.Zero);
        Assert.That(fixture.Clock.Timer!.Disposed, Is.True);
        fixture.Clock.Expire(); // A retired timer cannot reapply defaults or drain twice.
        await fixture.View.InitializeAsync([fixture.Cli]);
        Assert.That(fixture.Access.OpenCount, Is.EqualTo(1));
        Assert.That(fixture.View.Tabs, Has.Count.EqualTo(3));
        Assert.That(fixture.View.FilePath, Is.EqualTo(fixture.Drop));
    });

    [Test]
    public void TimeoutDrainsQueuedFilesAndLateSuccessCannotReplaceFixedDefaults() => SingleThreadTestContext.Run(async () =>
    {
        await using var fixture = new Fixture();
        var startup = fixture.View.InitializeAsync([fixture.Cli]);
        await fixture.Access.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.Clock.Timer!.Due, Is.EqualTo(TimeSpan.FromSeconds(2)));
        // Fire off the UI thread, then keep this UI turn until the boundary Open
        // has been submitted. No UI continuation is awaited by this worker.
        Task.Run(fixture.Clock.Expire).GetAwaiter().GetResult();
        // The deadline has fired, but its UI continuation has not fixed defaults yet.
        Assert.That(fixture.Operations.Startup.State, Is.EqualTo(StartupState.WaitingSettings));
        await fixture.View.OpenFilesAsync([fixture.Menu], OpenOrigin.Menu);
        await startup.WaitAsync(TimeSpan.FromSeconds(5));
        var fixedSettings = fixture.Operations.Startup.Settings;
        Assert.That(fixedSettings!.Snapshot, Is.SameAs(SettingsSnapshot.Default));
        Assert.That(fixedSettings.CanSave, Is.False);
        Assert.That(fixture.View.Document!.Text, Is.EqualTo("// menu"));
        Assert.That(fixture.View.Status, Does.Contain("既定値"));
        Assert.That(fixture.View.PendingWork.IsCompleted, Is.False, "Track the original I/O task after timeout.");
        await fixture.View.OpenDropAsync(FileDropInputTests.Files(fixture.Drop));
        var status = fixture.View.Status;
        fixture.Access.Release.Set();
        await fixture.View.PendingWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.Operations.Startup.Settings, Is.SameAs(fixedSettings));
        Assert.That(fixture.View.Status, Is.EqualTo(status));
        Assert.That(fixture.View.Tabs.Select(t => t.FullPath), Is.EqualTo(new[] { fixture.Cli, fixture.Menu, fixture.Drop }));
        Assert.That(fixture.Access.OpenCount, Is.EqualTo(1));
    });

    [TestCase(false)]
    [TestCase(true)]
    public void StopDuringSettingsReadWaitsForActualWorkerAndNeverOpensFiles(bool timeoutFirst) => SingleThreadTestContext.Run(async () =>
    {
        await using var fixture = new Fixture();
        var startup = fixture.View.InitializeAsync([]);
        await fixture.Access.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (timeoutFirst) { fixture.Clock.Expire(); await startup; }
        if (!timeoutFirst) await fixture.View.OpenFilesAsync([fixture.Cli], OpenOrigin.Menu);
        fixture.View.Stop();
        await fixture.View.OpenFilesAsync([fixture.Cli], OpenOrigin.Menu);
        await startup.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = fixture.View.PendingWork;
        Assert.That(pending.IsCompleted, Is.False);
        Assert.That(fixture.Operations.Startup.State, Is.EqualTo(StartupState.Stopping));
        fixture.Access.Release.Set();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.View.HasDocument, Is.False);
        Assert.That(fixture.View.Tabs, Is.Empty);
        Assert.That(fixture.Operations.Startup.OutstandingCount, Is.Zero);
        Assert.That(fixture.Clock.Timer!.Disposed, Is.True);
    });

    [Test]
    public void LateUnexpectedFailureIsObservedRatherThanAdoptedAsDefaultsAgain() => SingleThreadTestContext.Run(async () =>
    {
        await using var fixture = new Fixture();
        var fatal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.View.FatalFailure += () => fatal.TrySetResult();
        var startup = fixture.View.InitializeAsync([fixture.Cli]);
        await fixture.Access.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Expire();
        await startup;
        var fixedSettings = fixture.Operations.Startup.Settings;
        fixture.Access.Failure = new InvalidOperationException("controlled settings failure");
        fixture.Access.Release.Set();
        await fatal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.View.PendingWork;
        Assert.That(fixture.View.Status, Does.Contain("予期しない設定読込エラー"));
        Assert.That(fixture.Operations.Startup.Settings, Is.SameAs(fixedSettings));
        Assert.That(fixture.Access.OpenCount, Is.EqualTo(1));
    });

    [Test]
    public void KnownReadFailureUsesDefaultsAndKeepsNoticeAfterQueuedOpen() => SingleThreadTestContext.Run(async () =>
    {
        await using var fixture = new Fixture();
        fixture.Access.Failure = new UnauthorizedAccessException();
        fixture.Access.Release.Set();
        await fixture.View.InitializeAsync([fixture.Cli]);
        Assert.That(fixture.Operations.Startup.Settings!.Warning!.Code, Is.EqualTo(ErrorCode.AccessDenied));
        Assert.That(fixture.Operations.Startup.Settings.CanSave, Is.False);
        Assert.That(fixture.View.Status, Does.Contain("既定値"));
        Assert.That(fixture.View.Document!.Text, Is.EqualTo("// cli"));
    });

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void FailureAfterStoppingReachesShutdownWithoutUpdatingUi(bool timeoutFirst, bool unrelatedCancellation) => SingleThreadTestContext.Run(async () =>
    {
        await using var fixture = new Fixture();
        var startup = fixture.View.InitializeAsync([]);
        await fixture.Access.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (timeoutFirst) { fixture.Clock.Expire(); await startup; }
        fixture.View.Stop();
        await startup.WaitAsync(TimeSpan.FromSeconds(5));
        var status = fixture.View.Status;
        var fatalNotifications = 0;
        fixture.View.FatalFailure += () => fatalNotifications++;
        var failures = 0;
        var shutdown = MainWindow.StopAndWaitAsync(fixture.View.Stop, () => fixture.View.PendingWork, () => failures++);
        Assert.That(shutdown.IsCompleted, Is.False);
        fixture.ExpectedPendingFailure = fixture.Access.Failure = unrelatedCancellation
            ? new OperationCanceledException(new CancellationToken(true))
            : new InvalidOperationException("settings failed during shutdown");
        fixture.Access.Release.Set();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(failures, Is.EqualTo(1));
        Assert.That(fatalNotifications, Is.Zero, "Already closing: report through PendingWork instead of reopening a UI notification.");
        Assert.That(fixture.View.Status, Is.EqualTo(status));
        Assert.That(fixture.View.HasDocument, Is.False);
        Assert.That(fixture.Operations.Startup.State, Is.EqualTo(StartupState.Stopping));
    });

    [Test]
    public void FailedStatusNotificationStillSignalsFatalShutdown() => SingleThreadTestContext.Run(async () =>
    {
        await using var fixture = new Fixture();
        var startup = fixture.View.InitializeAsync([]);
        await fixture.Access.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Expire();
        await startup;
        var fatal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.View.FatalFailure += () => fatal.TrySetResult();
        fixture.ExpectedPendingFailure = new InvalidOperationException("status notification failed");
        System.ComponentModel.PropertyChangedEventHandler failedNotification = (_, args) =>
        {
            if (args.PropertyName == nameof(fixture.View.Status)) throw fixture.ExpectedPendingFailure;
        };
        fixture.View.PropertyChanged += failedNotification;
        try
        {
            fixture.Access.Failure = new InvalidOperationException("settings read failed");
            fixture.Access.Release.Set();
            await fatal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.View.PendingWork);
        }
        finally { fixture.View.PropertyChanged -= failedNotification; }
    });

    [TestCase(false)]
    [TestCase(true)]
    public void WorkerTimeoutOrUnrelatedCancellationCannotFixDefaults(bool cancellation) => SingleThreadTestContext.Run(async () =>
    {
        await using var fixture = new Fixture();
        var fatal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.View.FatalFailure += () => fatal.TrySetResult();
        var startup = fixture.View.InitializeAsync([fixture.Cli]);
        await fixture.Access.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Exception error = cancellation ? new OperationCanceledException(new CancellationToken(true)) : new TimeoutException("worker timeout");
        fixture.Access.Failure = error;
        fixture.Access.Release.Set();
        Exception? observed = null;
        try { await startup.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception failure) { observed = failure; }
        Assert.That(observed, Is.SameAs(error));
        await fatal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.View.PendingWork;
        Assert.That(fixture.Operations.Startup.State, Is.EqualTo(StartupState.WaitingSettings));
        Assert.That(fixture.Operations.Startup.Settings, Is.Null);
        Assert.That(fixture.View.Tabs, Is.Empty);
        Assert.That(fixture.Clock.Timer!.Disposed, Is.True);
    });

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "codeview-startup-" + Guid.NewGuid().ToString("N"));
        public readonly ControlledAccess Access = new();
        public readonly ControlledClock Clock = new();
        public readonly AppOperations Operations;
        public readonly MainWindowViewModel View;
        public Exception? ExpectedPendingFailure;
        public string Cli => Path.Combine(_root, "cli.cs");
        public string Menu => Path.Combine(_root, "menu.cs");
        public string Drop => Path.Combine(_root, "drop.cs");

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Cli, "// cli");
            File.WriteAllText(Menu, "// menu");
            File.WriteAllText(Drop, "// drop");
            var settings = Path.Combine(_root, "settings.json");
            File.WriteAllText(settings, "{\"excludedDirectoryNames\":[\"late-excluded\"]}");
            var source = new SourceAccess();
            Operations = new(new FileReader(source), new AvaloniaEditDocumentAdapter(), new DirectoryReader(source));
            View = new(Operations, new SettingsStore(new(settings, Path.Combine(_root, "user.json")), Access), timeProvider: Clock);
        }

        public async ValueTask DisposeAsync()
        {
            Access.Release.Set();
            View.Stop();
            try { await View.PendingWork.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (ReferenceEquals(error, ExpectedPendingFailure)) { }
            finally
            {
                Operations.Dispose();
                Access.Release.Dispose();
                Directory.Delete(_root, true);
            }
        }
    }

    private sealed class ControlledAccess : ISourceAccess
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        public int OpenCount;
        public Exception? Failure;
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct)
        {
            Interlocked.Increment(ref OpenCount);
            Entered.TrySetResult();
            Release.Wait(); // Deliberately non-cancellable, like a stalled filesystem call.
            if (Failure is { } error) throw error;
            return new SourceAccess().OpenRead(path, ct);
        }
        public SourceStamp GetStamp(SourcePath path) => throw new NotSupportedException();
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => throw new NotSupportedException();
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct) => throw new NotSupportedException();
        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ControlledClock : TimeProvider
    {
        public ControlledTimer? Timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.That(Timer, Is.Null, "One settings deadline per startup.");
            Assert.That(period, Is.EqualTo(Timeout.InfiniteTimeSpan));
            return Timer = new(callback, state, dueTime);
        }
        public void Expire() => Timer!.Fire();
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; } = due;
        public bool Disposed { get; private set; }
        public void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
