using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaEdit;
using CodeView.Operations;
using MarkView.Avalonia;
using MarkView.Avalonia.Mermaid;
using MarkView.Avalonia.SyntaxHighlighting;

namespace CodeView.Presentation;

internal sealed partial class MainWindow
{
    private const int MaximumMarkdownChars = 128 * 1024;
    private bool _markdownPreviewRequested;
    private CancellationTokenSource? _markdownLifetime;
    private Control _sourceSurface = null!;
    private Border _markdownSurface = null!;
    private MenuItem _markdownViewMenu = null!;

    private void ConfigureMarkdownView(KeyModifiers modifier)
    {
        _sourceSurface = this.FindControl<Control>("SourceSurface")!;
        _markdownSurface = this.FindControl<Border>("MarkdownSurface")!;
        _markdownViewMenu = this.FindControl<MenuItem>("MarkdownViewMenu")!;
        var gesture = CreateMarkdownViewGesture(modifier);
        _markdownViewMenu.InputGesture = gesture;
        // Route at the window so the shortcut works with the menu closed and from either view.
        AddHandler(KeyDownEvent, (_, key) =>
        {
            if (_closing || !_markdownViewMenu.IsEnabled || !gesture.Matches(key)) return;
            key.Handled = true;
            Run(ToggleMarkdownView);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Closed += (_, _) => ShowMarkdownPreview(false);
        _viewModel.RevealSource += () => ShowMarkdownPreview(false);
        _viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(MainWindowViewModel.Document)) return;
            ShowMarkdownPreview(false);
            var document = _viewModel.Document;
            var path = _viewModel.FilePath;
            _markdownViewMenu.IsEnabled = document is not null && CanPreviewMarkdown(path, document.TextLength);
            if (!_markdownViewMenu.IsEnabled) return;
            _markdownPreviewRequested = true;
            // Allow the normal document attachment and caret restoration to finish first.
            Dispatcher.UIThread.Post(() =>
            {
                if (!_closing && _markdownPreviewRequested && ReferenceEquals(document, _viewModel.Document))
                    ShowMarkdownPreview(true);
            }, DispatcherPriority.Background);
        };
    }

    internal static KeyGesture CreateMarkdownViewGesture(KeyModifiers modifier) =>
        new(Key.P, modifier);

    internal static string? ResolveMarkdownFileLink(string documentPath, string? url) =>
        string.IsNullOrWhiteSpace(url) || url.StartsWith('#') || url.Contains('\0')
            ? null : LocalMarkdownImages.Resolve(documentPath, url);

    internal static bool CanPreviewMarkdown(string? path, int length) => path is not null
        && length is >= 0 and <= MaximumMarkdownChars
        && (Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".markdown", StringComparison.OrdinalIgnoreCase));

    private void ToggleMarkdownViewClicked(object? sender, RoutedEventArgs args) => Run(ToggleMarkdownView);

    private void ToggleMarkdownView()
    {
        if (_closing || !_markdownViewMenu.IsEnabled) return;
        ShowMarkdownPreview(!_markdownSurface.IsVisible);
        if (_markdownSurface.Child is MarkdownPreview { Child: MarkdownViewer viewer })
            viewer.Focus();
        else
            this.FindControl<TextEditor>("Editor")!.TextArea.Focus();
    }

    private void ShowMarkdownPreview(bool show)
    {
        _markdownPreviewRequested = false;
        if (!show)
        {
            _markdownSurface.IsVisible = false;
            _sourceSurface.IsVisible = true;
            _markdownSurface.Child = null;
            _markdownLifetime?.Cancel();
            _markdownLifetime?.Dispose();
            _markdownLifetime = null;
            _markdownViewMenu.Header = "Markdownを表示";
            return;
        }

        if (_markdownSurface.IsVisible || _viewModel.Document is not { } document
            || !CanPreviewMarkdown(_viewModel.FilePath, document.TextLength))
            return;

        var focusPreview = _sourceSurface.IsKeyboardFocusWithin || FocusManager?.GetFocusedElement() is null;
        var path = _viewModel.FilePath;
        var lifetime = new CancellationTokenSource();
        _markdownLifetime = lifetime;
        var ct = lifetime.Token;
        try
        {
            var viewer = new MarkdownViewer
            {
                Focusable = true,
                IsLazyRenderingEnabled = true,
                BaseUri = LocalMarkdownImages.DocumentUri(path),
                Margin = new Avalonia.Thickness(18, 8)
            };
            viewer.LinkClicked += async (_, link) =>
            {
                if (_closing || ct.IsCancellationRequested || ResolveMarkdownFileLink(path, link.Url) is not { } target) return;
                link.Handled = true;
                await RunAsync(() => _viewModel.OpenFilesAsync([target], OpenOrigin.MarkdownLink));
            };
            viewer.Extensions.Add(new LocalMarkdownImages(path));
            viewer.UseTextMateHighlighting();
            viewer.UseMermaid();
            if (PlantUmlPreview.FromEnvironment() is { } plantUml)
                viewer.UseFenceRenderer("plantuml", (code, _) => plantUml.CreateBlock(code, ct));
            viewer.Markdown = document.Text;
            _markdownSurface.Child = new MarkdownPreview { Child = viewer };
            _sourceSurface.IsVisible = false;
            _markdownSurface.IsVisible = true;
            _markdownViewMenu.Header = "ソースを表示";
            if (focusPreview) viewer.Focus();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException
            or Avalonia.Markup.Xaml.XamlLoadException or System.IO.IOException)
        {
            ShowMarkdownPreview(false);
        }
    }
}

internal sealed partial class MarkdownPreview : Border
{
    public MarkdownPreview() => AvaloniaXamlLoader.Load(this);
}
