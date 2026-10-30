using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using CodeView.Documents;
using CodeView.Tests.Performance;

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
    private bool _inputInterrupted;
    private readonly bool _windowed = Environment.GetEnvironmentVariable("CODEVIEW_GUI_WINDOWED") == "1";
    private readonly int _windowChars = TextWindow.ParseWindowChars(Environment.GetEnvironmentVariable("CODEVIEW_GUI_WINDOW_CHARS"));
    private TextDocument? _sourceDocument;
    private DocumentReadLease? _sourceLease;
    private int _largestDisplayChars;
    private readonly List<long> _sourceOffsets = [];
    private readonly ProcessMemory _memory = new();
    private readonly CancellationTokenSource _sampling = new();
    private readonly Task _sampler;
    private readonly long _memoryBefore;
    private long _peakMemory;
    private int _sampleCount;
    private readonly long _allocatedBefore = GC.GetTotalAllocatedBytes(true);
    private readonly TimeSpan _pauseBefore = GC.GetTotalPauseDuration();
    private readonly int[] _collectionsBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

    private GuiPerformanceProbe(Window window, TextEditor editor, string output, bool hasInput)
    {
        (_window, _editor, _output, _hasInput) = (window, editor, output, hasInput);
        _memoryBefore = _peakMemory = _memory.ReadBytes();
        _sampler = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    _peakMemory = Math.Max(_peakMemory, _memory.ReadBytes());
                    _sampleCount++;
                    await Task.Delay(10, _sampling.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_sampling.IsCancellationRequested) { }
        });
    }

    internal static void Start() => Startup.Start();

    internal static void Attach(Window window, TextEditor editor, bool hasInput)
    {
        var output = Environment.GetEnvironmentVariable("CODEVIEW_GUI_PERF_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;
        if (!Path.IsPathFullyQualified(output) || File.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output)))
            throw new ArgumentException("計測結果には未使用の絶対パスを指定してください。");
        var probe = _instance = new(window, editor, output, hasInput);
        // This workload injects no input. Abort rather than mixing an interactive
        // selection/scroll operation with automatic document replacement.
        void InterruptForInput(object? sender, RoutedEventArgs args)
        {
            args.Handled = true;
            if (probe._inputInterrupted) return;
            probe._inputInterrupted = true;
            Phase("physical-input-interrupted");
            Dispatcher.UIThread.Post(() => probe.Finish("physical-input-during-probe"));
        }
        window.AddHandler(InputElement.PointerPressedEvent, InterruptForInput, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerWheelChangedEvent, InterruptForInput, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyDownEvent, InterruptForInput, RoutingStrategies.Tunnel);
        window.Closing += (_, args) => Phase($"window-closing cancelled-at-probe-handler={args.Cancel}");
        window.Closed += (_, _) => Phase("window-closed");
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

    internal static TextDocument SelectDisplayDocument(DocumentHandle handle, TextDocument document)
    {
        if (_instance is not { _windowed: true } probe) return document;
        probe._sourceDocument = document;
        probe._sourceLease?.Dispose();
        probe._sourceLease = handle.AcquireReadView();
        return probe.BuildDisplayWindow(0).Document;
    }

    private (TextDocument Document, TextLocation Target) BuildDisplayWindow(long offset)
    {
        var window = TextWindow.Create(_sourceLease!, offset, _windowChars);
        var document = new TextDocument(window.Text);
        document.UndoStack.SizeLimit = 0;
        _largestDisplayChars = Math.Max(_largestDisplayChars, document.TextLength);
        _sourceOffsets.Add(window.TargetOffset);
        return (document, document.GetLocation(checked((int)(window.TargetOffset - window.SourceOffset))));
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
        if (_finished || _inputInterrupted) return;
        if (index == ScrollProbePlan.StepCount) { Finish(); return; }
        var document = _sourceDocument ?? _editor.Document;
        var (line, column) = ScrollProbePlan.Target(index, document.LineCount, document.GetLineByNumber(1).Length);
        Phase($"scroll-{index}");
        var start = Stopwatch.GetTimestamp();
        if (_windowed)
        {
            var display = BuildDisplayWindow(document.GetOffset(line, column));
            _editor.Document = display.Document;
            _editor.ScrollTo(display.Target.Line, display.Target.Column);
        }
        else _editor.ScrollTo(line, column);
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
        _sampling.Cancel();
        // The sampler uses only worker continuations, never the UI SynchronizationContext.
        _sampler.GetAwaiter().GetResult();
        _peakMemory = Math.Max(_peakMemory, _memory.ReadBytes());
        using var process = Process.GetCurrentProcess();
        var allocatedBytes = GC.GetTotalAllocatedBytes(true) - _allocatedBefore;
        var pauseMilliseconds = (GC.GetTotalPauseDuration() - _pauseBefore).TotalMilliseconds;
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) }
            .Select((count, index) => count - _collectionsBefore[index]).ToArray();
        var lastGc = GC.GetGCMemoryInfo();
        var report = new
        {
            FormatVersion = 2, MeasuredAtUtc = DateTimeOffset.UtcNow,
            Scenario = "native-gui-scroll-proxy", Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            Failure = failure, CacheCondition = Environment.GetEnvironmentVariable("CODEVIEW_PERF_CACHE") ?? "unknown",
            DisplayMode = _windowed ? "bounded-window-prototype" : "full-document-candidate",
            ConfiguredWindowChars = _windowed ? (int?)_windowChars : null,
            OpenedMilliseconds = _openedMilliseconds, WindowTwoTicksMilliseconds = _emptyWindowTwoTicksMilliseconds,
            DocumentAttachmentMilliseconds = _attachmentMilliseconds, DocumentTwoTicksMilliseconds = _documentTwoTicksMilliseconds,
            ScrollSynchronousMilliseconds = _scrollTimes, AnimationTickIntervalsMilliseconds = _tickIntervals,
            DocumentChars = _editor.Document?.TextLength, DocumentLines = _editor.Document?.LineCount,
            SourceChars = _sourceDocument?.TextLength, SourceLines = _sourceDocument?.LineCount,
            LargestDisplayChars = _largestDisplayChars, VisitedSourceOffsets = _sourceOffsets,
            MemoryMetric = _memory.Metric, MemoryBefore = _memoryBefore, SampledPeakMemory = _peakMemory,
            MemoryIncrease = _peakMemory - _memoryBefore, SamplingIntervalMilliseconds = 10, MemorySamples = _sampleCount,
            ClientWidth = _window.ClientSize.Width, ClientHeight = _window.ClientSize.Height,
            RenderScaling = _window.RenderScaling, PhysicalPaint = "not-measured", PhysicalInput = "not-measured",
            InputInterrupted = _inputInterrupted,
            CpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
            AllocatedBytes = allocatedBytes, GcCollections = collections, GcPauseTotalMilliseconds = pauseMilliseconds,
            GcMeasurementScope = "probe-construction-to-finish-before-report-serialization",
            LastGcLohBytes = lastGc.GenerationInfo.Length > 3 ? (long?)lastGc.GenerationInfo[3].SizeAfterBytes : null,
            LastGcIndex = lastGc.Index, IndividualGcPauses = "not-traced", LohAllocatedBytes = "not-traced"
        };
        using (var stream = new FileStream(_output, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });
        Phase("complete");
        _sourceLease?.Dispose();
        _sourceLease = null;
        _sourceDocument = null;
        _instance = null;
        _memory.Dispose();
        _sampling.Dispose();
        if (failure is not null) Environment.ExitCode = 1;
        // Finish can run in the animation callback. Leave that callback before
        // initiating the normal product shutdown sequence.
        Dispatcher.UIThread.Post(_window.Close);
    }

    private static void Phase(string phase) => Console.WriteLine($"GUI-PROBE {Startup.Elapsed.TotalMilliseconds:F3} {phase}");
}
