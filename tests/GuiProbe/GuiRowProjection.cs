using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CodeView.Documents;
using CodeView.Contracts;
using CodeView.Search;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private readonly bool _rowsEnabled = Environment.GetEnvironmentVariable("CODEVIEW_GUI_ROWS") == "1";
    private readonly bool _rowChecks = Environment.GetEnvironmentVariable("CODEVIEW_GUI_ROW_CHECKS") == "1";
    private readonly bool _contextualRows = Environment.GetEnvironmentVariable("CODEVIEW_GUI_GRAPHEME_INDEX") == "1";
    private SyntaxHighlighting? _contextualSyntax;

    internal static bool HasContextualRows => _instance is { _rowsEnabled: true, _contextualRows: true };

    internal static bool TryReadContextualSelection(out (int Caret, int Start, int Length) selection)
    {
        selection = default;
        if (!HasContextualRows || _instance!._displaySession is not { } source) return false;
        selection = (checked((int)source.Caret), checked((int)source.SelectionStart), checked((int)source.SelectionLength));
        return true;
    }

    internal static bool TryApplyContextualMatches(InFileSearchViewModel search)
    {
        if (!HasContextualRows) return false;
        var probe = _instance!;
        var matches = search.IsVisible ? search.Matches : [];
        probe._contextLayer?.SetMatches(matches);
        probe._rowRenderer?.SetMatches(matches);
        return true;
    }

    internal static bool TryApplyContextualSyntax(SyntaxHighlighting? highlighting)
    {
        if (!HasContextualRows) return false;
        var probe = _instance!;
        if (ReferenceEquals(probe._contextualSyntax, highlighting)) return true;
        probe._contextualSyntax = highlighting;
        if (probe._contextLayer is { } layer && probe._displaySession is { } source &&
            probe._interactiveRows is { } rows)
        {
            layer.Apply(probe._editor.Document, source, rows, highlighting);
            layer.SetSelection(source.Anchor, source.Caret);
            TryApplyContextualMatches(probe._viewModel.InFileSearch);
        }
        return true;
    }
    private DocumentRowProjection? _interactiveRows;
    private RowProjectionRenderer? _rowRenderer;
    private RowClippingMargin? _rowMargin;
    private ContextualRowLayer? _contextLayer;
    private readonly List<object> _rowSamples = [];
    private object? _rowResults;
    private KeyEventArgs? _scriptedRowKey;
    private PointerEventArgs? _scriptedRowPointer;
    private int _rowCount;
    private TextDocument? _rowDisplay;
    private long _reusedRowDisplays;
    private bool _sawClippedRow;
    private const int RowColumns = 512;

    private static void ValidateRowMode(bool hasInput)
    {
        var rows = Environment.GetEnvironmentVariable("CODEVIEW_GUI_ROWS") == "1";
        if (rows && (!hasInput || Environment.GetEnvironmentVariable("CODEVIEW_GUI_WINDOWED") != "1" ||
            Environment.GetEnvironmentVariable("CODEVIEW_GUI_INTERACTIVE") != "1" ||
            TextWindow.ParseWindowChars(Environment.GetEnvironmentVariable("CODEVIEW_GUI_WINDOW_CHARS")) != DocumentDisplaySession.MaximumSliceChars))
            throw new ArgumentException("行別試作には入力、有界対話表示、16 Ki文字予算を指定してください。");
        if (Environment.GetEnvironmentVariable("CODEVIEW_GUI_ROW_CHECKS") == "1" &&
            (!rows || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"))
            throw new ArgumentException("行別表示の自動操作は専用CIで行別試作を指定してください。");
    }

    private int RequestedRowCount()
    {
        var view = _editor.TextArea.TextView;
        var lines = Math.Floor(view.Bounds.Height / view.DefaultLineHeight);
        return double.IsFinite(lines) ? (int)Math.Clamp(lines, 1, 512) : 1;
    }

    private void RevealRowCaret()
    {
        var lines = RequestedRowCount();
        // Use the full row budget even at EOF: RevealCaret can move the first row back.
        _displaySession!.RevealCaret(lines, Math.Max(2, Math.Min(RowColumns, (_windowChars - lines + 1) / lines) - 2));
        _revealRowsPending = true;
    }

    private TextDocument BuildRowDocument(bool reveal, bool reuseDisplay = false)
    {
        InstallSourceScrollBars();
        if (reveal) RevealRowCaret();
        _rowCount = RequestedRowCount();
        var previousText = _interactiveRows?.Text;
        _interactiveRows = _displaySession!.CreateRowProjection(_rowCount, RowColumns, _windowChars);
        foreach (var row in _interactiveRows.Rows) _sawClippedRow |= row.LeftClipped || row.RightClipped;
        // Keep the editor's visual lines when different source positions display the same text.
        // The source mapping still changes with every new projection.
        TextDocument display;
        if (reuseDisplay && _rowDisplay is { } current && ReferenceEquals(current, _editor.Document) &&
            string.Equals(previousText, _interactiveRows.Text, StringComparison.Ordinal))
        {
            display = current;
            _reusedRowDisplays++;
        }
        else display = CreateDisplayDocument(_interactiveRows.Text);
        _rowDisplay = display;
        if (_contextualRows)
        {
            var view = _editor.TextArea.TextView;
            if (_contextLayer is null)
            {
                _contextLayer = new ContextualRowLayer(_editor) { IsHitTestVisible = false };
                view.InsertLayer(_contextLayer, KnownLayer.Text, LayerInsertionPosition.Above);
                view.VisualLinesChanged += (_, _) => _contextLayer.InvalidateVisual();
            }
            _contextLayer.Apply(display, _displaySession, _interactiveRows, _contextualSyntax);
        }
        _sourceLineNumbers!.Apply(display, new SourceLineNumbers(_displaySession.FirstVisibleLine,
            _sourceDocument!.LineCount, display.LineCount));
        _rowRenderer ??= new RowProjectionRenderer(_editor);
        _rowMargin ??= new RowClippingMargin();
        _rowMargin.Apply(display, _interactiveRows);
        if (!_editor.TextArea.LeftMargins.Contains(_rowMargin)) _editor.TextArea.LeftMargins.Add(_rowMargin);
        if (!_editor.TextArea.TextView.BackgroundRenderers.Contains(_rowRenderer))
            _editor.TextArea.TextView.BackgroundRenderers.Add(_rowRenderer);
        _largestDisplayChars = Math.Max(_largestDisplayChars, display.TextLength);
        return display;
    }

    private void RenderRows()
    {
        _renderingInteractive = true;
        try
        {
            var source = _displaySession!;
            if (_interactiveRows is null || _rowCount != RequestedRowCount() ||
                !source.TryGetRowSelection(_interactiveRows, out _))
            {
                var display = BuildRowDocument(reveal: false, reuseDisplay: true);
                if (!ReferenceEquals(display, _editor.Document)) _editor.Document = display;
            }
            if (!source.TryGetRowSelection(_interactiveRows!, out var selection))
                throw new InvalidOperationException("現在の行投影に選択を対応できません。");
            if (_editor.SelectionLength != 0)
                _editor.Select(0, 0); // Synthetic separators must never enter native selection/copy.
            _editor.TextArea.Caret.CaretBrush = Brushes.Transparent;
            if (selection!.CaretOffset is { } caret) _editor.CaretOffset = caret;
            _rowRenderer!.Apply(_editor.Document, _interactiveRows!, selection);
            KeepRowCaretPhysicallyVisible();
            if (_contextualRows) TryApplyContextualMatches(_viewModel.InFileSearch);
            SyncSourceScrollBars();
            if (!source.TryGetRowSelection(_interactiveRows!, out selection))
                throw new InvalidOperationException("再表示後の行投影が失効しました。");
            _viewModel.SetPosition(1, 1);
            _contextLayer?.SetSelection(source.Anchor, source.Caret);
            if (_rowSamples.Count < 256)
                _rowSamples.Add(new { source.FirstVisibleLine, source.HorizontalSourceOffset,
                    source.Anchor, source.Caret, DisplayChars = _interactiveRows!.Text.Length,
                    DisplayRows = _interactiveRows.Rows.Length, _interactiveRows.EffectiveColumns,
                    CaretVisible = selection!.CaretOffset.HasValue, Spans = selection.Spans.ToArray() });
        }
        finally { _renderingInteractive = false; }
    }

    private void ClearRowProjection()
    {
        _interactiveRows = null;
        _rowDisplay = null;
        _contextLayer?.Clear();
        _rowRenderer?.Clear();
        _rowMargin?.Clear();
        _rowWheel.Reset();
        _revealRowsPending = false;
    }

    private bool BeginRowPointer(int offset, long id, bool extend, bool wholeLine) =>
        _interactiveRows is { } rows && _pointerSelection.BeginRows(_displaySession!, rows, offset, id, extend, wholeLine);

    private void UpdateRowPointer(long id)
    {
        if (_interactiveRows is not { } rows || HitPointer(_pointerPosition, clamp: true) is not { } offset) return;
        if (_pointerSelection.MoveRows(_displaySession!, rows, offset, id))
        {
            RenderInteractive();
            _pointerUpdates++;
        }
    }

    private void RowWheelChanged(object? sender, PointerWheelEventArgs args)
    {
        args.Handled = true;
        if (_finished || _displaySession is null) return;
        CancelPointerSelection();
        var delta = _rowWheel.Consume(args.Delta.X, args.Delta.Y, args.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (delta.Lines != 0 || delta.Columns != 0) ScrollRows(delta.Lines, delta.Columns);
    }
}

/// <summary>Bounded source selection; native selection cannot represent disjoint rows.</summary>
internal sealed class RowProjectionRenderer(AvaloniaEdit.TextEditor editor) : IBackgroundRenderer
{
    private TextDocument? _document;
    private DocumentRowSelection? _selection;
    private DocumentRowProjection? _projection;
    private IReadOnlyList<TextRange> _matches = [];
    public KnownLayer Layer => KnownLayer.Selection;
    public long DrawnSpans { get; private set; }
    internal long DrawnMatches { get; private set; }

    public void Apply(TextDocument document, DocumentRowProjection rows, DocumentRowSelection selection)
    {
        if (document.TextLength != rows.Text.Length) throw new ArgumentException("行投影が表示文書と一致しません。");
        _document = document;
        _selection = selection;
        _projection = rows;
        editor.TextArea.TextView.InvalidateLayer(Layer);
    }

    public void Clear() { _document = null; _selection = null; _projection = null; _matches = []; }

    internal void SetMatches(IReadOnlyList<TextRange> matches)
    {
        _matches = matches;
        editor.TextArea.TextView.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext context)
    {
        if (!textView.VisualLinesValid || !ReferenceEquals(textView.Document, _document) || _selection is null) return;
        using var clip = context.PushClip(new Rect(textView.Bounds.Size));
        if (_matches.Count != 0 && _projection is { } projection)
        {
            foreach (var row in projection.Rows)
            {
                if (row.Length == 0) continue;
                var end = row.SourceOffset + row.Length;
                var index = InFileSearch.LowerBound(_matches, row.SourceOffset);
                if (index > 0 && _matches[index - 1].End > row.SourceOffset) index--;
                for (; index < _matches.Count && _matches[index].Offset <= end; index++)
                {
                    var match = _matches[index];
                    var start = Math.Max(row.SourceOffset, match.Offset);
                    var length = Math.Min(end, match.End) - start;
                    if (length <= 0 && match.Length != 0) continue;
                    var segment = new SimpleSegment(row.DisplayOffset + (int)(start - row.SourceOffset), (int)length);
                    foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                    {
                        context.FillRectangle(InFileMatchRenderer.Highlight, new Rect(rect.X, rect.Y, Math.Max(2, rect.Width), rect.Height));
                        DrawnMatches++;
                    }
                }
            }
        }
        foreach (var span in _selection.Spans)
        {
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new SimpleSegment(span.Offset, span.Length)))
            {
                context.DrawRectangle(editor.TextArea.SelectionBrush, null, rect);
                DrawnSpans++;
            }
        }
        if (_selection.CaretOffset.HasValue)
        {
            var rect = editor.TextArea.Caret.CalculateCaretRectangle();
            context.DrawRectangle(editor.Foreground, null, new Rect(rect.X - textView.HorizontalOffset,
                rect.Y - textView.VerticalOffset, Math.Max(1, rect.Width), rect.Height));
        }
    }
}

