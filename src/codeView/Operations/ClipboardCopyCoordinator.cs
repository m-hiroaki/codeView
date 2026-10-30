using System.Runtime.ExceptionServices;
using CodeView.Documents;

namespace CodeView.Operations;

internal enum ClipboardCopyStatus { Copied, EmptySelection, Cancelled, Unavailable }
internal sealed record ClipboardCopyResult(ClipboardCopyStatus Status, long Characters, bool WriteStarted);
internal sealed class ClipboardUnavailableException : Exception;

internal sealed class ClipboardCopyRequest
{
    private readonly TaskCompletionSource<ClipboardCopyResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _lifetime = new();
    internal DocumentSelectionCopy? Selection { get; private set; }
    public long Characters { get; }
    public bool WriteStarted { get; internal set; }
    internal bool Cancelled { get; private set; }
    internal CancellationToken Cancellation => _lifetime?.Token ?? CancellationToken.None;
    public Task<ClipboardCopyResult> Completion => _completion.Task;

    internal ClipboardCopyRequest(DocumentSelectionCopy selection)
    {
        Selection = selection;
        Characters = selection.Length;
        _ = _completion.Task.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal void Cancel() { Cancelled = true; _lifetime?.Cancel(); }
    internal void ReleaseSnapshot() { Selection?.Dispose(); Selection = null; }
    internal void Release() { ReleaseSnapshot(); _lifetime?.Dispose(); _lifetime = null; }
    internal void Complete(ClipboardCopyStatus status) => _completion.TrySetResult(new(status, Characters, WriteStarted));
    internal void Fail(Exception error) => _completion.TrySetException(error);
}

/// <summary>
/// UI-owned, one active preparation/write and one latest waiter.
/// An uncancellable clipboard write keeps the slot until its real completion.
/// </summary>
internal sealed class ClipboardCopyCoordinator : IDisposable
{
    private readonly Func<string, Task> _write;
    private readonly Func<DocumentSelectionCopy, CancellationToken, Task<string>> _prepare;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private ClipboardCopyRequest? _waiting, _active;
    private bool _pumping, _disposed, _faulted;
    private Task _pump = Task.CompletedTask;
    private ExceptionDispatchInfo? _failure;
    public ClipboardCopyRequest? Current { get; private set; }
    public Task PendingWork => ObserveAsync();

    public ClipboardCopyCoordinator(Func<string, Task> write,
        Func<DocumentSelectionCopy, CancellationToken, Task<string>>? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        _write = write;
        _prepare = prepare ?? ((selection, ct) => Task.Run(() => selection.ReadText(ct), ct));
    }

    /// <summary>Takes ownership after successful admission; a rejected call leaves ownership with its caller.</summary>
    public ClipboardCopyRequest Start(DocumentSelectionCopy selection)
    {
        VerifyOwner();
        ObjectDisposedException.ThrowIf(_disposed || _faulted, this);
        ArgumentNullException.ThrowIfNull(selection);
        var request = new ClipboardCopyRequest(selection);
        if (selection.Length == 0)
        {
            request.Complete(ClipboardCopyStatus.EmptySelection);
            request.Release();
            return request; // Empty selection does not change the clipboard or replace prior work.
        }
        if (Current is { } old) Cancel(old);
        Current = _waiting = request;
        if (!_pumping) _pump = PumpAsync();
        return request;
    }

    public bool CanAdopt(ClipboardCopyRequest request) => !_disposed && !_faulted && !request.Cancelled && ReferenceEquals(Current, request);

    public void Invalidate()
    {
        VerifyOwner();
        if (Current is { } current) Cancel(current);
        if (_active is { } active) active.Cancel();
        Current = null;
    }

    private void Cancel(ClipboardCopyRequest request)
    {
        request.Cancel();
        if (!ReferenceEquals(_waiting, request)) return;
        _waiting = null;
        request.Complete(ClipboardCopyStatus.Cancelled);
        request.Release();
    }

    private async Task PumpAsync()
    {
        _pumping = true;
        try
        {
            while (!_disposed && !_faulted && _waiting is { } request)
            {
                _waiting = null;
                _active = request;
                string? text = null;
                var token = request.Cancellation;
                try
                {
                    if (!CanAdopt(request)) { request.Complete(ClipboardCopyStatus.Cancelled); continue; }
                    text = await _prepare(request.Selection!, token);
                    request.ReleaseSnapshot();
                    if (!CanAdopt(request)) { request.Complete(ClipboardCopyStatus.Cancelled); continue; }
                    if (text is null || text.Length != request.Characters)
                        throw new InvalidOperationException("コピー本文の長さが選択範囲と一致しません。");
                    request.WriteStarted = true;
                    await _write(text);
                    request.Complete(CanAdopt(request) ? ClipboardCopyStatus.Copied : ClipboardCopyStatus.Cancelled);
                }
                catch (OperationCanceledException error) when (token.IsCancellationRequested && error.CancellationToken == token)
                { request.Complete(ClipboardCopyStatus.Cancelled); }
                catch (ClipboardUnavailableException)
                { request.Complete(CanAdopt(request) ? ClipboardCopyStatus.Unavailable : ClipboardCopyStatus.Cancelled); }
                catch (Exception error)
                {
                    _faulted = true;
                    _failure = ExceptionDispatchInfo.Capture(error);
                    request.Fail(error);
                    if (_waiting is { } waiting) Cancel(waiting);
                }
                finally
                {
                    text = null;
                    request.Release();
                    _active = null;
                }
            }
        }
        finally { _pumping = false; }
    }

    private async Task ObserveAsync() { await _pump; _failure?.Throw(); }

    public void Dispose()
    {
        VerifyOwner();
        if (_disposed) return;
        _disposed = true;
        Invalidate();
    }

    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("コピー要求はUI所有スレッドで操作してください。");
    }
}
