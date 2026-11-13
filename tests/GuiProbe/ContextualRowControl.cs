using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using CodeView.Documents;

namespace CodeView.Presentation;

/// <summary>Single-row comparison control. The caller owns its layout lifetime.</summary>
internal sealed class ContextualRowControl : Control, IDisposable
{
    private TextLayout? _layout;
    private DocumentRowContext _range;
    private Rect[] _visible = [];
    private Rect[] _selection = [];
    private Point? _caret;
    private IPointer? _dragPointer;
    private DispatcherTimer? _dragTimer;
    private Point _dragPoint;
    private double _origin;

    internal ReadOnlySpan<Rect> SelectionBounds => _selection;
    internal Point? CaretPosition => _caret;
    internal event Func<Point, long, bool, bool>? SourceSelectionRequested;
    internal Func<Point, ContextualRowControl?>? FindDragRow { get; set; }
    internal Func<Point, bool>? ScrollDragViewport { get; set; }

    public void SetRow(string text, DocumentRowContext range, Typeface typeface, double size, IBrush? foreground, double tabWidth)
    {
        Dispose();
        if (text.Length != range.Length || text.Length > DocumentDisplaySession.MaximumSliceChars
            || range.VisibleOffset < 0 || range.VisibleOffset > text.Length
            || range.VisibleLength < 0 || range.VisibleLength > text.Length - range.VisibleOffset)
            throw new ArgumentException("描画文脈の長さが一致しません。", nameof(text));

        var run = new GenericTextRunProperties(typeface, size, foregroundBrush: foreground);
        var paragraph = new RowParagraph(run, FlowDirection, tabWidth);
        var layout = new TextLayout(new RowTextSource(text, run), paragraph);
        try
        {
            _visible = layout.HitTestTextRange(range.VisibleOffset, range.VisibleLength)
                .Where(rect => rect.Width > 0).ToArray();
            _origin = _visible.Length > 0 ? _visible.Min(rect => rect.Left) : 0;
            _range = range;
            _layout = layout;
        }
        catch
        {
            layout.Dispose();
            throw;
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(_visible.Length == 0 ? 0 : _visible.Max(rect => rect.Right) - _origin, _layout?.Height ?? 0);

    internal void SetSelection(long anchor, long caret)
    {
        _selection = [];
        _caret = null;
        if (_layout is { } layout && _visible.Length > 0)
        {
            var visibleStart = _range.SourceOffset + _range.VisibleOffset;
            var visibleEnd = visibleStart + _range.VisibleLength;
            var first = Math.Max(visibleStart, Math.Min(anchor, caret));
            var last = Math.Min(visibleEnd, Math.Max(anchor, caret));
            var shift = new Vector(-_origin, 0);
            if (last > first)
            {
                _selection = layout.HitTestTextRange((int)(first - _range.SourceOffset), (int)(last - first))
                    .SelectMany(rect => _visible.Select(clip => rect.Intersect(clip)))
                    .Where(rect => rect.Width > 0 && rect.Height > 0)
                    .Select(rect => rect.Translate(shift)).ToArray();
            }
            _caret = GetCaretRectangle(caret)?.Position;
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_layout is not { } layout) return;
        foreach (var rect in _selection) context.FillRectangle(Brushes.DarkSlateBlue, rect);
        foreach (var rect in _visible)
        {
            using var clip = context.PushClip(rect.Translate(new Vector(-_origin, 0)));
            layout.Draw(context, new Point(-_origin, 0));
        }
        if (_caret is { } caret)
            context.DrawLine(new Pen(Brushes.Gold), caret, caret + new Vector(0, layout.Height));
    }

    internal bool TryHitSource(Point point, out long offset, bool clamp = false)
    {
        offset = 0;
        var local = new Point(point.X + _origin, point.Y);
        if (_layout is not { } layout || _visible.Length == 0) return false;
        if (!_visible.Any(rect => rect.Contains(local)))
        {
            if (!clamp) return false;
            local = _visible.Select(rect => new Point(
                    Math.Clamp(local.X, rect.Left, rect.Right),
                    Math.Clamp(local.Y, rect.Top, rect.Bottom)))
                .MinBy(candidate => ((Vector)candidate - (Vector)local).SquaredLength);
        }
        var position = layout.HitTestPoint(local).TextPosition;
        if (position < _range.VisibleOffset || position > _range.VisibleOffset + _range.VisibleLength) return false;
        offset = _range.SourceOffset + position;
        return true;
    }

    internal long NearestBoundary(Point point, long before, long after)
    {
        if (_layout is not { } layout || before == after) return before;
        var x = point.X + _origin;
        var leading = layout.HitTestTextPosition((int)(before - _range.SourceOffset)).X;
        var trailing = layout.HitTestTextPosition((int)(after - _range.SourceOffset)).X;
        return Math.Abs(x - leading) <= Math.Abs(x - trailing) ? before : after;
    }

    internal double? GetSourceX(long offset)
    {
        var local = offset - _range.SourceOffset;
        return _layout is { } layout && local >= _range.VisibleOffset && local <= _range.VisibleOffset + _range.VisibleLength
            ? layout.HitTestTextPosition((int)local).X - _origin : null;
    }

    internal Rect? GetCaretRectangle(long offset)
    {
        var local = offset - _range.SourceOffset;
        if (_layout is not { } layout || local < _range.VisibleOffset || local > _range.VisibleOffset + _range.VisibleLength)
            return null;
        var position = layout.HitTestTextPosition((int)local).Position;
        return _visible.Any(rect => position.X >= rect.Left && position.X <= rect.Right)
            ? new Rect(position.X - _origin, position.Y, 1, layout.Height) : null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || _dragPointer is not null
            || e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        var point = e.GetPosition(this);
        if (TryHitSource(point, out var offset)
            && SourceSelectionRequested?.Invoke(point, offset, e.KeyModifiers.HasFlag(KeyModifiers.Shift)) == true)
        {
            e.Pointer.Capture(this);
            if (e.Pointer.Captured == this) _dragPointer = e.Pointer;
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Handled || !ReferenceEquals(_dragPointer, e.Pointer)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndDrag();
            return;
        }
        ExtendDrag(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!ReferenceEquals(_dragPointer, e.Pointer) || e.InitialPressMouseButton != MouseButton.Left) return;
        try { if (!e.Handled) ExtendDrag(e, allowScroll: false); }
        finally { EndDrag(); }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(_dragPointer, e.Pointer)) EndDrag();
        base.OnPointerCaptureLost(e);
    }

    private void ExtendDrag(PointerEventArgs e, bool allowScroll = true)
    {
        var point = e.GetPosition(this);
        var row = FindDragRow is { } find ? find(point) : this;
        if (row is not null)
        {
            _dragTimer?.Stop();
            if (!TryExtendTo(row, point)) EndDrag();
        }
        else if (allowScroll && ScrollDragViewport is not null)
        {
            // Keep the pointer in root coordinates while scrolling moves this row.
            if (VisualRoot is Visual root && this.TranslatePoint(point, root) is { } rootPoint)
            {
                _dragPoint = rootPoint;
                if (_dragTimer is null)
                {
                    _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                    _dragTimer.Tick += (_, _) => ScrollDrag();
                }
                _dragTimer.Start();
            }
        }
        e.Handled = true;
    }

    private void ScrollDrag()
    {
        if (_dragPointer?.Captured != this || VisualRoot is not Visual root
            || root.TranslatePoint(_dragPoint, this) is not { } point
            || ScrollDragViewport?.Invoke(point) != true)
            _dragTimer?.Stop();
    }

    private bool TryExtendTo(ContextualRowControl row, Point point)
    {
        var translated = ReferenceEquals(row, this) ? point : this.TranslatePoint(point, row);
        return translated is not { } local || !row.TryHitSource(local, out var offset)
            || row.SourceSelectionRequested?.Invoke(local, offset, true) == true;
    }

    internal bool TryExtendAt(Point point) => TryHitSource(point, out var offset)
        && SourceSelectionRequested?.Invoke(point, offset, true) == true;

    private void EndDrag()
    {
        var pointer = _dragPointer;
        _dragPointer = null;
        _dragTimer?.Stop();
        if (pointer?.Captured == this) pointer.Capture(null);
    }

    public void Dispose()
    {
        EndDrag();
        _layout?.Dispose();
        _layout = null;
        _visible = [];
        _selection = [];
        _caret = null;
        _origin = 0;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private sealed class RowParagraph(TextRunProperties run, FlowDirection direction, double tabWidth) : TextParagraphProperties
    {
        public override TextRunProperties DefaultTextRunProperties => run;
        public override FlowDirection FlowDirection => direction;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override TextWrapping TextWrapping => TextWrapping.NoWrap;
        public override double DefaultIncrementalTab => tabWidth;
        public override double LineHeight => double.NaN;
        public override bool FirstLineInParagraph => true;
        public override double Indent => 0;
    }

    private sealed class RowTextSource(string text, TextRunProperties run) : ITextSource
    {
        public TextRun? GetTextRun(int textSourceIndex) =>
            textSourceIndex < text.Length ? new TextCharacters(text.AsMemory(textSourceIndex), run) : new TextEndOfParagraph(1);
    }
}
