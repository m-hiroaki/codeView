using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CodeView.Documents;
using CodeView.Navigation;

namespace CodeView.Presentation;

internal sealed partial class MainWindow
{
    private Task _stickyWork = Task.CompletedTask;
    private CancellationTokenSource? _stickyCancellation;
    private StickyScopeRanges? _stickyRanges;
    private Border? _stickyHeader;
    private (Grid Row, TextBlock Number, TextBlock Code) _stickyType;
    private (Grid Row, TextBlock Number, TextBlock Code) _stickyMethod;
    private SyntaxColorizer? _stickySyntax;
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
        _stickyType = FindStickyRow("Type");
        _stickyMethod = FindStickyRow("Method");
        var view = editor.TextArea.TextView;
        view.VisualLinesChanged += (_, _) => UpdateStickyHeader(editor);
        view.ScrollOffsetChanged += (_, _) => UpdateStickyHeader(editor);
        _viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(MainWindowViewModel.Document) or nameof(MainWindowViewModel.StickyScrollEnabled))
                RestartStickyScroll(editor);
        };

        (Grid, TextBlock, TextBlock) FindStickyRow(string kind)
        {
            var number = this.FindControl<TextBlock>($"StickyScroll{kind}Number")!;
            var code = this.FindControl<TextBlock>($"StickyScroll{kind}Code")!;
            number.FontFamily = code.FontFamily = editor.FontFamily;
            number.FontSize = code.FontSize = editor.FontSize;
            return (this.FindControl<Grid>($"StickyScroll{kind}Row")!, number, code);
        }
    }

    private void SetStickySyntax(TextEditor editor, SyntaxColorizer? syntax)
    {
        _stickySyntax = syntax;
        _stickyIndex = -2;
        UpdateStickyHeader(editor);
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
            var ranges = await Task.Run(() => StickyScopeRanges.ReadCSharp(lease, ct), ct);
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
            header.IsVisible = index >= 0;
            if (index >= 0)
            {
                var active = ranges.Ranges[index];
                _stickyMethod.Row.IsVisible = active.Kind == DeclarationKind.Method;
                var type = _stickyMethod.Row.IsVisible ? ranges.Ranges[active.Parent] : active;
                SetDeclaration(_stickyType, type);
                if (_stickyMethod.Row.IsVisible) SetDeclaration(_stickyMethod, active);
            }
        }
        if (index >= 0 && view.TranslatePoint(default, editor) is { } origin)
        {
            _stickyType.Row.Height = _stickyMethod.Row.Height = view.DefaultLineHeight;
            var gutter = new GridLength(Math.Max(0, origin.X));
            _stickyType.Row.ColumnDefinitions[0].Width = _stickyMethod.Row.ColumnDefinitions[0].Width = gutter;
            header.Height = view.DefaultLineHeight * (_stickyMethod.Row.IsVisible ? 2 : 1);
            header.Margin = new Thickness(0, origin.Y,
                Math.Max(0, editor.Bounds.Width - origin.X - view.Bounds.Width), 0);
        }
        if (_stickyTarget is { } target && ReferenceEquals(editor.Document, _viewModel.Document))
        {
            _stickyTarget = null;
            if (target == StickyTarget(editor)) RevealBelowStickyHeader(editor, view);
        }

        void SetDeclaration((Grid Row, TextBlock Number, TextBlock Code) row, StickyScopeRange range)
        {
            row.Number.Text = range.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SetStickyDeclaration(row.Code, editor.Document, range, _stickySyntax);
        }
    }

    internal static void SetStickyDeclaration(TextBlock code, TextDocument document, StickyScopeRange range,
        SyntaxColorizer? syntax)
    {
        const int maximumLength = 512;
        var inlines = code.Inlines!;
        inlines.Clear();
        var remaining = maximumLength;
        for (var lineNumber = (int)range.StartLine; lineNumber <= range.HeaderEndLine && remaining > 0; lineNumber++)
        {
            var line = document.GetLineByNumber(lineNumber);
            var start = line.Offset;
            var offset = start;
            var end = Math.Min(line.EndOffset, start + remaining);
            if (end < line.EndOffset && char.IsLowSurrogate(document.GetCharAt(end))) end--;
            remaining -= end - line.Offset + 1;
            if (lineNumber != range.StartLine)
            {
                while (start < end && char.IsWhiteSpace(document.GetCharAt(start))) start++;
                offset = start;
                if (start < end) inlines.Add(new Run(" "));
            }
            if (start < end) syntax?.VisitColors(line.Offset, end, AddColor);
            AddRun(end, null);
            if (end < line.EndOffset || remaining <= 0 && lineNumber < range.HeaderEndLine)
                inlines.Add(new Run("…"));

            void AddColor(int colorStart, int colorEnd, IBrush brush)
            {
                AddRun(colorStart, null);
                AddRun(colorEnd, brush);
            }

            void AddRun(int until, IBrush? brush)
            {
                if (until <= offset) return;
                var text = document.GetText(offset, until - offset).Replace("\t", "    ", StringComparison.Ordinal);
                var run = new Run(text);
                if (brush is not null) run.Foreground = brush;
                inlines.Add(run);
                offset = until;
            }
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
