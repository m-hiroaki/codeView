using System.ComponentModel;
using Avalonia.Input;
using Avalonia.Collections;
using CodeView.Documents;
using AvaloniaEdit.Document;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Settings;
using CodeView.Navigation;

namespace CodeView.Presentation;

internal sealed class MainWindowViewModel(AppOperations operations, SettingsStore settingsStore, Func<Task>? treeYield = null,
    TimeProvider? timeProvider = null, RecentHistoryStore? recentHistory = null) : INotifyPropertyChanged
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
    private bool _originalSourceOffsetsAvailable = true;
    private CancellationTokenSource? _syntaxRequest;
    private bool _syncingTabs;
    private bool _attaching;
    private int _line = 1;
    private int _column = 1;
    private bool _pumping;
    private bool _stopping;
    private bool _initialized;
    private SettingsLoadResult? _settingsForSave;
    private bool _savingSettings;
    private bool _stickyPreferenceChanged;
    private Task _settingsWrite = Task.CompletedTask;
    private Task _stickySettingsWrite = Task.CompletedTask;
    private long _interactionEpoch;
    private long _selectionInteractionEpoch = -1;
    private SearchResultRow? _selectionRow;
    private SearchResultMoveRequest? _selectionRequest;
    private SearchResultMove? _selectionMove;
    internal const int JumpHistoryLimit = 100;
    private readonly record struct JumpLocation(SourcePath Path, EncodingChoice Encoding, TextPosition Position,
        double Horizontal, double Vertical);
    private List<(JumpLocation From, JumpLocation To)>? _jumps;
    private int _jumpIndex;
    private bool _restoringJump;
    private Task _pump = Task.CompletedTask;
    private Task _lateSettings = Task.CompletedTask;
    private Task _syntaxFailure = Task.CompletedTask;
    private Task _recentWork = Task.CompletedTask;
    private readonly HashSet<Task> _manualWork = [];
    private bool _isTreeVisible = true;
    public FileTreeViewModel FileTree { get; } = new(treeYield);
    public SearchPanelViewModel SearchPanel { get; } = new(() => operations.Searches, () => operations.Workspace, treeYield);
    public bool IsTreeVisible { get => _isTreeVisible && HasWorkspace; set { _isTreeVisible = value; Changed(nameof(IsTreeVisible)); } }

    public TextDocument? Document => _document;
    public bool HasDocument => _document is not null;
    public bool StickyScrollEnabled { get; private set; } = true;
    internal LoadedFile? StickyFile => _originalSourceOffsetsAvailable && operations.CurrentFile is { } file
        && file.Document.Length <= StickyScopeRanges.MaximumChars
        && (Path.GetExtension(file.Path.FullPath).Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || CppDeclarationScanner.IncludesPath(file.Path.FullPath)) ? file : null;
    public bool CanReopenEncoding => _tabManager.ActiveTab is not null;
    public bool CanReload => _tabManager.ActiveTab is not null;
    public bool CanJumpBack => !_stopping && !_restoringJump && _jumpIndex > 0;
    public bool CanJumpForward => !_stopping && !_restoringJump && _jumpIndex < (_jumps?.Count ?? 0);
    public bool HasTabs => Tabs.Count > 0;
    public bool IsWelcomeVisible => !HasTabs && !HasWorkspace;
    public IReadOnlyList<RecentItem> RecentItems { get; private set; } = [];
    public bool HasRecentItems => RecentItems.Count > 0;
    internal Task RecentWork => _recentWork;
    public bool CanSaveSettings => !_stopping && !_pumping && !_savingSettings && _settingsForSave is { CanSave: true };
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
    public string WindowTitle => FilePath.Length == 0 ? "codeView" : $"{FilePath} — codeView";
    public string Breadcrumb => FormatBreadcrumb(WorkspacePath, FilePath);
    public string BreadcrumbPath => FilePath.Length == 0 ? WorkspacePath : FilePath;

    internal static string FormatBreadcrumb(string workspace, string file)
    {
        if (workspace.Length != 0)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace));
            if (name.Length == 0) name = workspace;
            if (file.Length == 0) return name;
            var relative = Path.GetRelativePath(workspace, file);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return name + " / " + relative.Replace(Path.DirectorySeparatorChar.ToString(), " / ");
        }
        if (file.Length == 0) return "codeView";
        var parent = Path.GetDirectoryName(file);
        var folder = Path.GetFileName(parent);
        return string.IsNullOrEmpty(folder) ? Path.GetFileName(file) : folder + " / " + Path.GetFileName(file);
    }
    public string Status => _status;
    public string EncodingName => _encodingName;
    public string EncodingLabel => HasDocument ? _encodingName : "文字コードを指定…";
    public string Position => _position;
    public bool HasWorkspace => operations.Workspace is not null;
    public bool CanSearchWorkspace => operations.Workspace?.CanSearch == true;
    public string DefinitionMenuLabel => operations.UsesSemanticNavigation(FilePath) ? "定義へジャンプ（意味解析）" : "定義へジャンプ（簡易）";
    public string ReferencesMenuLabel => operations.UsesSemanticNavigation(FilePath) ? "参照を検索（意味解析）" : "参照を検索（名前一致）";
    internal SemanticWorkspaceOptions? SemanticOptions => operations.SemanticOptions;
