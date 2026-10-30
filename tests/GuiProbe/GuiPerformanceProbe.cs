using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using CodeView.Documents;
using CodeView.Contracts;
using CodeView.Operations;
using CodeView.Tests.Performance;

namespace CodeView.Presentation;

/// <summary>Compiled only into an explicitly requested local performance build.</summary>
internal sealed class GuiPerformanceProbe
{
    private static readonly Stopwatch Startup = new();
    private static GuiPerformanceProbe? _instance;
    private static Task _retiredCopyWork = Task.CompletedTask;
    private readonly Window _window;
    private readonly TextEditor _editor;
    private readonly MainWindowViewModel _viewModel;
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
    private readonly bool _selectionProbe = Environment.GetEnvironmentVariable("CODEVIEW_GUI_SELECTION") == "1";
    private readonly bool _holdAfterReport = Environment.GetEnvironmentVariable("CODEVIEW_GUI_HOLD_AFTER_REPORT") == "1";
    private readonly bool _interactive = Environment.GetEnvironmentVariable("CODEVIEW_GUI_INTERACTIVE") == "1";
    private DocumentDisplaySlice? _interactiveSlice;
    private bool _interactiveAttached;
    private bool _renderingInteractive;
    private bool _interactiveScrollPosted;
    private int _documentGeneration;
    private long _interactiveCommands;
    private long _interactiveScrolls;
    private double _interactiveScrollMaxMilliseconds;
    private double _lastInteractiveHorizontal;
    private double _lastInteractiveVertical;
    private readonly List<object> _interactiveScrollSamples = [];
    private ClipboardCopyCoordinator? _copies;
    private ClipboardCopyRequest? _copyUiRequest;
    private long _copyUiGeneration;
    private bool _copyObserverRunning;
    private Task _copyObserver = Task.CompletedTask;
    private long _copiesCompleted, _clipboardVerificationChecks, _clipboardVerificationFailures;
    private long _lastCopiedCharacters;
    private readonly bool _verifyClipboard = Environment.GetEnvironmentVariable("CODEVIEW_GUI_VERIFY_CLIPBOARD") == "1";
    private readonly int _windowChars = TextWindow.ParseWindowChars(Environment.GetEnvironmentVariable("CODEVIEW_GUI_WINDOW_CHARS"));
    private TextDocument? _sourceDocument;
    private DocumentDisplaySession? _displaySession;
    private int _largestDisplayChars;
    private readonly List<long> _sourceOffsets = [];
    private readonly List<object> _selectionSamples = [];
    private readonly ProcessMemory _memory = new();
    private readonly CancellationTokenSource _sampling = new();
    private readonly Task _sampler;
    private readonly long _memoryBefore;
    private long _peakMemory;
    private int _sampleCount;
    private readonly long _allocatedBefore = GC.GetTotalAllocatedBytes(true);
    private readonly TimeSpan _pauseBefore = GC.GetTotalPauseDuration();
    private readonly int[] _collectionsBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

