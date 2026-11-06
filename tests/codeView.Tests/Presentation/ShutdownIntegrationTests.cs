using System.ComponentModel;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class ShutdownIntegrationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void UiFailureNotificationCannotBypassActualIoShutdown(bool notificationFails) => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-ui-failure-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "first");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "body.cs");
        File.WriteAllText(file, "// unchanged original");
        using var source = new CodeView.Tests.Operations.WorkspaceTests.ControlledSource();
        using var operations = new AppOperations(new FileReader(source), new AvaloniaEditDocumentAdapter(), new DirectoryReader(source));
        var view = new MainWindowViewModel(operations,
            new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))), YieldAsync);
        Task refresh = Task.CompletedTask;
        Task? shutdown = null;
        var failures = 0;
        var closeRequests = 0;
        PropertyChangedEventHandler notification = (_, args) =>
        {
            if (notificationFails && args.PropertyName == nameof(view.Status))
                throw new InvalidOperationException("UI status subscriber");
        };
        try
        {
            await view.InitializeAsync([folder, file]);
            var handle = view.SelectedTab!.State.File!.Document;
            using var lease = handle.AcquireReadView();
            source.DelayResolution = true;
            refresh = view.RefreshWorkspaceAsync();
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            view.PropertyChanged += notification;
            MainWindow.RunUiAction(() => throw new InvalidOperationException("UI command"), () =>
                MainWindow.ReportUiFailure(() => failures++, () => view.SetStatus("Fatal UI error"), () =>
                {
                    closeRequests++;
                    shutdown = MainWindow.StopAndWaitAsync(view.Stop, () => view.PendingWork, () => failures++);
                }));
            Assert.That(closeRequests, Is.EqualTo(1));
            Assert.That(failures, Is.EqualTo(notificationFails ? 2 : 1));
            Assert.That(operations.Startup.State, Is.EqualTo(StartupState.Stopping));
            Assert.That(view.Document, Is.Null);
            Assert.That(view.Tabs, Is.Empty);
            Assert.That(handle.IsReleased, Is.True);
            Assert.That(lease.ReadRange(0, (int)lease.Length), Is.EqualTo("// unchanged original"));
            Assert.That(shutdown, Is.Not.Null);
            Assert.That(shutdown!.IsCompleted, Is.False, "Notification failure must not abandon pending native I/O.");
            source.Release.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            await refresh;
            Assert.That(File.ReadAllText(file), Is.EqualTo("// unchanged original"));
        }
        finally
        {
            view.PropertyChanged -= notification;
            source.Release.Set();
            view.Stop();
            await refresh;
            if (shutdown is not null) await shutdown;
            await view.PendingWork;
            Directory.Delete(root, true);
        }
    });

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public void NotificationFailuresCannotSkipCancellationReleaseAndActualIoWait(int faultCount) => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-shutdown-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "first");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "body.cs");
        File.WriteAllText(file, "// original");
        using var source = new CodeView.Tests.Operations.WorkspaceTests.ControlledSource();
        using var operations = new AppOperations(new FileReader(source), new AvaloniaEditDocumentAdapter(), new DirectoryReader(source));
        var view = new MainWindowViewModel(operations,
            new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))), YieldAsync);
        Task refresh = Task.CompletedTask;
        var panelFault = new InvalidOperationException("search panel notification");
        var displayFault = new InvalidOperationException("document display notification");
        var treeFault = new InvalidOperationException("tree notification");
        var panelFailed = false;
        var finalWorkspaceNotification = false;
        PropertyChangedEventHandler panelNotification = (_, args) =>
        {
            if (faultCount > 0 && !panelFailed && args.PropertyName == nameof(view.SearchPanel.IsRunning))
            { panelFailed = true; throw panelFault; }
        };
        PropertyChangedEventHandler displayNotification = (_, args) =>
        {
            if (args.PropertyName == nameof(view.HasWorkspace)) finalWorkspaceNotification = true;
            if (faultCount > 1 && args.PropertyName == nameof(view.Status)) throw displayFault;
        };
        PropertyChangedEventHandler treeNotification = (_, args) =>
        {
            if (faultCount > 1 && args.PropertyName == nameof(view.FileTree.HasRows)) throw treeFault;
        };
        try
        {
            await view.InitializeAsync([folder, file]);
            var handle = view.SelectedTab!.State.File!.Document;
            using var lease = handle.AcquireReadView();
            source.DelayResolution = true;
            refresh = view.RefreshWorkspaceAsync();
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            view.SearchPanel.PropertyChanged += panelNotification;
            view.PropertyChanged += displayNotification;
            view.FileTree.PropertyChanged += treeNotification;
            Exception? observed = null;
            var failures = 0;
            var shutdown = MainWindow.StopAndWaitAsync(() =>
            {
                try { view.Stop(); }
                catch (Exception error) { observed = error; throw; }
            }, () => view.PendingWork, () => failures++);
            Assert.That(failures, Is.EqualTo(faultCount == 0 ? 0 : 1));
            if (faultCount == 0) Assert.That(observed, Is.Null);
            else if (faultCount == 1) Assert.That(observed, Is.SameAs(panelFault));
            else Assert.That(((AggregateException)observed!).InnerExceptions, Is.EqualTo(new[] { panelFault, displayFault, treeFault }));
            Assert.That(operations.Startup.State, Is.EqualTo(StartupState.Stopping));
            Assert.That(view.Document, Is.Null);
            Assert.That(view.Tabs, Is.Empty);
            Assert.That(view.FileTree.Rows, Is.Empty);
            Assert.That(view.HasWorkspace, Is.False);
            Assert.That(finalWorkspaceNotification, Is.True);
            Assert.That(handle.IsReleased, Is.True);
            Assert.That(lease.ReadRange(0, (int)lease.Length), Is.EqualTo("// original"));
            Assert.That(shutdown.IsCompleted, Is.False, "A cancellation request is not completion of native I/O.");
            view.Stop(); // Idempotent even after cleanup failed.
            await view.OpenFilesAsync([file], OpenOrigin.Menu);
            Assert.That(view.Tabs, Is.Empty);
            source.Release.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            await refresh;
            Assert.That(view.HasWorkspace, Is.False);
            Assert.That(failures, Is.EqualTo(faultCount == 0 ? 0 : 1));
        }
        finally
        {
            view.SearchPanel.PropertyChanged -= panelNotification;
            view.PropertyChanged -= displayNotification;
            view.FileTree.PropertyChanged -= treeNotification;
            source.Release.Set();
            view.Stop();
            await refresh;
            await view.PendingWork;
            Directory.Delete(root, true);
        }
    });

    private static async Task YieldAsync() => await Task.Yield();
}
