using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CodeView.Operations;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private readonly bool _referenceChecks = Environment.GetEnvironmentVariable("CODEVIEW_GUI_REFERENCE_CHECKS") == "1";
    private object? _referenceResults;

    private static void ValidateReferenceMode(bool hasInput)
    {
        if (Environment.GetEnvironmentVariable("CODEVIEW_GUI_REFERENCE_CHECKS") != "1") return;
        if (hasInput || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            new[] { "CODEVIEW_GUI_ROWS", "CODEVIEW_GUI_WINDOWED", "CODEVIEW_GUI_INTERACTIVE", "CODEVIEW_GUI_READONLY_CHECKS",
                "CODEVIEW_GUI_SEARCH_PANEL_CHECKS", "CODEVIEW_GUI_ROW_VISUAL_CHECKS" }
                .Any(name => Environment.GetEnvironmentVariable(name) == "1"))
            throw new ArgumentException("参照検索の自動操作は入力なしの専用CIで単独実行してください。");
    }

    private async Task RunReferenceChecksAsync()
    {
        var checks = new List<string>();
        void Require(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_sampling.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var ct = deadline.Token;
            var root = _output + ".inputs";
            Directory.CreateDirectory(root);
            var body = Path.Combine(root, "body.cs");
            const string text = "class Target {}\r\n// Target target TargetSuffix\r\nvar x = \"Target\";";
            await File.WriteAllTextAsync(body, text, ct);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var cp932 = Path.Combine(root, "日本語.txt");
            await File.WriteAllBytesAsync(cp932, Encoding.GetEncoding(932).GetBytes("// 日本語 Target"), ct);
            var unicode = Path.Combine(root, "unicode.cs");
            await File.WriteAllTextAsync(unicode, "日本語 日本語列 日本語", ct);
            string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var before = new[] { body, cp932, unicode }.ToDictionary(path => path, Hash);
            await _viewModel.OpenFilesAsync([root, body], OpenOrigin.Menu);
            await Frame(ct);
            var original = _editor.Document;
            Require(ReferenceEquals(original, _viewModel.Document) && original.Text == text && _editor.IsReadOnly, "original-readonly-document");
            _editor.Select(8, 0);
            _viewModel.SearchPanel.MatchCase = false;
            _viewModel.SearchPanel.WholeWord = false;
            _viewModel.SearchPanel.UseRegex = true;
            var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F12, KeyModifiers = KeyModifiers.Shift };
            RaiseSearchKey(_editor.TextArea, key);
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct);
            var panel = _viewModel.SearchPanel;
            Require(key.Handled && panel.Query == "Target", "shift-f12-source-identifier");
            Require(panel.MatchCase && panel.WholeWord && !panel.UseRegex, "fixed-reference-options");
            Require(panel.Rows.Count == 4 && panel.Rows.All(panel.CanActivate), "all-text-case-sensitive-word-results");
            var description = _window.FindControl<TextBlock>("SearchDescription")!;
            Require(description.IsEffectivelyVisible && description.Text!.Contains("名前一致") && description.Text.Contains("意味解析は行いません"), "visible-name-match-disclaimer");
            Require(ReferenceEquals(original, _editor.Document) && original.Text == text, "search-preserves-source");
            _editor.Select(6, 6);
            _window.FindControl<MenuItem>("FindReferencesMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await _viewModel.PendingWork.WaitAsync(ct);
            Require(panel.Query == "Target" && panel.Rows.Count == 4, "menu-selection-reference-search");
            var row = panel.Rows.Single(x => x.FullPath == cp932);
            var results = _window.FindControl<ListBox>("SearchResultsList")!;
            results.SelectedItem = row;
            RaiseSearchKey(results, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct); await Frame(ct);
            Require(_viewModel.FilePath == cp932 && _editor.SelectedText == "Target" && _editor.SelectionStart == 7, "result-enter-original-cp932-selection");
            Require(_editor.TextArea.IsFocused, "result-enter-editor-focus");
            var operation = panel.CurrentOperation;
            _editor.Select(0, 3);
            _window.FindControl<MenuItem>("FindReferencesMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await _viewModel.PendingWork.WaitAsync(ct);
            Require(ReferenceEquals(operation, panel.CurrentOperation) && _viewModel.Status.Contains("識別子"), "invalid-selection-keeps-results");
            await _viewModel.OpenFilesAsync([unicode], OpenOrigin.Menu);
            await Frame(ct);
            _editor.Select(0, 3);
            _window.FindControl<MenuItem>("FindReferencesMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await _viewModel.PendingWork.WaitAsync(ct);
            Require(panel.Query == "日本語" && panel.Rows.Count == 3 && panel.Rows.All(panel.CanActivate), "unicode-selection-and-word-boundaries");
            // Two matches in unicode.cs and one in the CP932 comment.
            _viewModel.CloseWorkspace();
            RaiseSearchKey(_editor.TextArea, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F12, KeyModifiers = KeyModifiers.Shift });
            await _viewModel.PendingWork.WaitAsync(ct);
            Require(_viewModel.Status.Contains("フォルダを開いて"), "missing-workspace-guidance");
            Require(before.All(pair => Hash(pair.Key) == pair.Value), "all-input-sha256-unchanged");
            _referenceResults = new { Passed = true, Checks = checks, Scope = "Native routed keys/menu/bindings and original-source selection; not physical keys/IME or performance acceptance." };
            Finish();
        }
        catch (Exception error)
        {
            _referenceResults = new { Passed = false, Checks = checks, Error = error.ToString() };
            Console.Error.WriteLine(error);
            Finish("reference-check-failed: " + error.Message);
        }
    }

    private Task Frame(CancellationToken ct)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _window.RequestAnimationFrame(_ => ready.TrySetResult());
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
}
