using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Collections;
using Avalonia.Threading;
using CodeView.Contracts;
using CodeView.Operations;
using CodeView.Search;

namespace CodeView.Presentation;

internal sealed class SearchPanelViewModel(Func<SearchCoordinator> coordinator, Func<WorkspaceState?> workspace,
    Func<Task>? yieldToUi = null) : INotifyPropertyChanged
{
    private readonly Func<Task> _yieldToUi = yieldToUi ?? YieldAsync;
    private readonly SearchResults _results = new();
    private SearchCoordinator? _coordinator;
    private WorkspaceSearchOperation? _operation;
    private WorkspaceState? _workspace;
    private SearchProgress? _lastProgress;
    private Task _pump = Task.CompletedTask;
    private bool _pumping, _stopping, _visible, _running;
    private string _query = "", _notice = "フォルダを開いて検索してください。", _progress = "";
    private bool _matchCase, _wholeWord, _regex;
    public AvaloniaList<SearchResultRow> Rows => _results.Rows;
    public SearchResultRow? SelectedRow { get; set; }
    public string Query { get => _query; set { _query = value; Changed(nameof(Query), nameof(CanStart), nameof(HasUnappliedConditions)); } }
    public bool MatchCase { get => _matchCase; set { _matchCase = value; Changed(nameof(MatchCase), nameof(HasUnappliedConditions)); } }
    public bool WholeWord { get => _wholeWord; set { _wholeWord = value; Changed(nameof(WholeWord), nameof(HasUnappliedConditions)); } }
    public bool UseRegex { get => _regex; set { _regex = value; Changed(nameof(UseRegex), nameof(HasUnappliedConditions)); } }
    public bool HasUnappliedConditions => _operation is { Request: var request }
        && (request.Query != Query || request.Options != new SearchOptions(MatchCase, WholeWord, UseRegex));
    public string AppliedConditions { get; private set; } = "";
    public bool HasAppliedConditions => AppliedConditions.Length > 0;
    public bool IsVisible { get => _visible; set { _visible = value; Changed(nameof(IsVisible)); } }
    public bool IsRunning => _running;
    public bool CanStart => !_stopping && workspace()?.CanSearch == true && (_coordinator?.CanStart ?? true) && !string.IsNullOrEmpty(Query);
    public string Summary => $"{_notice} 確定 {_results.CommittedCount:N0}件 / 未確定 {_results.Count - _results.CommittedCount:N0}件";
    public string Progress => _progress;
    public string Description { get; private set; } = "フォルダ内のテキスト検索";
    public Task PendingWork => _pump;
    internal WorkspaceSearchOperation? CurrentOperation => _operation;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool CanActivate(SearchResultRow row) => !_stopping && _operation is { } operation
        && _coordinator?.CanAdoptTerminal(operation) == true && _results.CanActivate(row);

    public void RefreshWorkspace()
    {
        var current = workspace();
        if (!ReferenceEquals(_workspace, current))
        {
            _workspace = current;
            _operation = null;
            SetAppliedConditions();
            _results.Clear();
            SelectedRow = null;
            _running = false;
            _progress = "";
            _lastProgress = null;
            Description = "フォルダ内のテキスト検索";
            Changed(nameof(Description));
            _notice = current is null ? "フォルダを開いて検索してください。" : "検索語を入力してください。";
            Changed(nameof(Summary), nameof(Progress), nameof(IsRunning), nameof(SelectedRow));
        }
        Changed(nameof(CanStart));
    }

    public Task StartReferencesAsync(string name) => StartSymbolSearchAsync(name, definitions: false);
    public Task StartDefinitionsAsync(string name) => StartSymbolSearchAsync(name, definitions: true);

    private Task StartSymbolSearchAsync(string name, bool definitions)
    {
        if (_stopping) return Task.CompletedTask;
        Query = name;
        MatchCase = true;
        WholeWord = true;
        UseRegex = false;
        var description = definitions ? "定義へジャンプ（簡易・名前一致）"
            : "参照検索（名前一致）：宣言・コメント・文字列も含みます。意味解析は行いません。";
        return StartCoreAsync(description, definitions, references: !definitions);
    }

    public Task StartAsync() => StartCoreAsync("フォルダ内のテキスト検索", definitions: false);

    internal SearchResultRow? SingleCompleteDefinition(WorkspaceSearchOperation? operation)
    {
        if (operation is null || !operation.Request.Definitions || !IsCurrent(operation)
            || operation.CancellationCause != SearchCancellationCause.None || _results.Count != 1
            || _coordinator?.TryFinalize(operation, out var result) != true
            || result?.Status != OperationStatus.Success || result.Value.IsPartial)
            return null;
        var row = Rows[0];
        return CanActivate(row) ? row : null;
    }

    private Task StartCoreAsync(string description, bool definitions, bool references = false)
    {
        if (!CanStart) return Task.CompletedTask;
        _coordinator ??= coordinator();
        var accepted = references ? _coordinator.StartReferences(Query)
            : _coordinator.Start(Query, new(MatchCase, WholeWord, UseRegex), definitions: definitions);
        if (accepted.Status != OperationStatus.Success) return Task.CompletedTask;
        _operation = accepted.Value;
        SetAppliedConditions();
        Description = description;
        Changed(nameof(Description));
        _results.Clear();
        SelectedRow = null;
        _running = true;
        _notice = "検索を開始しています…";
        _progress = "";
        _lastProgress = null;
        IsVisible = true;
        Changed(nameof(Summary), nameof(Progress), nameof(IsRunning), nameof(SelectedRow));
        if (!_pumping) _pump = PumpAsync();
        return _pump;
    }

    private void SetAppliedConditions()
    {
        AppliedConditions = "";
        if (_operation is { Request: { Query: { } query, Options: var options } })
        {
            const int previewLength = 64;
            if (query.Length > previewLength)
            {
                var end = char.IsHighSurrogate(query[previewLength - 1]) ? previewLength - 1 : previewLength;
                query = query[..end] + "…";
            }

            var preview = query.Replace("\r", "␍", StringComparison.Ordinal)
                .Replace("\n", "␊", StringComparison.Ordinal).Replace("\t", "⇥", StringComparison.Ordinal);
            var comparison = options.MatchCase ? "大小文字を区別" : "大小文字を無視";
            var boundary = options.WholeWord ? "単語単位" : "部分一致";
            var mode = options.UseRegex ? "正規表現" : "通常";
            AppliedConditions = $"実行した条件 「{preview}」・{mode}・{comparison}・{boundary}";
        }
        Changed(nameof(AppliedConditions), nameof(HasAppliedConditions), nameof(HasUnappliedConditions));
    }

    public void Cancel()
    {
        if (_stopping || !_running) return;
        _coordinator?.Cancel();
        _results.RemoveProvisional();
        if (SelectedRow is { } selected && !_results.CanActivate(selected)) SelectedRow = null;
        _notice = "取消処理中… 採用済みの確定結果を保持します。";
        Changed(nameof(Summary), nameof(SelectedRow));
    }

    public void Clear()
    {
        if (_stopping) return;
        (_coordinator ??= coordinator()).Clear();
        _operation = null;
        SetAppliedConditions();
        _results.Clear();
        SelectedRow = null;
        _running = false;
        _notice = "結果をクリアしました。";
        _progress = "";
        _lastProgress = null;
        Changed(nameof(Summary), nameof(Progress), nameof(IsRunning), nameof(SelectedRow));
    }

    private async Task PumpAsync()
    {
        _pumping = true;
        try
        {
            while (!_stopping && _operation is { } operation)
            {
                SearchEvent<SearchHit>.Hits? batch = null;
                var batchIndex = 0;
                if (await operation.Started && IsCurrent(operation))
                {
                    await using var progressTimer = new ProgressTimer();
                    try
                    {
                        while (IsCurrent(operation) && _coordinator!.CanAdoptEvents(operation))
                        {
                            if (batch is null && !await WaitForEventsAsync(operation, progressTimer)) break;
                            if (!IsCurrent(operation) || !_coordinator.CanAdoptEvents(operation)) break;
                            var watch = Stopwatch.StartNew();
                            var units = 0;
                            while (units < 256 && watch.Elapsed.TotalMilliseconds < 4)
                            {
                                if (batch is not null)
                                {
                                    _results.Stage(batch.Items[batchIndex++], operation.Request.Root.FullPath);
                                    units++;
                                    if (batchIndex == batch.Items.Count) { batch = null; batchIndex = 0; }
                                }
                                else if (_coordinator.TryRead(operation, out var item))
                                {
                                    units++;
                                    switch (item)
                                    {
                                        case SearchEvent<SearchHit>.Hits hits when hits.Items.Count > 0: batch = hits; break;
                                        case SearchEvent<SearchHit>.FileCommitted file: _results.Commit(file.FileScanId); break;
                                        case SearchEvent<SearchHit>.FileInvalidated file:
                                            _results.Invalidate(file.FileScanId);
                                            if (SelectedRow?.File.IsInvalidated == true)
                                            { SelectedRow = null; Changed(nameof(SelectedRow)); }
                                            break;
                                    }
                                }
                                else break;
                            }
                            _results.Publish();
                            _notice = "検索中… 検証中の結果には移動できません。";
                            UpdateProgress(operation);
                            Changed(nameof(Summary));
                            await _yieldToUi();
                        }
                    }
                    catch (OperationCanceledException cancelled) when (operation.Token.IsCancellationRequested && cancelled.CancellationToken == operation.Token) { }
                }
                var result = await operation.Completion;
                if (IsCurrent(operation) && _coordinator!.CanAdoptTerminal(operation))
                {
                    // A partially consumed UI batch is not part of the channel. Cancellation drops it as well.
                    if (operation.CancellationCause == SearchCancellationCause.User || result.Status == OperationStatus.Cancelled)
                    { _results.RemoveProvisional(); _notice = "検索を取り消しました。部分結果です。"; }
                    else if (_coordinator.TryFinalize(operation, out _)) ApplyCompletion(result.Value, operation.Request.Definitions);
                    else throw new InvalidOperationException("検索の排出が終端より先に中断されました。");
                    _running = false;
                    UpdateProgress(operation);
                    Changed(nameof(Summary), nameof(IsRunning));
                }
                if (ReferenceEquals(_operation, operation)) break;
            }
        }
        catch
        {
            _results.RemoveProvisional();
            _running = false;
            _notice = "予期しない検索エラーのため終了します。";
            Changed(nameof(Summary), nameof(IsRunning), nameof(CanStart));
            throw;
        }
        finally { _pumping = false; }
    }

    private async Task<bool> WaitForEventsAsync(WorkspaceSearchOperation operation, ProgressTimer timer)
    {
        var ready = _coordinator!.WaitForEventsAsync(operation).AsTask();
        while (!ready.IsCompleted)
        {
            await Task.WhenAny(ready, timer.Next);
            if (timer.Next.IsCompleted)
            {
                await timer.ConsumeAsync();
                if (IsCurrent(operation)) UpdateProgress(operation);
            }
        }
        return await ready;
    }

    private sealed class ProgressTimer : IAsyncDisposable
    {
        private readonly PeriodicTimer _timer = new(TimeSpan.FromMilliseconds(100));
        private Task<bool>? _pending;
        public Task<bool> Next => _pending ??= _timer.WaitForNextTickAsync().AsTask();
        public async Task ConsumeAsync() { await Next; _pending = null; }
        public async ValueTask DisposeAsync()
        {
            _timer.Dispose();
            if (_pending is not null) await _pending;
        }
    }

    private void ApplyCompletion(SearchCompletion end, bool definitions)
    {
        if (end.Reason == SearchEndReason.LimitReached) _results.MarkIncomplete();
        else if (end.IsPartial) _results.RemoveProvisional();
        if (SelectedRow?.File.IsInvalidated == true) { SelectedRow = null; Changed(nameof(SelectedRow)); }
        _notice = end.Reason switch
        {
            SearchEndReason.LimitReached => "結果は10,000件で打ち切りました。未完了の結果には移動できません。",
            SearchEndReason.Cancelled => "検索を取り消しました。部分結果です。",
            SearchEndReason.Failed => end.Error?.Code switch
            {
                ErrorCode.InvalidPattern => "検索条件が不正です。語長・改行・正規表現を確認してください。",
                ErrorCode.RegexTimedOut => "正規表現がタイムアウトしました。部分結果です。",
                _ => "検索を完了できませんでした。部分結果です。"
            },
            _ when definitions && _results.CommittedCount == 0 => end.IsPartial
                ? "定義は見つかりませんでした。未評価あり。" : "定義が見つかりません。",
            _ when definitions && end.IsPartial => "名前一致による宣言候補の部分結果です。",
            _ when definitions => "名前一致による宣言候補です。",
            _ => end.IsPartial ? "検索終了・部分結果です。" : "検索完了。"
        };
        if (end.Progress.FailedEntries > 0 || end.Progress.SkippedLongLines > 0 || end.Progress.SkippedScalarMatches > 0)
            _notice += $" 読取等の失敗 {end.Progress.FailedEntries:N0}件、長行未評価 {end.Progress.SkippedLongLines:N0}行、スカラー境界での除外 {end.Progress.SkippedScalarMatches:N0}件。";
    }

    private bool IsCurrent(WorkspaceSearchOperation operation) => !_stopping && ReferenceEquals(_operation, operation);
    private void UpdateProgress(WorkspaceSearchOperation operation)
    {
        if (operation.LatestProgress is not { } progress || ReferenceEquals(progress, _lastProgress)) return;
        _lastProgress = progress;
        _progress = $"走査 {progress.VisitedFiles:N0}ファイル / 読取完了 {progress.ReadSourceBytes / (1024d * 1024):N1} MiB / 対象外 {progress.ExcludedEntries:N0}項目";
        Changed(nameof(Progress));
    }

    public void Stop()
    {
        _stopping = true;
        _operation = null;
        SetAppliedConditions();
        _results.Clear();
        SelectedRow = null;
        _running = false;
        Changed(nameof(IsRunning), nameof(CanStart), nameof(SelectedRow));
    }

    private static async Task YieldAsync() => await Dispatcher.Yield(DispatcherPriority.Background);
    private void Changed(params string[] names)
    { foreach (var name in names) PropertyChanged?.Invoke(this, new(name)); }
}
