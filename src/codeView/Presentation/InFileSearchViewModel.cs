using System.ComponentModel;
using System.Threading.Channels;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Operations;
using CodeView.Search;

namespace CodeView.Presentation;

internal sealed class InFileSearchViewModel(AppOperations operations) : INotifyPropertyChanged
{
    private readonly Dictionary<TabState, State> _tabs = [];
    private readonly HashSet<Task> _work = [];
    private Task _failure = Task.CompletedTask;
    private State? _active;
    private CancellationTokenSource? _request;
    private bool _visible, _stopped, _running, _navigating;
    private string? _navigationNotice;

    private sealed class State(DocumentHandle? document)
    {
        public DocumentHandle? Document = document;
        public string Query = "";
        public SearchOptions Options;
        public readonly List<TextRange> Matches = [];
        public InFileScanResult? Scan;
        public TextRange? Current;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? MatchesChanged;
    public event Action? FatalFailure;
    internal long Revision { get; private set; }
    public Task PendingWork => Task.WhenAll(_work.Append(_failure));
    public IReadOnlyList<TextRange> Matches => _active is { } active && IsVisible ? active.Matches : Array.Empty<TextRange>();

    public bool CanOpen => _active?.Document is not null;

    public bool IsVisible
    {
        get => _visible && CanOpen;
        set
        {
            if (_visible == value) return;
            _visible = value;
            Cancel();
            if (value) Resume();
            Notify();
        }
    }

    public string Query
    {
        get => _active?.Query ?? "";
        set
        {
            value ??= "";
            if (_active is not { } state || state.Query == value) return;
            state.Query = value;
            Reset(state);
        }
    }

    public bool MatchCase
    {
        get => _active?.Options.MatchCase ?? false;
        set { if (_active is { } state) SetOptions(state, state.Options with { MatchCase = value }); }
    }

    public bool WholeWord
    {
        get => _active?.Options.WholeWord ?? false;
        set { if (_active is { } state) SetOptions(state, state.Options with { WholeWord = value }); }
    }

    public bool UseRegex
    {
        get => _active?.Options.UseRegex ?? false;
        set { if (_active is { } state) SetOptions(state, state.Options with { UseRegex = value }); }
    }

    public string Summary
    {
        get
        {
            if (_active is not { Query.Length: > 0 } state) return "検索語を入力してください。";
            if (_navigationNotice is { } notice) return notice;
            var count = state.Matches.Count;
            return state.Scan switch
            {
                { Error.Code: ErrorCode.InvalidPattern } => "検索条件が不正です。",
                { Error.Code: ErrorCode.RegexTimedOut } timeout => $"正規表現がタイムアウトしました。少なくとも {count:N0} 件。" + SkippedDetails(timeout),
                { IsPartial: true } partial => $"少なくとも {count:N0} 件。未評価あり。" + SkippedDetails(partial),
                { ReachedEnd: true } => count == 0 ? "見つかりません。" : $"{count:N0} 件。",
                _ when count == SearchLimits.ResultLimit => $"少なくとも {count:N0} 件。",
                _ when _running => $"検索中… {count:N0} 件。",
                _ => $"件数未確定。少なくとも {count:N0} 件。"
            };
        }
    }

    internal void Attach(TabState? tab, IReadOnlyCollection<TabState> tabs, bool sourceOffsetsAvailable)
    {
        foreach (var closed in _tabs.Keys.Where(key => !tabs.Contains(key)).ToArray())
            _tabs.Remove(closed);
        if (tab is null)
        {
            if (_active is null) return;
            Cancel();
            _active = null;
            Notify();
            return;
        }

        var document = sourceOffsetsAvailable && tab.Status != TabStatus.Reloading ? tab.File?.Document : null;
        if (!_tabs.TryGetValue(tab, out var state))
            _tabs.Add(tab, state = new(document));
        if (ReferenceEquals(_active, state) && ReferenceEquals(state.Document, document)) return;
        Cancel();
        _active = state;
        if (!ReferenceEquals(state.Document, document))
        {
            state.Document = document;
            Reset(state);
            return;
        }
        Resume();
        Notify();
    }

