using Avalonia;

namespace CodeView;

internal static class Program
{
    private static readonly ProcessExitStatus ExitStatus = new();

    internal static void MarkFailed()
    {
        ExitStatus.MarkFailed();
        Environment.ExitCode = 1;
    }

    [STAThread]
    public static int Main(string[] args)
    {
#if CODEVIEW_PERFORMANCE_PROBE
        Presentation.GuiPerformanceProbe.Start();
        var builder = BuildAvaloniaApp();
        Presentation.GuiPerformanceProbe.Checkpoint("builder-configured");
        var result = builder.StartWithClassicDesktopLifetime(args);
#else
        var result = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
#endif
        return ExitStatus.Resolve(result != 0 ? result : Environment.ExitCode);
    }

    internal static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}

/// <summary>Retains a failure even if the UI lifetime subsequently resets Environment.ExitCode.</summary>
internal sealed class ProcessExitStatus
{
    private int _failed;
    public void MarkFailed() => Interlocked.Exchange(ref _failed, 1);
    public int Resolve(int lifetimeExitCode) => lifetimeExitCode != 0 ? lifetimeExitCode : Volatile.Read(ref _failed);
}
