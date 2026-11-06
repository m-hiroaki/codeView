using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using CodeView.Operations;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private async Task<object> CheckRowLifecycleAsync(string originalBody, CancellationToken ct)
    {
        var primaryPath = _viewModel.FilePath;
        var secondaryPath = Environment.GetEnvironmentVariable("CODEVIEW_GUI_ROW_SECONDARY")
            ?? throw new InvalidOperationException("No secondary lifecycle fixture.");
        RequireRows(!string.Equals(primaryPath, secondaryPath, StringComparison.Ordinal), "Use distinct lifecycle fixtures.");
        var checks = new List<string>();
        void Check(string name, bool passed) { RequireRows(passed, name); checks.Add(name); }
        var primaryTab = _viewModel.SelectedTab!;
        var firstHandle = _rowViewHandle!;
        var first = _displaySession!;
        first.Select(Math.Min(3, first.Length), 0);
        first.SetViewport(_sourceDocument!.LineCount, Math.Min(32, first.MaximumLineLength));
        RenderInteractive();
        var expected = new RowViewState(first.Anchor, first.Caret, first.FirstVisibleLine, first.HorizontalSourceOffset);
        await _viewModel.OpenFilesAsync([secondaryPath], OpenOrigin.Menu).WaitAsync(ct);
        await RowFrame(ct);
        var secondaryTab = _viewModel.SelectedTab!;
        var secondaryHandle = _rowViewHandle!;
        Check("real-tab-opens-secondary", !ReferenceEquals(primaryTab, secondaryTab) && _viewModel.Tabs.Count == 2);
        Check("inactive-tab-keeps-no-view-lease", firstHandle.ActiveReadLeases == 0 && !firstHandle.IsReleased);
        var secondary = _displaySession!;
        secondary.Select(secondary.Length, 0);
        secondary.SetViewport(_sourceDocument!.LineCount, Math.Min(4, secondary.MaximumLineLength));
        RenderInteractive();
        var expectedSecondary = new RowViewState(secondary.Anchor, secondary.Caret, secondary.FirstVisibleLine, secondary.HorizontalSourceOffset);
        _viewModel.SelectedTab = primaryTab;
        await RowFrame(ct);
        Check("real-tab-restores-selection-and-view", ReferenceEquals(_rowViewHandle, firstHandle) && CurrentRowView() == expected);
        Check("tab-restore-keeps-offscreen-caret", originalBody.Length == 0 || !_interactiveRows!.TryDisplayPosition(_displaySession!.Caret, out _));
        _viewModel.SelectedTab = secondaryTab;
        await RowFrame(ct);
        Check("secondary-tab-restores-independent-state", ReferenceEquals(_rowViewHandle, secondaryHandle) && CurrentRowView() == expectedSecondary);
        _viewModel.SelectedTab = primaryTab;
        await RowFrame(ct);
        var oldRows = _interactiveRows!;
        var oldGeneration = _documentGeneration;
        var oldSelection = _displaySession!.BeginCopy();
        var copied = oldSelection.ReadText(ct);
        try
        {
            await _viewModel.ReloadAsync().WaitAsync(ct);
            await RowFrame(ct);
            Check("real-reload-replaces-body-identity", !ReferenceEquals(_rowViewHandle, firstHandle) && firstHandle.IsReleased && _documentGeneration != oldGeneration);
            Check("reload-does-not-restore-old-selection-or-view", _displaySession!.Anchor == 0 && _displaySession.Caret == 0 &&
                _displaySession.FirstVisibleLine == 1 && _displaySession.HorizontalSourceOffset == 0);
            Check("reload-rejects-old-projection", !_displaySession.TryMoveFromRows(oldRows, 0));
            Check("reload-copy-lease-keeps-original", oldSelection.ReadText(ct) == copied && firstHandle.ActiveReadLeases == 1);
        }
        finally { oldSelection.Dispose(); }
        Check("retired-copy-lease-released", firstHandle.ActiveReadLeases == 0);

        using var pointer = new Pointer(100002, PointerType.Mouse, true);
        var view = _editor.TextArea.TextView;
        Point Root(Point point) => view.TranslatePoint(point, _window) ?? throw new InvalidOperationException("No lifecycle pointer transform.");
        void Raise(PointerEventArgs args)
        {
            _scriptedRowPointer = args;
            try { _editor.RaiseEvent(args); }
            finally { _scriptedRowPointer = null; }
            RequireRows(!_finished, "Lifecycle pointer failed.");
        }
        void Press() => Raise(new PointerPressedEventArgs(_editor, pointer, _window, Root(new Point(1, 5)), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        Press();
        Check("tab-switch-starts-with-capture", pointer.Captured == _editor && _pointerSelection.IsActive);
        _viewModel.SelectedTab = secondaryTab;
        await RowFrame(ct);
        Check("real-tab-switch-releases-capture", pointer.Captured is null && !_pointerSelection.IsActive && _pointerTimer?.IsEnabled != true);
        _viewModel.SelectedTab = primaryTab;
        await RowFrame(ct);
        var reloadedHandle = _rowViewHandle!;
        Press();
        Raise(new PointerEventArgs(InputElement.PointerMovedEvent, _editor, pointer, _window, Root(new Point(10, view.Bounds.Height + 10)), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
        Check("close-starts-with-edge-timer", pointer.Captured == _editor && _pointerTimer?.IsEnabled == true);
        _viewModel.CloseFile();
        await RowFrame(ct);
        Check("real-close-releases-capture-and-body", pointer.Captured is null && !_pointerSelection.IsActive && _pointerTimer?.IsEnabled != true &&
            reloadedHandle.IsReleased && reloadedHandle.ActiveReadLeases == 0 && _viewModel.Tabs.Count == 1);
        await _viewModel.OpenFilesAsync([primaryPath], OpenOrigin.Menu).WaitAsync(ct);
        await RowFrame(ct);
        Check("reopen-uses-fresh-view", !ReferenceEquals(_rowViewHandle, reloadedHandle) && _displaySession!.Caret == 0 &&
            _displaySession.Anchor == 0 && _displaySession.FirstVisibleLine == 1 && _displaySession.HorizontalSourceOffset == 0);

        await _viewModel.OpenFilesAsync([Path.GetDirectoryName(primaryPath)!], OpenOrigin.Menu).WaitAsync(ct);
        Check("real-search-workspace-ready", _viewModel.CanSearchWorkspace);
        _displaySession!.MoveToDocumentBoundary(true);
        RevealRowCaret();
        RenderInteractive();
        var panel = _viewModel.SearchPanel;
        var scalarLength = originalBody.Length > 1 && char.IsHighSurrogate(originalBody[0]) && char.IsLowSurrogate(originalBody[1]) ? 2 : Math.Min(1, originalBody.Length);
        panel.Query = scalarLength == 0 ? "CODEVIEW_LIFECYCLE_NO_MATCH_9ACB" : originalBody[..scalarLength];
        panel.MatchCase = true; panel.UseRegex = false; panel.WholeWord = false; panel.IsVisible = true;
        await panel.StartAsync().WaitAsync(ct);
        await panel.PendingWork.WaitAsync(ct);
        await RowFrame(ct);
        SearchResultRow? selected = null;
        if (scalarLength > 0)
        {
            selected = panel.Rows.FirstOrDefault(r => r.FullPath == primaryPath && r.Hit.Range.Start.Line == 1 && r.Hit.Range.Start.Column == 1);
            Check("real-search-committed-primary-hit", selected is not null && panel.CanActivate(selected));
            var results = _window.FindControl<ListBox>("SearchResultsList") ?? throw new InvalidOperationException("No search results control.");
            results.SelectedItem = selected;
            var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            long? caretAtRestore = null;
            void Restored(int line, int column) => caretAtRestore = _displaySession!.Caret;
            _viewModel.RestoreCaret += Restored;
            try
            {
                _scriptedRowKey = enter;
                try { results.RaiseEvent(enter); }
                finally { _scriptedRowKey = null; }
                await _viewModel.PendingWork.WaitAsync(ct);
                Check("search-caret-before-deferred-selection", caretAtRestore == 0);
                await RowFrame(ct);
            }
            finally { _viewModel.RestoreCaret -= Restored; }
            Check("real-search-enter-selects-original", enter.Handled && _displaySession!.SelectionStart == 0 &&
                _displaySession.SelectionLength == scalarLength && _displaySession.Caret == 0 && _interactiveRows!.TryDisplayPosition(0, out _));
        }
        else Check("empty-search-has-no-fabricated-hit", panel.Rows.Count == 0);
        _viewModel.CloseWorkspace();
        var before = CurrentRowView();
        if (selected is not null) await _viewModel.ActivateSearchResultAsync(selected).WaitAsync(ct);
        Check("closed-search-cannot-adopt-stale-result", !panel.Rows.Any() && CurrentRowView() == before);
        panel.IsVisible = false;
        await RowFrame(ct);
        Check("lifecycle-body-unchanged", _sourceDocument!.Text == originalBody && _viewModel.FilePath == primaryPath);
        Check("lifecycle-workload-started-once", _rowWorkStarts == 1 && !_finished);
        return new { Checks = checks, SearchOutcome = scalarLength == 0 ? "empty" : "hit", WorkloadStarts = _rowWorkStarts,
            History = "weak handle keys, scalar values only" };
    }

    private RowViewState CurrentRowView() => new(_displaySession!.Anchor, _displaySession.Caret,
        _displaySession.FirstVisibleLine, _displaySession.HorizontalSourceOffset);
}
