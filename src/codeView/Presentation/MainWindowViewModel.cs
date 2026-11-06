using System.ComponentModel;
using Avalonia.Input;
using Avalonia.Collections;
using CodeView.Documents;
using AvaloniaEdit.Document;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Settings;

namespace CodeView.Presentation;

internal sealed class MainWindowViewModel(AppOperations operations, SettingsStore settingsStore, Func<Task>? treeYield = null,
    TimeProvider? timeProvider = null) : INotifyPropertyChanged
{
    private readonly string _baseDirectory = Environment.CurrentDirectory;
    private readonly CancellationTokenSource _lifetime = new();
    private TextDocument? _document;
    private string _status = "ファイルを開いて、読み始めましょう。";
    private string _filePath = "";
    private string _encodingName = "";
    private string _position = "";
    private readonly TabManager _tabManager = operations.Tabs;
    private TabViewModel? _selectedTab;
    private TabState? _displayedTab;
    private DocumentHandle? _displayedHandle;
    private bool _syncingTabs;
    private bool _attaching;
    private int _line = 1;
    private int _column = 1;
    private bool _pumping;
    private bool _stopping;
    private bool _initialized;
    private long _interactionEpoch;
    private long _selectionInteractionEpoch = -1;
    private SearchResultRow? _selectionRow;
    private SearchResultMoveRequest? _selectionRequest;
    private SearchResultMove? _selectionMove;
    private Task _pump = Task.CompletedTask;
    private Task _lateSettings = Task.CompletedTask;
    private readonly HashSet<Task> _manualWork = [];
    private bool _isTreeVisible = true;
    public FileTreeViewModel FileTree { get; } = new(treeYield);
    public SearchPanelViewModel SearchPanel { get; } = new(() => operations.Searches, () => operations.Workspace, treeYield);
    public bool IsTreeVisible { get => _isTreeVisible && HasWorkspace; set { _isTreeVisible = value; Changed(nameof(IsTreeVisible)); } }

    public TextDocument? Document => _document;
    public bool HasDocument => _document is not null;
    public bool CanReopenEncoding => _tabManager.ActiveTab is not null;
    public bool CanReload => _tabManager.ActiveTab is not null;
    public bool HasTabs => Tabs.Count > 0;
    public AvaloniaList<TabViewModel> Tabs { get; } = [];
    public long ViewEpoch { get; private set; }
    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_stopping || _syncingTabs || value is null || ReferenceEquals(_selectedTab, value)) return;
            if (_tabManager.Select(value.State)) { SyncTabs(); DisplayActiveTab(); }
        }
    }
    public string FilePath => _filePath;
    public string Status => _status;
    public string EncodingName => _encodingName;
    public string EncodingLabel => HasDocument ? _encodingName : "文字コードを指定…";
    public string Position => _position;
    public bool HasWorkspace => operations.Workspace is not null;
    public bool CanSearchWorkspace => operations.Workspace?.CanSearch == true;
    public string WorkspacePath => operations.Workspace?.RequestedPath.FullPath ?? "";
    public string WorkspaceSummary => operations.Workspace is not { } workspace ? "" : workspace.Status switch
    {
        WorkspaceStatus.Loading => "フォルダを確認中…",
        WorkspaceStatus.Failed => "フォルダを読み込めません。再読み込みできます。",
        _ => $"フォルダ: {workspace.RootListing!.Entries.Count:N0}項目"
    };
    public Task PendingWork => Task.WhenAll(new[] { _pump, _lateSettings, operations.PendingTreeWork, operations.PendingTabWork, operations.PendingSearchWork, operations.PendingResultMoveWork, SearchPanel.PendingWork }.Concat(_manualWork));
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? FatalFailure;
    public event Action<int, int>? RestoreCaret;
    public event Action<double, double, long>? RestoreViewport;
    public event Action<long, int>? SelectSearchRange;
    public event Action? ClearSearchSelection;
    public void RecordUserInteraction() => _interactionEpoch = checked(_interactionEpoch + 1);
    public bool CanApplyResultSelection(long epoch) => !_stopping && HasDocument && ViewEpoch == epoch
        && _interactionEpoch == _selectionInteractionEpoch && _selectionRow is { } row && SearchPanel.CanActivate(row)
        && _selectionRequest is { } request && _selectionMove is { } move && operations.ResultNavigator.CanAdopt(request, move);

    public Task ActivateSearchResultAsync(SearchResultRow row) => TrackManualAsync(ActivateSearchResultCoreAsync(row));

    private async Task ActivateSearchResultCoreAsync(SearchResultRow row)
    {
        if (_stopping || !SearchPanel.CanActivate(row) || SearchPanel.CurrentOperation is not { } source) return;
        var interaction = _interactionEpoch;
        ClearSearchSelection?.Invoke();
        var navigator = operations.ResultNavigator;
        var request = navigator.Start(source, row.Hit);
        SetStatus("検索結果への移動を待機しています…");
        if (await request.Started is not null && !_stopping)
        {
            SyncTabs(); DisplayActiveTab();
            if (interaction == _interactionEpoch && ReferenceEquals(navigator.Current, request) && SearchPanel.CanActivate(row))
                SetStatus("結果の更新情報を確認しています…");
        }
        var result = await request.Completion;
        if (_stopping) return;
        SyncTabs();
        DisplayActiveTab();
        if (interaction != _interactionEpoch || !ReferenceEquals(navigator.Current, request) || !SearchPanel.CanActivate(row)) return;
        if (result.Status == OperationStatus.Failure)
        { SetStatus(FileErrorText(result.Error!.Code) + " 再読み込み・再検索してください。"); return; }
        if (result.Status != OperationStatus.Success || !navigator.CanAdopt(request, result.Value)) return;
        var move = result.Value.Move;
        (_selectionRow, _selectionRequest, _selectionMove, _selectionInteractionEpoch) = (row, request, result.Value, interaction);
        ViewEpoch = checked(ViewEpoch + 1); // Supersede a deferred viewport restoration from document attachment.
        SetPosition(checked((int)move.Position.Line), checked((int)move.Position.Column));
        RestoreCaret?.Invoke(_line, _column);
        SelectSearchRange?.Invoke(move.Offset, move.SelectionLength);
        SetStatus(move.Warning switch
        {
            DocumentMoveWarning.Stale => "ファイルまたはタブ本文が検索時と異なります。再読み込み・再検索してください。選択は行いません。",
            DocumentMoveWarning.InvalidPosition => "結果位置が古い可能性があります。有効な位置へ移動しました。再検索してください。",
            DocumentMoveWarning.ContentMismatch => "一致内容が変わっています。選択せず移動しました。再検索してください。",
            DocumentMoveWarning.UnverifiedLength => "長い一致の全体を再確認できないため、選択せず移動しました。再検索してください。",
            _ => "検索結果へ移動しました。"
        });
    }

    public async Task InitializeAsync(string[] paths)
    {
        if (_initialized || _stopping) return;
        _initialized = true;
        if (paths.Length > 0) Submit(paths, OpenOrigin.CommandLine);
        var settingsTask = Task.Run(() => settingsStore.LoadAsync(_lifetime.Token), _lifetime.Token);
        _lateSettings = ObserveLateSettingsAsync(settingsTask);
        SettingsLoadResult settings;
        try { settings = await settingsTask.WaitAsync(TimeSpan.FromSeconds(2), timeProvider ?? TimeProvider.System, _lifetime.Token); }
        catch (TimeoutException)
        {
            settings = new(SettingsSnapshot.Default, null, new(ErrorCode.IoFailure), false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        if (_stopping) return;
        operations.Startup.CompleteSettings(settings);
        await EnsurePumpAsync();
        // Keep the fallback notice visible after queued files finish loading instead
        // of immediately replacing it with their loading/completion status.
        if (!_stopping && settings.Warning is not null)
            SetStatus(Status + " 設定を読み込めないため、既定値を使用します。");
    }

    private async Task ObserveLateSettingsAsync(Task<SettingsLoadResult> task)
    {
        try { _ = await task; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_stopping)
            {
                SetStatus("予期しない設定読込エラーのため終了します。");
                FatalFailure?.Invoke();
            }
        }
    }

    public async Task OpenFilesAsync(IReadOnlyList<string> paths, OpenOrigin origin)
    {
        if (_stopping) return;
        Submit(paths, origin);
        await EnsurePumpAsync();
    }

    public Task OpenDropAsync(IDataTransfer data)
    {
        if (_stopping) return Task.CompletedTask;
        var extracted = FileDropInput.Read(data);
        if (extracted.Status == OperationStatus.Failure)
        {
            SetStatus(extracted.Error!.Code == ErrorCode.QueueFull ? "ドロップの項目数またはパス文字数が上限を超えています。"
                : "ローカルのファイルまたはフォルダをドロップしてください。");
            return Task.CompletedTask;
        }
        return OpenFilesAsync(extracted.Value, OpenOrigin.Drop);
    }

    private void Submit(IReadOnlyList<string> paths, OpenOrigin origin)
    {
        var result = operations.Startup.Submit(paths, origin);
        if (result.Status == OperationStatus.Failure)
            SetStatus(result.Error!.Code == ErrorCode.QueueFull ? "開く要求が上限に達しました。" : "パスを確認してください。");
    }

    private Task EnsurePumpAsync()
    {
        if (_pumping) return _pump;
        _pump = PumpAsync();
        return _pump;
    }

    private async Task PumpAsync()
    {
        _pumping = true;
        try
        {
            while (!_stopping && operations.Startup.TryTake(out var request))
            {
                try
                {
                    var folderSeen = false;
                    var skippedFolders = 0;
                    foreach (var path in request!.Paths)
                    {
                        if (_stopping) break;
                        if (await OpenPathAsync(path, !folderSeen))
                        {
                            if (folderSeen) skippedFolders++;
                            folderSeen = true;
                        }
                    }
                    if (!_stopping && skippedFolders > 0)
                        SetStatus(Status + $" 後続のフォルダ{skippedFolders}件は開いていません。");
                }
                finally { operations.Startup.CompleteOpen(request!.Id); }
            }
        }
        finally { _pumping = false; }
    }

    private async Task<bool> OpenPathAsync(string input, bool allowFolder)
    {
        SourcePath path;
        try { path = SourcePath.Create(input, _baseDirectory); }
        catch (ArgumentException) { SetStatus("パスを確認してください。"); return false; }
        OperationResult<SourceEntry> inspected;
        try { inspected = await operations.InspectPathAsync(path, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        if (_stopping || inspected.Status == OperationStatus.Cancelled) return false;
        if (inspected.Status == OperationStatus.Failure)
        { SetStatus(ErrorText(inspected.Error!.Code)); return false; }
        if (inspected.Value.Kind == SourceEntryKind.Directory)
        {
            if (allowFolder) await OpenFolderAsync(path);
            return true;
        }
        await LoadAsync(input);
        return false;
    }

    private async Task OpenFolderAsync(SourcePath path)
    {
        var settings = operations.Startup.Settings?.Snapshot;
        if (settings is null || _stopping) return;
        SetStatus("フォルダを読み込み中… " + path.DisplayPath);
        var work = operations.OpenFolderAsync(path, settings);
        var workspace = operations.Workspace;
        FileTree.Clear();
        WorkspaceChanged();
        var result = await work;
        if (_stopping || !ReferenceEquals(operations.Workspace, workspace)) return;
        await FileTree.BindAsync(workspace?.Tree);
        if (_stopping || !ReferenceEquals(operations.Workspace, workspace)) return;
        WorkspaceChanged();
        if (result.Status == OperationStatus.Cancelled) return;
        SetStatus(result.Status == OperationStatus.Success ? "フォルダを開きました。"
            : "フォルダを読み込めません。 " + ErrorText(result.Error!.Code));
    }

    public async Task RefreshWorkspaceAsync()
    {
        if (operations.Workspace is { } workspace)
            await TrackManualAsync(OpenFolderAsync(workspace.RequestedPath));
    }

    private async Task TrackManualAsync(Task operation)
    {
        _manualWork.Add(operation);
        try { await operation; }
        finally { _manualWork.Remove(operation); }
    }

    public void CloseWorkspace()
    {
        operations.CloseWorkspace();
        FileTree.Clear();
        WorkspaceChanged();
        SetStatus("フォルダを閉じました。");
    }

    public async Task ActivateTreeRowAsync(FileTreeRow row)
    {
        if (_stopping || !FileTree.IsCurrentRow(row)) return;
        if (row.CanExpand)
        {
            if (row.Node.IsExpanded) FileTree.Collapse(row);
            else await ExpandTreeRowAsync(row);
        }
        else if (row.IsFile) await OpenFilesAsync([row.Node.Entry.Path.FullPath], OpenOrigin.Tree);
        else SetStatus("このリンクまたは特殊項目は展開できません。フォルダリンクはルートとして明示指定してください。");
    }

    public async Task ExpandTreeRowAsync(FileTreeRow row, bool reload = false)
    {
        if (!_stopping) await TrackManualAsync(FileTree.ExpandAsync(row, reload));
    }

    public void CollapseTreeRow(FileTreeRow row)
    {
        if (!_stopping) FileTree.Collapse(row);
    }

    private void WorkspaceChanged()
    {
        SearchPanel.RefreshWorkspace();
        Changed(nameof(HasWorkspace), nameof(CanSearchWorkspace), nameof(WorkspacePath), nameof(WorkspaceSummary), nameof(IsTreeVisible));
    }

    private static string ErrorText(ErrorCode code) => code switch
    {
        ErrorCode.NotFound => "パスが見つかりません。",
        ErrorCode.AccessDenied => "読み取る権限がありません。",
        ErrorCode.NotRegularFile => "対応する通常ファイルまたはフォルダを指定してください。",
        ErrorCode.BrokenLink => "リンク先が見つかりません。",
        _ => "読み取れませんでした。"
    };

    public async Task ReloadAsync()
    {
        if (!_stopping && _tabManager.ActiveTab is { } tab)
            await TrackManualAsync(ReloadTabAsync(tab, null));
    }

    public async Task ReopenEncodingAsync(EncodingChoice choice)
    {
        if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
        if (!_stopping && _tabManager.ActiveTab is { } tab)
            await TrackManualAsync(ReloadTabAsync(tab, choice));
    }

    private async Task ReloadTabAsync(TabState tab, EncodingChoice? choice)
    {
        var work = _tabManager.ReloadAsync(tab, choice);
        SyncTabs();
        DisplayActiveTab();
        var result = await work;
        if (_stopping) return;
        SyncTabs();
        if (ReferenceEquals(_tabManager.ActiveTab, tab)) DisplayActiveTab();
        if (result.Status == OperationStatus.Failure && result.Error!.Code == ErrorCode.QueueFull)
            SetStatus("読込待ちが上限に達しました。");
    }

    private async Task LoadAsync(string input)
    {
        if (_stopping) return;
        SourcePath path;
        try { path = SourcePath.Create(input, _baseDirectory); }
        catch (ArgumentException) { SetStatus("パスを確認してください。"); return; }
        var work = _tabManager.OpenAsync(path);
        var target = _tabManager.ActiveTab;
        SyncTabs();
        DisplayActiveTab();
        var result = await work;
        if (_stopping) return;
        SyncTabs();
        if (ReferenceEquals(_tabManager.ActiveTab, target)) DisplayActiveTab();
        if (result.Status == OperationStatus.Failure && result.Error!.Code == ErrorCode.QueueFull)
            SetStatus("読込待ちが上限に達しました。");
    }

    private void SyncTabs()
    {
        _syncingTabs = true;
        try
        {
            foreach (var row in Tabs.Where(row => !_tabManager.Tabs.Contains(row.State)).ToArray()) Tabs.Remove(row);
            foreach (var tab in _tabManager.Tabs)
                if (!Tabs.Any(row => ReferenceEquals(row.State, tab))) Tabs.Add(new(tab));
            var duplicateNames = Tabs.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Tabs) row.Refresh(duplicateNames.Contains(row.Name));
            _selectedTab = Tabs.FirstOrDefault(row => ReferenceEquals(row.State, _tabManager.ActiveTab));
            Changed(nameof(SelectedTab), nameof(HasTabs), nameof(CanReload), nameof(CanReopenEncoding));
        }
        finally { _syncingTabs = false; }
    }

    private void DisplayActiveTab()
    {
        var tab = _tabManager.ActiveTab;
        var file = tab?.File;
        var changedDocument = !ReferenceEquals(_displayedTab, tab) || !ReferenceEquals(_displayedHandle, file?.Document);
        var position = tab?.Position ?? new TextPosition(1, 1);
        var horizontal = tab?.HorizontalOffset ?? 0;
        var vertical = tab?.VerticalOffset ?? 0;
        _attaching = true;
        try
        {
            if (changedDocument)
            {
                ViewEpoch = checked(ViewEpoch + 1);
#if CODEVIEW_PERFORMANCE_PROBE
                if (file is not null) GuiPerformanceProbe.BeginDocumentAttachment();
#endif
                _document = file?.Document.ClaimForDisplay();
#if CODEVIEW_PERFORMANCE_PROBE
                if (file is not null) _document = GuiPerformanceProbe.SelectDisplayDocument(file.Document, _document!, position);
                else GuiPerformanceProbe.ClearDocument();
#endif
                _displayedTab = tab;
                _displayedHandle = file?.Document;
            }
            _filePath = tab?.Path.FullPath ?? "";
            _encodingName = file?.EncodingName.ToLowerInvariant() switch
            {
                "utf-16" => "UTF-16 LE", "utf-16be" => "UTF-16 BE",
                "utf-32" => "UTF-32 LE", "utf-32be" => "UTF-32 BE",
                "shift_jis" => "Shift_JIS（CP932）", null => "",
                _ => file!.EncodingName.ToUpperInvariant()
            };
            SetStatus(tab is null ? "ファイルを開いて、読み始めましょう。" : tab.Status switch
            {
                TabStatus.Loading => "読み込み中… " + tab.Path.DisplayPath,
                TabStatus.Reloading => "再読み込み中… " + tab.Path.DisplayPath,
                _ when tab.Error is not null => FileErrorText(tab.Error.Code),
                _ when tab.WasCancelled => "読込を取り消しました。",
                _ => "読み取り専用"
            });
            if (changedDocument) Changed(nameof(Document), nameof(HasDocument));
            Changed(nameof(FilePath), nameof(EncodingName), nameof(EncodingLabel), nameof(CanReopenEncoding));
        }
        finally { _attaching = false; }
        if (changedDocument)
        {
            SetPosition((int)Math.Min(int.MaxValue, position.Line), (int)Math.Min(int.MaxValue, position.Column));
            if (_document is not null)
            {
                RestoreCaret?.Invoke(_line, _column);
                RestoreViewport?.Invoke(horizontal, vertical, ViewEpoch);
            }
#if CODEVIEW_PERFORMANCE_PROBE
            if (file is not null) GuiPerformanceProbe.EndDocumentAttachment();
#endif
        }
    }

    private static string FileErrorText(ErrorCode code) => code switch
    {
        ErrorCode.Binary => "バイナリのため表示できません。テキストの場合は文字コードを指定して再試行してください。",
        ErrorCode.DecodeFailed => "指定した文字コードで読み取れないか、自動判定できません。文字コードを選び直してください。",
        ErrorCode.NotFound => "ファイルが見つかりません。",
        ErrorCode.AccessDenied => "ファイルを読む権限がありません。",
        ErrorCode.NotRegularFile => "通常のファイルを指定してください。",
        ErrorCode.BrokenLink => "リンク先のファイルが見つかりません。",
        ErrorCode.ChangedDuringRead => "読込中にファイルが更新されました。再読み込みしてください。",
        ErrorCode.UnsupportedSize => "本文の位置が表示モデルの範囲を超えています。",
        _ => "ファイルを読み込めませんでした。"
    };

    public void CycleTab(int direction)
    {
        if (_stopping || Tabs.Count == 0) return;
        var index = Tabs.IndexOf(_selectedTab!);
        SelectedTab = Tabs[(index + (direction < 0 ? Tabs.Count - 1 : 1)) % Tabs.Count];
    }

    public void CloseTab(TabViewModel tab)
    {
        if (_stopping || !_tabManager.Close(tab.State)) return;
        SyncTabs();
        DisplayActiveTab();
    }

    public void SetViewport(double horizontal, double vertical)
    {
        if (!_attaching && !_stopping && _displayedTab is { } tab && _document is not null)
            _tabManager.SaveView(tab, new(_line, _column), horizontal, vertical);
    }

    public void SetPosition(int line, int column)
    {
        if (_attaching) return;
        var document = _document;
#if CODEVIEW_PERFORMANCE_PROBE
        if (GuiPerformanceProbe.TryGetOriginalPosition(out var original, out var source))
        { document = source; line = checked((int)original.Line); column = checked((int)original.Column); }
#endif
        _line = document is null ? 1 : Math.Clamp(line, 1, document.LineCount);
        _column = document is null ? 1 : Math.Clamp(column, 1, document.GetLineByNumber(_line).Length + 1);
        _position = HasDocument ? $"行 {_line}, 列 {_column}" : "";
        if (!_stopping && _displayedTab is { } tab && _document is not null)
            _tabManager.SaveView(tab, new(_line, _column), tab.HorizontalOffset, tab.VerticalOffset);
        Changed(nameof(Position));
    }

    public void CloseFile()
    {
        if (_selectedTab is { } tab) CloseTab(tab);
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _selectionRow = null;
        _selectionRequest = null;
        _selectionMove = null;
        SearchPanel.Stop();
        _lifetime.Cancel();
        operations.Dispose();
        SyncTabs();
        DisplayActiveTab();
        FileTree.Clear();
        WorkspaceChanged();
    }

    public void SetStatus(string status) { _status = status; Changed(nameof(Status)); }
    private void Changed(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
