using Avalonia;

namespace CodeView;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
#if CODEVIEW_PERFORMANCE_PROBE
        Presentation.GuiPerformanceProbe.Start();
#endif
        var result = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return result != 0 ? result : Environment.ExitCode;
    }

    internal static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