    private GuiPerformanceProbe(Window window, TextEditor editor, MainWindowViewModel viewModel, string output, bool hasInput)
    {
        (_window, _editor, _output, _hasInput) = (window, editor, output, hasInput);
        _viewModel = viewModel;
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

    internal static void Attach(Window window, TextEditor editor, MainWindowViewModel viewModel, bool hasInput)
    {
        var output = Environment.GetEnvironmentVariable("CODEVIEW_GUI_PERF_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;
        if (!Path.IsPathFullyQualified(output) || File.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output)))
            throw new ArgumentException("計測結果には未使用の絶対パスを指定してください。");
        if (Environment.GetEnvironmentVariable("CODEVIEW_GUI_SELECTION") == "1" &&
            Environment.GetEnvironmentVariable("CODEVIEW_GUI_WINDOWED") != "1")
            throw new ArgumentException("選択表示の試験には有界表示を指定してください。");
        if (Environment.GetEnvironmentVariable("CODEVIEW_GUI_INTERACTIVE") == "1" &&
            (Environment.GetEnvironmentVariable("CODEVIEW_GUI_WINDOWED") != "1" || !hasInput))
            throw new ArgumentException("対話試作には有界表示と入力ファイルを指定してください。");
        var probe = _instance = new(window, editor, viewModel, output, hasInput);
        // This workload injects no input. Abort rather than mixing an interactive
        // selection/scroll operation with automatic document replacement.
        void InterruptForInput(object? sender, RoutedEventArgs args)
        {
            if (probe._finished || probe._interactive) return;
            args.Handled = true;
            if (probe._inputInterrupted) return;
            probe._inputInterrupted = true;
            Phase($"physical-input-interrupted event={args.RoutedEvent?.Name}");
            Dispatcher.UIThread.Post(() => probe.Finish("physical-input-during-probe"));
        }
        window.AddHandler(InputElement.PointerPressedEvent, InterruptForInput, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerWheelChangedEvent, InterruptForInput, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyDownEvent, InterruptForInput, RoutingStrategies.Tunnel);
        window.Closing += (_, args) =>
        {
            Phase($"window-closing cancelled-at-probe-handler={args.Cancel}");
            if (probe._interactive && !probe._finished) probe.Finish();
        };
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

    internal static TextDocument SelectDisplayDocument(DocumentHandle handle, TextDocument document, TextPosition position)
    {
        if (_instance is not { _windowed: true } probe) return document;
        probe._sourceDocument = document;
        probe.InvalidateCopies();
        probe.DetachInteractive();
        probe._displaySession?.Dispose();
        probe._displaySession = new DocumentDisplaySession(handle);
        probe._interactiveSlice = null;
        probe._documentGeneration++;
        if (probe._interactive)
        {
            probe._displaySession.MoveToPosition(position);
            probe._interactiveSlice = probe._displaySession.CreateSlice(probe._displaySession.Caret, probe._windowChars);
            return CreateDisplayDocument(probe._interactiveSlice.Text);
        }
        return probe.BuildDisplayWindow(0).Document;
    }

    internal static bool TryGetOriginalPosition(out TextPosition position, out TextDocument source)
    {
        if (_instance is { _interactive: true, _displaySession: { } session, _sourceDocument: { } document })
        { position = session.Position; source = document; return true; }
        position = default;
        source = null!;
        return false;
    }

    internal static bool HasInteractiveDocument => _instance is { _interactive: true, _displaySession: not null };
    internal static Task PendingCopyWork => _instance is { _copies: { } copies } probe
        ? Task.WhenAll(copies.PendingWork, probe._copyObserver) : _retiredCopyWork;

    internal static bool TryRestoreOriginal(int line, int column)
    {
        if (_instance is not { _interactive: true, _displaySession: { } session } probe) return false;
        session.MoveToPosition(new(line, column));
        probe._interactiveSlice = null;
        probe.RenderInteractive();
        return true;
    }

    internal static bool TrySelectOriginal(long offset, int length, bool clear = false)
    {
        if (_instance is not { _interactive: true, _displaySession: { } session } probe) return false;
        if (clear) session.MoveCaret(session.Caret);
        else session.Select(checked(offset + length), offset);
        probe.RenderInteractive();
        return true;
    }

    internal static void ClearDocument()
    {
        if (_instance is not { _interactive: true } probe) return;
        probe.InvalidateCopies();
        probe.DetachInteractive();
        probe._displaySession?.Dispose();
        probe._displaySession = null;
        probe._sourceDocument = null;
        probe._interactiveSlice = null;
        probe._editor.Document = null!;
        probe._documentGeneration++;
    }

    private static TextDocument CreateDisplayDocument(string text)
    {
        var document = new TextDocument(text);
        document.UndoStack.SizeLimit = 0;
        return document;
    }

    private void RenderInteractive()
    {
        if (_renderingInteractive || _displaySession is null || _finished) return;
        _renderingInteractive = true;
        try
        {
            var source = _displaySession;
            var slice = _interactiveSlice;
            var margin = _windowChars / 4;
            if (slice is null || source.Caret < slice.SourceOffset || source.Caret > slice.SourceOffset + slice.Text.Length ||
                (slice.SourceOffset > 0 && source.Caret < slice.SourceOffset + margin) ||
                (slice.SourceOffset + slice.Text.Length < source.Length && source.Caret > slice.SourceOffset + slice.Text.Length - margin))
            {
                slice = _interactiveSlice = source.CreateSlice(source.Caret, _windowChars);
                _editor.Document = CreateDisplayDocument(slice.Text);
            }
            if (!source.TryGetDisplaySelection(slice, out var visible) || visible.CaretOffset is not { } caret)
                throw new InvalidOperationException("原文位置を表示範囲へ変換できません。");
            _editor.Select(visible.Start, visible.Length);
            _editor.CaretOffset = caret;
            QueueInteractiveScroll();
            _largestDisplayChars = Math.Max(_largestDisplayChars, slice.Text.Length);
            _viewModel.SetPosition(1, 1); // The probe translates this notification to the original position.
        }
        finally { _renderingInteractive = false; }
    }

    private void QueueInteractiveScroll()
    {
        if (_interactiveScrollPosted) return;
        _interactiveScrollPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _interactiveScrollPosted = false;
            // Read only the latest session/slice. Never capture a retired source or caret.
            if (_finished || _displaySession is not { } source || _interactiveSlice is not { } slice ||
                !source.TryGetDisplaySelection(slice, out var visible) || visible.CaretOffset is not { } caret) return;
            var start = Stopwatch.GetTimestamp();
            _editor.TextArea.TextView.EnsureVisualLines();
            _editor.TextArea.Caret.BringCaretToView();
            _interactiveScrolls++;
            _interactiveScrollMaxMilliseconds = Math.Max(_interactiveScrollMaxMilliseconds, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            _lastInteractiveHorizontal = _editor.TextArea.TextView.HorizontalOffset;
            _lastInteractiveVertical = _editor.TextArea.TextView.VerticalOffset;
            if (_interactiveScrollSamples.Count < 256)
            {
                var rectangle = _editor.TextArea.Caret.CalculateCaretRectangle();
                var view = (Avalonia.Controls.Primitives.IScrollable)_editor.TextArea.TextView;
                _interactiveScrollSamples.Add(new
                {
                    SourcePosition = source.Position, SourceCaret = source.Caret,
                    Horizontal = view.Offset.X, Vertical = view.Offset.Y, Viewport = view.Viewport,
                    CaretRectangle = rectangle,
                    CaretVisible = rectangle.Left >= view.Offset.X - 1 && rectangle.Right <= view.Offset.X + view.Viewport.Width + 1 &&
                        rectangle.Top >= view.Offset.Y - 1 && rectangle.Bottom <= view.Offset.Y + view.Viewport.Height + 1
                });
            }
        }, DispatcherPriority.Loaded);
    }

