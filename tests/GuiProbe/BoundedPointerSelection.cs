using CodeView.Documents;

namespace CodeView.Presentation;

/// <summary>Probe-only drag state. Does not retain documents, slices or pointer devices.</summary>
internal sealed class BoundedPointerSelection
{
    public long? PointerId { get; private set; }
    public bool IsActive => PointerId.HasValue;
    private long? _lineAnchor;
    private long _lineTarget;

    public bool Begin(DocumentDisplaySession source, DocumentDisplaySlice slice, int offset, long pointerId, bool extend)
    {
        if (IsActive) return false;
        if (!source.TryMoveFromDisplay(slice, offset, extend)) return false;
        PointerId = pointerId;
        return true;
    }

    public bool BeginLine(DocumentDisplaySession source, DocumentDisplaySlice slice, int offset, long pointerId, bool extend)
    {
        if (IsActive) return false;
        var anchorPosition = source.AnchorPosition;
        var anchor = anchorPosition.Line;
        if (source.Anchor > source.Caret && anchorPosition.Column == 1 && anchor > 1) anchor--;
        if (!source.TryMoveFromDisplay(slice, offset)) return false;
        _lineTarget = source.Position.Line;
        _lineAnchor = extend ? anchor : _lineTarget;
        source.SelectLines(_lineAnchor.Value, _lineTarget);
        PointerId = pointerId;
        return true;
    }

    public bool Move(DocumentDisplaySession source, DocumentDisplaySlice slice, int offset, long pointerId)
    {
        if (PointerId != pointerId || !source.TryMoveFromDisplay(slice, offset, extendSelection: true)) return false;
        if (_lineAnchor is { } anchor)
        {
            _lineTarget = source.Position.Line;
            source.SelectLines(anchor, _lineTarget);
        }
        return true;
    }

    public bool StepOutside(DocumentDisplaySession source, long pointerId, int horizontal, int vertical)
    {
        if (horizontal is < -1 or > 1 || vertical is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(horizontal));
        if (PointerId != pointerId) return false;
        var before = source.Caret;
        var beforeLine = _lineTarget;
        if (_lineAnchor is { } anchor)
        {
            if (vertical == 0) return false;
            source.MoveToPosition(new(Math.Max(1, _lineTarget + vertical), 1));
            _lineTarget = source.Position.Line;
            source.SelectLines(anchor, _lineTarget);
        }
        else if (vertical != 0) source.MoveLines(vertical, extendSelection: true);
        else if (horizontal != 0)
            for (var index = 0; index < 32; index++) source.MoveHorizontal(horizontal, extendSelection: true);
        return source.Caret != before || (_lineAnchor.HasValue && _lineTarget != beforeLine);
    }

    public bool End(long pointerId)
    {
        if (PointerId != pointerId) return false;
        Cancel();
        return true;
    }

    public void Cancel()
    {
        PointerId = null;
        _lineAnchor = null;
        _lineTarget = 0;
    }
}
