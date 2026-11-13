using Avalonia;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
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
            await File.WriteAllTextAsync(unicode, "// 日本語 日本語列 日本語", ct);
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
            var textView = _editor.TextArea.TextView;
            textView.EnsureVisualLines();
            Require(textView.LineTransformers.OfType<SyntaxColorizer>().Count() == 1
                && textView.VisualLines.SelectMany(line => line.Elements)
                    .Select(element => element.TextRunProperties.ForegroundBrush?.ToString()).Distinct().Count() > 1,
                "syntax-colors-visible");
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
            Require(!textView.LineTransformers.OfType<SyntaxColorizer>().Any() && _viewModel.SyntaxStatus.Length == 0,
                "syntax-detaches-for-unsupported-tab");
            Require(_editor.TextArea.IsFocused, "result-enter-editor-focus");
            var operation = panel.CurrentOperation;
            _editor.Select(0, 3);
            _window.FindControl<MenuItem>("FindReferencesMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await _viewModel.PendingWork.WaitAsync(ct);
            Require(ReferenceEquals(operation, panel.CurrentOperation) && _viewModel.Status.Contains("識別子"), "invalid-selection-keeps-results");
            await _viewModel.OpenFilesAsync([unicode], OpenOrigin.Menu);
            await Frame(ct);
            _editor.Select(3, 3);
            _window.FindControl<MenuItem>("FindReferencesMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await _viewModel.PendingWork.WaitAsync(ct);
            Require(panel.Query == "日本語" && panel.Rows.Count == 3 && panel.Rows.All(panel.CanActivate), "unicode-selection-and-word-boundaries");
            // Two matches in unicode.cs and one in the CP932 comment.
            await _viewModel.OpenFilesAsync([body], OpenOrigin.Menu);
            await Frame(ct);
            _editor.Select(8, 0);
            var definitionKey = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F12 };
            RaiseSearchKey(_editor.TextArea, definitionKey);
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct); await Frame(ct);
            Require(definitionKey.Handled && panel.Description.Contains("簡易") && panel.Rows.Count == 1
                && _editor.SelectedText == "Target" && _editor.SelectionStart == 6, "f12-single-definition-auto-selection");

            var duplicate = Path.Combine(root, "duplicate.cs");
            await File.WriteAllTextAsync(duplicate, "namespace Other { class @Target {} }", ct);
            before.Add(duplicate, Hash(duplicate));
            _window.FindControl<MenuItem>("FindDefinitionsMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await _viewModel.PendingWork.WaitAsync(ct);
            Require(panel.Rows.Count == 2 && _viewModel.FilePath == body, "definition-menu-multiple-candidates");
            results.SelectedItem = panel.Rows.Single(x => x.FullPath == duplicate);
            RaiseSearchKey(results, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct); await Frame(ct);
            Require(_viewModel.FilePath == duplicate && _editor.SelectedText == "@Target"
                && _editor.TextArea.IsFocused, "definition-enter-original-selection");
            var findKey = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F,
                KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control };
            RaiseSearchKey(_editor.TextArea, findKey);
            await Frame(ct);
            var query = _window.FindControl<TextBox>("InFileQuery")!;
            if (!findKey.Handled || !query.IsFocused || !query.IsEffectivelyVisible)
                Console.Error.WriteLine($"In-file shortcut handled={findKey.Handled} open={_viewModel.InFileSearch.CanOpen} " +
                    $"shown={_viewModel.InFileSearch.IsVisible} visible={query.IsEffectivelyVisible} focused={query.IsFocused}");
            Require(findKey.Handled && query.IsFocused && query.IsEffectivelyVisible, "in-file-shortcut-focus");
            var client = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            query.RaiseEvent(client);
            Require(client.Client?.SupportsPreedit == true, "in-file-ime-client");
            client.Client!.SetPreeditText("変換中");
            RaiseSearchKey(query, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Frame(ct);
            Require(_viewModel.InFileSearch.IsVisible, "in-file-preedit-escape-keeps-panel");
            Require(string.IsNullOrEmpty(_viewModel.InFileSearch.Query), "in-file-preedit-does-not-search");
            client.Client.SetPreeditText(null);
            query.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "Target" });
            await _viewModel.PendingWork.WaitAsync(ct);
            Require(_viewModel.InFileSearch.Query == "Target" && _viewModel.InFileSearch.Matches.Count == 1,
                "in-file-committed-input-matches");
            RaiseSearchKey(query, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct);
            Require(_editor.SelectedText == "Target" && _viewModel.InFileSearch.Summary.Contains("折り返し"),
                "in-file-enter-selects-wrapped-match");
            RaiseSearchKey(query, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Require(!_viewModel.InFileSearch.IsVisible && _editor.TextArea.IsFocused, "in-file-escape-restores-editor");
            RaiseSearchKey(_editor.TextArea, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F,
                KeyModifiers = (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) | KeyModifiers.Shift });
            await Frame(ct);
            var folderQuery = _window.FindControl<TextBox>("SearchQuery")!;
            Require(folderQuery.IsFocused, "folder-shortcut-focus");
            folderQuery.SelectAll();
            var folderClient = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            folderQuery.RaiseEvent(folderClient);
            Require(folderClient.Client?.SupportsPreedit == true, "folder-ime-client");
            var originalOperation = panel.CurrentOperation;
            folderClient.Client!.SetPreeditText("変換中");
            RaiseSearchKey(folderQuery, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Require(ReferenceEquals(originalOperation, panel.CurrentOperation), "folder-preedit-enter-does-not-search");
            folderClient.Client.SetPreeditText(null);
            folderQuery.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "日本語" });
            var applied = _window.FindControl<TextBlock>("AppliedSearchConditions")!;
            var pending = _window.FindControl<TextBlock>("UnappliedSearchConditions")!;
            await Frame(ct);
            Require(pending.IsEffectivelyVisible && applied.Text?.Contains("「Target」") == true
                && ReferenceEquals(originalOperation, panel.CurrentOperation), "folder-edited-input-shows-applied-conditions");
            RaiseSearchKey(folderQuery, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Require(panel.IsRunning && panel.Query == "日本語", "folder-enter-starts-search");
            RaiseSearchKey(folderQuery, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await _viewModel.PendingWork.WaitAsync(ct);
            Require(panel.IsVisible && panel.Summary.Contains("取り消し"), "folder-escape-cancels-with-panel-visible");
            RaiseSearchKey(folderQuery, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await _viewModel.PendingWork.WaitAsync(ct);
            Require(panel.Rows.Count == 3 && panel.Rows.All(panel.CanActivate), "folder-enter-retries-committed-query");
            await Frame(ct);
            Require(!pending.IsEffectivelyVisible && applied.Text?.Contains("「日本語」") == true,
                "folder-run-updates-applied-conditions");
            var resultsPane = _window.FindControl<Border>("SearchResultsPane")!;
            var resultsSplitter = _window.FindControl<GridSplitter>("SearchResultsSplitter")!;
            var initialHeight = resultsPane.Bounds.Height;
            DragSplitter(resultsSplitter, new Vector(0, 24));
            await Frame(ct);
            var resizedHeight = resultsPane.Bounds.Height;
            Require(resizedHeight < initialHeight - 8 && panel.Rows.Count == 3, "folder-drag-resizes-results");
            panel.IsVisible = false;
            await Frame(ct);
            Require(!resultsSplitter.IsEffectivelyVisible && panel.Rows.Count == 3
                && _window.FindControl<Grid>("MainLayout")!.RowDefinitions[3].ActualHeight == 0, "folder-hide-releases-row");
            panel.IsVisible = true;
            await Frame(ct);
            Require(Math.Abs(resultsPane.Bounds.Height - resizedHeight) < 1, "folder-show-restores-height");

            var treePane = _window.FindControl<Border>("FileTreePane")!;
            var treeSplitter = _window.FindControl<GridSplitter>("FileTreeSplitter")!;
            var initialWidth = treePane.Bounds.Width;
            DragSplitter(treeSplitter, new Vector(24, 0));
            await Frame(ct);
            var resizedWidth = treePane.Bounds.Width;
            Require(resizedWidth > initialWidth + 8, "tree-drag-resizes-width");
            _viewModel.IsTreeVisible = false;
            await Frame(ct);
            Require(!treeSplitter.IsEffectivelyVisible
                && _window.FindControl<Grid>("DocumentLayout")!.ColumnDefinitions[0].ActualWidth == 0, "tree-hide-releases-column");
            _viewModel.IsTreeVisible = true;
            await Frame(ct);
            Require(Math.Abs(treePane.Bounds.Width - resizedWidth) < 1, "tree-show-restores-width");
            _viewModel.CloseWorkspace();
            RaiseSearchKey(_editor.TextArea, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F12, KeyModifiers = KeyModifiers.Shift });
            await _viewModel.PendingWork.WaitAsync(ct);
            Require(_viewModel.Status.Contains("フォルダを開いて"), "missing-workspace-guidance");
            var longLine = Path.Combine(root, "long-line.cs");
            var longText = "class Target {}\n" + new string(' ', 8193);
            await File.WriteAllTextAsync(longLine, longText, ct);
            before.Add(longLine, Hash(longLine));
            await _viewModel.OpenFilesAsync([longLine], OpenOrigin.Menu);
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct);
            Require(_viewModel.SyntaxStatus.Contains("長行") && _editor.Document.Text == longText
                && !textView.LineTransformers.OfType<SyntaxColorizer>().Any(), "syntax-long-line-stays-plain");
            await _viewModel.OpenFilesAsync([body], OpenOrigin.Menu);
            while (_viewModel.HasTabs) _viewModel.CloseFile();
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct);
            Require(!_viewModel.HasDocument && !textView.LineTransformers.OfType<SyntaxColorizer>().Any(),
                "syntax-closed-tab-result-discarded");
            await CheckReloadAsync(root, before, Hash, Require, ct);
            await CheckTreeAsync(root, before, Hash, Require, ct);
            await CheckTabsAsync(root, before, Hash, Require, ct);
            await CheckTabViewStateAsync(root, before, Hash, Require, ct);
            await CheckWorkspaceSwitchAsync(root, before, Hash, Require, ct);
            await CheckTreeLinksAsync(root, before, Hash, Require, ct);
            await CheckFileDropAsync(root, before, Hash, Require, ct);
            await CheckSingleFileDisplayAsync(root, before, Hash, Require, ct);
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

    private async Task CheckReloadAsync(string root, Dictionary<string, string> inputs, Func<string, string> hash,
        Action<bool, string> require, CancellationToken ct)
    {
        var path = Path.Combine(root, "reload.txt");
        const string initial = "最初の本文";
        const string replacement = "日本語の本文";
        var cp932 = Encoding.GetEncoding(932);
        await File.WriteAllTextAsync(path, initial, ct);
        await _viewModel.OpenFilesAsync([path], OpenOrigin.Menu);
        await Frame(ct);
        var first = _editor.Document;
        var tab = _viewModel.SelectedTab!.State;
        var version = tab.DocumentVersion;
        var epoch = tab.LoadEpoch;
        var reload = _window.FindControl<MenuItem>("ReloadMenu")!;

        async Task ClickAsync(MenuItem menu)
        {
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct);
        }

        await File.WriteAllBytesAsync(path, cp932.GetBytes(replacement), ct);
        require(first.Text == initial && ReferenceEquals(_editor.Document, first), "reload-external-update-keeps-open-snapshot");
        await ClickAsync(_window.FindControl<MenuItem>("ReopenUtf8Menu")!);
        require(ReferenceEquals(_editor.Document, first) && tab.DocumentVersion == version && tab.LoadEpoch > epoch
            && tab.Error is not null && _viewModel.EncodingName == "UTF-8", "reload-wrong-encoding-keeps-document");
        epoch = tab.LoadEpoch;
        await ClickAsync(_window.FindControl<MenuItem>("ReopenCp932Menu")!);
        var second = _editor.Document;
        require(!ReferenceEquals(second, first) && second.Text == replacement && tab.DocumentVersion > version
            && tab.LoadEpoch > epoch && tab.Error is null && _viewModel.EncodingName.Contains("CP932"),
            "reload-cp932-replaces-document");
        version = tab.DocumentVersion;
        epoch = tab.LoadEpoch;
        _editor.TextArea.Caret.Offset = replacement.Length;
        await Frame(ct);

        File.Delete(path);
        await ClickAsync(reload);
        require(ReferenceEquals(_editor.Document, second) && tab.DocumentVersion == version && tab.LoadEpoch > epoch
            && tab.Error?.Code == CodeView.Contracts.ErrorCode.NotFound
            && _editor.TextArea.Caret.Offset == replacement.Length, "reload-deleted-file-keeps-document-and-caret");

        const string shorter = "短";
        epoch = tab.LoadEpoch;
        await File.WriteAllBytesAsync(path, cp932.GetBytes(shorter), ct);
        inputs.Add(path, hash(path));
        await ClickAsync(reload);
        require(_editor.Document.Text == shorter && _editor.TextArea.Caret.Offset == shorter.Length
            && _viewModel.Position == "行 1, 列 2" && tab.DocumentVersion > version && tab.LoadEpoch > epoch
            && tab.Error is null && _viewModel.EncodingName.Contains("CP932"), "reload-shorter-document-clamps-caret");
    }

    private async Task CheckTreeAsync(string root, Dictionary<string, string> inputs, Func<string, string> hash,
        Action<bool, string> require, CancellationToken ct)
    {
        var workspace = Path.Combine(root, "tree");
        var branch = Directory.CreateDirectory(Path.Combine(workspace, "branch"));
        Directory.CreateDirectory(Path.Combine(workspace, "empty"));
        Directory.CreateDirectory(Path.Combine(workspace, "node_modules"));
        var path = Path.Combine(branch.FullName, "nested.txt");
        await File.WriteAllTextAsync(path, "tree body", ct);
        inputs.Add(path, hash(path));
        var existing = _editor.Document;
        await _viewModel.OpenFilesAsync([workspace], OpenOrigin.Menu);
        await Frame(ct);
        var tree = _window.FindControl<ListBox>("FileTreeList")!;
        var rows = _viewModel.FileTree.Rows;
        require(rows.Select(row => row.Name).SequenceEqual(["branch", "empty"]) && ReferenceEquals(existing, _editor.Document),
            "tree-root-direct-filtered-and-tab-preserved");
        var folder = rows[0];
        tree.SelectedItem = folder;
        tree.Focus();

        async Task PressAsync(Key key)
        {
            RaiseSearchKey(tree, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct);
        }

        await PressAsync(Key.Right);
        var file = rows.Single(row => row.Name == "nested.txt");
        require(ReferenceEquals(file.Parent, folder), "tree-right-expands-selected-folder");
        tree.SelectedItem = file;
        await PressAsync(Key.Left);
        require(ReferenceEquals(tree.SelectedItem, folder), "tree-left-selects-parent");
        await PressAsync(Key.Left);
        require(rows.Count == 2 && !folder.Node.IsExpanded, "tree-left-collapses-folder");

        var empty = rows[1];
        var container = (ListBoxItem)tree.ContainerFromItem(empty)!;
        var disclosure = container.GetVisualDescendants().OfType<Button>().Single();
        var emptyTap = await DoubleTapAsync(tree, ct);
        var disclosureTap = await DoubleTapAsync(disclosure, ct);
        require(!emptyTap.Handled && !disclosureTap.Handled && rows.Count == 2
            && !folder.Node.IsExpanded && !empty.Node.IsExpanded, "tree-background-and-disclosure-double-tap-ignored");

        await DoubleTapAsync(container, ct);
        require(ReferenceEquals(tree.SelectedItem, empty) && empty.Node.IsExpanded && empty.Detail == "空"
            && !folder.Node.IsExpanded, "tree-double-tap-uses-hit-row-and-shows-empty");
        tree.SelectedItem = folder;
        await PressAsync(Key.Right);
        tree.SelectedItem = rows.Single(row => row.Name == "nested.txt");
        await PressAsync(Key.Enter);
        require(_viewModel.FilePath == path && _editor.Document.Text == "tree body", "tree-enter-opens-source");
        require(_editor.TextArea.IsFocused, "tree-enter-focuses-editor");

        const int extraFiles = 300;
        for (var number = 0; number < extraFiles; number++)
            await File.WriteAllTextAsync(Path.Combine(branch.FullName, $"item-{number:D3}.txt"), "", ct);
        tree.SelectedItem = folder;
        tree.Focus();
        await PressAsync(Key.F5);
        require(rows.Count == extraFiles + 3 && rows[1].Name == "item-000.txt"
            && _editor.Document.Text == "tree body", "tree-f5-refreshes-branch-and-keeps-document");
        var lastFile = rows[^2];
        var realized = tree.GetVisualDescendants().OfType<ListBoxItem>().Count();
        require(realized > 0 && realized < rows.Count && tree.ContainerFromItem(lastFile) is null,
            "tree-virtualizes-offscreen-items");
        tree.ScrollIntoView(lastFile);
        await Frame(ct);
        await Frame(ct);
        require(tree.ContainerFromItem(lastFile) is { IsVisible: true }, "tree-scroll-realizes-offscreen-file");
    }

    private async Task CheckTabsAsync(string root, Dictionary<string, string> inputs, Func<string, string> hash,
        Action<bool, string> require, CancellationToken ct)
    {
        while (_viewModel.HasTabs) _viewModel.CloseFile();
        var firstPath = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "first")).FullName, "shared.txt");
        var secondPath = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "second")).FullName, "shared.txt");
        await File.WriteAllTextAsync(firstPath, "first body", ct);
        await File.WriteAllTextAsync(secondPath, "second body", ct);
        inputs.Add(firstPath, hash(firstPath));
        inputs.Add(secondPath, hash(secondPath));
        await _viewModel.OpenFilesAsync([firstPath, secondPath], OpenOrigin.Menu);
        await Frame(ct);
        var list = _window.FindControl<ListBox>("TabList")!;
        var first = _viewModel.Tabs[0];
        var second = _viewModel.Tabs[1];
        require(list.IsEffectivelyVisible && first.Label.Contains("first") && second.Label.Contains("second")
            && _viewModel.Tabs.Count == 2 && _editor.Document.Text == "second body", "tabs-same-name-distinct-labels");
        var secondDocument = _editor.Document;

        list.SelectedItem = first;
        await Frame(ct);
        require(_viewModel.FilePath == firstPath && _editor.Document.Text == "first body", "tabs-selection-attaches-document");
        await _viewModel.OpenFilesAsync([secondPath], OpenOrigin.Menu);
        await Frame(ct);
        require(_viewModel.Tabs.Count == 2 && ReferenceEquals(_editor.Document, secondDocument), "tabs-repeat-open-reuses-document");

        async Task<KeyEventArgs> PressAsync(Key key, KeyModifiers modifiers)
        {
            var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
            RaiseSearchKey(_editor.TextArea, args);
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct);
            return args;
        }

        var next = await PressAsync(Key.Tab, KeyModifiers.Control);
        require(next.Handled && ReferenceEquals(list.SelectedItem, first) && _editor.TextArea.IsFocused,
            "tabs-ctrl-tab-wraps-and-focuses-editor");
        var previous = await PressAsync(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift);
        require(previous.Handled && ReferenceEquals(list.SelectedItem, second)
            && ReferenceEquals(_editor.Document, secondDocument), "tabs-ctrl-shift-tab-wraps-back");

        var container = (ListBoxItem)list.ContainerFromItem(first)!;
        container.GetVisualDescendants().OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Frame(ct);
        require(_viewModel.Tabs.Count == 1 && ReferenceEquals(list.SelectedItem, second)
            && ReferenceEquals(_editor.Document, secondDocument), "tabs-close-button-keeps-active-document");
        var closeModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        await PressAsync(Key.W, closeModifier);
        require(!_viewModel.HasTabs && !_viewModel.HasDocument && !list.IsEffectivelyVisible,
            "tabs-close-shortcut-clears-last-tab");
    }

    private async Task CheckTabViewStateAsync(string root, Dictionary<string, string> inputs, Func<string, string> hash,
        Action<bool, string> require, CancellationToken ct)
    {
        var firstPath = Path.Combine(root, "view-first.txt");
        var secondPath = Path.Combine(root, "view-second.txt");
        static string Body(string word) => word + "\n" + string.Concat(Enumerable.Repeat(new string('a', 600) + "\n", 160));
        await File.WriteAllTextAsync(firstPath, Body("FirstToken"), ct);
        await File.WriteAllTextAsync(secondPath, Body("SecondToken"), ct);
        inputs.Add(firstPath, hash(firstPath));
        inputs.Add(secondPath, hash(secondPath));
        await _viewModel.OpenFilesAsync([firstPath, secondPath], OpenOrigin.Menu);
        await Frame(ct);
        var tabs = _window.FindControl<ListBox>("TabList")!;
        var search = _viewModel.InFileSearch;
        var query = _window.FindControl<TextBox>("InFileQuery")!;
        var scrollable = (IScrollable)_editor.TextArea.TextView;
        var first = _viewModel.Tabs[0];
        var second = _viewModel.Tabs[1];

        async Task SelectAsync(TabViewModel tab)
        {
            tabs.SelectedItem = tab;
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct);
            await Frame(ct);
        }

        async Task<Vector> SetAsync(TabViewModel tab, string word, bool wholeWord, int line, int column, Vector offset)
        {
            await SelectAsync(tab);
            search.IsVisible = true;
            search.WholeWord = wholeWord;
            query.Text = word;
            await _viewModel.PendingWork.WaitAsync(ct);
            _editor.TextArea.Caret.Line = line;
            _editor.TextArea.Caret.Column = column;
            await Frame(ct);
            scrollable.Offset = offset;
            await Frame(ct);
            return scrollable.Offset;
        }

        var firstOffset = await SetAsync(first, "FirstToken", true, 55, 120, new(560, 720));
        var secondOffset = await SetAsync(second, "SecondToken", false, 91, 200, new(920, 1220));
        require(firstOffset.X > 0 && firstOffset.Y > 0 && secondOffset.X > firstOffset.X
            && secondOffset.Y > firstOffset.Y, "tab-view-distinct-scroll-offsets");

        static bool SameOffset(Vector left, Vector right) => Math.Abs(left.X - right.X) <= 1
            && Math.Abs(left.Y - right.Y) <= 1;
        await SelectAsync(first);
        require(_editor.TextArea.Caret.Line == 55 && _editor.TextArea.Caret.Column == 120
            && SameOffset(scrollable.Offset, firstOffset), "tab-view-restores-first-caret-and-scroll");
        require(query.Text == "FirstToken" && search.WholeWord && search.Matches.Count == 1,
            "tab-view-restores-first-search-conditions");

        await SelectAsync(second);
        require(_editor.TextArea.Caret.Line == 91 && _editor.TextArea.Caret.Column == 200
            && SameOffset(scrollable.Offset, secondOffset), "tab-view-restores-second-caret-and-scroll");
        require(query.Text == "SecondToken" && !search.WholeWord && search.Matches.Count == 1,
            "tab-view-restores-second-search-conditions");
        var handle = second.State.File!.Document;
        _viewModel.CloseFile();
        await _viewModel.PendingWork.WaitAsync(ct);
        await Frame(ct);
        require(handle.IsReleased && handle.ActiveReadLeases == 0 && query.Text == "FirstToken"
            && search.Matches.Count == 1, "tab-view-close-releases-source-and-restores-search");
    }

    private async Task CheckWorkspaceSwitchAsync(string root, Dictionary<string, string> inputs, Func<string, string> hash,
        Action<bool, string> require, CancellationToken ct)
    {
        var oldRoot = Directory.CreateDirectory(Path.Combine(root, "workspace-old")).FullName;
        var newRoot = Directory.CreateDirectory(Path.Combine(root, "workspace-new")).FullName;
        var oldPath = Path.Combine(oldRoot, "old.txt");
        var newPath = Path.Combine(newRoot, "new.txt");
        var branch = Directory.CreateDirectory(Path.Combine(oldRoot, "branch"));
        await File.WriteAllTextAsync(oldPath, "WorkspaceMarker", ct);
        await File.WriteAllTextAsync(newPath, "WorkspaceMarker", ct);
        inputs.Add(oldPath, hash(oldPath));
        inputs.Add(newPath, hash(newPath));
        var tabs = _viewModel.Tabs.ToArray();
        var document = _editor.Document;
        var panel = _viewModel.SearchPanel;

        await _viewModel.OpenFilesAsync([oldRoot], OpenOrigin.Menu);
        var oldBranch = _viewModel.FileTree.Rows.Single(row => row.Node.Entry.Path.FullPath == branch.FullName);
        panel.Query = "WorkspaceMarker";
        panel.UseRegex = false;
        await panel.StartAsync().WaitAsync(ct);
        var oldResult = panel.Rows.Single();
        require(_viewModel.WorkspacePath == oldRoot && oldResult.FullPath == oldPath && panel.CanActivate(oldResult),
            "workspace-first-root-search-results");

        await _viewModel.OpenFilesAsync([newRoot], OpenOrigin.Menu);
        await Frame(ct);
        require(_viewModel.WorkspacePath == newRoot && _viewModel.FileTree.Rows.Single().Path == newPath
            && !_viewModel.FileTree.IsCurrentRow(oldBranch), "workspace-switch-replaces-tree");
        require(panel.Rows.Count == 0 && !panel.CanActivate(oldResult) && _viewModel.CanSearchWorkspace,
            "workspace-switch-invalidates-previous-results");
        await _viewModel.ExpandTreeRowAsync(oldBranch);
        await _viewModel.ActivateSearchResultAsync(oldResult);
        require(_viewModel.Tabs.SequenceEqual(tabs) && ReferenceEquals(document, _editor.Document)
            && _viewModel.FileTree.Rows.Count == 1, "workspace-old-row-cannot-replace-current-view");
        await panel.StartAsync().WaitAsync(ct);
        var currentResult = panel.Rows.Single();
        require(currentResult.FullPath == newPath && panel.CanActivate(currentResult), "workspace-search-uses-new-root");

        var resultList = _window.FindControl<ListBox>("SearchResultsList")!;
        await Frame(ct);
        resultList.SelectedItem = currentResult;
        var backgroundTap = await DoubleTapAsync(resultList, ct);
        require(!backgroundTap.Handled && ReferenceEquals(document, _editor.Document),
            "search-double-tap-background-does-not-open-selection");
        var resultContainer = (ListBoxItem)resultList.ContainerFromItem(currentResult)!;
        var snippet = resultContainer.GetVisualDescendants().OfType<TextBlock>().Last();
        var resultTap = await DoubleTapAsync(snippet, ct);
        require(resultTap.Handled && _viewModel.FilePath == newPath && _editor.SelectedText == "WorkspaceMarker"
            && _editor.TextArea.IsFocused, "search-double-tap-opens-hit-and-selects-text");

        await File.WriteAllTextAsync(newPath, "External edit", ct);
        inputs[newPath] = hash(newPath);
        await DoubleTapAsync(snippet, ct);
        require(_viewModel.FilePath == newPath && _editor.SelectionLength == 0
            && _viewModel.Status.Contains("再検索") && _editor.Document.Text == "WorkspaceMarker",
            "search-double-tap-stale-hit-keeps-document-without-selection");
        _viewModel.CloseFile();
        await Frame(ct);

        _viewModel.CloseWorkspace();
        await Frame(ct);
        require(!_viewModel.HasWorkspace && !_viewModel.CanSearchWorkspace && !panel.CanStart
            && panel.Rows.Count == 0 && !_viewModel.FileTree.HasRows && _viewModel.Tabs.SequenceEqual(tabs)
            && ReferenceEquals(document, _editor.Document), "workspace-close-preserves-open-tabs");
    }

    private async Task CheckTreeLinksAsync(string root, Dictionary<string, string> inputs, Func<string, string> hash,
        Action<bool, string> require, CancellationToken ct)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(root, "link-workspace")).FullName;
        var external = Directory.CreateDirectory(Path.Combine(root, "link-target")).FullName;
        var target = Path.Combine(external, "source.txt");
        var fileLink = Path.Combine(workspace, "file-link.txt");
        var folderLink = Path.Combine(workspace, "folder-link");
        var regular = Path.Combine(workspace, "regular.txt");
        const string body = "LinkedOnlyMarker";
        await File.WriteAllTextAsync(target, body, ct);
        await File.WriteAllTextAsync(regular, "plain text", ct);
        File.CreateSymbolicLink(fileLink, target);
        Directory.CreateSymbolicLink(folderLink, external);
        Directory.CreateSymbolicLink(Path.Combine(external, "cycle"), workspace);
        inputs.Add(target, hash(target));
        inputs.Add(regular, hash(regular));

        await _viewModel.OpenFilesAsync([workspace], OpenOrigin.Menu);
        await Frame(ct);
        var rows = _viewModel.FileTree.Rows;
        require(rows.Select(row => row.Name).SequenceEqual(["folder-link", "file-link.txt", "regular.txt"])
            && rows[0].Detail == "リンク" && rows[1].Detail == "リンク", "tree-links-visible-in-name-order");
        var tree = _window.FindControl<ListBox>("FileTreeList")!;
        var existing = _editor.Document;

        async Task OpenSelectedAsync(FileTreeRow row)
        {
            tree.SelectedItem = row;
            RaiseSearchKey(tree, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct);
        }

        await OpenSelectedAsync(rows[0]);
        require(rows.Count == 3 && !rows[0].CanExpand && _viewModel.WorkspacePath == workspace
            && ReferenceEquals(_editor.Document, existing), "tree-folder-link-does-not-expand");
        var panel = _viewModel.SearchPanel;
        panel.Query = body;
        await panel.StartAsync().WaitAsync(ct);
        require(panel.Rows.Count == 0 && !panel.IsRunning, "tree-links-not-followed-by-workspace-search");

        await OpenSelectedAsync(rows[1]);
        require(_viewModel.FilePath == fileLink && _editor.Document.Text == body && _editor.TextArea.IsFocused,
            "tree-file-link-explicitly-opens");
        var opened = _editor.Document;
        await _viewModel.OpenFilesAsync([folderLink], OpenOrigin.Menu);
        await Frame(ct);
        require(_viewModel.WorkspacePath == folderLink && rows.Select(row => row.Name).SequenceEqual(["cycle", "source.txt"])
            && !rows[0].CanExpand && ReferenceEquals(_editor.Document, opened), "tree-folder-link-explicit-root-and-cycle-blocked");
        await panel.StartAsync().WaitAsync(ct);
        require(panel.Rows.Single().FullPath == target, "tree-search-resolves-explicit-root");
    }

    private async Task CheckFileDropAsync(string root, Dictionary<string, string> inputs, Func<string, string> hash,
        Action<bool, string> require, CancellationToken ct)
    {
        var firstRoot = Directory.CreateDirectory(Path.Combine(root, "drop-first")).FullName;
        var secondRoot = Directory.CreateDirectory(Path.Combine(root, "drop-second")).FullName;
        var path = Path.Combine(firstRoot, "日本語 空白 #.txt");
        const string content = "first line\r\n本文";
        await File.WriteAllTextAsync(path, content, ct);
        inputs.Add(path, hash(path));
        var storage = _window.StorageProvider;
        using var file = await storage.TryGetFileFromPathAsync(new Uri(path)) ?? throw new IOException(path);
        using var first = await storage.TryGetFolderFromPathAsync(new Uri(firstRoot)) ?? throw new IOException(firstRoot);
        using var second = await storage.TryGetFolderFromPathAsync(new Uri(secondRoot)) ?? throw new IOException(secondRoot);

        async Task<DragEventArgs> DropAsync(DragDropEffects effects, params DataTransferItem[] items)
        {
            using var data = new DataTransfer();
            foreach (var item in items) data.Add(item);
            var area = _editor.TextArea;
            var args = new DragEventArgs(DragDrop.DropEvent, data, area, new Point(12, 12), KeyModifiers.None)
            { DragEffects = effects };
            area.RaiseEvent(args);
            await _viewModel.PendingWork.WaitAsync(ct);
            await Frame(ct);
            return args;
        }

        var existing = _editor.Document;
        var workspace = _viewModel.WorkspacePath;
        var move = await DropAsync(DragDropEffects.Move, DataTransferItem.CreateFile(file));
        require(move.Handled && move.DragEffects == DragDropEffects.None && ReferenceEquals(existing, _editor.Document),
            "drop-move-only-rejected");
        var mixed = await DropAsync(DragDropEffects.Copy, DataTransferItem.CreateFile(first), DataTransferItem.CreateText("invalid"));
        require(mixed.Handled && mixed.DragEffects == DragDropEffects.None && _viewModel.WorkspacePath == workspace
            && ReferenceEquals(existing, _editor.Document), "drop-mixed-invalid-rejected-atomically");

        var accepted = await DropAsync(DragDropEffects.Copy | DragDropEffects.Move,
            DataTransferItem.CreateFile(first), DataTransferItem.CreateFile(file), DataTransferItem.CreateFile(second));
        require(accepted.Handled && accepted.DragEffects == DragDropEffects.Copy && _viewModel.WorkspacePath == firstRoot
            && _viewModel.FilePath == path && _editor.Document.Text == content && _editor.IsReadOnly
            && _viewModel.Status.Contains("後続のフォルダ1件"), "drop-file-and-first-folder-open-read-only");
    }

    private async Task CheckSingleFileDisplayAsync(string root, Dictionary<string, string> inputs, Func<string, string> hash,
        Action<bool, string> require, CancellationToken ct)
    {
        while (_viewModel.HasTabs) _viewModel.CloseFile();
        _viewModel.CloseWorkspace();
        var empty = Path.Combine(root, "empty.txt");
        var binary = Path.Combine(root, "binary.dat");
        var text = Path.Combine(root, "small.txt");
        const string content = "先頭\r\n最終行";
        await File.WriteAllTextAsync(empty, "", ct);
        await File.WriteAllBytesAsync(binary, [0, 1, 2, 0, 3], ct);
        await File.WriteAllTextAsync(text, content, ct);
        foreach (var path in new[] { empty, binary, text }) inputs.Add(path, hash(path));
        var status = _window.FindControl<TextBlock>("DocumentStatus")!;
        var sawLoading = false;
        void ObserveLoading(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainWindowViewModel.Status)
                && _viewModel.SelectedTab?.State.Status == TabStatus.Loading
                && status.Text?.StartsWith("読み込み中…", StringComparison.Ordinal) == true)
                sawLoading = true;
        }

        _viewModel.PropertyChanged += ObserveLoading;
        try { await _viewModel.OpenFilesAsync([empty], OpenOrigin.Menu); }
        finally { _viewModel.PropertyChanged -= ObserveLoading; }
        await Frame(ct);
        require(sawLoading, "single-file-loading-status-is-bound");
        require(_viewModel.HasDocument && _editor.Document.TextLength == 0 && _editor.IsReadOnly
            && status.Text == "読み取り専用", "single-file-empty-is-readable");

        await _viewModel.OpenFilesAsync([binary], OpenOrigin.Menu);
        await Frame(ct);
        require(!_viewModel.HasDocument && _viewModel.SelectedTab?.State.Status == TabStatus.Failed
            && status.Text?.Contains("バイナリのため表示できません") == true, "single-file-binary-is-rejected-with-status");

        await _viewModel.OpenFilesAsync([text], OpenOrigin.Menu);
        await Frame(ct);
        require(!_viewModel.HasWorkspace && _viewModel.FilePath == text && _editor.Document.Text == content
            && _editor.IsReadOnly && status.Text == "読み取り専用", "single-file-valid-open-recovers-after-failure");
    }

    private async Task<TappedEventArgs> DoubleTapAsync(Control source, CancellationToken ct)
    {
        using var pointer = new Pointer(1, PointerType.Mouse, true);
        var args = new TappedEventArgs(InputElement.DoubleTappedEvent,
            new PointerEventArgs(null, source, pointer, _window, default, 0, default!, KeyModifiers.None));
        source.RaiseEvent(args);
        await _viewModel.PendingWork.WaitAsync(ct);
        await Frame(ct);
        return args;
    }

    private static void DragSplitter(GridSplitter splitter, Vector delta)
    {
        splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragStartedEvent });
        splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = delta });
        splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = delta });
    }

    private Task Frame(CancellationToken ct)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _window.RequestAnimationFrame(_ => ready.TrySetResult());
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
}
