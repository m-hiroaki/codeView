using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
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
        _viewModel = viewModel;
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        this.FindControl<MenuItem>("OpenMenu")!.InputGesture = new KeyGesture(Key.O, modifier);
        this.FindControl<MenuItem>("CloseMenu")!.InputGesture = new KeyGesture(Key.W, modifier);
        KeyDown += async (_, key) =>
        {
            if (key.Key == Key.Tab && (key.KeyModifiers == KeyModifiers.Control || key.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)))
            {
                key.Handled = true;
                viewModel.CycleTab(key.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                if (viewModel.HasDocument) this.FindControl<TextEditor>("Editor")!.Focus();
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
                viewModel.CloseFile();
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
        GuiPerformanceProbe.Attach(this, editor, args.Length > 0);
#endif
        editor.TextArea.Caret.PositionChanged += (_, _) => viewModel.SetPosition(editor.TextArea.Caret.Line, editor.TextArea.Caret.Column);
        editor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
            viewModel.SetViewport(editor.TextArea.TextView.HorizontalOffset, editor.TextArea.TextView.VerticalOffset);
        viewModel.RestoreCaret += (line, column) =>
        {
            editor.TextArea.Caret.Location = new TextLocation(line, column);
            editor.ScrollTo(line, column);
        };
        viewModel.RestoreViewport += (horizontal, vertical, epoch) => Dispatcher.UIThread.Post(() =>
        {
            if (_closing || !viewModel.HasDocument || viewModel.ViewEpoch != epoch) return;
            // The candidate's ScrollTo*Offset methods are no-ops in 12.0.0.
            var scrollable = (IScrollable)editor.TextArea.TextView;
            scrollable.Offset = EditorViewport.Constrain(horizontal, vertical, scrollable.Extent, scrollable.Viewport);
        }, DispatcherPriority.Loaded);
        Opened += async (_, _) =>
        {
            await RunAsync(() => viewModel.InitializeAsync(args));
#if CODEVIEW_PERFORMANCE_PROBE
            GuiPerformanceProbe.InitializationFinished(viewModel.HasDocument);
#endif
        };
        viewModel.FatalFailure += () => { Environment.ExitCode = 1; Close(); };
        Closing += async (_, eventArgs) =>
        {
            if (_allowClose) return;
            eventArgs.Cancel = true;
            if (_closing) return;
            _closing = true;
            viewModel.Stop();
            try { await viewModel.PendingWork.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
            catch (Exception) { Environment.ExitCode = 1; }
            _allowClose = true;
            Close();
        };
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
            _viewModel.SetStatus("予期しないエラーのため終了します。");
            Environment.ExitCode = 1;
            if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(1);
            else Close();
        }
    }

    private void FileDragOver(object? sender, DragEventArgs args)
    {
        args.Handled = true;
        try { args.DragEffects = !_closing && FileDropInput.CanOfferCopy(args.DataTransfer, args.DragEffects)
            ? DragDropEffects.Copy : DragDropEffects.None; }
        catch (Exception)
        {
            args.DragEffects = DragDropEffects.None;
            _viewModel.SetStatus("ドロップデータを確認できません。");
            Environment.ExitCode = 1;
            Close();
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
            _viewModel.FileTree.SelectedRow = row;
            await RunAsync(() => _viewModel.ActivateTreeRowAsync(row));
            this.FindControl<ListBox>("FileTreeList")!.Focus();
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
            if (row.CanExpand && row.Node.IsExpanded) _viewModel.CollapseTreeRow(row);
            else if (row.Parent is { } parent) _viewModel.FileTree.SelectedRow = parent;
        }
    }
    private void CloseWorkspaceClicked(object? sender, RoutedEventArgs args) => _viewModel.CloseWorkspace();
    private async void ReloadClicked(object? sender, RoutedEventArgs args) => await RunAsync(_viewModel.ReloadAsync);
    private async void EncodingClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<EncodingChoice>(tag, out var choice))
            await RunAsync(() => _viewModel.ReopenEncodingAsync(choice));
    }
    private void CloseFileClicked(object? sender, RoutedEventArgs args) => _viewModel.CloseFile();
    private void TabSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (ReferenceEquals(args.Source, sender) && _viewModel.HasDocument) this.FindControl<TextEditor>("Editor")!.Focus();
    }
    private void CloseTabClicked(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is Button { DataContext: TabViewModel tab }) _viewModel.CloseTab(tab);
    }
    private void ExitClicked(object? sender, RoutedEventArgs args) => Close();
}
