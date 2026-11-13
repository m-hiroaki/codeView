using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Navigation;
using CodeView.Search;
using CodeView.Settings;

namespace CodeView.Operations;

internal sealed record LoadedFile(SourcePath Path, SourceStamp Stamp, string EncodingName, DocumentHandle Document);

internal sealed class AppOperations(FileReader reader, IDocumentAdapter documents, DirectoryReader directories) : IDisposable
{
    private readonly SemaphoreSlim _loadSlot = new(1, 1);
    private bool _stopping;
    private readonly SemaphoreSlim _treeSlot = new(1, 1);
    private SemaphoreSlim? _inFileSearchSlot;
    private SemaphoreSlim? _syntaxSlot;
    private BundledGrammars? _grammars;
    private readonly OperationIdentity _workspaceEpoch = new();
    private readonly OperationIdentity _requestIds = new();
    private readonly OperationIdentity _workspaceSearchEpoch = new();
    private CancellationTokenSource? _workspaceLifetime;
    private readonly List<Task> _retiredTreeWork = [];
    private TabManager? _tabs;
    private SearchCoordinator? _searches;
    private SearchResultNavigator? _resultNavigator;
    private StartupOpenQueue? _startup;

    public StartupOpenQueue Startup => _startup ??= new(_requestIds);
    public LoadedFile? CurrentFile => _tabs?.ActiveTab?.File;
    public TabManager Tabs => !_stopping ? _tabs ??= new(BuildDocument, _loadSlot) : throw new ObjectDisposedException(nameof(AppOperations));
    public Task PendingTabWork => _tabs?.PendingWork ?? Task.CompletedTask;
    public SearchCoordinator Searches => !_stopping ? _searches ??= new(StartSearch,
        () => Workspace, _workspaceSearchEpoch, _requestIds, StartReferences) : throw new ObjectDisposedException(nameof(AppOperations));
    private WorkspaceSearchSession StartSearch(WorkspaceSearchRequest request, CancellationToken ct)
    {
        var search = new TextSearch(directories, reader);
        return request.Definitions ? CreateNavigator(search).FindDefinitions(request, ct)
            : search.StartWorkspace(request, ct);
    }

    private WorkspaceSearchSession StartReferences(WorkspaceSearchRequest request, CancellationToken ct) =>
        CreateNavigator(new TextSearch(directories, reader)).FindReferences(request, ct);

    private ISymbolNavigator CreateNavigator(TextSearch search) => new SimpleSymbolNavigator(search, reader);

    internal async Task<T> RunInFileSearchAsync<T>(DocumentHandle document, Func<DocumentReadLease, T> scan, CancellationToken ct)
    {
        var slot = _inFileSearchSlot ??= new(1, 1);
        await slot.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var lease = documents.AcquireReadView(document);
            return await Task.Run(() => scan(lease));
        }
        finally { slot.Release(); }
    }

    internal async Task<(SyntaxHighlighting? Highlighting, string Status)> HighlightAsync(
        DocumentHandle document, string scope, CancellationToken ct)
    {
        var slot = _syntaxSlot ??= new(1, 1);
        await slot.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var lease = documents.AcquireReadView(document);
            var grammars = _grammars ??= new();
            return await Task.Run(() => SyntaxHighlighting.Read(lease, scope, grammars, ct));
        }
        finally { slot.Release(); }
    }

    public Task PendingSearchWork => _searches?.PendingWork ?? Task.CompletedTask;
    public SearchResultNavigator ResultNavigator => !_stopping ? _resultNavigator ??= new(Tabs, Searches, reader)
        : throw new ObjectDisposedException(nameof(AppOperations));
    public Task PendingResultMoveWork => _resultNavigator?.PendingWork ?? Task.CompletedTask;
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
        _resultNavigator?.Clear();
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
        List<Exception>? failures = null;
        try { _resultNavigator?.Clear(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { _searches?.InvalidateWorkspace(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { CancelWorkspace(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        finally { Workspace = null; }
        if (failures is not null) throw new AggregateException(failures);
    }

    private void CancelWorkspace()
    {
        List<Exception>? failures = null;
        if (Workspace?.Tree is { } tree)
        {
            try { tree.Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
            finally
            {
                _retiredTreeWork.RemoveAll(task => task.IsCompletedSuccessfully);
                _retiredTreeWork.Add(tree.PendingWork);
            }
        }
        var lifetime = _workspaceLifetime;
        _workspaceLifetime = null;
        try { lifetime?.Cancel(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        finally { lifetime?.Dispose(); }
        if (failures is not null) throw new AggregateException(failures);
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
        List<Exception>? failures = null;
        Attempt(Startup.Stop);
        Attempt(() => _resultNavigator?.Dispose());
        Attempt(() => _searches?.Dispose());
        Attempt(() => _tabs?.Dispose());
        Attempt(CloseWorkspace);
        Attempt(CloseFile);
        // Cancelled workers still need the semaphore to release their slot.
        if (failures is not null) throw new AggregateException(failures);

        void Attempt(Action dispose)
        {
            try { dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
    }
}
