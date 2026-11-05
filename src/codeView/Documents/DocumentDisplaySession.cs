using AvaloniaEdit.Document;
using CodeView.Contracts;

namespace CodeView.Documents;

/// <summary>
/// Original-source caret/selection for the bounded-display integration candidate.
/// UI-thread owned; not yet connected to the product editor.
/// </summary>
internal sealed class DocumentDisplaySession : IDisposable
{
    internal const int MaximumSliceChars = 16 * 1024;
    private DocumentHandle? _handle;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private TextDocument? _source;
    private DocumentReadLease? _lease;
    private DocumentDisplaySlice? _slice;
    private long _anchor;
    private long _caret;
    private long? _preferredColumn;

    public DocumentDisplaySession(DocumentHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
        _source = handle.ClaimForDisplay();
        _lease = handle.AcquireReadView();
    }

    public long Length { get { VerifyAccess(); return _lease!.Length; } }
    public long Anchor { get { VerifyAccess(); return _anchor; } }
    public long Caret { get { VerifyAccess(); return _caret; } }
    public TextPosition Position { get { VerifyAccess(); return PositionAt(_caret); } }
    public TextPosition AnchorPosition { get { VerifyAccess(); return PositionAt(_anchor); } }
    public long SelectionStart { get { VerifyAccess(); return Math.Min(_anchor, _caret); } }
    public long SelectionLength { get { VerifyAccess(); return Math.Abs(_anchor - _caret); } }

    public void MoveCaret(long offset, bool extendSelection = false)
    {
        VerifyAccess();
        offset = Normalize(offset);
        _caret = offset;
        if (!extendSelection) _anchor = offset;
        _preferredColumn = null;
    }

    public void Select(long anchor, long caret)
    {
        VerifyAccess();
        // Validate both endpoints before changing either one.
        var start = Normalize(anchor);
        var end = Normalize(caret);
        _anchor = start;
        _caret = end;
        _preferredColumn = null;
    }

    public void SelectAll() => Select(0, Length);

    public void SelectLines(long anchorLine, long caretLine)
    {
        VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfLessThan(anchorLine, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(anchorLine, _source!.LineCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(caretLine, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(caretLine, _source.LineCount);
        var anchor = _source.GetLineByNumber(checked((int)anchorLine));
        var caret = _source.GetLineByNumber(checked((int)caretLine));
        // A line selection includes its original delimiter, even if the display slice
        // contains only the middle of that line. The final empty line selects nothing.
        // DocumentLine boundaries already preserve CRLF and scalar boundaries. Avoid
        // re-reading distant rope leaves merely to normalize these known endpoints.
        _anchor = anchorLine <= caretLine ? anchor.Offset : anchor.Offset + anchor.TotalLength;
        _caret = anchorLine <= caretLine ? caret.Offset + caret.TotalLength : caret.Offset;
        _preferredColumn = null;
    }

    public void MoveToPosition(TextPosition position, bool extendSelection = false)
    {
        VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfLessThan(position.Line, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(position.Column, 1);
        if (position.Line > _source!.LineCount) { MoveCaret(Length, extendSelection); return; }
        var line = _source.GetLineByNumber(checked((int)position.Line));
        MoveCaret(line.Offset + Math.Min(position.Column - 1, line.Length), extendSelection);
    }

    public void MoveHorizontal(int direction, bool extendSelection = false)
    {
        VerifyAccess();
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (!extendSelection && _anchor != _caret)
        {
            MoveCaret(direction < 0 ? SelectionStart : Math.Max(_anchor, _caret));
            return;
        }
        var next = direction < 0 ? Math.Max(0, _caret - 1) : Math.Min(Length, _caret + 1);
        if (direction > 0 && SplitsBoundary(next)) next++;
        MoveCaret(next, extendSelection);
    }

    public void MoveLines(int lineDelta, bool extendSelection = false)
    {
        VerifyAccess();
        if (lineDelta == 0) return;
        var current = PositionAt(_caret);
        var column = _preferredColumn ?? current.Column;
        var line = _source!.GetLineByNumber(checked((int)Math.Clamp(current.Line + (long)lineDelta, 1, _source.LineCount)));
        MoveCaret(line.Offset + Math.Min(column - 1, line.Length), extendSelection);
        _preferredColumn = column;
    }

    public void MoveToLineBoundary(bool end, bool extendSelection = false)
    {
        VerifyAccess();
        var line = _source!.GetLineByNumber(checked((int)Position.Line));
        MoveCaret(end ? line.EndOffset : line.Offset, extendSelection);
    }

    public void MoveToDocumentBoundary(bool end, bool extendSelection = false) =>
        MoveCaret(end ? Length : 0, extendSelection);

    public DocumentDisplaySlice CreateSlice(long targetOffset, int maxChars)
    {
        VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxChars, MaximumSliceChars);
        var target = Normalize(targetOffset);
        var start = Math.Clamp(target - maxChars / 4, 0, Math.Max(0, Length - maxChars));
        if (SplitsBoundary(start)) start++;
        var end = Math.Min(Length, start + maxChars);
        if (SplitsBoundary(end)) end--;
        var text = _lease!.ReadRange(start, checked((int)(end - start)));
        // Identity also invalidates delayed events from an earlier slice at the same offsets.
        return _slice = new DocumentDisplaySlice(start, PositionAt(start), target, text);
    }

    public bool TryMoveFromDisplay(DocumentDisplaySlice slice, int offset, bool extendSelection = false)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(slice);
        if (!ReferenceEquals(slice, _slice)) return false;
        MoveCaret(slice.ToSourceOffset(offset), extendSelection);
        return true;
    }

    public bool TryGetDisplaySelection(DocumentDisplaySlice slice, out DocumentDisplaySelection selection)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(slice);
        selection = default;
        if (!ReferenceEquals(slice, _slice)) return false;
        var start = Math.Clamp(SelectionStart - slice.SourceOffset, 0, slice.Text.Length);
        var end = Math.Clamp(Math.Max(_anchor, _caret) - slice.SourceOffset, 0, slice.Text.Length);
        var visibleCaret = _caret >= slice.SourceOffset && _caret <= slice.SourceOffset + slice.Text.Length
            ? (int?)(_caret - slice.SourceOffset) : null;
        selection = new((int)start, checked((int)(end - start)), visibleCaret, _anchor > _caret,
            SelectionLength > 0 && (SelectionStart < slice.SourceOffset || Math.Max(_anchor, _caret) > slice.SourceOffset + slice.Text.Length));
        return true;
    }

    /// <summary>The returned independent snapshot can be copied on a worker and survives tab/view closure.</summary>
    public DocumentSelectionCopy BeginCopy()
    {
        VerifyAccess();
        return new DocumentSelectionCopy(_handle!.AcquireReadView(), SelectionStart, SelectionLength);
    }

    public void Dispose()
    {
        if (_source is null) return;
        VerifyAccess();
        _slice = null;
        _source = null;
        _handle = null;
        _lease?.Dispose();
        _lease = null;
    }

    private long Normalize(long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Length);
        return SplitsBoundary(offset) ? offset - 1 : offset;
    }