/// <summary>Clipping marks live outside text and never participate in source positions.</summary>
internal sealed class RowClippingMargin : AbstractMargin
{
    private static readonly Pen MarkPen = new(Brushes.DarkOrange, 2);
    private TextDocument? _document;
    private DocumentRowProjection? _rows;
    public long DrawnMarks { get; private set; }
    public void Apply(TextDocument document, DocumentRowProjection rows)
    { _document = document; _rows = rows; InvalidateVisual(); }
    public void Clear() { _document = null; _rows = null; InvalidateVisual(); }
    protected override Size MeasureOverride(Size availableSize) => new(12, 0);
    public override void Render(DrawingContext context)
    {
        if (TextView is not { VisualLinesValid: true } view || !ReferenceEquals(view.Document, _document) || _rows is null) return;
        foreach (var line in view.VisualLines)
        {
            var row = _rows.Rows[line.FirstDocumentLine.LineNumber - 1];
            var y = line.VisualTop - view.VerticalOffset + view.DefaultLineHeight / 2;
            if (row.IsGraphemeRangeUnavailable)
            {
                context.DrawLine(MarkPen, new Point(1, y - 3), new Point(7, y + 3));
                context.DrawLine(MarkPen, new Point(1, y + 3), new Point(7, y - 3));
                DrawnMarks++;
                continue;
            }
            if (row.LeftClipped)
            { context.DrawLine(MarkPen, new Point(1, y - 3), new Point(1, y + 3)); DrawnMarks++; }
            if (row.RightClipped)
            { context.DrawLine(MarkPen, new Point(7, y - 3), new Point(7, y + 3)); DrawnMarks++; }
        }
    }
}
