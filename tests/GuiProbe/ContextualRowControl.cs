using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
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
    private double _origin;

    internal ReadOnlySpan<Rect> SelectionBounds => _selection;
    internal Point? CaretPosition => _caret;
    internal event Func<long, bool, bool>? SourceSelectionRequested;

    public void SetRow(string text, DocumentRowContext range, Typeface typeface, double size, IBrush? foreground)
    {
        Dispose();
        if (text.Length != range.Length || text.Length > DocumentDisplaySession.MaximumSliceChars
            || range.VisibleOffset < 0 || range.VisibleOffset > text.Length
            || range.VisibleLength < 0 || range.VisibleLength > text.Length - range.VisibleOffset)
            throw new ArgumentException("描画文脈の長さが一致しません。", nameof(text));

        var layout = new TextLayout(text, typeface, size, foreground, flowDirection: FlowDirection);
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
            if (caret >= visibleStart && caret <= visibleEnd)
            {
                var position = layout.HitTestTextPosition((int)(caret - _range.SourceOffset)).Position;
                if (_visible.Any(rect => position.X >= rect.Left && position.X <= rect.Right))
                    _caret = position + shift;
            }
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

    internal bool TryHitSource(Point point, out long offset)
    {
        offset = 0;
        var local = new Point(point.X + _origin, point.Y);
        if (_layout is not { } layout || !_visible.Any(rect => rect.Contains(local))) return false;
        var position = layout.HitTestPoint(local).TextPosition;
        if (position < _range.VisibleOffset || position > _range.VisibleOffset + _range.VisibleLength) return false;
        offset = _range.SourceOffset + position;
        return true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || _dragPointer is not null
            || e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        if (TryHitSource(e.GetPosition(this), out var offset)
            && SourceSelectionRequested?.Invoke(offset, e.KeyModifiers.HasFlag(KeyModifiers.Shift)) == true)
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
        try { if (!e.Handled) ExtendDrag(e); }
        finally { EndDrag(); }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(_dragPointer, e.Pointer)) _dragPointer = null;
        base.OnPointerCaptureLost(e);
    }

    private void ExtendDrag(PointerEventArgs e)
    {
        if (TryHitSource(e.GetPosition(this), out var offset)
            && SourceSelectionRequested?.Invoke(offset, true) != true)
            EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        var pointer = _dragPointer;
        _dragPointer = null;
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
}
