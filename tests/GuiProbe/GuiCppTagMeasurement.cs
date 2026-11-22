using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CodeView.Operations;
using CodeView.Tests.Performance;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private async Task MeasureCppTagsAsync(string mode, CancellationToken ct)
    {
        await Frame(ct);
        await Frame(ct);
        var firstDisplay = Startup.Elapsed.TotalMilliseconds;
        using var process = Process.GetCurrentProcess();
        var io = new ProcessIo();
        var prepStarted = Stopwatch.GetTimestamp();
        var prepCpu = process.TotalProcessorTime;
        var prepIo = io.Read();
        var setup = _viewModel.PendingWork;
        if (mode is "create" or "update")
        {
            _window.FindControl<MenuItem>("UpdateCppTagsMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            setup = _viewModel.PendingWork;
        }
        else if (mode is not ("simple" or "reopen")) throw new ArgumentException("Invalid tag measurement scenario.");

        var preparation = MeasurePreparationAsync();
        var scrollDuringSetup = 0;
        for (var step = 0; step < ScrollProbePlan.StepCount; step++)
        {
            var start = Stopwatch.GetTimestamp();
            var running = !setup.IsCompleted;
            var (line, column) = ScrollProbePlan.Target(step, _editor.Document.LineCount, _editor.Document.GetLineByNumber(1).Length);
            _editor.ScrollTo(line, column);
            _editor.TextArea.TextView.EnsureVisualLines();
            await Frame(ct);
            _tickIntervals.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (running) scrollDuringSetup++;
        }
        var prepared = await preparation;

        var origin = _viewModel.FilePath;
        var name = "IndexProbeTarget";
        var offset = _editor.Document.Text.IndexOf(name, StringComparison.Ordinal);
        if (offset < 0) throw new InvalidOperationException("Tag measurement fixture has no call site.");
        var latencies = new List<double>();
        string? target = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            await _viewModel.FindDefinitionsAsync(offset, offset, 0).WaitAsync(ct);
            await Frame(ct);
            latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var expectingTags = mode != "simple";
            if (_viewModel.SearchPanel.Description.Contains("定義タグ", StringComparison.Ordinal) != expectingTags
                || _viewModel.SearchPanel.Rows.Count != 1 || _viewModel.SearchPanel.IsVisible
                || Path.GetFileName(_viewModel.FilePath) != "target.cpp")
                throw new InvalidOperationException("Unexpected definition search result: " + _viewModel.SearchPanel.Summary);
            target = _viewModel.FilePath;
            await _viewModel.OpenFilesAsync([origin], OpenOrigin.Menu).WaitAsync(ct);
            await Frame(ct);
        }
        _referenceResults = new
        {
            Passed = true, Mode = mode, FirstDisplayMilliseconds = firstDisplay,
            PreparationMilliseconds = prepared.Elapsed, PreparationCpuMilliseconds = prepared.Cpu,
            PreparationIo = prepared.Io, ScrollDuringSetup = scrollDuringSetup,
            F12Milliseconds = latencies, Target = target
        };

        async Task<(double Elapsed, double Cpu, ProcessIoSample Io)> MeasurePreparationAsync()
        {
            await setup.WaitAsync(ct);
            process.Refresh();
            return (Stopwatch.GetElapsedTime(prepStarted).TotalMilliseconds,
                (process.TotalProcessorTime - prepCpu).TotalMilliseconds, ProcessIo.Difference(prepIo, io.Read()));
        }
    }
}
