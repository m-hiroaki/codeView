using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Search;
using CodeView.Settings;

namespace CodeView.Operations;

internal sealed record LoadedFile(SourcePath Path, SourceStamp Stamp, string EncodingName, DocumentHandle Document);

internal sealed class AppOperations(FileReader reader, IDocumentAdapter documents, DirectoryReader directories) : IDisposable
{
    private readonly SemaphoreSlim _loadSlot = new(1, 1);
    private bool _stopping;
    private readonly SemaphoreSlim _treeSlot = new(1, 1);
    private readonly OperationIdentity _workspaceEpoch = new();
    private readonly OperationIdentity _requestIds = new();
    private readonly OperationIdentity _workspaceSearchEpoch = new();
    private CancellationTokenSource? _workspaceLifetime;
    private readonly List<Task> _retiredTreeWork = [];
    private TabManager? _tabs;
    private SearchCoordinator? _searches;
    private StartupOpenQueue? _startup;

    public StartupOpenQueue Startup => _startup ??= new(_requestIds);
    public LoadedFile? CurrentFile => _tabs?.ActiveTab?.File;
    public TabManager Tabs => !_stopping ? _tabs ??= new(BuildDocument, _loadSlot) : throw new ObjectDisposedException(nameof(AppOperations));
    public Task PendingTabWork => _tabs?.PendingWork ?? Task.CompletedTask;
    public SearchCoordinator Searches => !_stopping ? _searches ??= new(new TextSearch(directories, reader).StartWorkspace,
        () => Workspace, _workspaceSearchEpoch, _requestIds) : throw new ObjectDisposedException(nameof(AppOperations));
    public Task PendingSearchWork => _searches?.PendingWork ?? Task.CompletedTask;
    public WorkspaceState? Workspace { get; private set; }
    public long WorkspaceSearchEpoch => _workspaceSearchEpoch.Current;
    public Task PendingTreeWork => Task.WhenAll(_retiredTreeWork.Append(Workspace?.Tree?.PendingWork ?? Task.CompletedTask));

    public Task<OperationResult<SourceEntry>> InspectPathAsync(SourcePath path, CancellationToken ct) =>
        _stopping ? Task.FromResult(OperationResult<SourceEntry>.Cancelled()) : directories.InspectPathAsync(path, ct);

    public async Task<OperationResult<WorkspaceState>> OpenFolderAsync(SourcePath path, SettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(settings);
        if (_stopping) return OperationResult<WorkspaceState>.Cancelled();
        var epoch = _workspaceEpoch.Advance();
        _workspaceSearchEpoch.Advance();
        _searches?.InvalidateWorkspace();
        CancelWorkspace();
        _workspaceLifetime = new CancellationTokenSource();
        var workspace = new WorkspaceState(path, settings, epoch, _workspaceLifetime.Token);
        Workspace = workspace;
        using var load = CancellationTokenSource.CreateLinkedTokenSource(workspace.Token);
        var entered = false;
        try
        {
            await _treeSlot.WaitAsync(load.Token);
            entered = true;
            var resolved = await directories.ResolveRootAsync(path, load.Token);
            if (!CanAdopt()) return OperationResult<WorkspaceState>.Cancelled();
            if (resolved.Status != OperationStatus.Success)
            {
                workspace.Fail(resolved.Error!);
                return OperationResult<WorkspaceState>.Failure(resolved.Error!);
            }
            workspace.Resolve(resolved.Value);
            var listing = await directories.ReadChildrenAsync(resolved.Value, workspace.Policy, load.Token);
            if (!CanAdopt()) return OperationResult<WorkspaceState>.Cancelled();
            workspace.Complete(listing);
            workspace.Tree = new TreeSession(directories, _treeSlot, workspace.Policy, workspace.Token, listing);
            return listing.IsComplete ? OperationResult<WorkspaceState>.Success(workspace)
                : OperationResult<WorkspaceState>.Failure(listing.Error!);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            return OperationResult<WorkspaceState>.Cancelled();
        }
        catch (Exception)
        {
            if (CanAdopt()) workspace.Fail(new(ErrorCode.Unexpected, path.DisplayPath));
            throw;
        }
        finally { if (entered) _treeSlot.Release(); }

        bool CanAdopt() => _workspaceEpoch.CanAdopt(epoch, !_stopping && ReferenceEquals(Workspace, workspace), load.IsCancellationRequested);
    }

    public void CloseWorkspace()
    {
        _workspaceEpoch.Advance();
        _workspaceSearchEpoch.Advance();
        _searches?.InvalidateWorkspace();
        CancelWorkspace();
        Workspace = null;
    }

    private void CancelWorkspace()
    {
        if (Workspace?.Tree is { } tree)
        {
            tree.Dispose();
            _retiredTreeWork.RemoveAll(task => task.IsCompletedSuccessfully);
            _retiredTreeWork.Add(tree.PendingWork);
        }
        _workspaceLifetime?.Cancel();
        _workspaceLifetime?.Dispose();
        _workspaceLifetime = null;
    }

    public async Task<OperationResult<LoadedFile>> OpenFileAsync(SourcePath path, EncodingChoice choice)
    {
        if (_stopping) return OperationResult<LoadedFile>.Cancelled();
        var result = await Tabs.OpenAsync(path, choice);
        var file = result.Status == OperationStatus.Success ? result.Value.File : null;
        return result.Status switch
        {
            OperationStatus.Success => file is null ? OperationResult<LoadedFile>.Cancelled() : OperationResult<LoadedFile>.Success(file),
            OperationStatus.Cancelled => OperationResult<LoadedFile>.Cancelled(),
            _ => OperationResult<LoadedFile>.Failure(result.Error!)
        };
    }

    private OperationResult<LoadedFile> BuildDocument(SourcePath path, EncodingChoice choice, CancellationToken ct)
    {
        var preparation = reader.Prepare(path, choice, ct);
        if (preparation.Status != OperationStatus.Success)
            return OperationResult<LoadedFile>.Failure(preparation.Error!);
        using var prepared = preparation.Value;
        using var builder = documents.CreateBuilder();
        var read = reader.ReadChunks(prepared, builder.Append, ct);
        if (read.Status != OperationStatus.Success)
            return OperationResult<LoadedFile>.Failure(read.Error!);
        ct.ThrowIfCancellationRequested();
        return OperationResult<LoadedFile>.Success(new(path, prepared.Stamp, prepared.Encoding.WebName, builder.Complete()));
    }

    public void CloseFile()
    {
        if (_tabs?.ActiveTab is { } tab) _tabs.Close(tab);
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        Startup.Stop();
        _searches?.Dispose();
        _tabs?.Dispose();
        CloseWorkspace();
        CloseFile();
        // Cancelled workers still need the semaphore to release their slot.
    }
}