#if CODEVIEW_PERFORMANCE_PROBE || CODEVIEW_SEMANTIC_PROBE
    internal int[] SemanticServerProcessIds => operations.SemanticServerProcessIds;
#endif
    public string WorkspacePath => operations.Workspace?.RequestedPath.FullPath ?? "";
    internal long? WorkspaceEpoch => operations.Workspace?.Epoch;
    public InFileSearchViewModel InFileSearch { get; } = new(operations);
    public string SyntaxStatus { get; private set; } = "";
    public event Action<SyntaxHighlighting?>? SyntaxChanged;
    public Task PendingWork => Task.WhenAll(new[] { _pump, _lateSettings, _syntaxFailure, _recentWork, _settingsWrite,
        _stickySettingsWrite, operations.PendingTreeWork, operations.PendingTabWork, operations.PendingSearchWork,
        operations.PendingResultMoveWork, SearchPanel.PendingWork, InFileSearch.PendingWork }.Concat(_manualWork));
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? FatalFailure;
    public event Action<int, int>? RestoreCaret;
    public event Action<double, double, long>? RestoreViewport;
    public event Action<long, int>? SelectSearchRange;
    public event Action? ClearSearchSelection;
    internal long InteractionEpoch => _interactionEpoch;
    public void RecordUserInteraction() => _interactionEpoch = checked(_interactionEpoch + 1);
    public bool CanApplyResultSelection(long epoch) => !_stopping && HasDocument && ViewEpoch == epoch
        && _interactionEpoch == _selectionInteractionEpoch && _selectionRow is { } row && SearchPanel.CanActivate(row)
        && _selectionRequest is { } request && _selectionMove is { } move && operations.ResultNavigator.CanAdopt(request, move);

    public Task FindReferencesAsync(int caret, int selectionStart, int selectionLength)
        => TrackManualAsync(FindSymbolAsync(caret, selectionStart, selectionLength, definitions: false));

    public Task FindDefinitionsAsync(int caret, int selectionStart, int selectionLength)
        => TrackManualAsync(FindSymbolAsync(caret, selectionStart, selectionLength, definitions: true));

    internal bool EnableBundledSemanticNavigation(long? rootEpoch, string? project, string? database, string appDirectory)
    {
        if (_stopping || rootEpoch != WorkspaceEpoch || !CanSearchWorkspace) return false;
        var (roslyn, clangd) = BundledLanguageServers.Resolve(appDirectory);
        if (roslyn is null && clangd is null)
        {
            SetStatus("同梱の Roslyn と clangd が見つかりません。配布物を確認してください。");
            return false;
        }

        if (!ConfigureSemanticNavigation(rootEpoch, new(roslyn, project, clangd, database))) return false;
        if (roslyn is null) SetStatus("同梱の Roslyn が見つかりません。C# では簡易検索を使用します。");
        if (clangd is null) SetStatus("同梱の clangd が見つかりません。C/C++ では簡易検索を使用します。");
        return true;
    }

    internal bool ConfigureSemanticNavigation(long? rootEpoch, SemanticWorkspaceOptions? options)
    {
        if (_stopping || rootEpoch != WorkspaceEpoch || !CanSearchWorkspace) return false;
        if (options is not null &&
            (!ValidExecutable(options.RoslynExecutable) || !ValidExecutable(options.ClangdExecutable)
            || options.RoslynExecutable is null && options.ClangdExecutable is null
            || options.CSharpProject is { } project && (!Path.IsPathFullyQualified(project) || !RoslynNavigation.SupportsProject(project) || !File.Exists(project))
            || options.CompilationDatabase is { } database && (!Path.IsPathFullyQualified(database) || !File.Exists(database))))
        {
            SetStatus("指定したプロジェクト設定の絶対パスと、同梱の言語サーバーを確認してください。");
            return false;
        }
        RecordUserInteraction();
        SearchPanel.Clear();
        operations.ConfigureSemanticNavigation(options);
        Changed(nameof(DefinitionMenuLabel), nameof(ReferencesMenuLabel));
        SetStatus(options is null ? "このフォルダでは簡易検索を使用します。" : "このフォルダの意味解析を有効にしました。");
        return true;

        static bool ValidExecutable(string? path) => path is null || Path.IsPathFullyQualified(path) && File.Exists(path);
    }

    private async Task FindSymbolAsync(int caret, int selectionStart, int selectionLength, bool definitions)
    {
        if (_stopping) return;
        var document = _document;
#if CODEVIEW_PERFORMANCE_PROBE
        if (GuiPerformanceProbe.HasContextualRows && GuiPerformanceProbe.TryGetOriginalPosition(out _, out var source))
            document = source;
#endif
        if (document is null) { SetStatus("探索するファイルを開いてください。"); return; }
        if (!CanSearchWorkspace) { SetStatus("探索するフォルダを開いてください。"); return; }
        if ((uint)caret > document.TextLength || (uint)selectionStart > document.TextLength || selectionLength < 0
            || selectionLength > document.TextLength - selectionStart)
        { SetStatus("4,096文字以内の識別子を1つ選択してください。"); return; }
        var center = selectionLength > 0 ? selectionStart : caret;
        if (operations.UsesSemanticNavigation(FilePath) && operations.CurrentFile is { } file)
        {
            if (operations.PrepareSemanticSearch(file, center, ViewEpoch, definitions) is not { } worker)
            {
                SetStatus("意味解析の設定で、対象のC#ソリューションまたはプロジェクトを指定してください。");
                return;
            }
            const string searching = "意味解析で検索しています…";
            SetStatus(searching);
            if (definitions)
                await StartDefinitionAsync(() => SearchPanel.StartSemanticAsync(true, worker));
            else
                await SearchPanel.StartSemanticAsync(false, worker);
            if (Status == searching) SetStatus("");
            return;
        }
        if (selectionLength > IdentifierExtractor.MaximumChars + 1)
        { SetStatus("4,096文字以内の識別子を1つ選択してください。"); return; }
        var origin = Math.Max(0, center - IdentifierExtractor.MaximumChars - 2);
        var end = (int)Math.Min(document.TextLength, (long)center + IdentifierExtractor.MaximumChars + 2);
        var context = document.GetText(origin, end - origin);
        var isCSharp = string.Equals(Path.GetExtension(FilePath), ".cs", StringComparison.OrdinalIgnoreCase);
        var cpp = definitions && CppDeclarationScanner.IncludesPath(FilePath);
        if (definitions && !isCSharp && !cpp) { SetStatus("簡易定義ジャンプはC#、C、C++のファイルに対応しています。"); return; }
        // Selection is authoritative even if the native caret is at its other end.
        var localCaret = selectionLength > 0 ? selectionStart - origin : caret - origin;
        if (!IdentifierExtractor.TryExtract(context, localCaret, selectionLength > 0 ? selectionStart - origin : localCaret,
                selectionLength, isCSharp, out var name, out var start, out var length)
            || (start == 0 && origin > 0) || (start + length == context.Length && end < document.TextLength))
        { SetStatus("有効な識別子を1つ選択するか、その語にカーソルを置いてください。Unicodeエスケープ表記は対象外です。"); return; }
        if (!definitions)
        {
            SetStatus("参照検索（名前一致）：宣言・コメント・文字列を含む全テキストが対象です。");
            await SearchPanel.StartReferencesAsync(name);
            return;
        }

        SetStatus("定義を検索しています（簡易・名前一致）。");
        await StartDefinitionAsync(() => SearchPanel.StartDefinitionsAsync(name, cpp));
    }

    private async Task StartDefinitionAsync(Func<Task> startSearch)
    {
        var interaction = _interactionEpoch;
        var view = ViewEpoch;
        var previous = SearchPanel.CurrentOperation;
        var work = startSearch();
        var operation = SearchPanel.CurrentOperation;
        await work;
        if (_stopping || interaction != _interactionEpoch || view != ViewEpoch
            || operation is null || ReferenceEquals(previous, operation)
            || !ReferenceEquals(SearchPanel.CurrentOperation, operation)) return;
        if (SearchPanel.SingleCompleteDefinition(operation) is { } row)
            await ActivateSearchResultCoreAsync(row, hidePanelOnSuccess: true);
        else
            SearchPanel.IsVisible = true;
    }

    public Task ActivateSearchResultAsync(SearchResultRow row) => TrackManualAsync(ActivateSearchResultCoreAsync(row));

    private async Task ActivateSearchResultCoreAsync(SearchResultRow row, bool hidePanelOnSuccess = false)
    {
        if (_stopping || _savingSettings || !SearchPanel.CanActivate(row) || SearchPanel.CurrentOperation is not { } source) return;
        var interaction = _interactionEpoch;
        var origin = CaptureJumpLocation();
        ClearSearchSelection?.Invoke();
        var navigator = operations.ResultNavigator;
        var request = navigator.Start(source, row.Hit);
        var moveStatus = "検索結果への移動を待機しています…";
        SetStatus(moveStatus);
        if (await request.Started is not null && !_stopping)
        {
            SyncTabs();
            DisplayActiveTab(updateStatus: !IsCurrentMove() && OwnsMoveStatus());
            if (IsCurrentMove() && OwnsMoveStatus())
            {
                moveStatus = "結果の更新情報を確認しています…";
                SetStatus(moveStatus);
            }
        }
        var result = await request.Completion;
        if (_stopping) return;
        SyncTabs();
        DisplayActiveTab(updateStatus: OwnsMoveStatus());
        if (!IsCurrentMove()) return;
        if (result.Status == OperationStatus.Failure)
        { SetStatus(FileErrorText(result.Error!.Code) + " 再読み込み・再検索してください。"); return; }
        if (result.Status != OperationStatus.Success || !navigator.CanAdopt(request, result.Value)) return;
        var move = result.Value.Move;
        if (hidePanelOnSuccess && move.Warning == DocumentMoveWarning.None && SearchPanel.IsVisible)
            SearchPanel.IsVisible = false;
        (_selectionRow, _selectionRequest, _selectionMove, _selectionInteractionEpoch) = (row, request, result.Value, interaction);
        ViewEpoch = checked(ViewEpoch + 1); // Supersede a deferred viewport restoration from document attachment.
        SetPosition(checked((int)move.Position.Line), checked((int)move.Position.Column));
        // A synchronous position notification can still describe the old/projected caret.
        // Restore from the confirmed move, never from notification-mutated view fields.
        RestoreCaret?.Invoke(checked((int)move.Position.Line), checked((int)move.Position.Column));
        SelectSearchRange?.Invoke(move.Offset, move.SelectionLength);
        if (move.Warning == DocumentMoveWarning.None && origin is { } from && CaptureJumpLocation() is { } to)
            RecordJump(from, to with { Position = move.Position });
        SetStatus(move.Warning switch
        {
            DocumentMoveWarning.Stale => "ファイルまたはタブ本文が検索時と異なります。再読み込み・再検索してください。選択は行いません。",
            DocumentMoveWarning.InvalidPosition => "結果位置が古い可能性があります。有効な位置へ移動しました。再検索してください。",
            DocumentMoveWarning.ContentMismatch => "一致内容が変わっています。選択せず移動しました。再検索してください。",
            DocumentMoveWarning.UnverifiedLength => "長い一致の全体を再確認できないため、選択せず移動しました。再検索してください。",
            _ => "検索結果へ移動しました。"
        });

        bool IsCurrentMove() => interaction == _interactionEpoch && ReferenceEquals(navigator.Current, request)
            && SearchPanel.CanActivate(row);
        bool OwnsMoveStatus() => (navigator.Current is null || ReferenceEquals(navigator.Current, request))
            && Status == moveStatus;
    }

    private JumpLocation? CaptureJumpLocation() => !_originalSourceOffsetsAvailable || _displayedTab is not { Status: TabStatus.Ready } tab
        ? null : new(tab.Path, tab.EncodingChoice, tab.Position, tab.HorizontalOffset, tab.VerticalOffset);

    private void RecordJump(JumpLocation from, JumpLocation to)
    {
        if (from.Path.FullPath == to.Path.FullPath && from.Position == to.Position) return;
        var jumps = _jumps ??= [];
        jumps.RemoveRange(_jumpIndex, jumps.Count - _jumpIndex);
        if (jumps.Count == JumpHistoryLimit) jumps.RemoveAt(0);
        jumps.Add((from, to));
        _jumpIndex = jumps.Count;
        Changed(nameof(CanJumpBack), nameof(CanJumpForward));
    }

    public Task JumpBackAsync() => TrackManualAsync(RestoreJumpAsync(back: true));
    public Task JumpForwardAsync() => TrackManualAsync(RestoreJumpAsync(back: false));

    private async Task RestoreJumpAsync(bool back)
    {
        if (_savingSettings || !(back ? CanJumpBack : CanJumpForward)) return;
        var cursor = _jumpIndex;
        var index = back ? cursor - 1 : cursor;
        var jump = _jumps![index];
        var destination = back ? jump.From : jump.To;
        var leaving = CaptureJumpLocation();
        var workspace = WorkspaceEpoch;
        var previous = _tabManager.ActiveTab;
        RecordUserInteraction();
        var interaction = _interactionEpoch;
        operations.ResultNavigator.Clear();
        _restoringJump = true;
        Changed(nameof(CanJumpBack), nameof(CanJumpForward));
        try
        {
            var existing = _tabManager.Tabs.FirstOrDefault(tab => tab.Path.FullPath == destination.Path.FullPath);
            var work = _tabManager.OpenAsync(destination.Path, destination.Encoding);
            var selection = _tabManager.SelectionEpoch;
            var target = _tabManager.ActiveTab;
            SyncTabs();
            DisplayActiveTab();
            var result = await work;
            if (_stopping) return;
            SyncTabs();
            if (_tabManager.SelectionEpoch != selection || !ReferenceEquals(target, _tabManager.ActiveTab)) return;
            DisplayActiveTab();
            if (WorkspaceEpoch != workspace || _interactionEpoch != interaction || _jumpIndex != cursor || _jumps is null || index >= _jumps.Count
                || _jumps[index] != jump) return;
            if (result.Status != OperationStatus.Success)
            {
                if (existing is null && target is { Status: TabStatus.Failed }) _tabManager.Close(target);
                if (previous is not null) _tabManager.Select(previous);
                SyncTabs();
                DisplayActiveTab();
                if (result.Status == OperationStatus.Failure)
                    SetStatus("履歴のファイルを開けません。 " + FileErrorText(result.Error!.Code));
                return;
            }

            ViewEpoch = checked(ViewEpoch + 1);
            var position = destination.Position;
            ClearSearchSelection?.Invoke();
            SetPosition((int)Math.Min(position.Line, int.MaxValue), (int)Math.Min(position.Column, int.MaxValue));
            RestoreCaret?.Invoke(_line, _column);
            SetViewport(destination.Horizontal, destination.Vertical);
            RestoreViewport?.Invoke(destination.Horizontal, destination.Vertical, ViewEpoch);
            var departure = back ? jump.To : jump.From;
            if (leaving is { } location && location.Path.FullPath == departure.Path.FullPath)
                _jumps[index] = back ? (jump.From, location) : (location, jump.To);
            _jumpIndex = back ? index : index + 1;
        }
        finally
        {
            _restoringJump = false;
            Changed(nameof(CanJumpBack), nameof(CanJumpForward));
        }
    }

    public async Task InitializeAsync(string[] paths, Task? recentStartupGate = null)
    {
        if (_initialized || _stopping) return;
        _initialized = true;
        if (recentHistory is not null)
            _recentWork = UpdateRecentAsync(recentStartupGate ?? Task.CompletedTask,
                () => recentHistory.LoadAsync(_lifetime.Token), reportFailure: false);
        if (paths.Length > 0) Submit(paths, OpenOrigin.CommandLine);
        var settingsTask = Task.Run(() => settingsStore.LoadAsync(_lifetime.Token), _lifetime.Token);
        _lateSettings = ObserveLateSettingsAsync(settingsTask);
        SettingsLoadResult settings;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2), timeProvider ?? TimeProvider.System))
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, deadline.Token))
        {
            try { settings = await settingsTask.WaitAsync(wait.Token); }
            catch (OperationCanceledException error) when (_lifetime.IsCancellationRequested
                && (error.CancellationToken == wait.Token || error.CancellationToken == _lifetime.Token)) { return; }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && error.CancellationToken == wait.Token)
            {
                // A worker TimeoutException is a fault, not the startup wait deadline.
                settings = settingsTask.IsCompleted ? await settingsTask
                    : new(SettingsSnapshot.Default, null, new(ErrorCode.IoFailure), false);
            }
        }
        if (_stopping) return;
        operations.Startup.CompleteSettings(settings);
        _settingsForSave = settings;
        if (!_stickyPreferenceChanged) SetStickyScroll(settings.Snapshot.StickyScrollEnabled);
        Changed(nameof(CanSaveSettings));
        await EnsurePumpAsync();
        // Keep the fallback notice visible after queued files finish loading instead
        // of immediately replacing it with their loading/completion status.
        if (!_stopping && settings.Warning is not null)
            SetStatus((Status + " 設定を読み込めないため、既定値を使用します。").TrimStart());
        if (_stickyPreferenceChanged) await PersistStickyScrollAsync();
    }

    private async Task ObserveLateSettingsAsync(Task<SettingsLoadResult> task)
    {
        try { _ = await task; }
        catch (OperationCanceledException error) when (_lifetime.IsCancellationRequested && error.CancellationToken == _lifetime.Token) { }
        catch (Exception) when (!_stopping)
        {
            try { SetStatus("予期しない設定読込エラーのため終了します。"); }
            finally { FatalFailure?.Invoke(); }
        }
    }

    public async Task OpenFilesAsync(IReadOnlyList<string> paths, OpenOrigin origin)
    {
        if (_stopping) return;
        Submit(paths, origin);
        await EnsurePumpAsync();
    }

    internal Task ReopenRecentAsync(RecentItem item) => !_stopping && IsWelcomeVisible && RecentItems.Contains(item)
        ? OpenFilesAsync([item.FullPath], OpenOrigin.Menu)
        : Task.CompletedTask;

    internal Task RemoveRecentAsync(string? itemPath)
    {
        if (_stopping || recentHistory is null) return Task.CompletedTask;
        var sources = Tabs.Select(tab => tab.FullPath).ToArray();
        var workspace = WorkspacePath;
        return _recentWork = UpdateRecentAsync(_recentWork,
            () => recentHistory.RemoveAsync(itemPath, sources, workspace, CancellationToken.None), reportFailure: true);
    }

    private void RecordRecent(List<RecentItem>? opened)
    {
        if (_stopping || recentHistory is null || opened is not { Count: > 0 }) return;
        var sources = Tabs.Select(tab => tab.FullPath).ToArray();
        var workspace = WorkspacePath;
        _recentWork = UpdateRecentAsync(_recentWork,
            () => recentHistory.RecordAsync(opened, sources, workspace, CancellationToken.None), reportFailure: false);
    }

    private async Task UpdateRecentAsync(Task previous, Func<Task<IReadOnlyList<RecentItem>>> update, bool reportFailure)
    {
        await previous;
        try
        {
            var items = await Task.Run(update);
            if (_stopping) return;
            RecentItems = items;
            Changed(nameof(RecentItems), nameof(HasRecentItems));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or System.Text.Json.JsonException or Win32Exception)
        {
            if (reportFailure && !_stopping) SetStatus("最近開いたものを更新できませんでした。履歴ファイルを確認してください。");
        }
    }

    public Task OpenDropAsync(IDataTransfer data, Action? accepted = null)
    {
        if (_stopping) return Task.CompletedTask;
        var extracted = FileDropInput.Read(data);
        if (extracted.Status == OperationStatus.Failure)
        {
            SetStatus(extracted.Error!.Code == ErrorCode.QueueFull ? "ドロップの項目数またはパス文字数が上限を超えています。"
                : "ローカルのファイルまたはフォルダをドロップしてください。");
            return Task.CompletedTask;
        }
        if (!Submit(extracted.Value, OpenOrigin.Drop)) return Task.CompletedTask;
        accepted?.Invoke();
        return EnsurePumpAsync();
    }

    private bool Submit(IReadOnlyList<string> paths, OpenOrigin origin)
    {
        var result = operations.Startup.Submit(paths, origin);
        if (result.Status == OperationStatus.Failure)
            SetStatus(result.Error!.Code == ErrorCode.QueueFull ? "開く要求が上限に達しました。" : "パスを確認してください。");
        return result.Status == OperationStatus.Success;
    }

    private Task EnsurePumpAsync()
    {
        if (_savingSettings) return Task.CompletedTask;
        if (_pumping) return _pump;
        _pump = PumpAsync();
        return _pump;
    }

    private async Task PumpAsync()
    {
        _pumping = true;
        try
        {
            Changed(nameof(CanSaveSettings));
            while (!_stopping && operations.Startup.TryTake(out var request))
            {
                try
                {
                    var folderSeen = false;
                    var skippedFolders = 0;
                    var failedPaths = 0;
                    string? firstFailure = null;
                    List<RecentItem>? opened = recentHistory is null ? null : [];
                    foreach (var path in request!.Paths)
                    {
                        if (_stopping) break;
                        var (isFolder, failure) = await OpenPathAsync(path, !folderSeen, opened);
                        if (isFolder)
                        {
                            if (folderSeen) skippedFolders++;
                            folderSeen = true;
                        }
                        if (failure is not null)
                        {
                            failedPaths++;
                            firstFailure ??= failure;
                        }
                    }
                    RecordRecent(opened);
                    if (!_stopping && (firstFailure is not null || skippedFolders > 0))
                    {
                        var notice = firstFailure ?? Status;
                        if (firstFailure is not null && request.Paths.Count > 1)
                            notice = $"開けなかった項目 {failedPaths:N0}件。 {firstFailure}";
                        if (skippedFolders > 0)
                            notice += $" 後続のフォルダ{skippedFolders}件は開いていません。";
                        if (notice != Status) SetStatus(notice.TrimStart());
                    }
                }
                finally { operations.Startup.CompleteOpen(request!.Id); }
            }
        }
        finally
        {
            _pumping = false;
            if (!_stopping) Changed(nameof(CanSaveSettings));
        }
    }

    private async Task<(bool IsFolder, string? Failure)> OpenPathAsync(string input, bool allowFolder, List<RecentItem>? opened)
    {
        SourcePath path;
        try { path = SourcePath.Create(input, _baseDirectory); }
        catch (ArgumentException) { return (false, "パスを確認してください。"); }
        OperationResult<SourceEntry> inspected;
        try { inspected = await operations.InspectPathAsync(path, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return (false, null); }
        if (_stopping || inspected.Status == OperationStatus.Cancelled) return (false, null);
        if (inspected.Status == OperationStatus.Failure)
            return (false, ErrorText(inspected.Error!.Code));
        if (inspected.Value.Kind == SourceEntryKind.Directory)
        {
            return (true, allowFolder ? await OpenFolderAsync(path, opened) : null);
        }
        return (false, await LoadAsync(path, opened));
    }

    private async Task<string?> OpenFolderAsync(SourcePath path, List<RecentItem>? opened = null)
    {
        var settings = operations.Startup.Settings?.Snapshot;
        if (settings is null || _stopping) return null;
        if (WorkspacePath != path.FullPath && _jumps is not null)
        {
            _jumps = null;
            _jumpIndex = 0;
            Changed(nameof(CanJumpBack), nameof(CanJumpForward));
        }
        SetStatus("フォルダを読み込み中… " + path.DisplayPath);
        var work = operations.OpenFolderAsync(path, settings);
        var workspace = operations.Workspace;
        FileTree.Clear();
        WorkspaceChanged();
        var result = await work;
        if (_stopping || !ReferenceEquals(operations.Workspace, workspace)) return null;
        await FileTree.BindAsync(workspace?.Tree);
        if (_stopping || !ReferenceEquals(operations.Workspace, workspace)) return null;
        WorkspaceChanged();
        if (result.Status == OperationStatus.Cancelled) return null;
        var failure = result.Status == OperationStatus.Failure
            ? "フォルダを読み込めません。 " + ErrorText(result.Error!.Code) : null;
        if (result.Status == OperationStatus.Success) AppendRecent(opened, path, isFolder: true);
        SetStatus(failure ?? "フォルダを開きました。");
        return failure;
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

    internal SettingsSnapshot? EditableSettings => CanSaveSettings ? _settingsForSave?.Snapshot : null;

    internal Task SaveExcludedNamesAsync(string[] names) => CanSaveSettings
        ? TrackManualAsync(_settingsWrite = SaveSettingsAsync(_settingsWrite, names, null)) : Task.CompletedTask;

    internal Task ToggleStickyScrollAsync()
    {
        if (_stopping) return Task.CompletedTask;
        _stickyPreferenceChanged = true;
        SetStickyScroll(!StickyScrollEnabled);
        return _settingsForSave is null ? Task.CompletedTask : PersistStickyScrollAsync();
    }

    private void SetStickyScroll(bool enabled)
    {
        if (StickyScrollEnabled == enabled) return;
        StickyScrollEnabled = enabled;
        Changed(nameof(StickyScrollEnabled));
    }

    internal Task PendingStickySettings => _stickySettingsWrite;

    private Task PersistStickyScrollAsync() => _stickySettingsWrite.IsCompleted
        ? _stickySettingsWrite = SaveLatestStickyScrollAsync() : _stickySettingsWrite;

    private async Task SaveLatestStickyScrollAsync()
    {
        while (!_stopping)
        {
            var enabled = StickyScrollEnabled;
            await (_settingsWrite = SaveSettingsAsync(_settingsWrite, null, enabled));
            if (StickyScrollEnabled == enabled) return;
        }
    }

    private async Task SaveSettingsAsync(Task previous, string[]? names, bool? stickyScroll)
    {
        await previous;
        if (stickyScroll is not null)
        {
            while (!_stopping && (_pumping || !operations.PendingResultMoveWork.IsCompleted))
                await Task.WhenAll(_pump, operations.PendingResultMoveWork);
        }
        else if (!operations.PendingResultMoveWork.IsCompleted)
        {
            SetStatus("検索結果への移動が完了してから設定を保存してください。");
            return;
        }
        if (_stopping || _settingsForSave is not { } loaded) return;
        if (!loaded.CanSave)
        {
            if (stickyScroll.HasValue) SetStatus("Sticky Scrollの設定を保存できません。切替はこのセッションに適用されます。");
            return;
        }
        var snapshot = names is null
            ? new SettingsSnapshot(loaded.Snapshot.ExcludedDirectoryNames, stickyScroll!.Value)
            : new SettingsSnapshot(names, loaded.Snapshot.StickyScrollEnabled);
        if (stickyScroll == loaded.Snapshot.StickyScrollEnabled
            || names is not null && loaded.Snapshot.ExcludedDirectoryNames.SequenceEqual(names, StringComparer.Ordinal)) return;
        _savingSettings = true;
        try
        {
            Changed(nameof(CanSaveSettings));
            var openSources = _tabManager.Tabs.Select(tab => tab.Path.FullPath).ToArray();
            var saved = await Task.Run(() => settingsStore.SaveAsync(loaded, snapshot, openSources, _lifetime.Token));
            if (_stopping) return;
            if (saved.Status == OperationStatus.Success)
            {
                _settingsForSave = saved.Value;
                if (names is not null) SetStatus("除外フォルダの設定を保存しました。再起動後に適用されます。");
            }
            else if (stickyScroll.HasValue)
                SetStatus("Sticky Scrollの設定を保存できません。切替はこのセッションに適用されます。");
            else if (saved.Error?.Code == ErrorCode.ChangedDuringRead)
                SetStatus("設定が外部で変更されたか、保存先を確認できません。再起動してからお試しください。");
            else
                SetStatus("除外フォルダの設定を保存できません。入力内容と保存先を確認してください。");
        }
        catch (OperationCanceledException error) when (_lifetime.IsCancellationRequested &&
            error.CancellationToken == _lifetime.Token) { }
        finally
        {
            _savingSettings = false;
            if (!_stopping)
            {
                Changed(nameof(CanSaveSettings));
                await EnsurePumpAsync();
            }
        }
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
        Changed(nameof(HasWorkspace), nameof(IsWelcomeVisible), nameof(CanSearchWorkspace), nameof(WorkspacePath), nameof(IsTreeVisible),
            nameof(DefinitionMenuLabel), nameof(ReferencesMenuLabel), nameof(Breadcrumb), nameof(BreadcrumbPath));
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

    private async Task<string?> LoadAsync(SourcePath path, List<RecentItem>? opened)
    {
        if (_stopping) return null;
        var work = _tabManager.OpenAsync(path);
        var target = _tabManager.ActiveTab;
        SyncTabs();
        DisplayActiveTab();
        var result = await work;
        if (_stopping) return null;
        SyncTabs();
        if (ReferenceEquals(_tabManager.ActiveTab, target)) DisplayActiveTab();
        if (result.Status != OperationStatus.Failure)
        {
            if (result.Status == OperationStatus.Success) AppendRecent(opened, path, isFolder: false);
            return null;
        }
        return result.Error!.Code == ErrorCode.QueueFull ? "読込待ちが上限に達しました。" : FileErrorText(result.Error.Code);
    }

    private void AppendRecent(List<RecentItem>? opened, SourcePath path, bool isFolder) =>
        opened?.Add(new(Path.TrimEndingDirectorySeparator(path.FullPath), isFolder,
            (timeProvider ?? TimeProvider.System).GetUtcNow()));

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
            Changed(nameof(SelectedTab), nameof(HasTabs), nameof(IsWelcomeVisible), nameof(CanReload), nameof(CanReopenEncoding));
        }
        finally { _syncingTabs = false; }
    }

    private void DisplayActiveTab(bool updateStatus = true)
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
                if (file is not null)
                {
                    var source = _document;
                    _document = GuiPerformanceProbe.SelectDisplayDocument(file.Document, source!, position);
                    _originalSourceOffsetsAvailable = ReferenceEquals(source, _document);
                }
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
            if (updateStatus)
                SetStatus(tab is null ? "ファイルを開いて、読み始めましょう。" : tab.Status switch
                {
                    TabStatus.Loading => "読み込み中… " + tab.Path.DisplayPath,
                    TabStatus.Reloading => "再読み込み中… " + tab.Path.DisplayPath,
                    _ when tab.Error is not null => FileErrorText(tab.Error.Code),
                    _ when tab.WasCancelled => "読込を取り消しました。",
                    _ => ""
                });
            if (changedDocument) Changed(nameof(Document), nameof(HasDocument));
            Changed(nameof(FilePath), nameof(WindowTitle), nameof(Breadcrumb), nameof(BreadcrumbPath), nameof(EncodingName), nameof(EncodingLabel), nameof(CanReopenEncoding),
                nameof(DefinitionMenuLabel), nameof(ReferencesMenuLabel));
        }
        finally { _attaching = false; }
        var canSearchOriginal = _originalSourceOffsetsAvailable;
