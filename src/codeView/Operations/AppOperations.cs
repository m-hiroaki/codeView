using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;

namespace CodeView.Operations;

internal sealed record LoadedFile(SourcePath Path, SourceStamp Stamp, string EncodingName, DocumentHandle Document);

internal sealed class AppOperations(FileReader reader, IDocumentAdapter documents) : IDisposable
{
    private readonly SemaphoreSlim _loadSlot = new(1, 1);
    private readonly OperationIdentity _loadEpoch = new();
    private CancellationTokenSource? _currentLoad;
    private bool _stopping;

    public StartupOpenQueue Startup { get; } = new();
    public LoadedFile? CurrentFile { get; private set; }

    public async Task<OperationResult<LoadedFile>> OpenFileAsync(SourcePath path, EncodingChoice choice)
    {
        if (_stopping) return OperationResult<LoadedFile>.Cancelled();
        _currentLoad?.Cancel();
        var epoch = _loadEpoch.Advance();
        using var cancellation = new CancellationTokenSource();
        _currentLoad = cancellation;
        var entered = false;
        try
        {
            await _loadSlot.WaitAsync(cancellation.Token);
            entered = true;
            var result = await Task.Run(() => BuildDocument(path, choice, cancellation.Token), cancellation.Token);
            if (!_loadEpoch.CanAdopt(epoch, !_stopping, cancellation.IsCancellationRequested))
            {
                if (result.Status == OperationStatus.Success) result.Value.Document.Dispose();
                return OperationResult<LoadedFile>.Cancelled();
            }
            if (result.Status == OperationStatus.Success)
            {
                var previous = CurrentFile;
                CurrentFile = result.Value;
                previous?.Document.Dispose();
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return OperationResult<LoadedFile>.Cancelled();
        }
        finally
        {
            if (ReferenceEquals(_currentLoad, cancellation)) _currentLoad = null;
            if (entered) _loadSlot.Release();
        }
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
        _loadEpoch.Advance();
        _currentLoad?.Cancel();
        CurrentFile?.Document.Dispose();
        CurrentFile = null;
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        Startup.Stop();
        CloseFile();
        // Cancelled workers still need the semaphore to release their slot.
    }
}
