using System.Runtime.InteropServices;

namespace CodeView.Presentation;

internal static class ChromeWin32
{
    private const uint WmDpiChanged = 0x02E0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, ref Rect lParam);

    internal static void ChangeDpi(nint hwnd, uint dpi, ref Rect rect)
        => SendMessage(hwnd, WmDpiChanged, (dpi << 16) | dpi, ref rect);
}