    private void AttachInteractive()
    {
        if (_interactiveAttached) return;
        _interactiveAttached = true;
        _editor.AddHandler(InputElement.KeyDownEvent, InteractiveKeyDown, RoutingStrategies.Tunnel);
        _editor.AddHandler(InputElement.PointerPressedEvent, InteractivePointerFocus, RoutingStrategies.Tunnel);
    }

    private void DetachInteractive()
    {
        if (!_interactiveAttached) return;
        _interactiveAttached = false;
        _editor.RemoveHandler(InputElement.KeyDownEvent, InteractiveKeyDown);
        _editor.RemoveHandler(InputElement.PointerPressedEvent, InteractivePointerFocus);
    }

    private void InteractivePointerFocus(object? sender, PointerPressedEventArgs args)
    {
        // Keyboard-only candidate. Do not enter the library's unintegrated drag-selection mode.
        args.Handled = true;
        _editor.TextArea.Focus();
    }

    private void InteractiveKeyDown(object? sender, KeyEventArgs args)
    {
        if (_finished || _displaySession is not { } source) return;
        var action = BoundedKeyMap.Translate(args.Key, args.KeyModifiers, OperatingSystem.IsMacOS());
        if (!action.Handled) return;
        args.Handled = true;
        var start = Stopwatch.GetTimestamp();
        try
        {
            var extend = action.ExtendSelection;
            switch (action.Command)
            {
                case BoundedKeyCommand.Left: source.MoveHorizontal(-1, extend); break;
                case BoundedKeyCommand.Right: source.MoveHorizontal(1, extend); break;
                case BoundedKeyCommand.Up: source.MoveLines(-1, extend); break;
                case BoundedKeyCommand.Down: source.MoveLines(1, extend); break;
                case BoundedKeyCommand.LineStart: source.MoveToLineBoundary(false, extend); break;
                case BoundedKeyCommand.LineEnd: source.MoveToLineBoundary(true, extend); break;
                case BoundedKeyCommand.DocumentStart: source.MoveToDocumentBoundary(false, extend); break;
                case BoundedKeyCommand.DocumentEnd: source.MoveToDocumentBoundary(true, extend); break;
                case BoundedKeyCommand.PageUp: source.MoveLines(-Math.Clamp(_editor.TextArea.TextView.VisualLines.Count, 1, 500), extend); break;
                case BoundedKeyCommand.PageDown: source.MoveLines(Math.Clamp(_editor.TextArea.TextView.VisualLines.Count, 1, 500), extend); break;
                case BoundedKeyCommand.SelectAll: source.SelectAll(); break;
                case BoundedKeyCommand.ClearSelection: source.MoveCaret(source.Caret); break;
                case BoundedKeyCommand.Copy: StartCopy(source); break;
                default: _viewModel.SetStatus("このキー操作は表示試作で未対応です。"); break;
            }
            RenderInteractive();
            _interactiveCommands++;
            if (_selectionSamples.Count < 256)
                _selectionSamples.Add(new
                {
                    DocumentGeneration = _documentGeneration, Command = action.Command.ToString(), Extend = extend,
                    RawKey = args.Key.ToString(), PhysicalKey = args.PhysicalKey.ToString(), Modifiers = args.KeyModifiers.ToString(), HasKeySymbol = !string.IsNullOrEmpty(args.KeySymbol),
                    SourceAnchor = source.Anchor, SourceCaret = source.Caret, SourcePosition = source.Position,
                    SourceSelectionLength = source.SelectionLength, SliceOffset = _interactiveSlice!.SourceOffset,
                    SliceChars = _interactiveSlice.Text.Length, NativeCaret = _editor.CaretOffset,
                    NativeSelectionStart = _editor.SelectionStart, NativeSelectionLength = _editor.SelectionLength,
                    StatusPosition = _viewModel.Position, SynchronousMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds
                });
        }
        catch (Exception) { Finish("interactive-command-failed"); }
    }

