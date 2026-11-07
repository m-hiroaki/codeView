using System.Collections.ObjectModel;
using System.Threading.Channels;
using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Search;

internal sealed record WorkspaceSearchRequest(SourcePath Root, ExclusionPolicy Exclusions, EncodingChoice Encoding,
    string? Query, SearchOptions Options, long RequestId, long WorkspaceEpoch, long SearchEpoch, bool Definitions = false);

internal sealed record SearchHit(long FileScanId, SourcePath Path, TextRange Range, SearchSnippet Snippet, SourceStamp Stamp);

internal abstract record SearchEvent<T>
{
    internal sealed record Hits(IReadOnlyList<T> Items) : SearchEvent<T>;
    internal sealed record FileCommitted(long FileScanId) : SearchEvent<T>;
    internal sealed record FileInvalidated(long FileScanId, OperationError Error) : SearchEvent<T>;
}

internal sealed record SearchProgress(long VisitedFiles = 0, long CommittedFiles = 0, long ExcludedEntries = 0,
    long FailedEntries = 0, long ReadSourceBytes = 0, long DecodedCharacters = 0,
    long EvaluatedRegexLines = 0, long SkippedLongLines = 0, long SkippedScalarMatches = 0);

internal enum SearchEndReason { Completed, Cancelled, LimitReached, Failed }

internal sealed record SearchCompletion(SearchEndReason Reason, OperationError? Error, long? UncommittedFileScanId,
    long GeneratedHits, long ReturnedHits, long CommittedHits, SearchProgress Progress,
    IReadOnlyDictionary<ErrorCode, long> ErrorCounts, IReadOnlyList<OperationError> ErrorSamples)
{
    public bool IsPartial => Reason != SearchEndReason.Completed || Progress.FailedEntries > 0
        || Progress.SkippedLongLines > 0 || Progress.SkippedScalarMatches > 0;
}

internal sealed class SearchWorkerException(SearchCompletion completion, Exception inner)
    : Exception("検索ワーカーで予期しない障害が発生しました。", inner)
{
    public SearchCompletion Completion { get; } = completion;
}

/// <summary>The operation owner supplies cancellation and must observe Completion, including failures.</summary>
internal sealed class WorkspaceSearchSession
{
    private readonly Channel<SearchEvent<SearchHit>> _channel = Channel.CreateBounded<SearchEvent<SearchHit>>(
        new BoundedChannelOptions(SearchLimits.ResultQueueBatches)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private SearchProgress _progress = new();
    private SearchCompletion? _terminal;
    public ChannelReader<SearchEvent<SearchHit>> Events => _channel.Reader;
    internal ChannelWriter<SearchEvent<SearchHit>> Writer => _channel.Writer;
    public Task<SearchCompletion> Completion { get; }
    public SearchProgress LatestProgress => Volatile.Read(ref _progress);
    public SearchCompletion? Terminal => Volatile.Read(ref _terminal);

    internal WorkspaceSearchSession(Func<WorkspaceSearchSession, SearchCompletion> run)
        => Completion = Task.Run(() => run(this)); // Run even for an already-cancelled request, to close the writer.

    internal void Publish(SearchProgress progress) => Volatile.Write(ref _progress, progress);
    internal void Finish(SearchCompletion terminal) => Volatile.Write(ref _terminal, terminal);
}

internal delegate OperationError? DeclarationFileSearch(PreparedText source, Func<TextRange, SearchSnippet, bool> consume);

internal sealed class TextSearch(DirectoryReader directories, FileReader files)
{
    public WorkspaceSearchSession StartWorkspace(WorkspaceSearchRequest request, CancellationToken ct)
        => StartWorkspace(request, ct, null);

