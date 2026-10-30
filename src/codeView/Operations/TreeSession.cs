using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Operations;

internal enum TreeLoadStatus { NotLoaded, Loading, Loaded, Failed }

internal sealed class TreeNodeState(TreeSession owner, SourceEntry entry)
{
    internal TreeSession Owner { get; } = owner;
    internal bool IsAttached { get; set; } = true;
    internal long Generation { get; set; }
    public SourceEntry Entry { get; } = entry;
    public bool CanExpand => Entry.Kind == SourceEntryKind.Directory && !Entry.IsLink;
    public bool IsExpanded { get; internal set; }
    public TreeLoadStatus Status { get; internal set; }
    public IReadOnlyList<TreeNodeState> Children { get; internal set; } = Array.Empty<TreeNodeState>();
    public OperationError? Error { get; internal set; }
}

/// <summary>Workspace-owned logical tree. All calls and continuations use the serialized operation context.</summary>
internal sealed class TreeSession : IDisposable
{
    private readonly DirectoryReader _reader;
    private readonly SemaphoreSlim _slot;
    private readonly ExclusionPolicy _policy;
    private readonly CancellationToken _workspaceToken;
    private readonly int _queueLimit;
    private readonly int _cacheLimit;
    private readonly Dictionary<TreeNodeState, Request> _requests = [];
    private readonly HashSet<Request> _work = [];
    private readonly LinkedList<TreeNodeState> _cache = [];
    private readonly Dictionary<TreeNodeState, LinkedListNode<TreeNodeState>> _cached = [];
    private int _cachedNodes;
    private bool _disposed;

    public TreeNodeState Root { get; }
    public int CachedNodeCount => _cachedNodes;
    public Task PendingWork => Task.WhenAll(_work.Select(request => request.Task));

    public TreeSession(DirectoryReader reader, SemaphoreSlim slot, ExclusionPolicy policy,
        CancellationToken workspaceToken, DirectoryListing listing, int queueLimit = 64, int cacheLimit = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(queueLimit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheLimit);
        (_reader, _slot, _policy, _workspaceToken, _queueLimit, _cacheLimit) =
            (reader, slot, policy, workspaceToken, queueLimit, cacheLimit);
        Root = new(this, new(listing.Path, listing.Path.DisplayPath, SourceEntryKind.Directory, false)) { IsExpanded = true };
        AdoptListing(Root, listing);
    }

    public Task<OperationResult<TreeNodeState>> ExpandAsync(TreeNodeState node, bool reload = false)
    {
        ValidateOwner(node);
        if (_disposed || _workspaceToken.IsCancellationRequested || !node.IsAttached)
            return Task.FromResult(OperationResult<TreeNodeState>.Cancelled());
        if (ReferenceEquals(node, Root) && (reload || node.Status != TreeLoadStatus.Loaded))
            throw new InvalidOperationException("ルートの再読込はワークスペース操作から行ってください。");
        if (!node.CanExpand)
            return Task.FromResult(OperationResult<TreeNodeState>.Failure(new(ErrorCode.NotRegularFile, node.Entry.Path.DisplayPath)));
        if (!reload && _requests.TryGetValue(node, out var existing)) return existing.Task;
        if (!reload && node.Status == TreeLoadStatus.Loaded)
        {
            RemoveCache(node);
            node.IsExpanded = true;
            return Task.FromResult(OperationResult<TreeNodeState>.Success(node));
        }
        // Count physical waiters, including cancelled requests whose continuations have not finished.
        if (_work.Count(request => !request.Entered) >= _queueLimit)
            return Task.FromResult(OperationResult<TreeNodeState>.Failure(new(ErrorCode.QueueFull, node.Entry.Path.DisplayPath, true)));
        CancelRequest(node);
        ClearChildren(node);
        node.Generation = checked(node.Generation + 1);
        node.IsExpanded = true;
        node.Status = TreeLoadStatus.Loading;
        node.Error = null;
        var request = new Request(node, node.Generation, CancellationTokenSource.CreateLinkedTokenSource(_workspaceToken));
        _requests[node] = request;
        _work.Add(request);
        request.Task = LoadAsync(request);
        return request.Task;
    }

