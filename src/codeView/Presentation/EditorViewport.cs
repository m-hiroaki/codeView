using Avalonia;

namespace CodeView.Presentation;

internal static class EditorViewport
{
    public static Vector Constrain(double horizontal, double vertical, Size extent, Size viewport)
    {
        return new(Clamp(horizontal, extent.Width, viewport.Width), Clamp(vertical, extent.Height, viewport.Height));

        static double Clamp(double requested, double length, double visible)
        {
            if (!double.IsFinite(requested) || requested < 0) throw new ArgumentOutOfRangeException(nameof(requested));
            if (!double.IsFinite(length) || !double.IsFinite(visible)) return 0;
            return Math.Clamp(requested, 0, Math.Max(0, length - visible));
        }
    }
}
