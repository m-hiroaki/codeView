using Avalonia.Controls;
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
#if CODEVIEW_PERFORMANCE_PROBE
        GuiPerformanceProbe.Attach(this, editor, args.Length > 0);
#endif
        editor.TextArea.Caret.PositionChanged += (_, _) => viewModel.SetPosition(editor.TextArea.Caret.Line, editor.TextArea.Caret.Column);
        viewModel.RestoreCaret += (line, column) =>
        {
            editor.TextArea.Caret.Location = new TextLocation(line, column);
            editor.ScrollTo(line, column);
        };
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

    private async void OpenClicked(object? sender, RoutedEventArgs args) => await RunAsync(OpenFilesAsync);
    private async void OpenFolderClicked(object? sender, RoutedEventArgs args) => await RunAsync(OpenFolderAsync);
    private async void RefreshWorkspaceClicked(object? sender, RoutedEventArgs args) => await RunAsync(_viewModel.RefreshWorkspaceAsync);
    private void CloseWorkspaceClicked(object? sender, RoutedEventArgs args) => _viewModel.CloseWorkspace();
    private async void ReloadClicked(object? sender, RoutedEventArgs args) => await RunAsync(_viewModel.ReloadAsync);
    private async void EncodingClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<EncodingChoice>(tag, out var choice))
            await RunAsync(() => _viewModel.ReopenEncodingAsync(choice));
    }
    private void CloseFileClicked(object? sender, RoutedEventArgs args) => _viewModel.CloseFile();
    private void ExitClicked(object? sender, RoutedEventArgs args) => Close();
}
