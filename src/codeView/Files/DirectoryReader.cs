using CodeView.Contracts;

namespace CodeView.Files;

internal enum SourceEntryKind { FileCandidate, Directory, Other }
internal sealed record SourceEntry(SourcePath Path, string Name, SourceEntryKind Kind, bool IsLink);
internal sealed record DirectoryListing(SourcePath Path, IReadOnlyList<SourceEntry> Entries, OperationError? Error)
{
    public bool IsComplete => Error is null;
}

internal sealed class ExclusionPolicy
{
    private readonly HashSet<string> _excluded;

    // Unknown filesystem comparison uses exact matching. Callers may opt in only when verified.
    public ExclusionPolicy(IEnumerable<string> names, bool caseSensitive = true)
    {
        ArgumentNullException.ThrowIfNull(names);
        _excluded = new(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            _excluded.Add(name);
        }
    }

    public bool IsExcludedDirectory(string name) => _excluded.Contains(name);
    public bool IsSearchTarget(SourceEntry entry) => entry.Kind == SourceEntryKind.FileCandidate && !entry.IsLink;
}

internal enum DirectoryWalkKind { File, SkippedLink, SkippedDirectory, SkippedSpecialEntry, Failed }
internal sealed record DirectoryWalkEvent(DirectoryWalkKind Kind, SourcePath Path, OperationError? Error = null, bool IsRoot = false);

/// <summary>Only direct tree children are sorted. Search traversal keeps one enumerator per depth.</summary>
internal sealed class DirectoryReader(ISourceAccess source)
{
    public Task<OperationResult<SourceEntry>> InspectPathAsync(SourcePath path, CancellationToken ct) =>
        Task.Run(() => source.InspectPath(path, ct), ct);

    public Task<OperationResult<SourcePath>> ResolveRootAsync(SourcePath path, CancellationToken ct) =>
        Task.Run(() => source.ResolveDirectory(path, ct), ct);

    public Task<DirectoryListing> ReadChildrenAsync(SourcePath path, ExclusionPolicy policy, CancellationToken ct) =>
        Task.Run(() => ReadChildren(path, policy, ct), ct);

    private DirectoryListing ReadChildren(SourcePath path, ExclusionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(policy);
        var entries = new List<SourceEntry>();
        OperationError? failure = null;
        try
        {
            foreach (var entry in source.EnumerateChildren(path, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Kind != SourceEntryKind.Directory || !policy.IsExcludedDirectory(entry.Name))
                    entries.Add(entry);
            }
        }
        catch (Exception error) when (IsExpected(error)) { failure = ToError(error, path); }
        ct.ThrowIfCancellationRequested();
        entries.Sort((left, right) =>
        {
            var directoryOrder = (right.Kind == SourceEntryKind.Directory).CompareTo(left.Kind == SourceEntryKind.Directory);
            if (directoryOrder != 0) return directoryOrder;
            var nameOrder = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return nameOrder != 0 ? nameOrder : StringComparer.Ordinal.Compare(left.Name, right.Name);
        });
        ct.ThrowIfCancellationRequested();
        return new(path, entries.AsReadOnly(), failure);
    }

    /// <summary>Enumerate on a worker. The supplied root is already resolved; no recursive arrays or prescan.</summary>
    public IEnumerable<DirectoryWalkEvent> WalkFiles(SourcePath root, ExclusionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(policy);
        ct.ThrowIfCancellationRequested();
        var frames = new Stack<Frame>();
        try
        {
            var initial = TryEnter(root, true, ct);
            if (initial.Error is not null) { yield return new(DirectoryWalkKind.Failed, root, initial.Error, true); yield break; }
            frames.Push(initial.Frame!);
            while (frames.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var frame = frames.Peek();
                var step = TryMove(frame, ct);
                ct.ThrowIfCancellationRequested();
                if (step.Entry is null)
                {
                    frames.Pop().Enumerator.Dispose();
                    if (step.Error is not null)
                        yield return new(DirectoryWalkKind.Failed, frame.Path, step.Error, frame.IsRoot);
                    continue;
                }
                var entry = step.Entry;
                if (entry.IsLink) { yield return new(DirectoryWalkKind.SkippedLink, entry.Path); continue; }
                if (entry.Kind == SourceEntryKind.Directory)
                {
                    if (policy.IsExcludedDirectory(entry.Name))
                    { yield return new(DirectoryWalkKind.SkippedDirectory, entry.Path); continue; }
                    var child = TryEnter(entry.Path, false, ct);
                    if (child.Error is not null) yield return new(DirectoryWalkKind.Failed, entry.Path, child.Error);
                    else frames.Push(child.Frame!);
                }
                else if (policy.IsSearchTarget(entry)) yield return new(DirectoryWalkKind.File, entry.Path);
                else yield return new(DirectoryWalkKind.SkippedSpecialEntry, entry.Path);
            }
        }
        finally
        {
            while (frames.Count > 0) frames.Pop().Enumerator.Dispose();
        }
    }

    private (Frame? Frame, OperationError? Error) TryEnter(SourcePath path, bool isRoot, CancellationToken ct)
    {
        try { return (new(path, source.EnumerateChildren(path, ct).GetEnumerator(), isRoot), null); }
        catch (Exception error) when (IsExpected(error)) { return (null, ToError(error, path)); }
    }

    private static (SourceEntry? Entry, OperationError? Error) TryMove(Frame frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return frame.Enumerator.MoveNext() ? (frame.Enumerator.Current, null) : (null, null); }
        catch (Exception error) when (IsExpected(error)) { return (null, ToError(error, frame.Path)); }
    }

    private static bool IsExpected(Exception error) => error is IOException or UnauthorizedAccessException;
    private static OperationError ToError(Exception error, SourcePath path) => new(error switch
    {
        FileNotFoundException or DirectoryNotFoundException => ErrorCode.NotFound,
        UnauthorizedAccessException => ErrorCode.AccessDenied,
        _ => ErrorCode.IoFailure
    }, path.DisplayPath, true);

    private sealed record Frame(SourcePath Path, IEnumerator<SourceEntry> Enumerator, bool IsRoot);
}
