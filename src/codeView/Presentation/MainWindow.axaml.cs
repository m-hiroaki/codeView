using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using CodeView.Files;
using CodeView.Operations;

namespace CodeView.Presentation;

internal sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _allowClose;
    private bool _closing;

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
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        this.FindControl<MenuItem>("OpenMenu")!.InputGesture = new KeyGesture(Key.O, modifier);
        this.FindControl<MenuItem>("CloseMenu")!.InputGesture = new KeyGesture(Key.W, modifier);
        this.FindControl<MenuItem>("FindDefinitionsMenu")!.InputGesture = new KeyGesture(Key.F12);
        this.FindControl<MenuItem>("FindReferencesMenu")!.InputGesture = new KeyGesture(Key.F12, KeyModifiers.Shift);
        this.FindControl<MenuItem>("WorkspaceSearchMenu")!.InputGesture = new KeyGesture(Key.F, modifier | KeyModifiers.Shift);
        this.FindControl<ListBox>("SearchResultsList")!.AddHandler(KeyDownEvent, SearchResultKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => viewModel.RecordUserInteraction(), RoutingStrategies.Tunnel, handledEventsToo: true);
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
            if (key.Key == Key.F && key.KeyModifiers == (modifier | KeyModifiers.Shift))
            { key.Handled = true; Run(ShowSearch); return; }
            if (key.Key == Key.Tab && (key.KeyModifiers == KeyModifiers.Control || key.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)))
            {
                key.Handled = true;
                Run(() =>
                {
                    viewModel.CycleTab(key.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                    if (viewModel.HasDocument) this.FindControl<TextEditor>("Editor")!.Focus();
                });
                return;
            }
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
            await StopAndWaitAsync(viewModel.Stop, () =>
            {
                var pending = viewModel.PendingWork;
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

    private async Task OpenFilesAsync()
    {
        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "ファイルを開く", AllowMultiple = true
        });
        var paths = selected.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0) await _viewModel.OpenFilesAsync(paths, OpenOrigin.Menu);
    }

    private void ShowSearch()
    {
        if (!_viewModel.CanSearchWorkspace) return;
        _viewModel.SearchPanel.IsVisible = true;
        this.FindControl<TextBox>("SearchQuery")!.Focus();
    }

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
        if (args.Source is Avalonia.StyledElement { DataContext: SearchResultRow row })
            await RunAsync(() => _viewModel.ActivateSearchResultAsync(row));
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
        var paths = selected.Select(folder => folder.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0) await _viewModel.OpenFilesAsync(paths, OpenOrigin.Menu);
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
            if (_closing || !FileDropInput.CanOfferCopy(args.DataTransfer, args.DragEffects))
            { args.DragEffects = DragDropEffects.None; return Task.CompletedTask; }
            args.DragEffects = DragDropEffects.Copy;
            return _viewModel.OpenDropAsync(args.DataTransfer);
        });
    }

    private async void OpenClicked(object? sender, RoutedEventArgs args) => await RunAsync(OpenFilesAsync);
    private async void OpenFolderClicked(object? sender, RoutedEventArgs args) => await RunAsync(OpenFolderAsync);
    private async void RefreshWorkspaceClicked(object? sender, RoutedEventArgs args) => await RunAsync(_viewModel.RefreshWorkspaceAsync);
    private async Task ActivateTreeRowAsync(FileTreeRow row)
    {
        await _viewModel.ActivateTreeRowAsync(row);
        if (row.IsFile && _viewModel.HasDocument) this.FindControl<TextEditor>("Editor")!.Focus();
    }

    private async void TreeDisclosureClicked(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is Button { DataContext: FileTreeRow row })
        {
            await RunAsync(async () =>
            {
                _viewModel.FileTree.SelectedRow = row;
                await _viewModel.ActivateTreeRowAsync(row);
                this.FindControl<ListBox>("FileTreeList")!.Focus();
            });
        }
    }

    private async void TreeDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (args.Source is Control control && IsDisclosureButton(control)) return;
        if (_viewModel.FileTree.SelectedRow is { } row)
        {
            args.Handled = true;
            await RunAsync(() => ActivateTreeRowAsync(row));
        }
    }

    private static bool IsDisclosureButton(Control control)
    {
        for (var current = control; current is not null; current = current.Parent as Control)
        {
            if (current is Button) return true;
            if (current is ListBoxItem) break;
        }
        return false;
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
            if (ReferenceEquals(args.Source, sender) && _viewModel.HasDocument) this.FindControl<TextEditor>("Editor")!.Focus();
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