#if CODEVIEW_PERFORMANCE_PROBE
        canSearchOriginal |= GuiPerformanceProbe.HasContextualRows;
#endif
        InFileSearch.Attach(tab, _tabManager.Tabs, canSearchOriginal);
        if (changedDocument)
        {
            RefreshSyntax(file);
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

    internal DocumentSelectionCopy? BeginCopy(int offset, int length)
    {
        if (_stopping || !_originalSourceOffsetsAvailable || length == 0 || _displayedHandle is not { } document)
            return null;
        if (offset < 0 || length < 0 || (long)offset + length > document.Length) return null;
        return new DocumentSelectionCopy(document.AcquireReadView(), offset, length);
    }

    public void CloseFile()
    {
        if (_selectedTab is { } tab) CloseTab(tab);
    }

    private void RefreshSyntax(LoadedFile? file)
    {
        CancelSyntax();
        SetSyntax(null, "");
        var canColorize = _originalSourceOffsetsAvailable;
#if CODEVIEW_PERFORMANCE_PROBE
        canColorize |= GuiPerformanceProbe.HasContextualRows;
#endif
        if (_stopping || !canColorize || file is null
            || SyntaxChanged is null || BundledGrammars.ScopeForPath(file.Path.FullPath) is not { } scope) return;
        var request = new CancellationTokenSource();
        _syntaxRequest = request;
        _ = ObserveSyntaxAsync(file.Document, scope, request);
    }

    private async Task ObserveSyntaxAsync(DocumentHandle document, string scope, CancellationTokenSource request)
    {
        try { await TrackManualAsync(HighlightAsync(document, scope, request)); }
        catch (Exception error)
        {
            if (_stopping)
            {
                RecordSyntaxFailure(error);
                return;
            }

            try { SetStatus("予期しない構文色分けエラーのため終了します。"); }
            catch (Exception notificationError) { error = new AggregateException(error, notificationError); }
            RecordSyntaxFailure(error);
            FatalFailure?.Invoke();
        }
    }

    private void RecordSyntaxFailure(Exception error)
    {
        _syntaxFailure = Task.FromException(error);
        _ = _syntaxFailure.Exception;
    }

    private async Task HighlightAsync(DocumentHandle document, string scope, CancellationTokenSource request)
    {
        try
        {
            // Let the ordinary text attach and render before loading any grammar.
            await Task.Delay(TimeSpan.FromMilliseconds(150), request.Token);
            var result = await operations.HighlightAsync(document, scope, request.Token);
            if (ReferenceEquals(_syntaxRequest, request) && !request.IsCancellationRequested)
                SetSyntax(result.Highlighting, result.Status);
        }
        catch (OperationCanceledException error) when (request.IsCancellationRequested && error.CancellationToken == request.Token) { }
        finally
        {
            if (ReferenceEquals(_syntaxRequest, request)) _syntaxRequest = null;
            request.Dispose();
        }
    }

    private void CancelSyntax()
    {
        var request = _syntaxRequest;
        _syntaxRequest = null;
        request?.Cancel();
    }

    private void SetSyntax(SyntaxHighlighting? highlighting, string status)
    {
        if (SyntaxStatus != status)
        {
            SyntaxStatus = status;
            Changed(nameof(SyntaxStatus));
        }

        SyntaxChanged?.Invoke(highlighting);
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _selectionRow = null;
        _selectionRequest = null;
        _selectionMove = null;
        List<Exception>? failures = null;
        Attempt(CancelSyntax);
        Attempt(InFileSearch.Stop);
        Attempt(SearchPanel.Stop);
        Attempt(_lifetime.Cancel);
        Attempt(operations.Dispose);
        Attempt(SyncTabs);
        Attempt(() => DisplayActiveTab());
        Attempt(FileTree.Clear);
        Attempt(WorkspaceChanged);
        if (failures?.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null) throw new AggregateException(failures);

        void Attempt(Action stop)
        {
            try { stop(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
    }

    public void SetStatus(string status) { _status = status; Changed(nameof(Status)); }
    private void Changed(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