    private bool SplitsBoundary(long offset)
    {
        if (offset == 0 || offset == Length) return false;
        char left, right;
        if (_slice is { } slice && offset > slice.SourceOffset && offset < slice.SourceOffset + slice.Text.Length)
        {
            // The immutable display text already contains both characters. Avoid traversing the
            // rope between distant leaves for each pointer hit inside the current display slice.
            var local = checked((int)(offset - slice.SourceOffset));
            left = slice.Text[local - 1];
            right = slice.Text[local];
        }
        else
        {
            left = _source!.GetCharAt(checked((int)(offset - 1)));
            right = _source.GetCharAt(checked((int)offset));
        }
        return (char.IsHighSurrogate(left) && char.IsLowSurrogate(right)) || (left == '\r' && right == '\n');
    }

    private TextPosition PositionAt(long offset)
    {
        var location = _source!.GetLocation(checked((int)offset));
        if (location.Line < 1 || location.Column < 1)
            throw new OverflowException("本文位置を表示モデルで表現できません。");
        return new TextPosition(location.Line, location.Column);
    }

    private void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(_source is null, this);
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("表示セッションは作成元のUIスレッドで操作してください。");
    }
}

/// <summary>A bounded rendering slice. Its coordinates never replace original-source coordinates.</summary>
internal sealed record DocumentDisplaySlice(long SourceOffset, TextPosition SourceStart, long TargetOffset, string Text)
{
    public int TargetDisplayOffset => checked((int)(TargetOffset - SourceOffset));

    public long ToSourceOffset(int displayOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(displayOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(displayOffset, Text.Length);
        return checked(SourceOffset + displayOffset);
    }
}

internal readonly record struct DocumentDisplaySelection(int Start, int Length, int? CaretOffset, bool IsReversed, bool IsClipped);

internal sealed class DocumentSelectionCopy(DocumentReadLease lease, long offset, long length) : IDisposable
{
    private DocumentReadLease? _lease = lease;
    public long Offset { get; } = offset;
    public long Length { get; } = length;

    public void WriteTo(TextWriter destination, CancellationToken ct) =>
        (_lease ?? throw new ObjectDisposedException(nameof(DocumentSelectionCopy))).CopyRangeTo(Offset, Length, destination, ct);

    public string ReadText(CancellationToken ct) =>
        (_lease ?? throw new ObjectDisposedException(nameof(DocumentSelectionCopy))).ReadRangeAsString(Offset, checked((int)Length), ct);

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}