    public void Collapse(TreeNodeState node)
    {
        ValidateOwner(node);
        if (_disposed || !node.IsAttached || ReferenceEquals(node, Root)) return;
        CancelRequest(node);
        node.Generation = checked(node.Generation + 1);
        node.IsExpanded = false;
        if (node.Status == TreeLoadStatus.Loading) node.Status = TreeLoadStatus.NotLoaded;
        // Keep only direct children; hidden descendant lists and requests are discarded.
        foreach (var child in node.Children)
        {
            CancelRequest(child);
            ClearChildren(child);
            child.Generation = checked(child.Generation + 1);
            child.IsExpanded = false;
            child.Status = TreeLoadStatus.NotLoaded;
            child.Error = null;
        }
        RemoveCache(node);
        if (node.Children.Count > 0)
        {
            _cached.Add(node, _cache.AddLast(node));
            _cachedNodes += node.Children.Count;
        }
        while (_cachedNodes > _cacheLimit)
        {
            var oldest = _cache.First!.Value;
            ClearChildren(oldest);
            oldest.Status = TreeLoadStatus.NotLoaded;
            oldest.Error = null;
        }
    }

    private async Task<OperationResult<TreeNodeState>> LoadAsync(Request request)
    {
        var node = request.Node;
        var entered = false;
        try
        {
            await _slot.WaitAsync(request.Cancellation.Token);
            entered = request.Entered = true;
            var listing = await _reader.ReadChildrenAsync(node.Entry.Path, _policy, request.Cancellation.Token);
            if (!CanAdopt()) return OperationResult<TreeNodeState>.Cancelled();
            AdoptListing(node, listing);
            return listing.IsComplete ? OperationResult<TreeNodeState>.Success(node)
                : OperationResult<TreeNodeState>.Failure(listing.Error!);
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
            return OperationResult<TreeNodeState>.Cancelled();
        }
        catch (Exception)
        {
            if (CanAdopt())
            {
                node.Status = TreeLoadStatus.Failed;
                node.Error = new(ErrorCode.Unexpected, node.Entry.Path.DisplayPath);
            }
            throw;
        }
        finally
        {
            if (_requests.TryGetValue(node, out var current) && ReferenceEquals(current, request)) _requests.Remove(node);
            _work.Remove(request);
            request.Cancellation.Dispose();
            if (entered) _slot.Release();
        }

        bool CanAdopt() => !_disposed && !_workspaceToken.IsCancellationRequested && !request.Cancellation.IsCancellationRequested
            && node.IsAttached && node.IsExpanded && node.Generation == request.Generation;
    }

    private void AdoptListing(TreeNodeState node, DirectoryListing listing)
    {
        node.Children = Array.AsReadOnly(listing.Entries.Select(entry => new TreeNodeState(this, entry)).ToArray());
        node.Error = listing.Error;
        node.Status = listing.IsComplete ? TreeLoadStatus.Loaded : TreeLoadStatus.Failed;
    }

    private void CancelRequest(TreeNodeState node)
    {
        if (_requests.Remove(node, out var request)) request.Cancellation.Cancel();
    }

    private void ClearChildren(TreeNodeState node)
    {
        RemoveCache(node);
        if (node.Children.Count == 0) return;
        var pending = new Stack<TreeNodeState>(node.Children);
        node.Children = Array.Empty<TreeNodeState>();
        while (pending.TryPop(out var child))
        {
            RemoveCache(child);
            CancelRequest(child);
            child.IsAttached = false;
            foreach (var descendant in child.Children) pending.Push(descendant);
            child.Children = Array.Empty<TreeNodeState>();
        }
    }

    private void RemoveCache(TreeNodeState node)
    {
        if (!_cached.Remove(node, out var cached)) return;
        _cache.Remove(cached);
        _cachedNodes -= node.Children.Count;
    }

    private void ValidateOwner(TreeNodeState node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!ReferenceEquals(node.Owner, this)) throw new ArgumentException("別のツリーのノードです。", nameof(node));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var request in _work) request.Cancellation.Cancel();
        _requests.Clear();
        ClearChildren(Root);
        Root.IsAttached = false;
    }

    private sealed class Request(TreeNodeState node, long generation, CancellationTokenSource cancellation)
    {
        public TreeNodeState Node { get; } = node;
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public bool Entered { get; set; }
        public Task<OperationResult<TreeNodeState>> Task { get; set; } = null!;
    }
}
