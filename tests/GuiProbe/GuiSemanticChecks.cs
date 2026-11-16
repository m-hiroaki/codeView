using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using CodeView.Operations;

namespace CodeView.Presentation;

internal sealed class GuiSemanticChecks(Window _window, MainWindowViewModel _viewModel, TextEditor _editor,
    Action<Control, KeyEventArgs> raiseSearchKey)
{
    internal sealed record Result(int[] ShutdownPids, IReadOnlyList<object> Timings,
        IReadOnlyList<double> ManagedFirstScroll, int MovedFrames);

    internal async Task<Result> RunAsync(string root, Action<bool, string> require, CancellationToken ct)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var panel = _viewModel.SearchPanel;
        var timings = new List<object>();
        var measure = Environment.GetEnvironmentVariable("CODEVIEW_GUI_SEMANTIC_MEASURE") == "1";
        var managedFirstScroll = new List<double>();
        var movedFrames = 0;
        require(_viewModel.SemanticServerProcessIds.Length == 0 && _viewModel.DefinitionMenuLabel.Contains("簡易"),
            "semantic-startup-uses-simple-search");
        var managed = Path.Combine(root, "managed");
        var managedCaller = Path.Combine(managed, "Caller.cs");
        await _viewModel.OpenFilesAsync([managed, managedCaller], OpenOrigin.Menu);
        require(_viewModel.SemanticOptions?.CSharpProject == Path.Combine(managed, "Demo.csproj")
            && Equals(_window.FindControl<MenuItem>("FindDefinitionsMenu")!.Header, "定義へジャンプ（意味解析）"),
            "semantic-folder-open-enables-roslyn");
        while (_viewModel.SemanticServerProcessIds.Length == 0 && !_viewModel.PendingWork.IsCompleted) await Frame(ct);
        require(_viewModel.SemanticServerProcessIds.Length == 1, "semantic-folder-open-starts-roslyn-before-f12");
        await CheckLanguageAsync(managed, "cs", "Run");
        var roslyn = _viewModel.SemanticServerProcessIds.Single();
        var config = await OpenOptionsAsync();
        require(config.GetVisualDescendants().OfType<TextBox>().Count() == 2, "semantic-dialog-omits-server-paths");
        Set(config, "RoslynProject", Path.Combine(managed, "missing.csproj"));
        Click(config, "EnableSemanticNavigation");
        await Frame(ct);
        require(config.IsVisible && _viewModel.SemanticServerProcessIds.SequenceEqual(new[] { roslyn })
            && _viewModel.Status.Contains("絶対パス"), "semantic-invalid-project-keeps-existing-session");
        config.Close();
        var native = Path.Combine(root, "native");
        await _viewModel.OpenFilesAsync([native, Path.Combine(native, "Caller.c")], OpenOrigin.Menu);
        await _viewModel.PendingWork.WaitAsync(ct);
        require(!IsRunning(roslyn) && _viewModel.SemanticServerProcessIds.Length == 0
            && _viewModel.DefinitionMenuLabel.Contains("簡易"), "semantic-workspace-switch-stops-server");

        config = await OpenOptionsAsync();
        Set(config, "ClangdDatabase", Path.Combine(native, "compile_commands.json"));
        Click(config, "EnableSemanticNavigation");
        while (config.IsVisible) await Frame(ct);
        require(_viewModel.SemanticServerProcessIds.Length == 0, "semantic-manual-enable-defers-server-launch");
        await CheckLanguageAsync(native, "c", "RunC");
        await CheckLanguageAsync(native, "cpp", "RunCpp");
        var clangd = _viewModel.SemanticServerProcessIds.Single();
        using (var process = Process.GetProcessById(clangd))
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(ct);
        }
        await JumpAsync(Path.Combine(native, "Caller.cpp"), Path.Combine(native, "Target.cpp"), "RunCpp");
        require(_viewModel.SemanticServerProcessIds.Single() != clangd, "semantic-restarts-after-server-crash");

        var restarted = _viewModel.SemanticServerProcessIds.Single();
        config = await OpenOptionsAsync();
        Click(config, "DisableSemanticNavigation");
        await _viewModel.PendingWork.WaitAsync(ct);
        await OpenAndSelectAsync(Path.Combine(native, "Caller.cpp"), "RunCpp");
        await SendKeyAsync(KeyModifiers.Shift);
        require(!IsRunning(restarted) && _viewModel.ReferencesMenuLabel.Contains("名前一致")
            && panel.Description.Contains("名前一致") && panel.Rows.Count > 1, "semantic-switches-back-to-name-search");

        config = await OpenOptionsAsync();
        Click(config, "EnableSemanticNavigation");
        while (config.IsVisible) await Frame(ct);
        await JumpAsync(Path.Combine(native, "Caller.cpp"), Path.Combine(native, "Target.cpp"), "RunCpp");
        var shutdownPids = _viewModel.SemanticServerProcessIds;
        require(shutdownPids.Length == 1, "semantic-server-active-before-app-exit");
        if (measure && movedFrames < 2) throw new InvalidOperationException("No scrolling was observed during analysis.");
        return new(shutdownPids, timings, managedFirstScroll, movedFrames);

        async Task<Window> OpenOptionsAsync()
        {
            _window.FindControl<MenuItem>("SemanticSettingsMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            while (true)
            {
                var window = desktop.Windows.FirstOrDefault(item => item.Title == "意味解析の設定" && item.IsVisible);
                if (window is not null) return window;
                await Frame(ct);
            }
        }

        async Task CheckLanguageAsync(string workspace, string extension, string symbol)
        {
            var caller = Path.Combine(workspace, "Caller." + extension);
            var target = Path.Combine(workspace, "Target." + extension);
            var elapsed = await JumpAsync(caller, target, symbol, measure && extension == "cs", retry: extension != "cs");
            require(_editor.SelectedText == symbol && _editor.TextArea.IsFocused,
                $"semantic-{extension}-f12-selects-original-definition");
            var repeatedDefinition = measure ? (await JumpAsync(caller, target, symbol)).Total : (double?)null;
            await OpenAndSelectAsync(caller, symbol);
            var watch = Stopwatch.StartNew();
            await SendKeyAsync(KeyModifiers.Shift);
            var repeats = watch.Elapsed.TotalMilliseconds;
            require(panel.IsVisible && panel.Description.Contains("意味解析") && panel.Rows.Count == 1
                && panel.Rows[0].FullPath == caller && panel.CanActivate(panel.Rows[0]),
                $"semantic-{extension}-shift-f12-finds-symbol-reference");
            timings.Add(new { Language = extension, elapsed.First, elapsed.Total,
                RepeatedDefinitionMilliseconds = repeatedDefinition, ReferencesMilliseconds = repeats });
        }

        async Task<(double First, double Total)> JumpAsync(string caller, string target, string symbol, bool scroll = false, bool retry = true)
        {
            await OpenAndSelectAsync(caller, symbol);
            var watch = Stopwatch.StartNew();
            var first = 0d;
            while (_viewModel.FilePath == caller)
            {
                panel.IsVisible = false;
                await SendKeyAsync(KeyModifiers.None, scroll && first == 0);
                if (first == 0) first = watch.Elapsed.TotalMilliseconds;
                if (_viewModel.FilePath != caller || !retry) break;
                if (!panel.Summary.Contains("部分結果"))
                    throw new InvalidOperationException($"Unexpected definition result for {caller}: {panel.Summary} / {_viewModel.Status}");
                await Task.Delay(50, ct);
            }
            if (_viewModel.FilePath != target || panel.IsVisible
                || _editor.SelectionStart != _editor.Document.Text.LastIndexOf(symbol, StringComparison.Ordinal))
                throw new InvalidOperationException($"Definition for {caller} expected {target}, opened {_viewModel.FilePath}; " +
                    $"selection={_editor.SelectionStart}/{_editor.SelectedText}; visible={panel.IsVisible}; {panel.Summary} / {_viewModel.Status}");
            return (first, watch.Elapsed.TotalMilliseconds);
        }

        async Task OpenAndSelectAsync(string path, string symbol)
        {
            await _viewModel.OpenFilesAsync([path], OpenOrigin.Menu);
            await Frame(ct);
            var offset = _editor.Document.Text.IndexOf(symbol, StringComparison.Ordinal);
            _editor.Select(offset + 1, 0);
        }

        async Task SendKeyAsync(KeyModifiers modifiers, bool scroll = false)
        {
            var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F12, KeyModifiers = modifiers };
            raiseSearchKey(_editor.TextArea, key);
            var pending = _viewModel.PendingWork;
            if (scroll)
            {
                var view = (IScrollable)_editor.TextArea.TextView;
                var watch = Stopwatch.StartNew();
                while (!pending.IsCompleted)
                {
                    var previous = view.Offset.Y;
                    var maximum = view.Extent.Height - view.Viewport.Height;
                    view.Offset = new Vector(0, maximum > 0 ? (previous + 48) % maximum : 0);
                    await Frame(ct);
                    if (view.Offset.Y != previous) movedFrames++;
                    managedFirstScroll.Add(watch.Elapsed.TotalMilliseconds);
                    watch.Restart();
                }
            }
            await pending.WaitAsync(ct);
            await Frame(ct);
            // The result selection is dispatched at Loaded priority after the search updates the tab.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, ct);
            if (!key.Handled) throw new InvalidOperationException("F12 was not handled.");
        }

        static void Set(Window window, string control, string value) =>
            window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == control).Text = value;
        static void Click(Window window, string control) => window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Name == control).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static bool IsRunning(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
        }
    }

    private Task Frame(CancellationToken ct)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _window.RequestAnimationFrame(_ => ready.TrySetResult());
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
}