    internal WorkspaceSearchSession StartWorkspace(WorkspaceSearchRequest request, CancellationToken ct, DeclarationFileSearch? declarations)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Root);
        ArgumentNullException.ThrowIfNull(request.Exclusions);
        return new(session => new WorkspaceWorker(directories, files, request, session, ct, declarations).Run());
    }

    private sealed class WorkspaceWorker(DirectoryReader directories, FileReader files, WorkspaceSearchRequest request,
        WorkspaceSearchSession session, CancellationToken ct, DeclarationFileSearch? declarations)
    {
        private readonly Dictionary<ErrorCode, long> _errors = new();
        private readonly List<OperationError> _samples = new(SearchLimits.ErrorSamples);
        private List<SearchHit> _batch = new(SearchLimits.BatchSize);
        private SearchProgress _progress = new();
        private LiteralMatcher? _literal;
        private RegexLineMatcher? _regex;
        private long _generated, _returned, _committed, _fileHits, _nextId;
        private long? _uncommitted;
        private PreparedText? _current;
        private long _lastProgress = Environment.TickCount64;
        private long _lastBatch = Environment.TickCount64;

        public SearchCompletion Run()
        {
            var reason = SearchEndReason.Completed;
            OperationError? error = null;
            Exception? unexpected = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (declarations is null && request.Options.UseRegex)
                {
                    var result = RegexLineMatcher.Create(request.Query, request.Options);
                    if (result.Status != OperationStatus.Success) throw new StopScan(SearchEndReason.Failed, result.Error);
                    _regex = result.Value;
                }
                else if (declarations is null)
                {
                    var result = LiteralMatcher.Create(request.Query, request.Options);
                    if (result.Status != OperationStatus.Success) throw new StopScan(SearchEndReason.Failed, result.Error);
                    _literal = result.Value;
                }
                // Reuse delegates for every file/chunk instead of allocating per decoder callback.
                Func<TextRange, bool> consume = Consume;
                Action<ReadOnlyMemory<char>> read = chunk =>
                {
                    _progress = _progress with { DecodedCharacters = checked(_progress.DecodedCharacters + chunk.Length) };
                    if (!(_literal?.Push(chunk.Span, consume, ct) ?? _regex!.Push(chunk.Span, consume, ct))) StopMatching();
                    if (Environment.TickCount64 - _lastBatch >= SearchLimits.ProgressIntervalMilliseconds) Flush();
                    Publish();
                };
                foreach (var entry in directories.WalkFiles(request.Root, request.Exclusions, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.Kind == DirectoryWalkKind.Failed)
                    {
                        RecordFailure(entry.Error!);
                        if (entry.IsRoot) throw new StopScan(SearchEndReason.Failed, entry.Error);
                    }
                    else if (entry.Kind != DirectoryWalkKind.File)
                        _progress = _progress with { ExcludedEntries = checked(_progress.ExcludedEntries + 1) };
                    else if (declarations is not null && !string.Equals(Path.GetExtension(entry.Path.FullPath), ".cs", StringComparison.OrdinalIgnoreCase))
                        _progress = _progress with { ExcludedEntries = checked(_progress.ExcludedEntries + 1) };
                    else ScanFile(entry.Path, consume, read);
                    Publish();
                }
                ct.ThrowIfCancellationRequested();
            }
            catch (StopScan stop) { reason = stop.Reason; error = stop.Error; }
            catch (OperationCanceledException cancelled) when (ct.IsCancellationRequested && cancelled.CancellationToken == ct)
            { reason = SearchEndReason.Cancelled; }
            catch (Exception failure) { reason = SearchEndReason.Failed; error = new(ErrorCode.Unexpected); unexpected = failure; }
            finally
            {
                _literal?.Abort();
                _regex?.Abort();
                _batch.Clear();
                session.Publish(_progress);
            }
            var terminal = new SearchCompletion(reason, error, _uncommitted, _generated, _returned, _committed, _progress,
                new ReadOnlyDictionary<ErrorCode, long>(_errors), _samples.AsReadOnly());
            session.Finish(terminal);
            if (unexpected is not null)
            {
                var failure = new SearchWorkerException(terminal, unexpected);
                session.Writer.TryComplete(failure);
                throw failure;
            }
            session.Writer.TryComplete();
            return terminal;
        }

        private void ScanFile(SourcePath path, Func<TextRange, bool> consume, Action<ReadOnlyMemory<char>> read)
        {
            _progress = _progress with { VisitedFiles = checked(_progress.VisitedFiles + 1) };
            var id = checked(++_nextId);
            var result = files.Prepare(path, request.Encoding, ct);
            if (result.Status != OperationStatus.Success)
            {
                if (result.Status == OperationStatus.Cancelled) ct.ThrowIfCancellationRequested();
                var error = result.Error ?? new OperationError(ErrorCode.Unexpected, path.DisplayPath);
                if (error.Code is ErrorCode.Binary or ErrorCode.NotRegularFile)
                    _progress = _progress with { ExcludedEntries = checked(_progress.ExcludedEntries + 1) };
                else RecordFailure(error);
                return;
            }
            using var prepared = result.Value;
            _current = prepared;
            _uncommitted = id;
            _fileHits = 0;
            if (_literal?.IsFinished == true) _literal.Reset();
            if (_regex?.IsFinished == true) _regex.Reset();
            try
            {
                OperationError? error;
                if (declarations is not null)
                {
                    error = declarations(prepared, ConsumeDeclaration);
                    if (error is null) CountReadBytes(prepared);
                }
                else
                {
                    var loaded = files.ReadChunks(prepared, read, ct);
                    error = loaded.Status == OperationStatus.Success ? null
                        : loaded.Error ?? new OperationError(ErrorCode.Unexpected, path.DisplayPath);
                    if (error is null)
                    {
                        CountReadBytes(prepared);
                        if (!(_literal?.Complete(consume, ct) ?? _regex!.Complete(consume, ct))) StopMatching();
                    }
                }
                if (error is not null)
                {
                    _literal?.Abort();
                    _regex?.Abort();
                    RecordFailure(error);
                    Flush();
                    Send(new SearchEvent<SearchHit>.FileInvalidated(id, error));
                }
                else
                {
                    Flush();
                    Send(new SearchEvent<SearchHit>.FileCommitted(id));
                    _committed = checked(_committed + _fileHits);
                    _progress = _progress with { CommittedFiles = checked(_progress.CommittedFiles + 1) };
                }
                _uncommitted = null;
            }
            finally
            {
                if (_regex is not null)
                    _progress = _progress with
                    {
                        EvaluatedRegexLines = checked(_progress.EvaluatedRegexLines + _regex.EvaluatedLines),
                        SkippedLongLines = checked(_progress.SkippedLongLines + _regex.SkippedLongLines),
                        SkippedScalarMatches = checked(_progress.SkippedScalarMatches + _regex.SkippedScalarMatches)
                    };
                _current = null;
            }
        }

        private void CountReadBytes(PreparedText prepared) =>
            _progress = _progress with { ReadSourceBytes = checked(_progress.ReadSourceBytes + prepared.Stamp.LengthBytes) };

        private bool Consume(TextRange range)
            => Collect(range, _literal?.CaptureSnippet() ?? _regex!.CaptureSnippet());

        private bool ConsumeDeclaration(TextRange range, SearchSnippet snippet)
        {
            if (!Collect(range, snippet)) StopMatching();
            return true;
        }

        private bool Collect(TextRange range, SearchSnippet snippet)
        {
            ct.ThrowIfCancellationRequested();
            _batch.Add(new(_uncommitted!.Value, _current!.Path, range, snippet, _current.Stamp));
            _generated = checked(_generated + 1);
            _fileHits = checked(_fileHits + 1);
            if (_generated == 1 || _batch.Count == SearchLimits.BatchSize || _generated == SearchLimits.ResultLimit
                || Environment.TickCount64 - _lastBatch >= SearchLimits.ProgressIntervalMilliseconds) Flush();
            return _generated < SearchLimits.ResultLimit;
        }

        private void StopMatching() => throw (_regex?.Error is { } error
            ? new StopScan(SearchEndReason.Failed, error)
            : new StopScan(SearchEndReason.LimitReached, null));

        private void Flush()
        {
            if (_batch.Count == 0) return;
            Send(new SearchEvent<SearchHit>.Hits(_batch.AsReadOnly()));
            _returned = checked(_returned + _batch.Count);
            _batch = new(SearchLimits.BatchSize);
            _lastBatch = Environment.TickCount64;
        }

        private void Send(SearchEvent<SearchHit> item)
            => session.Writer.WriteAsync(item, ct).AsTask().GetAwaiter().GetResult();

        private void RecordFailure(OperationError error)
        {
            _progress = _progress with { FailedEntries = checked(_progress.FailedEntries + 1) };
            _errors[error.Code] = checked(_errors.GetValueOrDefault(error.Code) + 1);
            if (_samples.Count < SearchLimits.ErrorSamples) _samples.Add(error);
        }

        private void Publish()
        {
            var now = Environment.TickCount64;
            if (now - _lastProgress < SearchLimits.ProgressIntervalMilliseconds) return;
            session.Publish(_progress);
            _lastProgress = now;
        }

        private sealed class StopScan(SearchEndReason reason, OperationError? error) : Exception
        {
            public SearchEndReason Reason { get; } = reason;
            public OperationError? Error { get; } = error;
        }
    }
}
