namespace CodeView.Operations;

/// <summary>Owned and changed only by the serialized operation entry point.</summary>
internal sealed class OperationIdentity
{
    private long _next;
    public long Current { get; private set; }

    public long Advance() => Current = checked(++_next);

    public bool CanAdopt(long epoch, bool ownerExists, bool cancellationRequested)
        => epoch > 0 && epoch == Current && ownerExists && !cancellationRequested;
}
