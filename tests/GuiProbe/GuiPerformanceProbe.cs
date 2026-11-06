using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using CodeView.Documents;
using CodeView.Contracts;
using CodeView.Operations;
using CodeView.Tests.Performance;

namespace CodeView.Presentation;

/// <summary>Compiled only into an explicitly requested local performance build.</summary>
internal sealed partial class GuiPerformanceProbe
{
    private static readonly Stopwatch Startup = new();
    private static readonly StartupTimeline StartupStages = new();
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
    private readonly bool _readOnlyChecks = Environment.GetEnvironmentVariable("CODEVIEW_GUI_READONLY_CHECKS") == "1";
    private readonly bool _searchPanelChecks = Environment.GetEnvironmentVariable("CODEVIEW_GUI_SEARCH_PANEL_CHECKS") == "1";
    private KeyEventArgs? _scriptedSearchKey;
    private Task _readOnlyWork = Task.CompletedTask;
    private object? _readOnlyResults;
    private object? _searchPanelResults;
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
    private readonly BoundedPointerSelection _pointerSelection = new();
    private IPointer? _selectionPointer;
    private Point _pointerPosition;
    private DispatcherTimer? _pointerTimer;
    private long _pointerUpdates, _pointerEdgeSteps;
    private readonly List<object> _pointerSamples = [];
    private ClipboardCopyCoordinator? _copies;
    private ClipboardCopyRequest? _copyUiRequest;
    private long _copyUiGeneration;
    private bool _copyObserverRunning;
    private Task _copyObserver = Task.CompletedTask;
    private long _copiesCompleted, _clipboardVerificationChecks, _clipboardVerificationFailures;
    private long _lastCopiedCharacters;
    private readonly bool _verifyClipboard = Environment.GetEnvironmentVariable("CODEVIEW_GUI_VERIFY_CLIPBOARD") == "1";
    private readonly int _windowChars = TextWindow.ParseWindowChars(Environment.GetEnvironmentVariable("CODEVIEW_GUI_WINDOW_CHARS"));
    private readonly bool _viewportWindow = Environment.GetEnvironmentVariable("CODEVIEW_GUI_VIEWPORT") == "1";
    private int _sliceViewportLines;
    private readonly List<object> _viewportSamples = [];
    private readonly SourceLineNumberMargin? _sourceLineNumbers;
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
        if (_windowed)
        {
            var margins = editor.TextArea.LeftMargins;
            var original = margins.OfType<LineNumberMargin>().Single();
            _sourceLineNumbers = new SourceLineNumberMargin();
            _sourceLineNumbers.SetValue(TextBlock.ForegroundProperty, original.GetValue(TextBlock.ForegroundProperty));
            margins[margins.IndexOf(original)] = _sourceLineNumbers;
        }
        if (_rowsEnabled) InstallSourceScrollBars(); // Reparent before any full source is attached to bindings.
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

    internal static void Start()
    {
        Startup.Start();
        StartupStages.Record("main-entered", 0);
    }