    private void InvalidateCopies()
    {
        _copyUiRequest = null;
        _copies?.Invalidate();
    }

    private void StartCopy(DocumentDisplaySession source)
    {
        if (source.SelectionLength == 0)
        {
            _copyUiRequest = null;
            _viewModel.SetStatus("コピーする範囲を選択してください。");
            return;
        }
        _copies ??= new ClipboardCopyCoordinator(WriteClipboardAsync);
        var selection = source.BeginCopy();
        try { _copyUiRequest = _copies.Start(selection); }
        catch { selection.Dispose(); throw; }
        _copyUiGeneration = _documentGeneration;
        _viewModel.SetStatus("原文の選択範囲をコピーしています…");
        if (!_copyObserverRunning) _copyObserver = ObserveCopiesAsync();
    }

    private async Task ObserveCopiesAsync()
    {
        _copyObserverRunning = true;
        try
        {
            while (_copyUiRequest is { } request)
            {
                var result = await request.Completion;
                if (_finished) return;
                if (!ReferenceEquals(request, _copyUiRequest)) continue;
                if (_copyUiGeneration == _documentGeneration && _copies!.CanAdopt(request))
                {
                    if (result.Status == ClipboardCopyStatus.Copied)
                    {
                        _copiesCompleted++;
                        _lastCopiedCharacters = result.Characters;
                        _viewModel.SetStatus($"原文の選択範囲をコピーしました（{result.Characters:N0} UTF-16単位）。");
                    }
                    else if (result.Status == ClipboardCopyStatus.Unavailable)
                        _viewModel.SetStatus("クリップボードを利用できません。再試行してください。");
                }
                _copyUiRequest = null;
            }
        }
        catch (Exception)
        {
            Program.MarkFailed();
            if (!_finished) Finish("clipboard-copy-failed");
        }
        finally { _copyObserverRunning = false; }
    }

