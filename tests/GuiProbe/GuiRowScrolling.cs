using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private ScrollBar? _sourceVerticalBar;
    private ScrollBar? _sourceHorizontalBar;
    private bool _syncingSourceBars;
    private bool _revealRowsPending;
    private readonly RowWheelAccumulator _rowWheel = new();
    private long _sourceBarChanges;
    private int? _sourceBarGestureGeneration;
    private long _sourceBarGestureToken;

    private void InstallSourceScrollBars()
    {
        if (_sourceVerticalBar is not null) return;
        if (_editor.Parent is not Panel parent) throw new InvalidOperationException("行別試作のEditor配置が一致しません。");
        var row = Grid.GetRow(_editor);
        var column = Grid.GetColumn(_editor);
        var host = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetRow(host, row);
        Grid.SetColumn(host, column);
        parent.Children.Remove(_editor);
        Grid.SetRow(_editor, 0);
        Grid.SetColumn(_editor, 0);
        host.Children.Add(_editor);
        _sourceVerticalBar = new ScrollBar { Orientation = Orientation.Vertical, Minimum = 1, Maximum = 1, Value = 1,
            AllowAutoHide = false, Focusable = false };
        _sourceHorizontalBar = new ScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 0,
            AllowAutoHide = false, Focusable = false };
        AutomationProperties.SetName(_sourceVerticalBar, "原文の先頭行");
        AutomationProperties.SetName(_sourceHorizontalBar, "原文の横位置（UTF-16）");
        Grid.SetColumn(_sourceVerticalBar, 1);
        Grid.SetRow(_sourceHorizontalBar, 1);
        host.Children.Add(_sourceVerticalBar);
        host.Children.Add(_sourceHorizontalBar);
        parent.Children.Add(host);
        _editor.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _sourceVerticalBar.ValueChanged += SourceBarChanged;
        _sourceHorizontalBar.ValueChanged += SourceBarChanged;
        foreach (var bar in new[] { _sourceVerticalBar, _sourceHorizontalBar })
        {
            bar.AddHandler(InputElement.PointerPressedEvent, SourceBarGestureStarted, RoutingStrategies.Tunnel);
            bar.AddHandler(InputElement.PointerReleasedEvent, SourceBarGestureEnded, RoutingStrategies.Tunnel);
            bar.AddHandler(InputElement.PointerCaptureLostEvent, SourceBarCaptureLost, RoutingStrategies.Bubble);
        }
    }

    private void SyncSourceScrollBars()
    {
        InstallSourceScrollBars();
        _syncingSourceBars = true;
        try
        {
            var source = _displaySession;
            _sourceVerticalBar!.Maximum = _sourceDocument?.LineCount ?? 1;
            _sourceVerticalBar.ViewportSize = Math.Max(1, _rowCount);
            _sourceVerticalBar.LargeChange = Math.Max(1, _rowCount - 1);
            _sourceVerticalBar.SmallChange = 1;
            _sourceVerticalBar.Value = source?.FirstVisibleLine ?? 1;
            _sourceHorizontalBar!.Maximum = source?.MaximumLineLength ?? 0;
            _sourceHorizontalBar.ViewportSize = _interactiveRows?.EffectiveColumns ?? RowColumns;
            _sourceHorizontalBar.SmallChange = 1;
            _sourceHorizontalBar.LargeChange = Math.Max(1, _sourceHorizontalBar.ViewportSize - 1);
            _sourceHorizontalBar.Value = source?.HorizontalSourceOffset ?? 0;
            _sourceVerticalBar.IsEnabled = source is not null && _sourceVerticalBar.Maximum > 1;
            _sourceHorizontalBar.IsEnabled = source is not null && _sourceHorizontalBar.Maximum > 0;
        }
        finally { _syncingSourceBars = false; }
    }

    private void SourceBarChanged(object? sender, RangeBaseValueChangedEventArgs args)
    {
        if (_syncingSourceBars || _finished || _displaySession is not { } source) return;
        if (_sourceBarGestureGeneration is { } generation && generation != _documentGeneration)
        { SyncSourceScrollBars(); return; }
        CancelPointerSelection();
        source.SetViewport((long)Math.Round(_sourceVerticalBar!.Value), (long)Math.Round(_sourceHorizontalBar!.Value));
        _revealRowsPending = false;
        _sourceBarChanges++;
        RenderInteractive();
    }

    private void SourceBarGestureStarted(object? sender, PointerPressedEventArgs args)
    {
        CancelPointerSelection();
        _sourceBarGestureGeneration = _documentGeneration;
        _sourceBarGestureToken++;
    }

    private void SourceBarGestureEnded(object? sender, PointerReleasedEventArgs args) => RetireSourceBarGesture();
    private void SourceBarCaptureLost(object? sender, PointerCaptureLostEventArgs args) => RetireSourceBarGesture();

    private void RetireSourceBarGesture()
    {
        var token = _sourceBarGestureToken;
        // Keep rejecting stale changes until the native thumb's release handlers have completed.
        Dispatcher.UIThread.Post(() =>
        {
            if (_finished || token != _sourceBarGestureToken) return;
            _sourceBarGestureGeneration = null;
            SyncSourceScrollBars();
        });
    }

    private void ScrollRows(long lines, long columns)
    {
        if (_displaySession is not { } source) return;
        source.ScrollViewport(lines, columns);
        source.SetViewport(source.FirstVisibleLine, Math.Min(source.HorizontalSourceOffset, source.MaximumLineLength));
        _revealRowsPending = false;
        RenderInteractive();
    }

    private void KeepRowCaretPhysicallyVisible()
    {
        var reveal = _revealRowsPending;
        _revealRowsPending = false;
        var view = _editor.TextArea.TextView;
        ((IScrollable)view).Offset = default;
        if (!reveal || _rowRenderer is null || _displaySession is not { } source) return;
        view.EnsureVisualLines();
        var rect = GetRowCaretRectangle(source.Caret);
        if (rect is { } visible && visible.Left >= 0 && visible.Right <= view.Bounds.Width) return;
        var column = source.Position.Column - 1;
        // Try retaining the preceding character. The projection widens its start
        // to a scalar or grapheme boundary. Both attempts stay within the row budget.
        if (column > 0 && TryReprojectAt(column - 1) &&
            GetRowCaretRectangle(source.Caret) is { } withContext &&
            withContext.Left >= 0 && withContext.Right <= view.Bounds.Width)
            return;
        if (!TryReprojectAt(column))
            throw new InvalidOperationException("原文Caretを行別投影へ再表示できません。");

        bool TryReprojectAt(long horizontalOffset)
        {
            source.SetViewport(source.FirstVisibleLine, horizontalOffset);
            _editor.Document = BuildRowDocument(reveal: false);
            if (!source.TryGetRowSelection(_interactiveRows!, out var selection) || selection!.CaretOffset is not { } caret)
                return false;
            _editor.Select(0, 0);
            _editor.CaretOffset = caret;
            _rowRenderer.Apply(_editor.Document, _interactiveRows!, selection);
            ((IScrollable)view).Offset = default;
            view.EnsureVisualLines();
            return true;
        }
    }

    private Rect? GetRowCaretRectangle(long sourceOffset)
    {
        if (_contextLayer is { } layer && _interactiveRows!.TryDisplayPosition(sourceOffset, out var displayOffset)
            && layer.TryGetCaretRectangle(displayOffset, sourceOffset, out var contextual))
            return contextual;
        return _editor.TextArea.Caret.CalculateCaretRectangle();
    }
}
