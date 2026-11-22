using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using CodeView.Documents;
using CodeView.Navigation;

namespace CodeView.Presentation;

internal sealed partial class MainWindow
{
    private Task _stickyWork = Task.CompletedTask;
    private CancellationTokenSource? _stickyCancellation;
    private CSharpScopeRanges? _stickyRanges;
    private Border? _stickyHeader;
    private TextBlock? _stickyLabel;
    private int _stickyIndex = -1;
    private (long View, long Interaction, double Offset, int Caret)? _stickyTarget;
#if CODEVIEW_PERFORMANCE_PROBE
    internal Task StickyScrollWork => _stickyWork;
#endif

    private async void ToggleStickyScrollClicked(object? sender, RoutedEventArgs args) =>
        await RunAsync(() => _viewModel.ToggleStickyScrollAsync());

    private void ConfigureStickyScroll(TextEditor editor)
    {
        _stickyHeader = this.FindControl<Border>("StickyScrollHeader")!;
        _stickyLabel = this.FindControl<TextBlock>("StickyScrollLabel")!;
        var view = editor.TextArea.TextView;
        view.VisualLinesChanged += (_, _) => UpdateStickyHeader(editor);
        view.ScrollOffsetChanged += (_, _) => UpdateStickyHeader(editor);
        _viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(MainWindowViewModel.Document) or nameof(MainWindowViewModel.StickyScrollEnabled))
                RestartStickyScroll(editor);
        };
    }

    private void RestartStickyScroll(TextEditor editor)
    {
        _stickyCancellation?.Cancel();
        _stickyCancellation = null;
        _stickyRanges = null;
        _stickyTarget = null;
        _stickyIndex = -1;
        _stickyHeader!.IsVisible = false;
        if (_closing || !_viewModel.StickyScrollEnabled || _viewModel.StickyDocument is not { } document) return;
        var cancellation = new CancellationTokenSource();
        _stickyCancellation = cancellation;
        _stickyWork = ReadStickyScopesAsync(_stickyWork, editor, document, cancellation);
    }

    private async Task ReadStickyScopesAsync(Task previous, TextEditor editor, DocumentHandle document,
        CancellationTokenSource cancellation)
    {
        var ct = cancellation.Token;
        try
        {
            await previous;
            // Give the newly attached document time to paint and coalesce rapid tab changes.
            await Task.Delay(125, ct);
#if CODEVIEW_PERFORMANCE_PROBE
            GuiPerformanceProbe.StickyAnalysisStarted();
#endif
            using var lease = document.AcquireReadView();
            var ranges = await Task.Run(() => CSharpScopeRanges.Read(lease, ct), ct);
            if (ct.IsCancellationRequested || _closing || !ReferenceEquals(document, _viewModel.StickyDocument)) return;
            _stickyRanges = ranges;
#if CODEVIEW_PERFORMANCE_PROBE
            GuiPerformanceProbe.StickyAnalysisCompleted(ranges is { Ranges.Count: > 0 });
#endif
            UpdateStickyHeader(editor);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { FailAndClose(); }
        finally
        {
            if (ReferenceEquals(_stickyCancellation, cancellation)) _stickyCancellation = null;
            cancellation.Dispose();
        }
    }

    private void UpdateStickyHeader(TextEditor editor)
    {
        var view = editor.TextArea.TextView;
        var ranges = _stickyRanges;
        if (ranges is null || !view.VisualLinesValid) return;
        var index = view.VisualLines.Count > 0
            ? ranges.Find(view.VisualLines[0].FirstDocumentLine.LineNumber) : -1;
        var header = _stickyHeader!;
        if (index != _stickyIndex)
        {
            _stickyIndex = index;
            if (index >= 0) _stickyLabel!.Text = ranges.Format(index);
            header.IsVisible = index >= 0;
        }
        if (index >= 0 && view.TranslatePoint(default, editor) is { } origin)
        {
            header.Height = view.DefaultLineHeight;
            header.Margin = new Thickness(origin.X, origin.Y,
                Math.Max(0, editor.Bounds.Width - origin.X - view.Bounds.Width), 0);
        }
        if (_stickyTarget is { } target && ReferenceEquals(editor.Document, _viewModel.Document))
        {
            _stickyTarget = null;
            if (target == StickyTarget(editor)) RevealBelowStickyHeader(editor, view);
        }
    }

    private (long View, long Interaction, double Offset, int Caret) StickyTarget(TextEditor editor) =>
        (_viewModel.ViewEpoch, _viewModel.InteractionEpoch, editor.TextArea.TextView.VerticalOffset, editor.CaretOffset);

    private void KeepStickyTargetVisible(TextEditor editor)
    {
        if (_stickyCancellation is not null || _stickyRanges is not null)
        {
            _stickyTarget = StickyTarget(editor);
            UpdateStickyHeader(editor);
        }
    }

    private void RevealBelowStickyHeader(TextEditor editor, TextView view)
    {
        if (_stickyHeader is not { IsVisible: true } header || view.VisualLines.Count == 0
            || editor.TextArea.Caret.Line < view.VisualLines[0].FirstDocumentLine.LineNumber
            || editor.TextArea.Caret.Line > view.VisualLines[^1].LastDocumentLine.LineNumber) return;
        var top = view.GetVisualPosition(editor.TextArea.Caret.Position, VisualYPosition.LineTop).Y;
        var inViewport = top - view.VerticalOffset;
        if (inViewport < -view.DefaultLineHeight || inViewport >= header.Height) return;
        var scrollable = (IScrollable)view;
        scrollable.Offset = EditorViewport.Constrain(view.HorizontalOffset,
            Math.Max(0, top - header.Height), scrollable.Extent, scrollable.Viewport);
    }
}
