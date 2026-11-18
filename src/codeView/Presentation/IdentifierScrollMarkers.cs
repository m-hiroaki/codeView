using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using CodeView.Contracts;

namespace CodeView.Presentation;

internal sealed class IdentifierScrollMarkers : Control
{
    private const int BucketCount = 256;
    private static readonly IBrush Marker = new SolidColorBrush(Color.FromRgb(145, 170, 255));
    private bool[]? _buckets;
    private TextEditor? _editor;
    private Track? _track;
    private Rect? _trackBounds;
    private int _processed;
#if CODEVIEW_PERFORMANCE_PROBE
    internal Rect? TrackBounds => _trackBounds;
    internal bool HasMarkerAt(int line, int totalLines) => _buckets?[BucketForLine(line, totalLines)] == true;
#endif

    internal void Update(TextEditor editor, TextDocument? document, IReadOnlyList<TextRange> matches)
    {
        if (matches.Count == 0 || document is null || document.LineCount <= 1)
        {
            if (_editor is null) return;
            LayoutUpdated -= UpdateTrackBounds;
            _editor = null;
            _track = null;
            _trackBounds = null;
            _processed = 0;
            Array.Clear(_buckets!);
            InvalidateVisual();
            return;
        }
        if (_editor is null)
        {
            _editor = editor;
            _buckets ??= new bool[BucketCount];
            LayoutUpdated += UpdateTrackBounds;
        }
        DocumentLine? line = null;
        for (; _processed < matches.Count; _processed++)
        {
            var offset = checked((int)matches[_processed].Offset);
            if (line is null || offset > line.EndOffset) line = document.GetLineByOffset(offset);
            _buckets![BucketForLine(line.LineNumber, document.LineCount)] = true;
        }
        UpdateTrackBounds(this, EventArgs.Empty);
        InvalidateVisual();
    }

    internal static int BucketForLine(int line, int lines) =>
        lines <= 1 ? 0 : (int)((long)(line - 1) * (BucketCount - 1) / (lines - 1));

    private void UpdateTrackBounds(object? sender, EventArgs args)
    {
        if (_editor is null) return;
        if (_track is null || TopLevel.GetTopLevel(_track) is null)
            _track = _editor.GetVisualDescendants().OfType<ScrollBar>()
                .FirstOrDefault(bar => bar.Orientation == Orientation.Vertical)?
                .GetVisualDescendants().OfType<Track>().FirstOrDefault();
        Rect? bounds = _track is { IsEffectivelyVisible: true, Bounds.Height: > 0 } track
            && track.TranslatePoint(default, this) is { } origin ? new Rect(origin, track.Bounds.Size) : null;
        if (_trackBounds == bounds) return;
        _trackBounds = bounds;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_trackBounds is not { Height: >= 2, Width: >= 2 } track || _buckets is null) return;
        using var clip = context.PushClip(track);
        var previous = double.NegativeInfinity;
        for (var index = 0; index < BucketCount; index++)
        {
            if (!_buckets[index]) continue;
            var y = track.Y + index * (track.Height - 2) / (BucketCount - 1);
            if (y - previous < 2 && index != BucketCount - 1) continue;
            context.DrawRectangle(Marker, null, new Rect(track.X, y, 2, 2));
            previous = y;
        }
    }
}