    private async Task WriteClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(_editor)?.Clipboard ?? throw new ClipboardUnavailableException();
        try
        {
            await clipboard.SetValueAsync(DataFormat.Text, text);
            await clipboard.FlushAsync();
            if (_verifyClipboard)
            {
                string? echoed = await clipboard.TryGetValueAsync(DataFormat.Text);
                _clipboardVerificationChecks++;
                if (!string.Equals(text, echoed, StringComparison.Ordinal)) _clipboardVerificationFailures++;
                echoed = null;
            }
        }
        catch (Exception error) when (error is ExternalException and not SEHException or IOException or NotSupportedException)
        { throw new ClipboardUnavailableException(); }
    }

    private (TextDocument Document, TextLocation Target) BuildDisplayWindow(long offset)
    {
        var window = _displaySession!.CreateSlice(offset, _windowChars);
        _displaySession.MoveCaret(window.TargetOffset, extendSelection: _sourceOffsets.Count > 0);
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
        var generation = probe._documentGeneration;
        probe._window.RequestAnimationFrame(_ => probe._window.RequestAnimationFrame(_ =>
        {
            if (probe._finished || generation != probe._documentGeneration) return;
            probe._documentTwoTicksMilliseconds = Startup.Elapsed.TotalMilliseconds;
            if (probe._interactive)
            {
                probe.AttachInteractive();
                probe.RenderInteractive();
                probe._editor.TextArea.Focus();
                Phase("interactive-ready");
                return;
            }
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
            if (_selectionProbe) ApplyNavigationSelection(index);
            else
            {
                var display = BuildDisplayWindow(document.GetOffset(line, column));
                _editor.Document = display.Document;
                _editor.ScrollTo(display.Target.Line, display.Target.Column);
            }
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

    private void ApplyNavigationSelection(int step)
    {
        GuiNavigationPlan.Apply(_displaySession!, step);
        var slice = _displaySession!.CreateSlice(_displaySession.Caret, _windowChars);
        if (!_displaySession.TryGetDisplaySelection(slice, out var visible) || visible.CaretOffset is not { } caret)
            throw new InvalidOperationException("原文選択を表示範囲へ変換できません。");
        var document = new TextDocument(slice.Text);
        document.UndoStack.SizeLimit = 0;
        _editor.Document = document;
        _editor.Select(visible.Start, visible.Length);
        _editor.CaretOffset = caret;
        var location = document.GetLocation(caret);
        _editor.ScrollTo(location.Line, location.Column);
        _editor.TextArea.Focus();
        if (_editor.SelectionStart != visible.Start || _editor.SelectionLength != visible.Length || _editor.CaretOffset != caret)
            throw new InvalidOperationException("表示文書へ選択範囲を反映できません。");
        _largestDisplayChars = Math.Max(_largestDisplayChars, document.TextLength);
        _sourceOffsets.Add(slice.TargetOffset);
        _selectionSamples.Add(new
        {
            Step = step, SourceAnchor = _displaySession.Anchor, SourceCaret = _displaySession.Caret,
            SourcePosition = _displaySession.Position, SliceOffset = slice.SourceOffset, SliceChars = slice.Text.Length,
            Projection = visible, NativeSelectionStart = _editor.SelectionStart,
            NativeSelectionLength = _editor.SelectionLength, NativeCaret = _editor.CaretOffset
        });
    }

    private void Finish(string? failure = null)
    {
        if (_finished) return;
        _finished = true;
        DetachInteractive();
        _copyUiRequest = null;
        _copies?.Dispose();
        _retiredCopyWork = _copies is { } copies ? Task.WhenAll(copies.PendingWork, _copyObserver) : Task.CompletedTask;
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
            Scenario = _interactive ? "native-interactive-bounded-candidate" : _selectionProbe ? "native-gui-source-selection-projection" : "native-gui-scroll-proxy", Os = RuntimeInformation.OSDescription,
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
            SourceCaretOffset = _displaySession?.Caret, SourceCaretPosition = _displaySession?.Position,
            SourceSelectionStart = _displaySession?.SelectionStart, SourceSelectionLength = _displaySession?.SelectionLength,
            SourceSelectionUi = _interactive ? "keyboard-bound selection and source clipboard copy; pointer not connected" : _selectionProbe ? "clipped selection applied; physical input not injected" : "not-connected; logical selection only",
            SelectionSamples = _selectionSamples,
            InteractiveCommands = _interactiveCommands, StatusPosition = _viewModel.Position,
            InteractivePostedScrolls = _interactiveScrolls, InteractivePostedScrollMaxMilliseconds = _interactiveScrollMaxMilliseconds,
            LastInteractiveHorizontal = _lastInteractiveHorizontal, LastInteractiveVertical = _lastInteractiveVertical,
            InteractiveScrollSamples = _interactiveScrollSamples,
            ClipboardCopiesCompleted = _copiesCompleted, LastCopiedCharacters = _lastCopiedCharacters,
            ClipboardVerificationEnabled = _verifyClipboard, ClipboardVerificationChecks = _clipboardVerificationChecks,
            ClipboardVerificationFailures = _clipboardVerificationFailures,
            HeldAfterReport = _holdAfterReport,
            MemoryMetric = _memory.Metric, MemoryBefore = _memoryBefore, SampledPeakMemory = _peakMemory,
            MemoryIncrease = _peakMemory - _memoryBefore, SamplingIntervalMilliseconds = 10, MemorySamples = _sampleCount,
            ClientWidth = _window.ClientSize.Width, ClientHeight = _window.ClientSize.Height,
            RenderScaling = _window.RenderScaling, PhysicalPaint = "not-measured",
            PhysicalInput = _interactive ? "interactive keyboard mode; end-to-end latency not measured" : "not-measured",
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
        _displaySession?.Dispose();
        _displaySession = null;
        _interactiveSlice = null;
        _sourceDocument = null;
        _instance = null;
        _memory.Dispose();
        _sampling.Dispose();
        if (failure is not null) Program.MarkFailed();
        // Finish can run in the animation callback. Leave that callback before
        // initiating the normal product shutdown sequence.
        if (_holdAfterReport && failure is null) Phase("awaiting-manual-close");
        else Dispatcher.UIThread.Post(_window.Close);
    }

    private static void Phase(string phase) => Console.WriteLine($"GUI-PROBE {Startup.Elapsed.TotalMilliseconds:F3} {phase}");
}
