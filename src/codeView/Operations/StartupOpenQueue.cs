using System.Collections.ObjectModel;
using CodeView.Contracts;
using CodeView.Settings;

namespace CodeView.Operations;

internal enum StartupState { WaitingSettings, Ready, Stopping }
internal enum OpenOrigin { CommandLine, Menu, Drop, Tree }

internal sealed record OpenRequest(long Id, ReadOnlyCollection<string> Paths, OpenOrigin Origin);

/// <summary>Serialized state only. The owner calls it on the UI operation entry point.</summary>
internal sealed class StartupOpenQueue
{
    internal const int MaxRequests = 64;
    internal const int MaxPaths = 256;
    internal const int MaxPathChars = 256 * 1024;
    private readonly Queue<OpenRequest> _waiting = new();
    private readonly Dictionary<long, OpenRequest> _outstanding = [];
    private long _nextId;
    private int _pathCount;
    private int _pathChars;

    public StartupState State { get; private set; } = StartupState.WaitingSettings;
    public SettingsLoadResult? Settings { get; private set; }
    public int OutstandingCount => _outstanding.Count;

    public OperationResult<OpenRequest> Submit(IReadOnlyList<string> paths, OpenOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (State == StartupState.Stopping)
            return OperationResult<OpenRequest>.Cancelled();
        if (paths.Count == 0 || !Enum.IsDefined(origin))
            return OperationResult<OpenRequest>.Failure(new(ErrorCode.InvalidPath));
        if (_outstanding.Count >= MaxRequests || paths.Count > MaxPaths - _pathCount)
            return OperationResult<OpenRequest>.Failure(new(ErrorCode.QueueFull));

        var chars = 0L;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
                return OperationResult<OpenRequest>.Failure(new(ErrorCode.InvalidPath));
            chars += path.Length;
            if (chars > MaxPathChars - _pathChars)
                return OperationResult<OpenRequest>.Failure(new(ErrorCode.QueueFull));
        }

        var request = new OpenRequest(checked(++_nextId), Array.AsReadOnly(paths.ToArray()), origin);
        _waiting.Enqueue(request);
        _outstanding.Add(request.Id, request);
        _pathCount += paths.Count;
        _pathChars += (int)chars;
        return OperationResult<OpenRequest>.Success(request);
    }

    public bool CompleteSettings(SettingsLoadResult settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (State != StartupState.WaitingSettings)
            return false;
        Settings = settings;
        State = StartupState.Ready;
        return true;
    }

    public bool TryTake(out OpenRequest? request)
    {
        request = null;
        return State == StartupState.Ready && _waiting.TryDequeue(out request);
    }

    public bool CompleteOpen(long requestId)
    {
        if (!_outstanding.TryGetValue(requestId, out var request) || _waiting.Any(x => x.Id == requestId))
            return false;
        _outstanding.Remove(requestId);
        _pathCount -= request.Paths.Count;
        foreach (var path in request.Paths)
            _pathChars -= path.Length;
        return true;
    }

    public void Stop()
    {
        State = StartupState.Stopping;
        _waiting.Clear();
        _outstanding.Clear();
        _pathCount = _pathChars = 0;
    }
}
