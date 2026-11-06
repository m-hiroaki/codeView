namespace CodeView.Presentation;

/// <summary>Retains sub-unit trackpad deltas; bounded work per routed event.</summary>
internal sealed class RowWheelAccumulator
{
    private double _lines;
    private double _columns;

    public (long Lines, long Columns) Consume(double x, double y, bool shift)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return default;
        var horizontal = Math.Clamp(x, -256, 256) + (shift ? Math.Clamp(y, -256, 256) : 0);
        _lines += shift ? 0 : -Math.Clamp(y, -256, 256) * 3;
        _columns += -horizontal * 16;
        var lines = (long)Math.Clamp(Math.Truncate(_lines), -512, 512);
        var columns = (long)Math.Clamp(Math.Truncate(_columns), -4096, 4096);
        _lines = Math.Abs(_lines) >= 512 ? 0 : _lines - lines;
        _columns = Math.Abs(_columns) >= 4096 ? 0 : _columns - columns;
        return (lines, columns);
    }

    public void Reset() => (_lines, _columns) = (0, 0);
}
