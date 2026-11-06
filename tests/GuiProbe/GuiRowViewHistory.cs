using CodeView.Documents;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private RowViewHistory? _rowHistory;
    private DocumentHandle? _rowViewHandle;
    private int? _restoredRowAttachmentGeneration;
    private int _rowWorkStarts;

    private void RememberRowView()
    {
        if (_rowsEnabled && _rowViewHandle is { IsReleased: false } handle && _displaySession is { } source)
            (_rowHistory ??= new()).Remember(handle, new(source.Anchor, source.Caret, source.FirstVisibleLine, source.HorizontalSourceOffset));
        _rowViewHandle = null;
        _restoredRowAttachmentGeneration = null;
    }

    private bool RestoreRowView(DocumentHandle handle)
    {
        _rowViewHandle = handle;
        if (_rowHistory is null || !_rowHistory.TryGet(handle, out var state)) return false;
        _displaySession!.Select(state!.Anchor, state.Caret);
        _displaySession.SetViewport(state.FirstLine, state.Horizontal);
        _restoredRowAttachmentGeneration = _documentGeneration;
        return true;
    }

    private bool PreserveRestoredRowAttachment(int line, int column)
    {
        var restored = _restoredRowAttachmentGeneration == _documentGeneration;
        _restoredRowAttachmentGeneration = null;
        // Ignore only the matching synchronous attachment notification, never a subsequent Go To.
        return restored && _displaySession!.Position == new CodeView.Contracts.TextPosition(line, column);
    }

    private void ClearRowHistory()
    {
        _rowViewHandle = null;
        _restoredRowAttachmentGeneration = null;
        _rowHistory?.Clear();
        _rowHistory = null;
    }
}
