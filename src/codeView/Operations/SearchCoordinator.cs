using CodeView.Contracts;
using CodeView.Files;
using CodeView.Search;

namespace CodeView.Operations;

internal enum SearchCancellationCause { None, User, Replaced, Cleared, WorkspaceChanged, Shutdown, Fatal }

internal sealed class WorkspaceSearchOperation
{
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<OperationResult<SearchCompletion>> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal WorkspaceState Workspace { get; }
    internal CancellationTokenSource? Lifetime { get; set; }
    internal CancellationToken Token { get; }
    internal WorkspaceSearchSession? Session { get; set; }
    public WorkspaceSearchRequest Request { get; }
    public SearchCancellationCause CancellationCause { get; internal set; }
    public Task<bool> Started => _started.Task;
    public Task<OperationResult<SearchCompletion>> Completion => _completion.Task;
    public SearchProgress? LatestProgress => Session?.LatestProgress;

    internal WorkspaceSearchOperation(WorkspaceState workspace, WorkspaceSearchRequest request)
    {
        (Workspace, Request) = (workspace, request);
        Lifetime = CancellationTokenSource.CreateLinkedTokenSource(workspace.Token);
        Token = Lifetime.Token;
    }

    internal void Start(WorkspaceSearchSession session) { Session = session; _started.TrySetResult(true); }
    internal void Complete(OperationResult<SearchCompletion> result) { _started.TrySetResult(false); _completion.TrySetResult(result); }
    internal void Fail(Exception failure)
    {
        _started.TrySetResult(false);
        _completion.TrySetException(failure);
        // A replaced view may abandon this notification. PendingWork still propagates the fatal failure.
        _ = _completion.Task.Exception;
    }
    internal void Release() { Lifetime?.Dispose(); Lifetime = null; }
}

