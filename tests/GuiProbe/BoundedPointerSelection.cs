using CodeView.Documents;

namespace CodeView.Presentation;

/// <summary>Probe-only drag state. Does not retain documents, slices or pointer devices.</summary>
internal sealed class BoundedPointerSelection
{
    public long? PointerId { get; private set; }
    public bool IsActive => PointerId.HasValue;

    public bool Begin(DocumentDisplaySession source, DocumentDisplaySlice slice, int offset, long pointerId, bool extend)
    {
        if (IsActive) return false;
        if (!source.TryMoveFromDisplay(slice, offset, extend)) return false;
        PointerId = pointerId;
        return true;
    }

    public bool Move(DocumentDisplaySession source, DocumentDisplaySlice slice, int offset, long pointerId) =>
        PointerId == pointerId && source.TryMoveFromDisplay(slice, offset, extendSelection: true);

    public bool StepOutside(DocumentDisplaySession source, long pointerId, int horizontal, int vertical)
    {
        if (horizontal is < -1 or > 1 || vertical is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(horizontal));
        if (PointerId != pointerId) return false;
        var before = source.Caret;
        if (vertical != 0) source.MoveLines(vertical, extendSelection: true);
        else if (horizontal != 0)
            for (var index = 0; index < 32; index++) source.MoveHorizontal(horizontal, extendSelection: true);
        return source.Caret != before;
    }

    public bool End(long pointerId)
    {
        if (PointerId != pointerId) return false;
        Cancel();
        return true;
    }

    public void Cancel() => PointerId = null;
}
