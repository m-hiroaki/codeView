using System.Collections.ObjectModel;
using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Operations;

internal enum TabStatus { Loading, Ready, Failed, Reloading, Closed }

internal sealed class TabState(long id, SourcePath path, EncodingChoice choice)
{
    public long Id { get; } = id;
    public SourcePath Path { get; } = path;
    public long LoadEpoch { get; internal set; }
    public long DocumentVersion { get; internal set; }
    public TabStatus Status { get; internal set; } = TabStatus.Loading;
    public LoadedFile? File { get; internal set; }
    public EncodingChoice EncodingChoice { get; internal set; } = choice;
    public OperationError? Error { get; internal set; }
    public bool WasCancelled { get; internal set; }
    public TextPosition Position { get; internal set; } = new(1, 1);
    public double HorizontalOffset { get; internal set; }
    public double VerticalOffset { get; internal set; }
}

/// <summary>Serialized tab ownership and FIFO loads. Only the load delegate runs on a worker.</summary>
internal sealed class TabManager : IDisposable
{
    private readonly Func<SourcePath, EncodingChoice, CancellationToken, OperationResult<LoadedFile>> _load;
    private readonly SemaphoreSlim _slot;
    private readonly int _maxWaiting;
    private readonly List<TabState> _tabs = [];
    private readonly Dictionary<string, TabState> _paths = new(StringComparer.Ordinal);
    private readonly Dictionary<TabState, Request> _requests = [];
    private readonly LinkedList<Request> _queue = [];
    private Task _pump = Task.CompletedTask;
    private Task _failure = Task.CompletedTask;
    private bool _pumping;
    private bool _disposed;
    private bool _faulted;
    private long _nextId;

    public ReadOnlyCollection<TabState> Tabs { get; }
    public TabState? ActiveTab { get; private set; }
    public long SelectionEpoch { get; private set; }
    public Task PendingWork => ObservePendingAsync();

