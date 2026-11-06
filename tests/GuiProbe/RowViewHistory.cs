using System.Runtime.CompilerServices;
using CodeView.Documents;

namespace CodeView.Presentation;

/// <summary>Probe-only primitive view state. Weak keys never keep a retired body or lease alive.</summary>
internal sealed class RowViewHistory
{
    private readonly ConditionalWeakTable<DocumentHandle, RowViewState> _views = new();
    private readonly int _owner = Environment.CurrentManagedThreadId;

    public void Remember(DocumentHandle handle, RowViewState state)
    {
        VerifyOwner();
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(state);
        ObjectDisposedException.ThrowIf(handle.IsReleased, handle);
        if (state.Anchor < 0 || state.Anchor > handle.Length || state.Caret < 0 || state.Caret > handle.Length ||
            state.FirstLine < 1 || state.FirstLine > handle.Length + 1 || state.Horizontal < 0 || state.Horizontal > handle.Length)
            throw new ArgumentOutOfRangeException(nameof(state));
        _views.Remove(handle);
        _views.Add(handle, state);
    }

    public bool TryGet(DocumentHandle handle, out RowViewState? state)
    {
        VerifyOwner();
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsReleased) { _views.Remove(handle); state = null; return false; }
        return _views.TryGetValue(handle, out state);
    }

    public void Clear() { VerifyOwner(); _views.Clear(); }

    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != _owner)
            throw new InvalidOperationException("閲覧状態は表示の所有スレッドで操作してください。");
    }
}

internal sealed record RowViewState(long Anchor, long Caret, long FirstLine, long Horizontal);
