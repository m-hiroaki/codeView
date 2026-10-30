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
    public long SelectionStart { get { VerifyAccess(); return Math.Min(_anchor, _caret); } }
    public long SelectionLength { get { VerifyAccess(); return Math.Abs(_anchor - _caret); } }

    public void MoveCaret(long offset, bool extendSelection = false)
    {
        VerifyAccess();
        offset = Normalize(offset);
        _caret = offset;
        if (!extendSelection) _anchor = offset;
    }

    public void Select(long anchor, long caret)
    {
        VerifyAccess();
        // Validate both endpoints before changing either one.
        var start = Normalize(anchor);
        var end = Normalize(caret);
        _anchor = start;
        _caret = end;
    }

    public void SelectAll() => Select(0, Length);

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
        var left = _source!.GetCharAt(checked((int)(offset - 1)));
        var right = _source.GetCharAt(checked((int)offset));
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

internal sealed class DocumentSelectionCopy(DocumentReadLease lease, long offset, long length) : IDisposable
{
    private DocumentReadLease? _lease = lease;
    public long Offset { get; } = offset;
    public long Length { get; } = length;

    public void WriteTo(TextWriter destination, CancellationToken ct) =>
        (_lease ?? throw new ObjectDisposedException(nameof(DocumentSelectionCopy))).CopyRangeTo(Offset, Length, destination, ct);

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}
