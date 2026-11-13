using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Operations;

namespace CodeView.Presentation;

internal sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _allowClose;
    private bool _closing;
    private ClipboardCopyCoordinator? _copies;

    public MainWindow(MainWindowViewModel viewModel, string[] args)
    {
#if CODEVIEW_PERFORMANCE_PROBE
        GuiPerformanceProbe.Checkpoint("window-constructor-start");
#endif
        _viewModel = viewModel;
        AvaloniaXamlLoader.Load(this);
#if CODEVIEW_PERFORMANCE_PROBE
        GuiPerformanceProbe.Checkpoint("window-xaml-loaded");
#endif
        DataContext = viewModel;
        ConfigureResizablePanels(viewModel);
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        this.FindControl<MenuItem>("OpenMenu")!.InputGesture = new KeyGesture(Key.O, modifier);
        this.FindControl<MenuItem>("CloseMenu")!.InputGesture = new KeyGesture(Key.W, modifier);
        this.FindControl<MenuItem>("FindDefinitionsMenu")!.InputGesture = new KeyGesture(Key.F12);
        this.FindControl<MenuItem>("FindReferencesMenu")!.InputGesture = new KeyGesture(Key.F12, KeyModifiers.Shift);
        this.FindControl<MenuItem>("InFileSearchMenu")!.InputGesture = new KeyGesture(Key.F, modifier);
        this.FindControl<MenuItem>("WorkspaceSearchMenu")!.InputGesture = new KeyGesture(Key.F, modifier | KeyModifiers.Shift);
        this.FindControl<ListBox>("SearchResultsList")!.AddHandler(KeyDownEvent, SearchResultKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        // The list's directional navigation may consume arrows before they bubble.
        this.FindControl<ListBox>("FileTreeList")!.AddHandler(KeyDownEvent, TreeKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, _) => viewModel.RecordUserInteraction(), RoutingStrategies.Tunnel, handledEventsToo: true);
        // Take the shortcut before AvaloniaEdit's built-in search handler receives it.
        AddHandler(KeyDownEvent, (_, key) =>
        {
            if (key.Key != Key.F || key.KeyModifiers != modifier) return;
            key.Handled = true;
            Run(ShowInFileSearch);
        }, RoutingStrategies.Tunnel);
        // Ctrl+Tab belongs to the open files, even when the focused control handles Tab.
        AddHandler(KeyDownEvent, (_, key) =>
        {
            if (key.Key != Key.Tab || key.KeyModifiers is not (KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift))
                || !viewModel.HasTabs) return;
            key.Handled = true;
            Run(() =>
            {
                viewModel.CycleTab(key.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                if (viewModel.HasDocument) this.FindControl<TextEditor>("Editor")!.TextArea.Focus();
            });
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => viewModel.RecordUserInteraction(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, (_, _) => viewModel.RecordUserInteraction(), RoutingStrategies.Tunnel, handledEventsToo: true);
        KeyDown += async (_, key) =>
        {
            if (key.Key == Key.F12 && key.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            {
                key.Handled = true;
                await RunAsync(() => FindSymbolAsync(definitions: key.KeyModifiers == KeyModifiers.None));
                return;
            }
            if (key.Key is Key.Enter or Key.Escape && await HandleSearchKeyAsync(key)) return;
            if (key.Key == Key.F && key.KeyModifiers == (modifier | KeyModifiers.Shift))
            { key.Handled = true; Run(ShowSearch); return; }
            if (key.KeyModifiers != modifier) return;
            if (key.Key == Key.O)
            {
                key.Handled = true;
                await RunAsync(OpenFilesAsync);
            }
            else if (key.Key == Key.W)
            {
                key.Handled = true;
                Run(viewModel.CloseFile);
            }
        };
        var editor = this.FindControl<TextEditor>("Editor")!;
        AddHandler(KeyDownEvent, async (_, key) =>
        {
            if (_closing || key.Key != Key.C || key.KeyModifiers != modifier || !editor.TextArea.IsKeyboardFocusWithin
                || editor.TextArea.Selection is AvaloniaEdit.Editing.RectangleSelection) return;
#if CODEVIEW_PERFORMANCE_PROBE
            if (GuiPerformanceProbe.HasInteractiveDocument) return;
#endif
            key.Handled = true;
            await RunAsync(() => CopySourceAsync(editor));
        }, RoutingStrategies.Tunnel);
        viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(MainWindowViewModel.Document)) _copies?.Invalidate();
        };
        SyntaxColorizer? syntaxColorizer = null;
        viewModel.SyntaxChanged += highlighting =>
        {
            var textView = editor.TextArea.TextView;
            if (syntaxColorizer is null && highlighting is null) return;
            if (syntaxColorizer is not null) textView.LineTransformers.Remove(syntaxColorizer);
            syntaxColorizer = highlighting is not null && viewModel.Document is { } document
                ? new(document, highlighting) : null;
            if (syntaxColorizer is not null) textView.LineTransformers.Add(syntaxColorizer);
            textView.Redraw();
        };
        InFileMatchRenderer? searchRenderer = null;
        viewModel.InFileSearch.MatchesChanged += () =>
        {
            var textView = editor.TextArea.TextView;
            if (viewModel.InFileSearch.IsVisible)
            {
                if (searchRenderer is null)
                {
                    searchRenderer = new(viewModel.InFileSearch);
                    textView.BackgroundRenderers.Add(searchRenderer);
                }
                textView.InvalidateLayer(searchRenderer.Layer);
            }
            else if (searchRenderer is { } existing)
            {
                textView.BackgroundRenderers.Remove(existing);
                textView.InvalidateLayer(existing.Layer);
                searchRenderer = null;
            }
        };
        viewModel.InFileSearch.FatalFailure += FailAndClose;
        // Text drag/drop is disabled in editor options. Its detach handler sets a local false,
        // so explicitly allow file drops to bubble from the text area to this window.
        DragDrop.SetAllowDrop(this, true);
        DragDrop.SetAllowDrop(editor.TextArea, true);
        AddHandler(DragDrop.DragEnterEvent, FileDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, FileDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, FileDropped, RoutingStrategies.Bubble, handledEventsToo: true);
#if CODEVIEW_PERFORMANCE_PROBE
        GuiPerformanceProbe.Checkpoint("probe-attach-start");
        GuiPerformanceProbe.Attach(this, editor, viewModel, args.Length > 0);
        GuiPerformanceProbe.Checkpoint("probe-attach-end");
#endif
        editor.TextArea.Caret.PositionChanged += (_, _) => viewModel.SetPosition(editor.TextArea.Caret.Line, editor.TextArea.Caret.Column);
        editor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
            viewModel.SetViewport(editor.TextArea.TextView.HorizontalOffset, editor.TextArea.TextView.VerticalOffset);
        viewModel.RestoreCaret += (line, column) =>
        {
#if CODEVIEW_PERFORMANCE_PROBE
            if (GuiPerformanceProbe.TryRestoreOriginal(line, column)) return;
#endif
            editor.TextArea.Caret.Location = new TextLocation(line, column);
            editor.ScrollTo(line, column);
        };
        viewModel.RestoreViewport += (horizontal, vertical, epoch) => Dispatcher.UIThread.Post(() =>
        {
            if (_closing || !viewModel.HasDocument || viewModel.ViewEpoch != epoch) return;
#if CODEVIEW_PERFORMANCE_PROBE
            if (GuiPerformanceProbe.HasInteractiveDocument) return;
#endif
            // The candidate's ScrollTo*Offset methods are no-ops in 12.0.0.
            var scrollable = (IScrollable)editor.TextArea.TextView;
            scrollable.Offset = EditorViewport.Constrain(horizontal, vertical, scrollable.Extent, scrollable.Viewport);
        }, DispatcherPriority.Loaded);
        viewModel.SelectSearchRange += (offset, length) =>
        {
            var epoch = viewModel.ViewEpoch;
            Dispatcher.UIThread.Post(() =>
            {
                if (_closing || !viewModel.CanApplyResultSelection(epoch)) return;
#if CODEVIEW_PERFORMANCE_PROBE
                if (GuiPerformanceProbe.TrySelectOriginal(offset, length)) { editor.TextArea.Focus(); return; }
#endif
                editor.Select(checked((int)offset), length);
                editor.CaretOffset = checked((int)offset);
                var location = editor.Document.GetLocation(checked((int)offset));
                editor.ScrollTo(location.Line, location.Column);
                // 12.0.0 ScrollTo resets targetX to zero; reveal the actual shaped caret rectangle.
                editor.TextArea.TextView.EnsureVisualLines();
                editor.TextArea.Caret.BringCaretToView(40);
                editor.TextArea.Focus();
            }, DispatcherPriority.Loaded);
        };
        viewModel.ClearSearchSelection += () =>
        {
#if CODEVIEW_PERFORMANCE_PROBE
            if (GuiPerformanceProbe.TrySelectOriginal(0, 0, clear: true)) return;
#endif
            if (viewModel.HasDocument) editor.Select(editor.CaretOffset, 0);
        };
        Opened += async (_, _) =>
        {
            await RunAsync(() => viewModel.InitializeAsync(args));
#if CODEVIEW_PERFORMANCE_PROBE
            GuiPerformanceProbe.InitializationFinished(viewModel.HasDocument);
#endif
        };
        viewModel.FatalFailure += () => { Program.MarkFailed(); Close(); };
        Closing += async (_, eventArgs) =>
        {
            if (_allowClose) return;
            eventArgs.Cancel = true;
            if (_closing) return;
            _closing = true;
            await StopAndWaitAsync(() =>
            {
                try { _copies?.Dispose(); }
                finally { viewModel.Stop(); }
            }, () =>
            {
                var pending = Task.WhenAll(viewModel.PendingWork, _copies?.PendingWork ?? Task.CompletedTask);
#if CODEVIEW_PERFORMANCE_PROBE
                pending = Task.WhenAll(pending, GuiPerformanceProbe.PendingCopyWork);
#endif
                return pending;
            }, Program.MarkFailed);
#if CODEVIEW_PERFORMANCE_PROBE
            GuiPerformanceProbe.ConfirmRowShutdown(viewModel);
#endif
            _allowClose = true;
            Dispatcher.UIThread.Post(Close);
        };
#if CODEVIEW_PERFORMANCE_PROBE
        GuiPerformanceProbe.Checkpoint("window-constructor-end");
#endif
    }

    internal static async Task StopAndWaitAsync(Action stop, Func<Task> getPending, Action markFailed,
        TimeProvider? timeProvider = null)
    {
        try { stop(); }
        catch (Exception) { markFailed(); }
        try
        {
            var pending = getPending();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2), timeProvider ?? TimeProvider.System);
            try { await pending.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !pending.IsCompleted)
            {
                // The wait expired; keep observing the actual work until process exit.
                _ = pending.ContinueWith(task =>
                {
                    _ = task.Exception;
                    markFailed();
                }, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (Exception) { markFailed(); }
    }

    private async Task CopySourceAsync(TextEditor editor)
    {
        var offset = editor.SelectionStart;
        var length = editor.SelectionLength;
        if (length == 0 && editor.Options.CutCopyWholeLine && editor.Document is { } document)
        {
            var line = document.GetLineByNumber(editor.TextArea.Caret.Line);
            offset = line.Offset;
            length = line.TotalLength;
        }

        if (_viewModel.BeginCopy(offset, length) is not { } selection) return;
        _copies ??= new ClipboardCopyCoordinator(WriteSourceClipboardAsync);
        ClipboardCopyRequest request;
        try { request = _copies.Start(selection); }
        catch { selection.Dispose(); throw; }

        var result = await request.Completion;
        if (result.Status == ClipboardCopyStatus.Unavailable && !_closing && _copies.CanAdopt(request))
            _viewModel.SetStatus("クリップボードを利用できません。再試行してください。");
    }

    private async Task WriteSourceClipboardAsync(string text)
    {
        var clipboard = Clipboard ?? throw new ClipboardUnavailableException();
        try
        {
            await clipboard.SetValueAsync(DataFormat.Text, text);
            await clipboard.FlushAsync();
        }
        catch (Exception error) when (error is ExternalException and not SEHException or IOException or NotSupportedException)
        { throw new ClipboardUnavailableException(); }
    }

    private async Task OpenFilesAsync()
    {
        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "ファイルを開く", AllowMultiple = true
        });
        await OpenPickerSelectionAsync(selected);
    }

    private void ConfigureResizablePanels(MainWindowViewModel viewModel)
    {
        const double MinimumTreeWidth = 160;
        const double MinimumResultsHeight = 155;
        var tree = this.FindControl<Grid>("DocumentLayout")!.ColumnDefinitions[0];
        var results = this.FindControl<Grid>("MainLayout")!.RowDefinitions[3];
        var savedTreeWidth = 280d;
        var savedResultsHeight = 280d;

        void UpdateLimits()
        {
            tree.MaxWidth = Math.Max(MinimumTreeWidth, (Bounds.Width > 0 ? Bounds.Width : Width) * 0.48);
            results.MaxHeight = Math.Max(MinimumResultsHeight, (Bounds.Height > 0 ? Bounds.Height : Height) * 0.55);
        }

        void UpdateTree()
        {
            if (viewModel.IsTreeVisible)
            {
                tree.MinWidth = MinimumTreeWidth;
                if (tree.Width.Value == 0) tree.Width = new GridLength(savedTreeWidth);
            }
            else
            {
                if (tree.Width.Value > 0) savedTreeWidth = tree.Width.Value;
                tree.MinWidth = 0;
                tree.Width = new GridLength(0);
            }
        }

        void UpdateResults()
        {
            if (viewModel.SearchPanel.IsVisible)
            {
                results.MinHeight = MinimumResultsHeight;
                if (results.Height.Value == 0) results.Height = new GridLength(savedResultsHeight);
            }
            else
            {
                if (results.Height.Value > 0) savedResultsHeight = results.Height.Value;
                results.MinHeight = 0;
                results.Height = new GridLength(0);
            }
        }

        SizeChanged += (_, _) => UpdateLimits();
        viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(MainWindowViewModel.IsTreeVisible)) UpdateTree();
        };
        viewModel.SearchPanel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(SearchPanelViewModel.IsVisible)) UpdateResults();
        };
        UpdateLimits();
        UpdateTree();
        UpdateResults();
    }

    private async Task<bool> HandleSearchKeyAsync(KeyEventArgs key)
    {
        var inFileInput = this.FindControl<TextBox>("InFileQuery")!;
        var folderInput = this.FindControl<TextBox>("SearchQuery")!;
        if (key.Key == Key.Enter)
        {
            if (_viewModel.InFileSearch.IsVisible && inFileInput.IsFocused
                && key.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift && !IsComposing(inFileInput))
            {
                key.Handled = true;
                await RunAsync(() => MoveInFileAsync(key.KeyModifiers == KeyModifiers.Shift));
                return true;
            }

            if (_viewModel.SearchPanel.IsVisible && folderInput.IsFocused
                && key.KeyModifiers == KeyModifiers.None && !IsComposing(folderInput))
            {
                key.Handled = true;
                await RunAsync(_viewModel.SearchPanel.StartAsync);
                return true;
            }
            return false;
        }

        if (key.KeyModifiers != KeyModifiers.None) return false;
        var results = this.FindControl<ListBox>("SearchResultsList")!;
        if (_viewModel.SearchPanel.IsVisible && _viewModel.SearchPanel.IsRunning
            && (folderInput.IsFocused || results.IsKeyboardFocusWithin)
            && (!folderInput.IsFocused || !IsComposing(folderInput)))
        {
            key.Handled = true;
            Run(_viewModel.SearchPanel.Cancel);
            return true;
        }

        var bar = this.FindControl<Border>("InFileSearchBar")!;
        var editor = this.FindControl<TextEditor>("Editor")!;
        if (_viewModel.InFileSearch.IsVisible && (bar.IsKeyboardFocusWithin || editor.TextArea.IsKeyboardFocusWithin)
            && !IsComposing(inFileInput))
        {
            key.Handled = true;
            Run(HideInFileSearch);
            return true;
        }
        return false;
    }

    private static bool IsComposing(TextBox input) =>
        input.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault()?.PreeditText is { Length: > 0 };

    private void ShowSearch()
    {
        if (!_viewModel.CanSearchWorkspace) return;
        var interaction = _viewModel.InteractionEpoch;
        var workspace = _viewModel.WorkspaceEpoch;
        _viewModel.SearchPanel.IsVisible = true;
        var input = this.FindControl<TextBox>("SearchQuery")!;
        if (!input.Focus())
            Dispatcher.UIThread.Post(() =>
            {
                if (!_closing && _viewModel.InteractionEpoch == interaction && _viewModel.SearchPanel.IsVisible
                    && _viewModel.CanSearchWorkspace && _viewModel.WorkspaceEpoch == workspace)
                    input.Focus();
            }, DispatcherPriority.Loaded);
    }

    private void ShowInFileSearch()
    {
        if (!_viewModel.InFileSearch.CanOpen) return;
        var interaction = _viewModel.InteractionEpoch;
        _viewModel.InFileSearch.IsVisible = true;
        var input = this.FindControl<TextBox>("InFileQuery")!;
        if (input.Focus())
        {
            input.SelectAll();
            return;
        }
        var revision = _viewModel.InFileSearch.Revision;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closing && _viewModel.InteractionEpoch == interaction
                && _viewModel.InFileSearch.IsVisible && _viewModel.InFileSearch.Revision == revision
                && input.Focus())
                input.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void HideInFileSearch()
    {
        _viewModel.InFileSearch.IsVisible = false;
        this.FindControl<TextEditor>("Editor")!.TextArea.Focus();
    }

    private async Task MoveInFileAsync(bool previous)
    {
        var editor = this.FindControl<TextEditor>("Editor")!;
        var document = editor.Document;
        var epoch = _viewModel.ViewEpoch;
        var caret = editor.CaretOffset;
        var start = editor.SelectionStart;
        var length = editor.SelectionLength;
        var search = _viewModel.InFileSearch;
        var work = search.MoveAsync(caret, start, length, previous);
        var revision = search.Revision;
        var target = await work;
        if (_closing || target is not { } match || _viewModel.ViewEpoch != epoch || search.Revision != revision
            || !ReferenceEquals(document, editor.Document) || editor.CaretOffset != caret
            || editor.SelectionStart != start || editor.SelectionLength != length) return;

        var offset = checked((int)match.Offset);
        editor.CaretOffset = offset;
        editor.Select(offset, checked((int)match.Length));
        var location = document.GetLocation(offset);
        editor.ScrollTo(location.Line, location.Column);
        editor.TextArea.TextView.EnsureVisualLines();
        editor.TextArea.Caret.BringCaretToView(40);
    }

    private void ShowInFileSearchClicked(object? sender, RoutedEventArgs args) => Run(ShowInFileSearch);
    private void HideInFileSearchClicked(object? sender, RoutedEventArgs args) => Run(HideInFileSearch);
    private async void InFilePreviousClicked(object? sender, RoutedEventArgs args) => await RunAsync(() => MoveInFileAsync(true));
    private async void InFileNextClicked(object? sender, RoutedEventArgs args) => await RunAsync(() => MoveInFileAsync(false));

    private Task FindSymbolAsync(bool definitions)
    {
        var editor = this.FindControl<TextEditor>("Editor")!;
        if (!ReferenceEquals(editor.Document, _viewModel.Document))
        {
            _viewModel.SetStatus("定義・参照探索は通常の本文表示で利用してください。表示投影の専用試作では未接続です。");
            return Task.CompletedTask;
        }
        return definitions
            ? _viewModel.FindDefinitionsAsync(editor.CaretOffset, editor.SelectionStart, editor.SelectionLength)
            : _viewModel.FindReferencesAsync(editor.CaretOffset, editor.SelectionStart, editor.SelectionLength);
    }

    private async void FindReferencesClicked(object? sender, RoutedEventArgs args) => await RunAsync(() => FindSymbolAsync(definitions: false));
    private async void FindDefinitionsClicked(object? sender, RoutedEventArgs args) => await RunAsync(() => FindSymbolAsync(definitions: true));

    private void ShowSearchClicked(object? sender, RoutedEventArgs args) => Run(ShowSearch);
    private async void StartSearchClicked(object? sender, RoutedEventArgs args) => await RunAsync(_viewModel.SearchPanel.StartAsync);
    private void CancelSearchClicked(object? sender, RoutedEventArgs args) => Run(_viewModel.SearchPanel.Cancel);
    private void ClearSearchClicked(object? sender, RoutedEventArgs args) => Run(_viewModel.SearchPanel.Clear);
    private void HideSearchClicked(object? sender, RoutedEventArgs args) => Run(() => _viewModel.SearchPanel.IsVisible = false);
    private async void SearchResultDoubleTapped(object? sender, TappedEventArgs args)
    {
        for (var current = args.Source as Visual; current is not null && !ReferenceEquals(current, sender);
            current = current.GetVisualParent())
        {
            if (current is not ListBoxItem { Content: SearchResultRow row }) continue;
            if (!_viewModel.SearchPanel.CanActivate(row)) return;
            args.Handled = true;
            await RunAsync(() => _viewModel.ActivateSearchResultAsync(row));
            return;
        }
    }
    private async void SearchResultKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None || _viewModel.SearchPanel.SelectedRow is not { } row) return;
        args.Handled = true;
        await RunAsync(() => _viewModel.ActivateSearchResultAsync(row));
    }

    private async Task OpenFolderAsync()
    {
        var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "フォルダを開く", AllowMultiple = false
        });
        await OpenPickerSelectionAsync(selected);
    }

    private async Task OpenPickerSelectionAsync(IReadOnlyList<IStorageItem> selected)
    {
        IReadOnlyList<string>? paths = null;
        try
        {
            if (_closing || selected.Count == 0) return;
            var result = FileDropInput.ReadPaths(selected, item => item);
            if (result.Status == OperationStatus.Success)
                paths = result.Value;
            else
                _viewModel.SetStatus(result.Error!.Code == ErrorCode.QueueFull
                    ? "選択した項目数またはパス文字数が上限を超えています。"
                    : "ローカルのファイルまたはフォルダを選択してください。");
        }
        finally
        {
            foreach (var item in selected) item.Dispose();
        }

        if (paths is not null) await _viewModel.OpenFilesAsync(paths, OpenOrigin.Menu);
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception)
        {
            FailAndClose();
        }
    }

    private void Run(Action operation) => RunUiAction(operation, FailAndClose);

    internal static void RunUiAction(Action operation, Action failed)
    {
        try { operation(); }
        catch (Exception) { failed(); }
    }

    private void FailAndClose() => ReportUiFailure(Program.MarkFailed,
        () => _viewModel.SetStatus("予期しないエラーのため終了します。"), Close);

    internal static void ReportUiFailure(Action markFailed, Action notify, Action close)
    {
        markFailed();
        // A failing status subscriber must not escape an async-void event and bypass
        // Closing's cancellation/wait boundary. The process is already marked failed.
        try { notify(); }
        catch (Exception) { }
        close();
    }

    private void FileDragOver(object? sender, DragEventArgs args)
    {
        args.Handled = true;
        try { args.DragEffects = !_closing && FileDropInput.CanOfferCopy(args.DataTransfer, args.DragEffects)
            ? DragDropEffects.Copy : DragDropEffects.None; }
        catch (Exception)
        {
            args.DragEffects = DragDropEffects.None;
            FailAndClose();
        }
    }

    private async void FileDropped(object? sender, DragEventArgs args)
    {
        args.Handled = true;
        await RunAsync(() =>
        {
            var allowed = args.DragEffects;
            args.DragEffects = DragDropEffects.None;
            if (_closing || (allowed & DragDropEffects.Copy) == 0 || !args.DataTransfer.Contains(DataFormat.File))
                return Task.CompletedTask;
            // Validate the full transfer here so a delivered over-limit drop can report its reason.
            return _viewModel.OpenDropAsync(args.DataTransfer, () => args.DragEffects = DragDropEffects.Copy);
        });
    }

    private async void OpenClicked(object? sender, RoutedEventArgs args) => await RunAsync(OpenFilesAsync);
    private async void OpenFolderClicked(object? sender, RoutedEventArgs args) => await RunAsync(OpenFolderAsync);
    private async void RefreshWorkspaceClicked(object? sender, RoutedEventArgs args) => await RunAsync(_viewModel.RefreshWorkspaceAsync);
    private async Task ActivateTreeRowAsync(FileTreeRow row)
    {
        var interaction = _viewModel.InteractionEpoch;
        await _viewModel.ActivateTreeRowAsync(row);
        if (!_closing && _viewModel.InteractionEpoch == interaction && row.IsFile
            && _viewModel.FileTree.IsCurrentRow(row) && _viewModel.HasDocument && _viewModel.FilePath == row.Node.Entry.Path.FullPath)
            this.FindControl<TextEditor>("Editor")!.TextArea.Focus();
    }

    private async void TreeDisclosureClicked(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is Button { DataContext: FileTreeRow row })
        {
            await RunAsync(async () =>
            {
                var interaction = _viewModel.InteractionEpoch;
                _viewModel.FileTree.SelectedRow = row;
                await _viewModel.ActivateTreeRowAsync(row);
                if (!_closing && _viewModel.InteractionEpoch == interaction && _viewModel.IsTreeVisible
                    && _viewModel.FileTree.IsCurrentRow(row))
                    this.FindControl<ListBox>("FileTreeList")!.Focus();
            });
        }
    }

    private async void TreeDoubleTapped(object? sender, TappedEventArgs args)
    {
        for (var current = args.Source as Visual; current is not null && !ReferenceEquals(current, sender);
            current = current.GetVisualParent())
        {
            if (current is Button) return;
            if (current is not ListBoxItem { Content: FileTreeRow row }) continue;
            if (!_viewModel.FileTree.IsCurrentRow(row)) return;
            args.Handled = true;
            _viewModel.FileTree.SelectedRow = row;
            await RunAsync(() => ActivateTreeRowAsync(row));
            return;
        }
    }

    private async Task RefreshTreeAsync()
    {
        if (_viewModel.FileTree.SelectedRow is { CanExpand: true } row)
            await _viewModel.ExpandTreeRowAsync(row, reload: true);
        else await _viewModel.RefreshWorkspaceAsync();
    }

    private async void RefreshTreeClicked(object? sender, RoutedEventArgs args) => await RunAsync(RefreshTreeAsync);

    private async void TreeKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.KeyModifiers != KeyModifiers.None) return;
        var row = _viewModel.FileTree.SelectedRow;
        if (args.Key == Key.F5) { args.Handled = true; await RunAsync(RefreshTreeAsync); }
        else if (row is not null && args.Key == Key.Enter)
        { args.Handled = true; await RunAsync(() => ActivateTreeRowAsync(row)); }
        else if (row is { CanExpand: true } && args.Key == Key.Right)
        { args.Handled = true; await RunAsync(() => _viewModel.ExpandTreeRowAsync(row)); }
        else if (row is not null && args.Key == Key.Left)
        {
            args.Handled = true;
            Run(() =>
            {
                if (row.CanExpand && row.Node.IsExpanded) _viewModel.CollapseTreeRow(row);
                else if (row.Parent is { } parent) _viewModel.FileTree.SelectedRow = parent;
            });
        }
    }
    private void CloseWorkspaceClicked(object? sender, RoutedEventArgs args) => Run(_viewModel.CloseWorkspace);
    private async void ReloadClicked(object? sender, RoutedEventArgs args) => await RunAsync(_viewModel.ReloadAsync);
    private async void EncodingClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<EncodingChoice>(tag, out var choice))
            await RunAsync(() => _viewModel.ReopenEncodingAsync(choice));
    }
    private void CloseFileClicked(object? sender, RoutedEventArgs args) => Run(_viewModel.CloseFile);
    private void TabSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        Run(() =>
        {
            if (ReferenceEquals(args.Source, sender) && _viewModel.HasDocument) this.FindControl<TextEditor>("Editor")!.TextArea.Focus();
        });
    }
    private void CloseTabClicked(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        Run(() =>
        {
            if (sender is Button { DataContext: TabViewModel tab }) _viewModel.CloseTab(tab);
        });
    }
    private void ExitClicked(object? sender, RoutedEventArgs args) => Close();
}