    internal static double Checkpoint(string stage)
    {
        var milliseconds = Startup.Elapsed.TotalMilliseconds;
        StartupStages.Record(stage, milliseconds);
        return milliseconds;
    }

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
        if (Environment.GetEnvironmentVariable("CODEVIEW_GUI_READONLY_CHECKS") == "1" &&
            (!hasInput || Environment.GetEnvironmentVariable("CODEVIEW_GUI_WINDOWED") == "1" ||
             Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"))
            throw new ArgumentException("読み取り専用の自動操作は入力あり/通常表示/専用CIで実行してください。");
        if (Environment.GetEnvironmentVariable("CODEVIEW_GUI_SEARCH_PANEL_CHECKS") == "1" &&
            (!hasInput || Environment.GetEnvironmentVariable("CODEVIEW_GUI_WINDOWED") == "1" ||
             Environment.GetEnvironmentVariable("CODEVIEW_GUI_READONLY_CHECKS") == "1" ||
             Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"))
            throw new ArgumentException("検索パネルの自動操作は入力あり/通常表示/専用CIで単独実行してください。");
        ValidateRowMode(hasInput);
        var probe = _instance = new(window, editor, viewModel, output, hasInput);
        // This workload injects no input. Abort rather than mixing an interactive
        // selection/scroll operation with automatic document replacement.
        void InterruptForInput(object? sender, RoutedEventArgs args)
        {
            if (probe._finished || (probe._interactive && !probe._rowChecks) ||
                ReferenceEquals(args, probe._scriptedSearchKey) || ReferenceEquals(args, probe._scriptedRowKey) ||
                ReferenceEquals(args, probe._scriptedRowPointer)) return;
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
            if (probe._searchPanelChecks && !probe._finished) probe.Finish("unexpected-close-during-search-checks");
        };
        window.Closed += (_, _) => Phase("window-closed");
        window.Opened += (_, _) =>
        {
            probe._openedMilliseconds = Checkpoint("window-opened");
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
        probe.ClearRowProjection();
        probe._documentGeneration++;
        if (probe._interactive)
        {
            probe._displaySession.MoveToPosition(position);
            if (probe._rowsEnabled) return probe.BuildRowDocument(reveal: true);
            probe._interactiveSlice = probe.CreateSourceSlice(probe._displaySession.Caret);
            var display = CreateDisplayDocument(probe._interactiveSlice.Text);
            probe.ApplyLineNumbers(display, probe._interactiveSlice);
            return display;
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
        ? Task.WhenAll(copies.PendingWork, probe._copyObserver, probe._readOnlyWork)
        : _instance is { } active ? active._readOnlyWork : _retiredCopyWork;

    internal static bool TryRestoreOriginal(int line, int column)
    {
        if (_instance is not { _interactive: true, _displaySession: { } session } probe) return false;
        session.MoveToPosition(new(line, column));
        probe._interactiveSlice = null;
        probe.ClearRowProjection();
        if (probe._rowsEnabled) probe.RevealRowCaret();
        probe.RenderInteractive();
        return true;
    }

    internal static bool TrySelectOriginal(long offset, int length, bool clear = false)
    {
        if (_instance is not { _interactive: true, _displaySession: { } session } probe) return false;
        if (clear) session.MoveCaret(session.Caret);
        else session.Select(checked(offset + length), offset);
        if (probe._rowsEnabled) probe.RevealRowCaret();
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
        probe.ClearRowProjection();
        probe._sourceLineNumbers?.Clear();
        if (probe._rowsEnabled) probe.SyncSourceScrollBars();
        probe._editor.Document = null!;
        probe._documentGeneration++;
    }

    private void ApplyLineNumbers(TextDocument display, DocumentDisplaySlice slice)
    {
        _sourceLineNumbers!.Apply(display, new SourceLineNumbers(slice.SourceStart.Line, _sourceDocument!.LineCount, display.LineCount));
        if (_viewportWindow && _viewportSamples.Count < 256)
            _viewportSamples.Add(new { RequestedLines = _sliceViewportLines, DisplayLines = display.LineCount,
                SourceFirstLine = slice.SourceStart.Line, SourceLines = _sourceDocument.LineCount,
                SliceChars = slice.Text.Length, slice.SourceOffset, slice.TargetOffset });
    }

    private static TextDocument CreateDisplayDocument(string text)
    {
        var document = new TextDocument(text);
        document.UndoStack.SizeLimit = 0;
        return document;
    }

    private int RequestedViewportLines()
    {
        var view = _editor.TextArea.TextView;
        var rows = Math.Ceiling(view.Bounds.Height / view.DefaultLineHeight) + 2;
        return double.IsFinite(rows) ? (int)Math.Clamp(rows, 1, 512) : 1;
    }

    private DocumentDisplaySlice CreateSourceSlice(long offset)
    {
        _sliceViewportLines = _viewportWindow ? RequestedViewportLines() : 0;
        return _viewportWindow
            ? _displaySession!.CreateViewportSlice(offset, _windowChars, _sliceViewportLines)
            : _displaySession!.CreateSlice(offset, _windowChars);
    }

    private void InteractiveSizeChanged(object? sender, SizeChangedEventArgs args) => RenderInteractive();

    private void RenderInteractive()
    {
        if (_renderingInteractive || _displaySession is null || _finished) return;
        if (_rowsEnabled) { RenderRows(); return; }
        _renderingInteractive = true;
        try
        {
            var source = _displaySession;
            var slice = _interactiveSlice;
            var margin = _windowChars / 4;
            if (slice is null || (_viewportWindow && RequestedViewportLines() != _sliceViewportLines) ||
                source.Caret < slice.SourceOffset || source.Caret > slice.SourceOffset + slice.Text.Length ||
                (!_pointerSelection.IsActive && slice.SourceOffset > 0 && source.Caret < slice.SourceOffset + margin) ||
                (!_pointerSelection.IsActive && slice.SourceOffset + slice.Text.Length < source.Length && source.Caret > slice.SourceOffset + slice.Text.Length - margin))
            {
                slice = _interactiveSlice = CreateSourceSlice(source.Caret);
                var display = CreateDisplayDocument(slice.Text);
                ApplyLineNumbers(display, slice);
                _editor.Document = display;
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
        _editor.AddHandler(InputElement.PointerPressedEvent, InteractivePointerPressed, RoutingStrategies.Tunnel);
        _editor.AddHandler(InputElement.PointerMovedEvent, InteractivePointerMoved, RoutingStrategies.Tunnel);
        _editor.AddHandler(InputElement.PointerReleasedEvent, InteractivePointerReleased, RoutingStrategies.Tunnel);
        _editor.PointerCaptureLost += InteractivePointerCaptureLost;
        if (_rowsEnabled) _editor.AddHandler(InputElement.PointerWheelChangedEvent, RowWheelChanged, RoutingStrategies.Tunnel);
        if (_viewportWindow || _rowsEnabled) _editor.SizeChanged += InteractiveSizeChanged;
    }

    private void DetachInteractive()
    {
        if (!_interactiveAttached) return;
        _interactiveAttached = false;
        CancelPointerSelection();
        _editor.RemoveHandler(InputElement.KeyDownEvent, InteractiveKeyDown);
        _editor.RemoveHandler(InputElement.PointerPressedEvent, InteractivePointerPressed);
        _editor.RemoveHandler(InputElement.PointerMovedEvent, InteractivePointerMoved);
        _editor.RemoveHandler(InputElement.PointerReleasedEvent, InteractivePointerReleased);
        _editor.PointerCaptureLost -= InteractivePointerCaptureLost;
        if (_rowsEnabled) _editor.RemoveHandler(InputElement.PointerWheelChangedEvent, RowWheelChanged);
        if (_viewportWindow || _rowsEnabled) _editor.SizeChanged -= InteractiveSizeChanged;
    }

    private void RecordPointer(string phase, PointerEventArgs? args = null)
    {
        if (_pointerSamples.Count >= 256) return;
        _pointerSamples.Add(new { Phase = phase, PointerId = args?.Pointer.Id,
            Active = _pointerSelection.PointerId, Captured = _selectionPointer?.Captured?.GetType().Name,
            Left = args?.GetCurrentPoint(_editor).Properties.IsLeftButtonPressed,
            Point = args?.GetPosition(_editor.TextArea.TextView),
            Anchor = _displaySession?.Anchor, Caret = _displaySession?.Caret });
    }

    private void CancelPointerSelection()
    {
        _pointerTimer?.Stop();
        _pointerSelection.Cancel();
        var pointer = _selectionPointer;
        _selectionPointer = null;
        if (pointer?.Captured == _editor) pointer.Capture(null);
    }

    private int? HitPointer(Point point, bool clamp)
    {
        var view = _editor.TextArea.TextView;
        if (view.Bounds.Width <= 0 || view.Bounds.Height <= 0) return null;
        if (!clamp && !new Rect(view.Bounds.Size).Contains(point)) return null;
        view.EnsureVisualLines();
        var x = Math.Clamp(point.X, 0, Math.Max(0, view.Bounds.Width - 0.1));
        var y = Math.Clamp(point.Y, 0, Math.Max(0, view.Bounds.Height - 0.1));
        var position = view.GetPosition(new Point(x + view.HorizontalOffset, y + view.VerticalOffset));
        return position is { } found ? _editor.Document.GetOffset(found.Location) :
            y <= 0 ? 0 : _editor.Document.TextLength;
    }

    private void InteractivePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        RecordPointer("press", args);
        args.Handled = true; // The library must not start its local-document selection mode.
        if (_finished || _displaySession is not { } source || (!_rowsEnabled && _interactiveSlice is null)) return;
        _editor.TextArea.Focus();
        if (!args.GetCurrentPoint(_editor).Properties.IsLeftButtonPressed || args.Pointer.Type != PointerType.Mouse) return;
        try
        {
            var point = args.GetPosition(_editor.TextArea.TextView);
            var marginPoint = _sourceLineNumbers is { } margin ? args.GetPosition(margin) : default;
            var onLineNumber = _sourceLineNumbers is { } numbers &&
                new Rect(numbers.Bounds.Size).Contains(marginPoint);
            var offset = HitPointer(point, clamp: onLineNumber);
            if (offset is null) return;
            var started = _rowsEnabled
                ? BeginRowPointer(offset.Value, args.Pointer.Id, args.KeyModifiers.HasFlag(KeyModifiers.Shift), onLineNumber)
                : onLineNumber
                    ? _pointerSelection.BeginLine(source, _interactiveSlice!, offset.Value, args.Pointer.Id, args.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    : _pointerSelection.Begin(source, _interactiveSlice!, offset.Value, args.Pointer.Id, args.KeyModifiers.HasFlag(KeyModifiers.Shift));
            if (!started) return;
            _selectionPointer = args.Pointer;
            _pointerPosition = args.GetPosition(_editor.TextArea.TextView);
            args.Pointer.Capture(_editor);
            RecordPointer("captured", args);
            RenderInteractive();
            _pointerUpdates++;
        }
        catch (Exception) { Finish("pointer-selection-failed"); }
    }

    private void InteractivePointerMoved(object? sender, PointerEventArgs args)
    {
        if (_pointerSelection.IsActive) RecordPointer("move", args);
        if (_pointerSelection.PointerId != args.Pointer.Id) return;
        args.Handled = true;
        if (args.Pointer.Captured != _editor) { CancelPointerSelection(); return; }
        try
        {
            _pointerPosition = args.GetPosition(_editor.TextArea.TextView);
            UpdatePointerSelection(args.Pointer.Id);
            var outside = PointerOutside();
            if (outside.Horizontal != 0 || outside.Vertical != 0)
            {
                if (_pointerTimer is null)
                {
                    _pointerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                    _pointerTimer.Tick += PointerEdgeTick;
                }
                _pointerTimer.Start();
            }
            else _pointerTimer?.Stop();
        }
        catch (Exception) { Finish("pointer-selection-failed"); }
    }

    private (int Horizontal, int Vertical) PointerOutside()
    {
        var size = _editor.TextArea.TextView.Bounds.Size;
        return (_pointerPosition.X < 0 ? -1 : _pointerPosition.X >= size.Width ? 1 : 0,
            _pointerPosition.Y < 0 ? -1 : _pointerPosition.Y >= size.Height ? 1 : 0);
    }

    private void UpdatePointerSelection(long pointerId)
    {
        if (_rowsEnabled) { UpdateRowPointer(pointerId); return; }
        if (_displaySession is not { } source || _interactiveSlice is not { } slice) return;
        if (HitPointer(_pointerPosition, clamp: true) is { } offset &&
            _pointerSelection.Move(source, slice, offset, pointerId))
        {
            RenderInteractive();
            _pointerUpdates++;
        }
    }

    private void PointerEdgeTick(object? sender, EventArgs args)
    {
        try
        {
            if (_finished || _displaySession is not { } source || _pointerSelection.PointerId is not { } id)
            { _pointerTimer?.Stop(); return; }
            var outside = PointerOutside();
            if (!_pointerSelection.StepOutside(source, id, outside.Horizontal, outside.Vertical))
            { _pointerTimer?.Stop(); return; }
            _pointerEdgeSteps++;
            if (_rowsEnabled) RevealRowCaret();
            RenderInteractive(); // No selection update from ScrollOffsetChanged; no recursive scroll loop.
        }
        catch (Exception) { Finish("pointer-selection-failed"); }
    }

    private void InteractivePointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        RecordPointer("release", args);
        if (_pointerSelection.PointerId != args.Pointer.Id) return;
        args.Handled = true;
        try
        {
            _pointerPosition = args.GetPosition(_editor.TextArea.TextView);
            // Outside release keeps the last timed endpoint rather than snapping back to the display edge.
            var outside = PointerOutside();
            if (outside.Horizontal == 0 && outside.Vertical == 0) UpdatePointerSelection(args.Pointer.Id);
        }
        catch (Exception) { Finish("pointer-selection-failed"); }
        finally { CancelPointerSelection(); }
    }

    private void InteractivePointerCaptureLost(object? sender, PointerCaptureLostEventArgs args)
    {
        RecordPointer("capture-lost");
        CancelPointerSelection();
    }

    private void InteractiveKeyDown(object? sender, KeyEventArgs args)
    {
        if (_finished || _displaySession is not { } source) return;
        CancelPointerSelection();
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
            if (_rowsEnabled && action.Command is not (BoundedKeyCommand.Copy or BoundedKeyCommand.SelectAll)) RevealRowCaret();
            RenderInteractive();
            _interactiveCommands++;
            if (_selectionSamples.Count < 256)
                _selectionSamples.Add(new
                {
                    DocumentGeneration = _documentGeneration, Command = action.Command.ToString(), Extend = extend,
                    RawKey = args.Key.ToString(), PhysicalKey = args.PhysicalKey.ToString(), Modifiers = args.KeyModifiers.ToString(), HasKeySymbol = !string.IsNullOrEmpty(args.KeySymbol),
                    SourceAnchor = source.Anchor, SourceCaret = source.Caret, SourcePosition = source.Position,
                    SourceSelectionLength = source.SelectionLength, SliceOffset = _interactiveSlice?.SourceOffset,
                    SliceChars = _interactiveSlice?.Text.Length, RowProjection = _rowsEnabled, NativeCaret = _editor.CaretOffset,
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
        var window = CreateSourceSlice(offset);
        _displaySession!.MoveCaret(window.TargetOffset, extendSelection: _sourceOffsets.Count > 0);
        var document = new TextDocument(window.Text);
        document.UndoStack.SizeLimit = 0;
        ApplyLineNumbers(document, window);
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
            if (probe._readOnlyChecks || probe._searchPanelChecks)
            {
                probe._readOnlyWork = probe.RunReadOnlyChecksAsync();
                return;
            }
            if (probe._interactive)
            {
                probe.AttachInteractive();
                probe.RenderInteractive();
                probe._editor.TextArea.Focus();
                Phase("interactive-ready");
                if (probe._rowChecks) probe._readOnlyWork = probe.RunRowChecksAsync();
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

    private async Task RunReadOnlyChecksAsync()
    {
        try
        {
            if (_searchPanelChecks)
                _searchPanelResults = await GuiSearchPanelProbe.RunAsync(_window, _editor, _viewModel, _sampling.Token);
            else _readOnlyResults = await GuiReadOnlyProbe.RunAsync(_window, _editor, _viewModel, _sampling.Token);
            Finish();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Finish((_searchPanelChecks ? "search-panel-check-failed: " : "readonly-check-failed: ") + error.Message);
        }
    }

    internal static void RaiseSearchKey(Control target, KeyEventArgs args)
    {
        var probe = _instance ?? throw new InvalidOperationException("No active GUI probe.");
        if (!probe._searchPanelChecks) throw new InvalidOperationException("Not a search panel test.");
        probe._scriptedSearchKey = args;
        try { target.RaiseEvent(args); }
        finally { probe._scriptedSearchKey = null; }
    }

    private void ApplyNavigationSelection(int step)
    {
        GuiNavigationPlan.Apply(_displaySession!, step);
        var slice = CreateSourceSlice(_displaySession!.Caret);
        if (!_displaySession.TryGetDisplaySelection(slice, out var visible) || visible.CaretOffset is not { } caret)
            throw new InvalidOperationException("原文選択を表示範囲へ変換できません。");
        var document = new TextDocument(slice.Text);
        document.UndoStack.SizeLimit = 0;
        ApplyLineNumbers(document, slice);
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
        _retiredCopyWork = _copies is { } copies ? Task.WhenAll(copies.PendingWork, _copyObserver, _readOnlyWork) : _readOnlyWork;
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
            ProductInformationalVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Scenario = _searchPanelChecks ? "native-gui-search-panel-controls" : _readOnlyChecks ? "native-gui-readonly-controls" : _interactive ? "native-interactive-bounded-candidate" : _selectionProbe ? "native-gui-source-selection-projection" : "native-gui-scroll-proxy", Os = RuntimeInformation.OSDescription,
            ReadOnlyChecks = _readOnlyResults,
            SearchPanelChecks = _searchPanelResults,
            RowProjectionEnabled = _rowsEnabled, RowProjectionChecks = _rowResults,
            RowProjectionSamples = _rowSamples,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            Failure = failure, CacheCondition = Environment.GetEnvironmentVariable("CODEVIEW_PERF_CACHE") ?? "unknown",
            DisplayMode = _windowed ? "bounded-window-prototype" : "full-document-candidate",
            ConfiguredWindowChars = _windowed ? (int?)_windowChars : null,
            ViewportWindowEnabled = _viewportWindow, RequestedViewportLines = _sliceViewportLines,
            SourceLineCount = _sourceDocument?.LineCount, DisplayLineCount = _editor.Document?.LineCount,
            ViewportSamples = _viewportSamples,
            OpenedMilliseconds = _openedMilliseconds, WindowTwoTicksMilliseconds = _emptyWindowTwoTicksMilliseconds,
            StartupStages = StartupStages.Snapshot(),
            DocumentAttachmentMilliseconds = _attachmentMilliseconds, DocumentTwoTicksMilliseconds = _documentTwoTicksMilliseconds,
            ScrollSynchronousMilliseconds = _scrollTimes, AnimationTickIntervalsMilliseconds = _tickIntervals,
            DocumentChars = _editor.Document?.TextLength, DocumentLines = _editor.Document?.LineCount,
            EditorReadOnly = _editor.IsReadOnly, TextDragDropEnabled = _editor.Options.EnableTextDragDrop,
            UndoCanUndo = _editor.Document?.UndoStack.CanUndo, UndoCanRedo = _editor.Document?.UndoStack.CanRedo,
            SourceChars = _sourceDocument?.TextLength, SourceLines = _sourceDocument?.LineCount,
            LargestDisplayChars = _largestDisplayChars, VisitedSourceOffsets = _sourceOffsets,
            SourceCaretOffset = _displaySession?.Caret, SourceCaretPosition = _displaySession?.Position,
            SourceSelectionStart = _displaySession?.SelectionStart, SourceSelectionLength = _displaySession?.SelectionLength,
            SourceLineNumberMap = _sourceLineNumbers?.Numbers,
            SourceLineNumberLabelsRendered = _sourceLineNumbers?.RenderedLabels,
            LastFirstSourceLineLabel = _sourceLineNumbers?.LastFirstLabel, LastLastSourceLineLabel = _sourceLineNumbers?.LastLastLabel,
            SourceSelectionUi = _interactive ? "source keyboard/mouse drag selection and clipboard; probe only" : _selectionProbe ? "clipped selection applied; physical input not injected" : "not-connected; logical selection only",
            SelectionSamples = _selectionSamples,
            InteractiveCommands = _interactiveCommands, StatusPosition = _viewModel.Position,
            InteractivePostedScrolls = _interactiveScrolls, InteractivePostedScrollMaxMilliseconds = _interactiveScrollMaxMilliseconds,
            LastInteractiveHorizontal = _lastInteractiveHorizontal, LastInteractiveVertical = _lastInteractiveVertical,
            InteractiveScrollSamples = _interactiveScrollSamples,
            PointerSamples = _pointerSamples, PointerUpdates = _pointerUpdates, PointerEdgeSteps = _pointerEdgeSteps, PointerActive = _pointerSelection.IsActive,
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
        ClearRowProjection();
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
