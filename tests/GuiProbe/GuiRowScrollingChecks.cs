using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private async Task<string[]> CheckRowScrollingAsync(CancellationToken ct)
    {
        var source = _displaySession!;
        var anchor = source.Anchor;
        var caret = source.Caret;
        using var originalSelection = source.BeginCopy();
        var copied = originalSelection.ReadText(ct);
        var checks = new List<string>();
        void Check(string name, bool passed) { RequireRows(passed, name); checks.Add(name); }
        using var pointer = new Pointer(100001, PointerType.Mouse, true);
        var view = _editor.TextArea.TextView;
        Point Root(Point point) => view.TranslatePoint(point, _window) ?? throw new InvalidOperationException("No native text-view transform.");
        void Raise(PointerEventArgs args)
        {
            _scriptedRowPointer = args;
            try { _editor.RaiseEvent(args); }
            finally { _scriptedRowPointer = null; }
            RequireRows(!_finished, "Routed pointer operation failed.");
        }
        void Wheel(Vector delta, KeyModifiers modifiers = KeyModifiers.None) => Raise(new PointerWheelEventArgs(
            _editor, pointer, _window, Root(new Point(10, 5)), 0, new PointerPointProperties(), modifiers, delta));
        void Press(Point point) => Raise(new PointerPressedEventArgs(_editor, pointer, _window, Root(point), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        void Move(Point point) => Raise(new PointerEventArgs(InputElement.PointerMovedEvent, _editor, pointer, _window, Root(point), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
        void Release(Point point) => Raise(new PointerReleasedEventArgs(_editor, pointer, _window, Root(point), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));

        Check("source-scrollbar-extents", _sourceVerticalBar!.Maximum == _sourceDocument!.LineCount &&
            _sourceHorizontalBar!.Maximum == source.MaximumLineLength);
        var currentGeneration = _documentGeneration;
        var staleFirst = source.FirstVisibleLine;
        var staleHorizontal = source.HorizontalSourceOffset;
        _sourceBarGestureGeneration = currentGeneration - 1;
        _sourceVerticalBar.Value = _sourceVerticalBar.Maximum;
        _sourceHorizontalBar!.Value = _sourceHorizontalBar.Maximum;
        Check("stale-scrollbar-gesture-rejected", source.FirstVisibleLine == staleFirst && source.HorizontalSourceOffset == staleHorizontal &&
            _sourceVerticalBar.Value == staleFirst && _sourceHorizontalBar.Value == staleHorizontal);
        _sourceBarGestureToken++;
        RetireSourceBarGesture();
        _sourceBarGestureToken++;
        _sourceBarGestureGeneration = currentGeneration; // A new gesture must survive an older release callback.
        await RowFrame(ct);
        Check("stale-scrollbar-release-rejected", _sourceBarGestureGeneration == currentGeneration);
        RetireSourceBarGesture();
        await RowFrame(ct);
        Check("current-scrollbar-release-retires", _sourceBarGestureGeneration is null);
        _sourceVerticalBar.Value = _sourceVerticalBar.Maximum;
        _sourceHorizontalBar!.Value = _sourceHorizontalBar.Maximum;
        Check("source-scrollbar-last-position", source.FirstVisibleLine == _sourceDocument.LineCount &&
            source.HorizontalSourceOffset == source.MaximumLineLength);
        Check("scrollbar-preserves-source-selection", source.Anchor == anchor && source.Caret == caret);
        using (var after = source.BeginCopy()) Check("scrollbar-preserves-original-copy", after.ReadText(ct) == copied);
        _sourceVerticalBar.Value = 1;
        _sourceHorizontalBar.Value = 0;
        _rowWheel.Reset();
        Wheel(new Vector(0, -.125));
        Wheel(new Vector(0, -.125));
        Check("sub-unit-wheel-retained", source.FirstVisibleLine == 1);
        Wheel(new Vector(0, -.125));
        Check("routed-trackpad-accumulation", source.FirstVisibleLine == Math.Min(2, _sourceDocument.LineCount));
        Wheel(new Vector(0, -.125), KeyModifiers.Shift);
        Check("routed-shift-wheel-horizontal", source.HorizontalSourceOffset == Math.Min(2, source.MaximumLineLength));
        Wheel(new Vector(-1, -1));
        Check("routed-diagonal-wheel", source.FirstVisibleLine == Math.Min(5, _sourceDocument.LineCount) &&
            source.HorizontalSourceOffset == Math.Min(18, source.MaximumLineLength));
        Check("wheel-preserves-source-selection", source.Anchor == anchor && source.Caret == caret);

        _sourceVerticalBar.Value = 1;
        _sourceHorizontalBar.Value = 0;
        RenderInteractive();
        await RowFrame(ct);
        Press(new Point(1, view.DefaultLineHeight / 2));
        Check("routed-pointer-captured", _pointerSelection.IsActive && pointer.Captured == _editor);
        Move(new Point(Math.Min(50, view.Bounds.Width - 1), Math.Min(view.DefaultLineHeight * 2.5, view.Bounds.Height - 1)));
        Check("routed-pointer-selects-original", source.Length == 0 || source.SelectionLength > 0);
        var beforeEdge = _pointerEdgeSteps;
        Move(new Point(10, view.Bounds.Height + 5));
        await Task.Delay(120, ct); // The existing single timer, never recursive offset-change callbacks.
        Check("routed-edge-timer", _sourceDocument.LineCount == 1 || _pointerEdgeSteps > beforeEdge);
        Release(new Point(10, view.Bounds.Height + 5));
        Check("routed-pointer-release", !_pointerSelection.IsActive && pointer.Captured is null && _pointerTimer?.IsEnabled != true);
        Press(new Point(1, view.DefaultLineHeight / 2));
        pointer.Capture(null);
        Check("routed-capture-loss", !_pointerSelection.IsActive && _pointerTimer?.IsEnabled != true);
        Press(new Point(1, view.DefaultLineHeight / 2));
        var generation = _documentGeneration;
        DetachInteractive(); // Same cancellation path used by tab/document replacement and shutdown.
        Check("replacement-detaches-capture", !_pointerSelection.IsActive && pointer.Captured is null && _pointerTimer?.IsEnabled != true);
        AttachInteractive();
        Check("reattach-single-owner", _interactiveAttached && generation == _documentGeneration);
        if (_contextualRows && _sourceDocument.GetLineByNumber(1).Length > 0)
        {
            TryRestoreOriginal(1, 1);
            await RowFrame(ct);
            Check("contextual-caret-rectangle-visible",
                _interactiveRows!.TryDisplayPosition(source.Caret, out var display)
                && _contextLayer!.TryGetCaretRectangle(display, source.Caret, out var contextual)
                && contextual is { } placed && GetRowCaretRectangle(source.Caret) == placed
                && placed.Left >= 0 && placed.Right <= view.Bounds.Width + 1);
        }
        var targetLine = _sourceDocument.LineCount > 1 && _sourceDocument.GetLineByNumber(2).Length > _sourceDocument.GetLineByNumber(1).Length ? 2 : 1;
        TryRestoreOriginal(targetLine, _sourceDocument.GetLineByNumber(targetLine).Length + 1);
        await RowFrame(ct);
        var caretRect = GetRowCaretRectangle(source.Caret);
        Check("source-caret-rectangle-visible", _interactiveRows!.TryDisplayPosition(source.Caret, out _) &&
            caretRect is { } visible && visible.Left >= 0 && visible.Right <= view.Bounds.Width + 1 &&
            visible.Top >= 0 && visible.Bottom <= view.Bounds.Height + 1);
        var target = _sourceDocument.GetLineByNumber(targetLine);
        if (target.Length > 512)
        {
            var previousSource = target.EndOffset - 1;
            if (char.IsLowSurrogate(_sourceDocument.GetCharAt(previousSource))) previousSource--;
            Check("long-line-end-keeps-preceding-character", source.Caret == target.EndOffset &&
                _interactiveRows!.TryDisplayPosition(previousSource, out var previous) &&
                _editor.Document.GetCharAt(previous) == _sourceDocument.GetCharAt(previousSource));
        }
        var firstLine = _sourceDocument.GetLineByNumber(1);
        if (_sourceDocument.GetText(firstLine.Offset, firstLine.Length) == "before")
        {
            _sourceVerticalBar.Value = 1;
            _sourceHorizontalBar.Value = 0;
            RenderInteractive();
            await RowFrame(ct);
            var blank = new Point(view.Bounds.Width - 2, view.DefaultLineHeight / 2);
            Press(blank);
            Check("trailing-blank-click-selects-line-end", _pointerSelection.IsActive &&
                source.Anchor == firstLine.EndOffset && source.Caret == firstLine.EndOffset);
            Release(blank);
            Check("trailing-blank-click-updates-source-position",
                _window.FindControl<TextBlock>("CaretPositionStatus")?.Text == "行 1, 列 7");
            Press(new Point(1, view.DefaultLineHeight / 2));
            var outsideRight = new Point(view.Bounds.Width + 5, view.DefaultLineHeight / 2);
            Move(outsideRight);
            RequireRows(pointer.Captured == _editor && _pointerTimer?.IsEnabled == true,
                "Horizontal edge drag did not start.");
            while (_pointerTimer?.IsEnabled == true) await Task.Delay(10, ct);
            Check("horizontal-edge-drag-stops-at-line-end", source.Anchor == firstLine.Offset &&
                source.Caret == firstLine.EndOffset && _pointerTimer?.IsEnabled == false);
            Release(outsideRight);
        }
        source.Select(anchor, caret);
        source.SetViewport(1, 0);
        RenderInteractive();
        return checks.ToArray();
    }
}
