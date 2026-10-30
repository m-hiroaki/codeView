using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit;

namespace CodeView.Presentation;

/// <summary>Compiled only into an explicitly requested local performance build.</summary>
internal sealed class GuiPerformanceProbe
{
    private static readonly Stopwatch Startup = new();
    private static GuiPerformanceProbe? _instance;
    private readonly Window _window;
    private readonly TextEditor _editor;
    private readonly string _output;
    private readonly bool _hasInput;
    private readonly List<double> _scrollTimes = [];
    private readonly List<double> _tickIntervals = [];
    private long _attachmentStart;
    private double? _attachmentMilliseconds;
    private double? _openedMilliseconds;
    private double? _emptyWindowTwoTicksMilliseconds;
    private double? _documentTwoTicksMilliseconds;
    private long _previousTick;
    private bool _finished;

    private GuiPerformanceProbe(Window window, TextEditor editor, string output, bool hasInput)
        => (_window, _editor, _output, _hasInput) = (window, editor, output, hasInput);

    internal static void Start() => Startup.Start();

    internal static void Attach(Window window, TextEditor editor, bool hasInput)
    {
        var output = Environment.GetEnvironmentVariable("CODEVIEW_GUI_PERF_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;
        if (!Path.IsPathFullyQualified(output) || File.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output)))
            throw new ArgumentException("計測結果には未使用の絶対パスを指定してください。");
        var probe = _instance = new(window, editor, output, hasInput);
        window.Opened += (_, _) =>
        {
            probe._openedMilliseconds = Startup.Elapsed.TotalMilliseconds;
            Phase("window-opened");
            window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ =>
            {
                probe._emptyWindowTwoTicksMilliseconds = Startup.Elapsed.TotalMilliseconds;
                if (!hasInput) probe.Finish();
            }));
        };
    }

    internal static void InitializationFinished(bool hasDocument)
    {
        if (_instance is { _hasInput: true } probe && !hasDocument) probe.Finish("open-failed");
    }

    internal static void BeginDocumentAttachment()
    {
        if (_instance is not { } probe) return;
        Phase("attachment-start");
        probe._attachmentStart = Stopwatch.GetTimestamp();
    }

    internal static void EndDocumentAttachment()
    {
        if (_instance is not { } probe) return;
        probe._attachmentMilliseconds = Stopwatch.GetElapsedTime(probe._attachmentStart).TotalMilliseconds;
        Phase("attachment-end");
        probe._window.RequestAnimationFrame(_ => probe._window.RequestAnimationFrame(_ =>
        {
            probe._documentTwoTicksMilliseconds = Startup.Elapsed.TotalMilliseconds;
            probe._previousTick = Stopwatch.GetTimestamp();
            probe.ScrollStep(0);
        }));
    }

    private void ScrollStep(int index)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_finished) return;
        if (index == ScrollProbePlan.StepCount) { Finish(); return; }
        var document = _editor.Document;
        var (line, column) = ScrollProbePlan.Target(index, document.LineCount, document.GetLineByNumber(1).Length);
        Phase($"scroll-{index}");
        var start = Stopwatch.GetTimestamp();
        _editor.ScrollTo(line, column);
        _editor.TextArea.TextView.EnsureVisualLines();
        _scrollTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        _window.RequestAnimationFrame(_ =>
        {
            var now = Stopwatch.GetTimestamp();
            _tickIntervals.Add(Stopwatch.GetElapsedTime(_previousTick, now).TotalMilliseconds);
            _previousTick = now;
            ScrollStep(index + 1);
        });
    }

    private void Finish(string? failure = null)
    {
        if (_finished) return;
        _finished = true;
        using var process = Process.GetCurrentProcess();
        var report = new
        {
            FormatVersion = 1, MeasuredAtUtc = DateTimeOffset.UtcNow,
            Scenario = "native-gui-scroll-proxy", Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            Failure = failure, CacheCondition = Environment.GetEnvironmentVariable("CODEVIEW_PERF_CACHE") ?? "unknown",
            OpenedMilliseconds = _openedMilliseconds, WindowTwoTicksMilliseconds = _emptyWindowTwoTicksMilliseconds,
            DocumentAttachmentMilliseconds = _attachmentMilliseconds, DocumentTwoTicksMilliseconds = _documentTwoTicksMilliseconds,
            ScrollSynchronousMilliseconds = _scrollTimes, AnimationTickIntervalsMilliseconds = _tickIntervals,
            DocumentChars = _editor.Document?.TextLength, DocumentLines = _editor.Document?.LineCount,
            ClientWidth = _window.ClientSize.Width, ClientHeight = _window.ClientSize.Height,
            RenderScaling = _window.RenderScaling, PhysicalPaint = "not-measured", PhysicalInput = "not-measured",
            CpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds
        };
        using (var stream = new FileStream(_output, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });
        Phase("complete");
        if (failure is not null) Environment.ExitCode = 1;
        _window.Close();
    }

    private static void Phase(string phase) => Console.WriteLine($"GUI-PROBE {Startup.Elapsed.TotalMilliseconds:F3} {phase}");
}
