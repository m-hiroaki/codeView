using CodeView.Contracts;
using CodeView.Files;
using CodeView.Settings;

namespace CodeView.Operations;

internal enum WorkspaceStatus { Loading, Ready, Failed }

/// <summary>State is adopted on the serialized operation entry point; no UI/document references.</summary>
internal sealed class WorkspaceState(SourcePath requestedPath, SettingsSnapshot settings, long epoch, CancellationToken token)
{
    public SourcePath RequestedPath { get; } = requestedPath;
    public SettingsSnapshot Settings { get; } = settings;
    public ExclusionPolicy Policy { get; } = new(settings.ExcludedDirectoryNames);
    public long Epoch { get; } = epoch;
    public CancellationToken Token { get; } = token;
    public WorkspaceStatus Status { get; private set; } = WorkspaceStatus.Loading;
    public SourcePath? ResolvedPath { get; private set; }
    public DirectoryListing? RootListing { get; private set; }
    public OperationError? Error { get; private set; }
    public bool CanSearch => Status == WorkspaceStatus.Ready && !Token.IsCancellationRequested;

    internal void Resolve(SourcePath path) => ResolvedPath = path;
    internal void Complete(DirectoryListing listing)
    {
        RootListing = listing;
        Error = listing.Error;
        Status = listing.IsComplete ? WorkspaceStatus.Ready : WorkspaceStatus.Failed;
    }
    internal void Fail(OperationError error)
    {
        Error = error;
        Status = WorkspaceStatus.Failed;
    }
}
