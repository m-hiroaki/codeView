using System.Runtime.ExceptionServices;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Search;

namespace CodeView.Operations;

internal sealed record SearchResultMove(TabState Tab, long DocumentVersion, long LoadEpoch, long SelectionEpoch, DocumentMove Move);

internal sealed class SearchResultMoveRequest(WorkspaceSearchOperation source, SearchHit hit)
{
    internal WorkspaceSearchOperation Source { get; } = source;
    internal SearchHit Hit { get; } = hit;
    internal CancellationTokenSource? Lifetime { get; private set; } = CancellationTokenSource.CreateLinkedTokenSource(source.Workspace.Token);
    internal TaskCompletionSource<TabState?> StartedSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<OperationResult<SearchResultMove>> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<TabState?> Started => StartedSource.Task;
    public Task<OperationResult<SearchResultMove>> Completion => CompletionSource.Task;
    internal CancellationToken Cancellation => Lifetime?.Token ?? CancellationToken.None;
    internal bool WasCancelled { get; private set; }
    internal void Cancel() { WasCancelled = true; Lifetime?.Cancel(); }
    internal void Release() { Lifetime?.Dispose(); Lifetime = null; }
}

/// <summary>One active move and one latest waiter; metadata I/O retains the slot until it really ends.</summary>
internal sealed class SearchResultNavigator(TabManager tabs, SearchCoordinator searches, FileReader files) : IDisposable
{
    private SearchResultMoveRequest? _waiting, _active;
    private bool _pumping, _disposed, _faulted;
    private Task _pump = Task.CompletedTask;
    private ExceptionDispatchInfo? _failure;
    public SearchResultMoveRequest? Current { get; private set; }
    public Task PendingWork => ObserveAsync();

    public SearchResultMoveRequest Start(WorkspaceSearchOperation source, SearchHit hit)
    {
        if (_disposed || _faulted) throw new ObjectDisposedException(nameof(SearchResultNavigator));
        if (Current is { } old) Cancel(old);
        var request = new SearchResultMoveRequest(source, hit);
        Current = _waiting = request;
        if (!_pumping) _pump = PumpAsync();
        return request;
    }

    public bool CanAdopt(SearchResultMoveRequest request, SearchResultMove move) => CanAdoptTarget(request, move.Tab,
        move.DocumentVersion, move.LoadEpoch, move.SelectionEpoch);

    private bool CanAdoptTarget(SearchResultMoveRequest request, TabState tab, long version, long loadEpoch, long selection)
        => Valid(request) && ReferenceEquals(tabs.ActiveTab, tab) && tabs.SelectionEpoch == selection
        && tab.Status == TabStatus.Ready && tab.DocumentVersion == version && tab.LoadEpoch == loadEpoch;

    private bool Valid(SearchResultMoveRequest request) => !_disposed && !_faulted && ReferenceEquals(Current, request)
        && !request.WasCancelled && searches.CanAdoptTerminal(request.Source);

    private void Cancel(SearchResultMoveRequest request)
    {
        request.Cancel();
        if (!ReferenceEquals(_waiting, request)) return;
        _waiting = null;
        request.StartedSource.TrySetResult(null);
        request.CompletionSource.TrySetResult(OperationResult<SearchResultMove>.Cancelled());
        request.Release();
    }

    public void Clear()
    {
        if (Current is { } current) Cancel(current);
        Current = null;
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
                try
                {
                    if (!Valid(request)) { CompleteCancelled(request); continue; }
                    var load = tabs.OpenAsync(request.Hit.Path);
                    var target = tabs.ActiveTab;
                    if (target is null || target.Path.FullPath != request.Hit.Path.FullPath)
                    {
                        var rejected = await load;
                        request.StartedSource.TrySetResult(null);
                        request.CompletionSource.TrySetResult(rejected.Status == OperationStatus.Failure
                            ? OperationResult<SearchResultMove>.Failure(rejected.Error!) : OperationResult<SearchResultMove>.Cancelled());
                        continue;
                    }
                    var selection = tabs.SelectionEpoch;
                    var loadEpoch = target.LoadEpoch;
                    request.StartedSource.TrySetResult(target);
                    var loaded = await load;
                    if (!Valid(request) || !ReferenceEquals(tabs.ActiveTab, target) || tabs.SelectionEpoch != selection || target.LoadEpoch != loadEpoch)
                    { CompleteCancelled(request); continue; }
                    if (loaded.Status != OperationStatus.Success)
                    {
                        request.CompletionSource.TrySetResult(loaded.Status == OperationStatus.Failure
                            ? OperationResult<SearchResultMove>.Failure(loaded.Error!) : OperationResult<SearchResultMove>.Cancelled());
                        continue;
                    }
                    var file = target.File!;
                    var version = target.DocumentVersion;
                    var stamp = await files.CheckStampAsync(request.Hit.Path, request.Cancellation);
                    if (!CanAdoptTarget(request, target, version, loadEpoch, selection)) { CompleteCancelled(request); continue; }
                    if (stamp.Status != OperationStatus.Success)
                    { request.CompletionSource.TrySetResult(OperationResult<SearchResultMove>.Failure(stamp.Error!)); continue; }
                    var snippet = request.Hit.Snippet;
                    var prefix = snippet.Text.Substring(snippet.MatchStart, snippet.MatchLength);
                    DocumentMove move;
                    try
                    {
                        move = DocumentNavigation.Resolve(file.Document, request.Hit.Range, prefix,
                            request.Hit.Stamp == stamp.Value && request.Hit.Stamp == file.Stamp);
                    }
                    catch (OverflowException)
                    { request.CompletionSource.TrySetResult(OperationResult<SearchResultMove>.Failure(new(ErrorCode.UnsupportedSize, request.Hit.Path.DisplayPath))); continue; }
                    request.CompletionSource.TrySetResult(OperationResult<SearchResultMove>.Success(new(target, version, loadEpoch, selection, move)));
                }
                catch (OperationCanceledException cancelled) when (request.Cancellation.IsCancellationRequested && cancelled.CancellationToken == request.Cancellation)
                { CompleteCancelled(request); }
                catch (Exception failure)
                {
                    _faulted = true;
                    _failure = ExceptionDispatchInfo.Capture(failure);
                    request.StartedSource.TrySetResult(null);
                    request.CompletionSource.TrySetException(failure);
                    _ = request.Completion.Exception;
                    if (_waiting is { } waiting) Cancel(waiting);
                }
                finally
                {
                    request.StartedSource.TrySetResult(null);
                    // The explicit cancellation flag survives release for the final UI adoption check.
                    request.Release();
                    _active = null;
                }
            }
        }
        finally { _pumping = false; }
    }

    private static void CompleteCancelled(SearchResultMoveRequest request)
    {
        request.StartedSource.TrySetResult(null);
        request.CompletionSource.TrySetResult(OperationResult<SearchResultMove>.Cancelled());
    }

    private async Task ObserveAsync() { await _pump; _failure?.Throw(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        if (_active is { } active) active.Cancel();
    }
}
