namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private readonly bool _measuringStickyScroll = Environment.GetEnvironmentVariable("CODEVIEW_GUI_STICKY_MEASURE") == "1";
    private readonly List<string> _stickyScrollPhases = [];
    private double? _stickyStarted;
    private double? _stickyCompleted;
    private bool _stickyScopesFound;

    internal static void StickyAnalysisStarted()
    {
        if (_instance is { _measuringStickyScroll: true } probe) probe._stickyStarted = Startup.Elapsed.TotalMilliseconds;
    }

    internal static void StickyAnalysisCompleted(bool scopesFound)
    {
        if (_instance is not { _measuringStickyScroll: true } probe) return;
        probe._stickyCompleted = Startup.Elapsed.TotalMilliseconds;
        probe._stickyScopesFound = scopesFound;
    }
}