    private void SetOptions(State state, SearchOptions options)
    {
        if (state.Options == options) return;
        state.Options = options;
        Reset(state);
    }

    private void Reset(State state)
    {
        Cancel();
        state.Matches.Clear();
        state.Scan = null;
        state.Current = null;
        Resume();
        Notify();
    }

    private void Resume()
    {
        if (_stopped || !IsVisible || _active is not { Document: { } document, Query.Length: > 0, Scan: null } state
            || state.Matches.Count == SearchLimits.ResultLimit) return;
        var request = BeginRequest();
        var query = state.Query;
        var options = state.Options;
        var retained = state.Matches.Count;
        Track(ScanAsync(), notifyFailure: true);

        async Task ScanAsync()
        {
            var ct = request.Token;
            try
            {
                await Task.Delay(150, ct);
                var channel = Channel.CreateBounded<TextRange[]>(new BoundedChannelOptions(2)
                { SingleReader = true, SingleWriter = true });
                var worker = RunProducerAsync();
                var drained = false;
                try
                {
                    await foreach (var batch in channel.Reader.ReadAllAsync(ct))
                    {
                        if (!IsCurrent(state, request)) break;
                        state.Matches.AddRange(batch);
                        NotifyResults();
                    }
                    drained = IsCurrent(state, request);
                }
                finally
                {
                    if (!drained) request.Cancel();
                    try
                    {
                        var result = await worker;
                        if (IsCurrent(state, request)) state.Scan = result;
                    }
                    catch (OperationCanceledException error) when (!drained && ct.IsCancellationRequested
                        && error.CancellationToken == ct)
                    {
                        // Preserve an exception from the result consumer while stopping its producer.
                    }
                }

                async Task<InFileScanResult> RunProducerAsync()
                {
                    try
                    {
                        return await operations.RunInFileSearchAsync(document,
                            lease => Produce(lease, query, options, retained, channel.Writer, ct), ct);
                    }
                    finally { channel.Writer.TryComplete(); }
                }
            }
            catch (OperationCanceledException e) when (ct.IsCancellationRequested && e.CancellationToken == ct) { }
            finally { Finish(request); }
        }
    }

    private static InFileScanResult Produce(DocumentReadLease lease, string query, SearchOptions options,
        int retained, ChannelWriter<TextRange[]> output, CancellationToken ct)
    {
        var batch = new List<TextRange>(SearchLimits.BatchSize);
        var count = 0;
        var lastPublish = Environment.TickCount64;
        var scan = InFileSearch.Scan(lease, query, options, match =>
        {
            count++;
            if (count > retained) batch.Add(match);
            if (count == retained + 1 || batch.Count == SearchLimits.BatchSize
                || Environment.TickCount64 - lastPublish >= SearchLimits.ProgressIntervalMilliseconds) Flush();
            return count < SearchLimits.ResultLimit;
        }, ct);
        Flush();
        return scan;

        void Flush()
        {
            if (batch.Count == 0) return;
            output.WriteAsync(batch.ToArray(), ct).AsTask().GetAwaiter().GetResult();
            batch.Clear();
            lastPublish = Environment.TickCount64;
        }
    }

