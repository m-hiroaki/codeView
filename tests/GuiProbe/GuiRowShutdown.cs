using System.Text.Json;
using CodeView.Documents;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private List<WeakReference<DocumentHandle>>? _rowSeenHandles;
    private readonly List<object> _rowAttachmentSamples = [];
    private static string? _rowShutdownPath;
    private static List<WeakReference<DocumentHandle>>? _rowShutdownHandles;
    private static bool _rowShutdownHistoryCleared;

    private void PrepareRowShutdown()
    {
        if (!_rowChecks) return;
        _rowShutdownPath = _output + ".shutdown.json";
        _rowShutdownHandles = _rowSeenHandles;
        _rowShutdownHistoryCleared = _rowHistory is null && _rowViewHandle is null;
        _rowSeenHandles = null;
    }

    internal static void ConfirmRowShutdown(MainWindowViewModel view)
    {
        if (_rowShutdownPath is not { } path) return;
        _rowShutdownPath = null;
        var live = 0; var unreleased = 0; var leases = 0;
        foreach (var weak in _rowShutdownHandles ?? [])
        {
            if (!weak.TryGetTarget(out var handle)) continue;
            live++;
            if (!handle.IsReleased) unreleased++;
            leases += handle.ActiveReadLeases;
        }
        _rowShutdownHandles = null;
        var passed = _instance is null && _rowShutdownHistoryCleared && !view.HasDocument && view.Tabs.Count == 0 &&
            view.PendingWork.IsCompletedSuccessfully && _retiredCopyWork.IsCompletedSuccessfully && unreleased == 0 && leases == 0;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(stream, new { Passed = passed, HistoryCleared = _rowShutdownHistoryCleared,
                ViewHasDocument = view.HasDocument, Tabs = view.Tabs.Count, PendingViewCompleted = view.PendingWork.IsCompletedSuccessfully,
                PendingCopyCompleted = _retiredCopyWork.IsCompletedSuccessfully, SurvivingWeakHandles = live,
                UnreleasedHandles = unreleased, ActiveLeases = leases,
                Scope = "After real ViewModel.Stop and StopAndWait, before final native window close." });
        }
        catch (Exception error) { Console.Error.WriteLine(error); passed = false; }
        if (!passed) Program.MarkFailed();
    }
}
