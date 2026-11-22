using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Navigation;

internal sealed record CppIndexUpdate(int Files, int Parsed, int Definitions, bool Complete);

internal sealed class CppDefinitionIndex(SourcePath root, ExclusionPolicy policy, EncodingChoice encoding,
    DirectoryReader directories, FileReader files, CppDefinitionCache cache, CancellationToken lifetime) : IDisposable
{
    internal readonly record struct Match(CppIndexedFile File, CppDefinition Definition);
    private sealed record Published(long Generation, bool Complete, Dictionary<string, List<Match>> ByName);

    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly object _stateGate = new();
    private FileSystemWatcher? _watcher;
    private string? _canonicalRoot;
    private Published? _published;
    private long _generation;
    private bool _watcherFailed;
    private bool _disposed;

    internal Task RestoreAsync() => Task.Run(async () =>
    {
        if (!cache.Exists) return;
        try
        {
            await _worker.WaitAsync(lifetime).ConfigureAwait(false);
            try
            {
                var generation = StartObservation();
                var persisted = cache.Read(lifetime);
                if (persisted is null) return;
                var (current, complete) = Enumerate(lifetime);
                if (complete != persisted.EnumerationComplete || current.Count != persisted.Files.Count
                    || current.Any(pair => !persisted.Files.TryGetValue(pair.Key, out var old)
                        || old.Stamp != pair.Value.Stamp || pair.Value.Error is not null))
                    return;
                Publish(persisted, generation, lifetime);
            }
            finally { _worker.Release(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    });

    internal Task<CppIndexUpdate> UpdateAsync(bool rebuild, Action<CppIndexUpdate>? progress, CancellationToken cancellation)
        => Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellation);
            var ct = linked.Token;
            await _worker.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var generation = StartObservation();
                using var writeLock = cache.BeginUpdate();
                var previous = rebuild ? null : cache.Read(ct);
                var (current, complete) = Enumerate(ct);
                var snapshot = Build(current, complete, previous, progress, ct, out var stats);
                cache.Save(snapshot, () => CanPublish(generation), ct);
                Publish(snapshot, generation, ct);
                return stats;
            }
            finally { _worker.Release(); }
        });

    internal bool TryGet(string name, out IReadOnlyList<Match> matches, out bool complete, out long generation)
    {
        lock (_stateGate)
        {
            generation = _generation;
            if (_published is { } current && current.Generation == generation
                && !lifetime.IsCancellationRequested && current.ByName.TryGetValue(name, out var hit))
            {
                matches = hit;
                complete = current.Complete;
                return true;
            }
        }
        matches = [];
        complete = false;
        return false;
    }

    internal bool IsCurrent(long generation)
    {
        lock (_stateGate)
            return _published?.Generation == generation && CanPublish(generation);
    }

    internal void Invalidate()
    {
        lock (_stateGate)
        {
            _generation++;
            _published = null;
        }
    }

    private (Dictionary<string, CppIndexedFile> Files, bool Complete) Enumerate(CancellationToken ct)
    {
        var current = new Dictionary<string, CppIndexedFile>(CppDefinitionCache.PathComparer);
        var entries = 0;
        var complete = true;
        var memory = 0L;
        foreach (var item in directories.WalkFiles(root, policy, ct, () => ++entries <= CppDefinitionCache.MaximumEntries))
        {
            if (item.Kind == DirectoryWalkKind.Failed) complete = false;
            if (item.Kind != DirectoryWalkKind.File || !CppDeclarationScanner.IncludesPath(item.Path.FullPath)) continue;
            if (current.Count >= CppDefinitionCache.MaximumFiles)
            {
                complete = false;
                break;
            }
            var relative = Path.GetRelativePath(root.FullPath, item.Path.FullPath);
            memory += 192L + 2L * relative.Length;
            if (memory > CppDefinitionCache.MaximumEstimatedBytes / 2)
            {
                complete = false;
                break;
            }
            var stamp = files.CheckStamp(item.Path, ct);
            var file = stamp.Status == OperationStatus.Success
                ? new CppIndexedFile(relative, stamp.Value, 0, null, [])
                : new CppIndexedFile(relative, default, 0, stamp.Error?.Code ?? ErrorCode.IoFailure, []);
            if (!current.TryAdd(relative, file)) complete = false;
        }
        return (current, complete && entries <= CppDefinitionCache.MaximumEntries);
    }

    private CppDefinitionSnapshot Build(Dictionary<string, CppIndexedFile> current, bool complete,
        CppDefinitionSnapshot? previous, Action<CppIndexUpdate>? progress, CancellationToken ct, out CppIndexUpdate stats)
    {
        var result = new Dictionary<string, CppIndexedFile>(CppDefinitionCache.PathComparer);
        var collector = CppDeclarationScanner.CreateDefinitionCollector();
        long sourceBytes = 0;
        long memory = 0;
        var definitions = 0;
        var parsed = 0;
        var processed = 0;
        foreach (var currentFile in current.Values)
        {
            ct.ThrowIfCancellationRequested();
            var file = currentFile;
            memory += file.EstimatedBytes;
            if (memory > CppDefinitionCache.MaximumEstimatedBytes)
            {
                complete = false;
                break;
            }
            if (file.Error is null)
            {
                if (previous is not null && previous.Files.TryGetValue(file.RelativePath, out var old)
                    && old.Error is null && old.Stamp == file.Stamp)
                    file = old;
                else if (file.Stamp.LengthBytes <= CppDefinitionCache.MaximumFileBytes
                    && file.Stamp.LengthBytes <= CppDefinitionCache.MaximumSourceBytes - sourceBytes)
                {
                    sourceBytes += file.Stamp.LengthBytes;
                    parsed++;
                    file = Parse(file, collector, CppDefinitionCache.MaximumDefinitions - definitions,
                        CppDefinitionCache.MaximumEstimatedBytes - memory, ct);
                }
                else file = file with { Error = ErrorCode.UnsupportedSize };
            }
            var addedBytes = file.EstimatedBytes - currentFile.EstimatedBytes;
            if (file.Definitions.Count > CppDefinitionCache.MaximumDefinitions - definitions
                || addedBytes > CppDefinitionCache.MaximumEstimatedBytes - memory)
                file = currentFile with { Error = ErrorCode.UnsupportedSize };
            else
            {
                memory += addedBytes;
                definitions += file.Definitions.Count;
            }
            result.Add(file.RelativePath, file);
            processed++;
            if (processed % 64 == 0) progress?.Invoke(new(processed, parsed, definitions, false));
        }
        var snapshot = new CppDefinitionSnapshot(result, complete);
        stats = new(processed, parsed, definitions, snapshot.IsComplete);
        return snapshot;
    }

    private CppIndexedFile Parse(CppIndexedFile file, CppDeclarationScanner collector, int remaining, long available, CancellationToken ct)
    {
        var sourcePath = SourcePath.Create(file.RelativePath, root.FullPath);
        var expected = Path.GetFullPath(file.RelativePath, _canonicalRoot!);
        if (!CppDefinitionCache.PathComparer.Equals(expected, LspFilePath.Resolve(sourcePath.FullPath)))
            return file with { Error = ErrorCode.ChangedDuringRead };
        var prepared = files.Prepare(sourcePath, encoding, ct, file.Stamp.LengthBytes);
        if (prepared.Status != OperationStatus.Success) return file with { Error = prepared.Error!.Code };
        using var source = prepared.Value;
        if (source.Stamp != file.Stamp) return file with { Error = ErrorCode.ChangedDuringRead };
        var definitions = new List<CppDefinition>();
        var limited = false;
        var error = collector.CollectDefinitions(files, source, definition =>
        {
            available -= 192L + 2L * (definition.Name.Length + (definition.Qualifier?.Length ?? 0));
            if (definitions.Count >= remaining || available < 0)
            {
                limited = true;
                return false;
            }
            definitions.Add(definition);
            return true;
        }, ct);
        return file with
        {
            CodePage = source.Encoding.CodePage,
            Error = limited ? ErrorCode.UnsupportedSize : error?.Code,
            Definitions = error is null ? definitions : []
        };
    }

    private long StartObservation()
    {
        lock (_stateGate)
        {
            lifetime.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            _canonicalRoot ??= LspFilePath.Resolve(root.FullPath);
            if (_watcher is null || _watcherFailed)
            {
                _watcher?.Dispose();
                var watcher = new FileSystemWatcher(root.FullPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 32 * 1024
                };
                watcher.Changed += Changed;
                watcher.Created += Changed;
                watcher.Deleted += Changed;
                watcher.Renamed += Renamed;
                watcher.Error += WatcherError;
                _watcher = watcher;
                _watcherFailed = false;
                try { watcher.EnableRaisingEvents = true; }
                catch
                {
                    _watcher = null;
                    watcher.Dispose();
                    throw;
                }
            }
            _published = null;
            return ++_generation;
        }
    }

    private void Changed(object sender, FileSystemEventArgs change)
    {
        if (IsIncludedChange(change.FullPath)) Invalidate();
    }

    private void Renamed(object sender, RenamedEventArgs change)
    {
        if (IsIncludedChange(change.FullPath) || IsIncludedChange(change.OldFullPath)) Invalidate();
    }

    private bool IsIncludedChange(string path)
    {
        var relative = Path.GetRelativePath(root.FullPath, path);
        var parts = relative.Split(Path.DirectorySeparatorChar);
        return !parts.Any(policy.IsExcludedDirectory);
    }

    private void WatcherError(object sender, ErrorEventArgs error)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(sender, _watcher)) return;
            _watcherFailed = true;
            Invalidate();
        }
    }

    private bool CanPublish(long generation)
    {
        lock (_stateGate)
            return !_disposed && !_watcherFailed && !lifetime.IsCancellationRequested && generation == _generation;
    }

    private void Publish(CppDefinitionSnapshot snapshot, long generation, CancellationToken ct)
    {
        var byName = new Dictionary<string, List<Match>>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files.Values)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var definition in file.Definitions)
            {
                if (!byName.TryGetValue(definition.Name, out var values)) byName.Add(definition.Name, values = []);
                values.Add(new(file, definition));
            }
        }
        lock (_stateGate)
        {
            ct.ThrowIfCancellationRequested();
            if (CanPublish(generation)) _published = new(generation, snapshot.IsComplete, byName);
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        lock (_stateGate)
        {
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            _published = null;
        }
        watcher?.Dispose();
    }
}
