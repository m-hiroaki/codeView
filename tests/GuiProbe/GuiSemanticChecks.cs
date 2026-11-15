using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CodeView.Operations;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private async Task<object> CheckSemanticAsync(string root, Action<bool, string> require, CancellationToken ct)
    {
        var tools = (Roslyn: Env("CODEVIEW_TEST_ROSLYN"), Clangd: Env("CODEVIEW_TEST_CLANGD"));
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var panel = _viewModel.SearchPanel;
        var timings = new List<object>();
        require(_viewModel.SemanticServerProcessIds.Length == 0 && _viewModel.DefinitionMenuLabel.Contains("簡易"),
            "semantic-startup-uses-simple-search");
        var managed = Path.Combine(root, "managed");
        var managedCaller = Path.Combine(managed, "Caller.cs");
        await _viewModel.OpenFilesAsync([managed, managedCaller], OpenOrigin.Menu);
        var config = await OpenOptionsAsync();
        Set(config, "RoslynExecutable", Path.Combine(managed, "missing.exe"));
        Click(config, "EnableSemanticNavigation");
        await Frame(ct);
        require(config.IsVisible && _viewModel.SemanticServerProcessIds.Length == 0
            && _viewModel.Status.Contains("絶対パス"), "semantic-unavailable-server-leaves-simple-search");
        Set(config, "RoslynExecutable", tools.Roslyn);
        Set(config, "RoslynProject", Path.Combine(managed, "Demo.csproj"));
        Click(config, "EnableSemanticNavigation");
        while (config.IsVisible) await Frame(ct);
        require(_viewModel.SemanticServerProcessIds.Length == 0
            && Equals(_window.FindControl<MenuItem>("FindDefinitionsMenu")!.Header, "定義へジャンプ（意味解析）"),
            "semantic-enable-defers-server-launch");
        await CheckLanguageAsync(managed, "cs", "Run");
        var roslyn = _viewModel.SemanticServerProcessIds.Single();
        var native = Path.Combine(root, "native");
        await _viewModel.OpenFilesAsync([native, Path.Combine(native, "Caller.c")], OpenOrigin.Menu);
        await _viewModel.PendingWork.WaitAsync(ct);
        require(!IsRunning(roslyn) && _viewModel.SemanticServerProcessIds.Length == 0
            && _viewModel.DefinitionMenuLabel.Contains("簡易"), "semantic-workspace-switch-stops-server");

        config = await OpenOptionsAsync();
        Set(config, "ClangdExecutable", tools.Clangd);
        Set(config, "ClangdDatabase", Path.Combine(native, "compile_commands.json"));
        Click(config, "EnableSemanticNavigation");
        while (config.IsVisible) await Frame(ct);
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
        Set(config, "ClangdExecutable", tools.Clangd);
        Click(config, "EnableSemanticNavigation");
        while (config.IsVisible) await Frame(ct);
        await JumpAsync(Path.Combine(native, "Caller.cpp"), Path.Combine(native, "Target.cpp"), "RunCpp");
        var shutdownPids = _viewModel.SemanticServerProcessIds;
        require(shutdownPids.Length == 1, "semantic-server-active-before-app-exit");
        return new { Timings = timings, ShutdownPids = shutdownPids };

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
            var elapsed = await JumpAsync(caller, target, symbol);
            require(_editor.SelectedText == symbol && _editor.TextArea.IsFocused,
                $"semantic-{extension}-f12-selects-original-definition");
            await OpenAndSelectAsync(caller, symbol);
            var watch = Stopwatch.StartNew();
            await SendKeyAsync(KeyModifiers.Shift);
            var repeats = watch.Elapsed.TotalMilliseconds;
            require(panel.IsVisible && panel.Description.Contains("意味解析") && panel.Rows.Count == 1
                && panel.Rows[0].FullPath == caller && panel.CanActivate(panel.Rows[0]),
                $"semantic-{extension}-shift-f12-finds-symbol-reference");
            timings.Add(new { Language = extension, elapsed.First, elapsed.Total, ReferencesMilliseconds = repeats });
        }

        async Task<(double First, double Total)> JumpAsync(string caller, string target, string symbol)
        {
            await OpenAndSelectAsync(caller, symbol);
            var watch = Stopwatch.StartNew();
            var first = 0d;
            while (_viewModel.FilePath == caller)
            {
                panel.IsVisible = false;
                await SendKeyAsync(KeyModifiers.None);
                if (first == 0) first = watch.Elapsed.TotalMilliseconds;
                if (_viewModel.FilePath != caller) break;
                if (!panel.Summary.Contains("部分結果"))
                    throw new InvalidOperationException("Unexpected semantic definition result: " + panel.Summary);
                await Task.Delay(50, ct);
            }
            if (_viewModel.FilePath != target || panel.IsVisible
                || _editor.SelectionStart != _editor.Document.Text.LastIndexOf(symbol, StringComparison.Ordinal))
                throw new InvalidOperationException("Definition did not directly select the expected source range.");
            return (first, watch.Elapsed.TotalMilliseconds);
        }

        async Task OpenAndSelectAsync(string path, string symbol)
        {
            await _viewModel.OpenFilesAsync([path], OpenOrigin.Menu);
            await Frame(ct);
            var offset = _editor.Document.Text.IndexOf(symbol, StringComparison.Ordinal);
            _editor.Select(offset + 1, 0);
        }

        async Task SendKeyAsync(KeyModifiers modifiers)
        {
            var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F12, KeyModifiers = modifiers };
            RaiseSearchKey(_editor.TextArea, key);
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct);
            await Frame(ct);
            if (!key.Handled) throw new InvalidOperationException("F12 was not handled.");
        }

        static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name);
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
}
