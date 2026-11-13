using Avalonia;
using Avalonia.Controls;
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
    private double _origin;

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

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_layout is not { } layout) return;
        foreach (var rect in _visible)
        {
            using var clip = context.PushClip(rect.Translate(new Vector(-_origin, 0)));
            layout.Draw(context, new Point(-_origin, 0));
        }
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

    public void Dispose()
    {
        _layout?.Dispose();
        _layout = null;
        _visible = [];
        _origin = 0;
        InvalidateMeasure();
        InvalidateVisual();
    }
}