    public TabManager(Func<SourcePath, EncodingChoice, CancellationToken, OperationResult<LoadedFile>> load,
        SemaphoreSlim slot, int maxWaiting = StartupOpenQueue.MaxPaths)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWaiting, 1);
        (_load, _slot, _maxWaiting) = (load, slot, maxWaiting);
        Tabs = _tabs.AsReadOnly();
    }

    public Task<OperationResult<TabState>> OpenAsync(SourcePath path, EncodingChoice choice = EncodingChoice.Auto)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_disposed || _faulted) return Cancelled();
        if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
        if (_paths.TryGetValue(path.FullPath, out var existing))
        {
            Activate(existing);
            if (_requests.TryGetValue(existing, out var request)) return request.Completion.Task;
            return Task.FromResult(existing.File is not null ? OperationResult<TabState>.Success(existing)
                : existing.WasCancelled ? OperationResult<TabState>.Cancelled()
                : OperationResult<TabState>.Failure(existing.Error!));
        }
        if (_queue.Count >= _maxWaiting) return QueueFull(path);
        var tab = new TabState(checked(++_nextId), path, choice);
        _tabs.Add(tab);
        _paths.Add(path.FullPath, tab);
        Activate(tab);
        return Enqueue(tab, choice);
    }

    public Task<OperationResult<TabState>> ReloadAsync(TabState tab, EncodingChoice? choice = null)
    {
        if (_disposed || _faulted || !Contains(tab)) return Cancelled();
        var selected = choice ?? tab.EncodingChoice;
        if (!Enum.IsDefined(selected)) throw new ArgumentOutOfRangeException(nameof(choice));
        var replacesWaiting = _requests.TryGetValue(tab, out var previous) && previous.Node is not null;
        if (_queue.Count - (replacesWaiting ? 1 : 0) >= _maxWaiting) return QueueFull(tab.Path);
        CancelRequest(tab);
        return Enqueue(tab, selected);
    }

    public bool Select(TabState tab)
    {
        if (_disposed || _faulted || !Contains(tab)) return false;
        Activate(tab);
        return true;
    }

    public bool SaveView(TabState tab, TextPosition position, double horizontalOffset, double verticalOffset)
    {
        if (_disposed || !Contains(tab)) return false;
        if (!double.IsFinite(horizontalOffset) || horizontalOffset < 0) throw new ArgumentOutOfRangeException(nameof(horizontalOffset));
        if (!double.IsFinite(verticalOffset) || verticalOffset < 0) throw new ArgumentOutOfRangeException(nameof(verticalOffset));
        if (position.Line < 1 || position.Column < 1) throw new ArgumentOutOfRangeException(nameof(position));
        (tab.Position, tab.HorizontalOffset, tab.VerticalOffset) = (position, horizontalOffset, verticalOffset);
        return true;
    }

    public bool Close(TabState tab)
    {
        if (!Contains(tab)) return false;
        tab.LoadEpoch = checked(tab.LoadEpoch + 1);
        CancelRequest(tab);
        var index = _tabs.IndexOf(tab);
        _tabs.RemoveAt(index);
        _paths.Remove(tab.Path.FullPath);
        tab.File?.Document.Dispose();
        tab.File = null;
        tab.Status = TabStatus.Closed;
        tab.Error = null;
        if (ReferenceEquals(ActiveTab, tab)) Activate(_tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)]);
        return true;
    }

    private void Activate(TabState? tab)
    {
        SelectionEpoch = checked(SelectionEpoch + 1);
        ActiveTab = tab;
    }

    private bool Contains(TabState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return _paths.TryGetValue(tab.Path.FullPath, out var current) && ReferenceEquals(tab, current);
    }

    private async Task ObservePendingAsync()
    {
        await _pump;
        await _failure;
    }

    private Task<OperationResult<TabState>> Enqueue(TabState tab, EncodingChoice choice)
    {
        tab.LoadEpoch = checked(tab.LoadEpoch + 1);
        tab.Status = tab.File is null ? TabStatus.Loading : TabStatus.Reloading;
        tab.Error = null;
        tab.WasCancelled = false;
        var request = new Request(tab, tab.LoadEpoch, choice);
        request.Node = _queue.AddLast(request);
        _requests[tab] = request;
        if (!_pumping) _pump = PumpAsync();
        return request.Completion.Task;
    }

    private async Task PumpAsync()
    {
        _pumping = true;
        try
        {
            while (!_disposed && !_faulted && _queue.First is { } node)
            {
                var request = node.Value;
                _queue.RemoveFirst();
                request.Node = null;
                var entered = false;
                LoadedFile? unadopted = null;
                try
                {
                    await _slot.WaitAsync(request.Cancellation.Token);
                    entered = true;
                    var loaded = await Task.Run(() => _load(request.Tab.Path, request.Choice, request.Cancellation.Token), request.Cancellation.Token);
                    if (loaded.Status == OperationStatus.Success) unadopted = loaded.Value;
                    if (!CanAdopt(request))
                    {
                        request.Completion.TrySetResult(OperationResult<TabState>.Cancelled());
                        continue;
                    }
                    var tab = request.Tab;
                    if (loaded.Status == OperationStatus.Success)
                    {
                        var nextVersion = checked(tab.DocumentVersion + 1);
                        var old = tab.File;
                        tab.File = loaded.Value;
                        tab.EncodingChoice = request.Choice;
                        tab.DocumentVersion = nextVersion;
                        tab.Status = TabStatus.Ready;
                        unadopted = null;
                        old?.Document.Dispose();
                        request.Completion.TrySetResult(OperationResult<TabState>.Success(tab));
                    }
                    else
                    {
                        Fail(tab, loaded.Error, loaded.Status == OperationStatus.Cancelled);
                        request.Completion.TrySetResult(loaded.Status == OperationStatus.Cancelled
                            ? OperationResult<TabState>.Cancelled() : OperationResult<TabState>.Failure(loaded.Error!));
                    }
                }
                catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
                { request.Completion.TrySetResult(OperationResult<TabState>.Cancelled()); }
                catch (Exception error)
                {
                    if (CanAdopt(request)) Fail(request.Tab, new(ErrorCode.Unexpected, request.Tab.Path.DisplayPath), false);
                    _faulted = true;
                    _failure = Task.FromException(error);
                    request.Completion.TrySetException(error);
                    foreach (var waiting in _queue.ToArray()) CancelRequest(waiting.Tab);
                }
                finally
                {
                    unadopted?.Document.Dispose();
                    if (_requests.TryGetValue(request.Tab, out var current) && ReferenceEquals(current, request)) _requests.Remove(request.Tab);
                    request.Cancellation.Dispose();
                    if (entered) _slot.Release();
                }
            }
        }
        finally { _pumping = false; }
    }

    private bool CanAdopt(Request request) => !_disposed && Contains(request.Tab)
        && request.Tab.LoadEpoch == request.Epoch && !request.Cancellation.IsCancellationRequested;

    private static void Fail(TabState tab, OperationError? error, bool cancelled)
    {
        tab.Status = tab.File is null ? TabStatus.Failed : TabStatus.Ready;
        tab.Error = error;
        tab.WasCancelled = cancelled;
    }

    private void CancelRequest(TabState tab)
    {
        if (!_requests.Remove(tab, out var request)) return;
        request.Cancellation.Cancel();
        if (request.Node is { } queued)
        {
            _queue.Remove(queued);
            request.Node = null;
            request.Completion.TrySetResult(OperationResult<TabState>.Cancelled());
            request.Cancellation.Dispose();
        }
    }

    private static Task<OperationResult<TabState>> Cancelled() => Task.FromResult(OperationResult<TabState>.Cancelled());
    private static Task<OperationResult<TabState>> QueueFull(SourcePath path) => Task.FromResult(OperationResult<TabState>.Failure(new(ErrorCode.QueueFull, path.DisplayPath, true)));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var tab in _tabs.ToArray()) Close(tab);
    }

    private sealed class Request(TabState tab, long epoch, EncodingChoice choice)
    {
        public TabState Tab { get; } = tab;
        public long Epoch { get; } = epoch;
        public EncodingChoice Choice { get; } = choice;
        public CancellationTokenSource Cancellation { get; } = new();
        public LinkedListNode<Request>? Node { get; set; }
        public TaskCompletionSource<OperationResult<TabState>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