/// <summary>All entry points and continuations are owned by the serialized operation context.</summary>
internal sealed class SearchCoordinator(Func<WorkspaceSearchRequest, CancellationToken, WorkspaceSearchSession> start,
    Func<WorkspaceState?> workspace, OperationIdentity epoch, OperationIdentity requestIds) : IDisposable
{
    private WorkspaceSearchOperation? _waiting;
    private WorkspaceSearchOperation? _active;
    private Task _pump = Task.CompletedTask;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _failure;
    private bool _pumping, _disposed, _faulted;

    public WorkspaceSearchOperation? Current { get; private set; }
    public bool CanStart => !_disposed && !_faulted && workspace()?.CanSearch == true;
    public Task PendingWork => ObservePendingAsync();

    public OperationResult<WorkspaceSearchOperation> Start(string? query, SearchOptions options = default,
        EncodingChoice encoding = EncodingChoice.Auto, bool definitions = false)
    {
        if (!CanStart) return OperationResult<WorkspaceSearchOperation>.Cancelled();
        if (!Enum.IsDefined(encoding)) throw new ArgumentOutOfRangeException(nameof(encoding));
        var owner = workspace()!;
        var request = new WorkspaceSearchRequest(owner.ResolvedPath!, owner.Policy, encoding, query, options,
            requestIds.Advance(), owner.Epoch, epoch.Advance(), definitions);
        if (Current is { } previous) Stop(previous, SearchCancellationCause.Replaced);
        var operation = new WorkspaceSearchOperation(owner, request);
        Current = _waiting = operation;
        if (!_pumping) _pump = PumpAsync();
        return OperationResult<WorkspaceSearchOperation>.Success(operation);
    }

    public void Cancel()
    {
        if (Current is { } current) Stop(current, SearchCancellationCause.User);
    }

    public void Clear()
    {
        epoch.Advance();
        Invalidate(SearchCancellationCause.Cleared);
    }

    // AppOperations advances the shared epoch before replacing/closing the root.
    internal void InvalidateWorkspace() => Invalidate(SearchCancellationCause.WorkspaceChanged);

    private void Invalidate(SearchCancellationCause cause)
    {
        if (Current is { } current) Stop(current, cause);
        Current = null;
    }

    public bool CanAdoptEvents(WorkspaceSearchOperation operation) => IsCurrent(operation)
        && operation.CancellationCause == SearchCancellationCause.None && !operation.Token.IsCancellationRequested;

    public bool CanAdoptTerminal(WorkspaceSearchOperation operation) => IsCurrent(operation)
        && operation.CancellationCause is SearchCancellationCause.None or SearchCancellationCause.User;

    public bool TryRead(WorkspaceSearchOperation operation, out SearchEvent<SearchHit>? item)
    {
        item = null;
        return CanAdoptEvents(operation) && operation.Session is { } session && session.Events.TryRead(out item);
    }

    public ValueTask<bool> WaitForEventsAsync(WorkspaceSearchOperation operation) =>
        CanAdoptEvents(operation) && operation.Session is { } session
            ? session.Events.WaitToReadAsync(operation.Token) : ValueTask.FromResult(false);

    public bool TryFinalize(WorkspaceSearchOperation operation, out OperationResult<SearchCompletion>? result)
    {
        result = null;
        if (!CanAdoptTerminal(operation) || !operation.Completion.IsCompletedSuccessfully) return false;
        if (operation.Session is { } session && !session.Events.Completion.IsCompletedSuccessfully) return false;
        result = operation.Completion.Result;
        return true;
    }

    private bool IsCurrent(WorkspaceSearchOperation operation) => !_disposed && !_faulted
        && ReferenceEquals(Current, operation) && ReferenceEquals(workspace(), operation.Workspace)
        && operation.Workspace.CanSearch && operation.Request.WorkspaceEpoch == operation.Workspace.Epoch
        && operation.Request.SearchEpoch == epoch.Current;

    private void Stop(WorkspaceSearchOperation operation, SearchCancellationCause cause)
    {
        // Replacement/clear may supersede an earlier explicit cancel.
        if (cause == SearchCancellationCause.User && operation.CancellationCause != SearchCancellationCause.None) return;
        operation.CancellationCause = cause;
        operation.Lifetime?.Cancel();
        Discard(operation);
        if (ReferenceEquals(_waiting, operation))
        {
            _waiting = null;
            operation.Complete(OperationResult<SearchCompletion>.Cancelled());
            operation.Release();
        }
    }

    private static void Discard(WorkspaceSearchOperation operation)
    {
        if (operation.Session is not { } session) return;
        while (session.Events.TryRead(out _)) { }
    }

    private async Task PumpAsync()
    {
        _pumping = true;
        try
        {
            while (!_disposed && !_faulted && _waiting is { } operation)
            {
                _waiting = null;
                _active = operation;
                try
                {
                    operation.Token.ThrowIfCancellationRequested();
                    var session = start(operation.Request, operation.Token);
                    operation.Start(session);
                    var end = await session.Completion;
                    operation.Complete(OperationResult<SearchCompletion>.Success(end));
                }
                catch (OperationCanceledException cancelled) when (operation.Token.IsCancellationRequested && cancelled.CancellationToken == operation.Token)
                { operation.Complete(OperationResult<SearchCompletion>.Cancelled()); }
                catch (Exception failure)
                {
                    _faulted = true;
                    _failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure);
                    operation.CancellationCause = SearchCancellationCause.Fatal;
                    operation.Fail(failure);
                    if (_waiting is { } waiting) Stop(waiting, SearchCancellationCause.Fatal);
                }
                finally
                {
                    if (operation.CancellationCause != SearchCancellationCause.None || !IsCurrent(operation)) Discard(operation);
                    // The worker is already terminal; discarding a failed channel makes its failure observable.
                    if (operation.Session is { } session && session.Events.Completion.IsCompleted)
                    {
                        try { await session.Events.Completion; }
                        catch (SearchWorkerException) when (_faulted) { }
                    }
                    operation.Release();
                    _active = null;
                }
            }
        }
        finally { _pumping = false; }
    }

    private async Task ObservePendingAsync()
    {
        await _pump;
        _failure?.Throw();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        epoch.Advance();
        Invalidate(SearchCancellationCause.Shutdown);
        if (_active is { } active) Stop(active, SearchCancellationCause.Shutdown);
    }
}