    public Task<TextRange?> MoveAsync(long caret, long selectionStart, int selectionLength, bool previous)
    {
        if (_stopped || !IsVisible || _active is not { Document: { } document, Query.Length: > 0 } state)
            return Task.FromResult<TextRange?>(null);
        if (_navigating) Cancel();
        if (state.Scan is { Error.Code: ErrorCode.InvalidPattern }) return Task.FromResult<TextRange?>(null);
        var boundary = selectionLength > 0 ? selectionStart + (previous ? 0 : selectionLength) : caret;
        var excludeBoundary = selectionLength == 0 && state.Current is { Length: 0 } current && current.Offset == caret;
        var index = InFileSearch.LowerBound(state.Matches, boundary);
        if (previous)
        {
            if (index > 0 && (index < state.Matches.Count || state.Scan is { ReachedEnd: true }))
                return Task.FromResult(Apply(state, state.Matches[index - 1], false, state.Scan));
        }
        else
        {
            if (excludeBoundary && index < state.Matches.Count && state.Matches[index].Offset == boundary) index++;
            if (index < state.Matches.Count)
                return Task.FromResult(Apply(state, state.Matches[index], false, state.Scan));
        }
        if (state.Scan is { ReachedEnd: true } end)
        {
            TextRange? wrapped = state.Matches.Count > 0 ? state.Matches[previous ? state.Matches.Count - 1 : 0] : null;
            return Task.FromResult(Apply(state, wrapped, wrapped is not null, end));
        }

        var request = BeginRequest();
        var query = state.Query;
        var options = state.Options;
        _navigating = true;
        var work = FindAsync();
        Track(work, notifyFailure: false);
        NotifyResults();
        return work;

        async Task<TextRange?> FindAsync()
        {
            var ct = request.Token;
            try
            {
                var result = await operations.RunInFileSearchAsync(document,
                    lease => InFileSearch.Find(lease, query, options, boundary, previous, excludeBoundary, ct), ct);
                return IsCurrent(state, request) ? Apply(state, result.Match, result.Wrapped, result.Scan) : null;
            }
            catch (OperationCanceledException e) when (ct.IsCancellationRequested && e.CancellationToken == ct) { return null; }
            finally { Finish(request); }
        }
    }

    private TextRange? Apply(State state, TextRange? match, bool wrapped, InFileScanResult? scan)
    {
        state.Current = match;
        _navigationNotice = (scan?.Error?.Code, match, wrapped) switch
        {
            (ErrorCode.InvalidPattern, _, _) => "検索条件が不正です。",
            (ErrorCode.RegexTimedOut, _, _) => "正規表現がタイムアウトしました。未評価あり。",
            (_, null, _) => scan?.IsPartial == true ? "見つかりませんでした。未評価あり。" : "見つかりません。",
            (_, _, true) => scan?.IsPartial == true ? "折り返しました。未評価あり。" : "折り返しました。",
            _ when scan?.IsPartial == true => "一致に移動しました。未評価あり。",
            _ => null
        };
        if (_navigationNotice is not null && scan is { } result)
            _navigationNotice += SkippedDetails(result);
        NotifyResults();
        return match;
    }

    private static string SkippedDetails(InFileScanResult scan)
    {
        var details = "";
        if (scan.SkippedLongLines > 0) details += $" 長行未評価 {scan.SkippedLongLines:N0}行。";
        if (scan.SkippedScalarMatches > 0) details += $" 文字の途中での一致を除外 {scan.SkippedScalarMatches:N0}件。";
        return details;
    }

    private CancellationTokenSource BeginRequest()
    {
        Cancel();
        _running = true;
        return _request = new();
    }

    private bool IsCurrent(State state, CancellationTokenSource request) =>
        !_stopped && ReferenceEquals(_active, state) && ReferenceEquals(_request, request) && !request.IsCancellationRequested;

    private void Finish(CancellationTokenSource request)
    {
        request.Dispose();
        if (!ReferenceEquals(_request, request)) return;
        _request = null;
        _running = false;
        _navigating = false;
        NotifyResults();
    }

    private void Cancel()
    {
        Revision = checked(Revision + 1);
        var request = _request;
        _request = null;
        request?.Cancel();
        _running = false;
        _navigating = false;
        _navigationNotice = null;
    }

    public void Stop()
    {
        _stopped = true;
        Cancel();
        _tabs.Clear();
        _active = null;
        Notify();
    }

    private async void Track(Task task, bool notifyFailure)
    {
        _work.Add(task);
        try { await task; }
        catch (Exception error)
        {
            // Keep the first fault visible after the completed operation leaves the work set.
            if (_failure.IsCompletedSuccessfully)
            {
                _failure = Task.FromException(error);
                _ = _failure.Exception;
            }
            if (notifyFailure && !_stopped) FatalFailure?.Invoke();
        }
        finally { _work.Remove(task); }
    }

    private void Notify()
    {
        foreach (var name in new[] { nameof(CanOpen), nameof(IsVisible), nameof(Query), nameof(MatchCase), nameof(WholeWord), nameof(UseRegex) })
            PropertyChanged?.Invoke(this, new(name));
        NotifyResults();
    }

    private void NotifyResults()
    {
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
        MatchesChanged?.Invoke();
    }
}
