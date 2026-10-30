using System.ComponentModel;
using AvaloniaEdit.Document;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Settings;

namespace CodeView.Presentation;

internal sealed class MainWindowViewModel(AppOperations operations, SettingsStore settingsStore, Func<Task>? treeYield = null) : INotifyPropertyChanged
{
    private readonly string _baseDirectory = Environment.CurrentDirectory;
    private readonly CancellationTokenSource _lifetime = new();
    private TextDocument? _document;
    private string _status = "ファイルを開いて、読み始めましょう。";
    private string _filePath = "";
    private string _encodingName = "";
    private string _position = "";
    private SourcePath? _failedPath;
    private EncodingChoice _encodingChoice = EncodingChoice.Auto;
    private int _line = 1;
    private int _column = 1;
    private bool _pumping;
    private bool _stopping;
    private bool _initialized;
    private Task _pump = Task.CompletedTask;
    private Task _lateSettings = Task.CompletedTask;
    private readonly HashSet<Task> _manualWork = [];
    private bool _isTreeVisible = true;
    public FileTreeViewModel FileTree { get; } = new(treeYield);
    public bool IsTreeVisible { get => _isTreeVisible && HasWorkspace; set { _isTreeVisible = value; Changed(nameof(IsTreeVisible)); } }

    public TextDocument? Document => _document;
    public bool HasDocument => _document is not null;
    public bool CanReopenEncoding => HasDocument || _failedPath is not null;
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
    public Task PendingWork => Task.WhenAll(new[] { _pump, _lateSettings, operations.PendingTreeWork }.Concat(_manualWork));
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? FatalFailure;
    public event Action<int, int>? RestoreCaret;

    public async Task InitializeAsync(string[] paths)
    {
        if (_initialized || _stopping) return;
        _initialized = true;
        if (paths.Length > 0) Submit(paths, OpenOrigin.CommandLine);
        var settingsTask = Task.Run(() => settingsStore.LoadAsync(_lifetime.Token), _lifetime.Token);
        _lateSettings = ObserveLateSettingsAsync(settingsTask);
        SettingsLoadResult settings;
        try { settings = await settingsTask.WaitAsync(TimeSpan.FromSeconds(2), _lifetime.Token); }
        catch (TimeoutException)
        {
            settings = new(SettingsSnapshot.Default, null, new(ErrorCode.IoFailure), false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        if (_stopping) return;
        operations.Startup.CompleteSettings(settings);
        if (settings.Warning is not null) SetStatus("設定を読み込めないため、既定値を使用します。");
        await EnsurePumpAsync();
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

    private void WorkspaceChanged() => Changed(nameof(HasWorkspace), nameof(CanSearchWorkspace), nameof(WorkspacePath), nameof(WorkspaceSummary), nameof(IsTreeVisible));

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
        if (operations.CurrentFile is not null)
            await TrackManualAsync(LoadAsync(operations.CurrentFile.Path.FullPath, _encodingChoice, true));
    }

    public async Task ReopenEncodingAsync(EncodingChoice choice)
    {
        if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
        var path = operations.CurrentFile?.Path ?? _failedPath;
        if (path is not null) await TrackManualAsync(LoadAsync(path.FullPath, choice, true));
    }

    private async Task LoadAsync(string input, EncodingChoice choice = EncodingChoice.Auto, bool preservePosition = false)
    {
        if (_stopping) return;
        SourcePath path;
        try { path = SourcePath.Create(input, _baseDirectory); }
        catch (ArgumentException) { SetStatus("パスを確認してください。"); return; }
        var line = preservePosition ? _line : 1;
        var column = preservePosition ? _column : 1;
        SetStatus("読み込み中… " + path.DisplayPath);
        var result = await operations.OpenFileAsync(path, choice);
        if (_stopping || result.Status == OperationStatus.Cancelled) return;
        if (result.Status == OperationStatus.Failure)
        {
            _failedPath = path;
            Changed(nameof(CanReopenEncoding));
            SetStatus(result.Error!.Code switch
            {
                ErrorCode.Binary => "バイナリのため表示できません。テキストの場合は文字コードを指定して再試行してください。",
                ErrorCode.DecodeFailed => "指定した文字コードで読み取れないか、自動判定できません。文字コードを選び直してください。",
                ErrorCode.NotFound => "ファイルが見つかりません。",
                ErrorCode.AccessDenied => "ファイルを読む権限がありません。",
                ErrorCode.NotRegularFile => "通常のファイルを指定してください。",
                ErrorCode.BrokenLink => "リンク先のファイルが見つかりません。",
                ErrorCode.ChangedDuringRead => "読込中にファイルが更新されました。再読み込みしてください。",
                _ => "ファイルを読み込めませんでした。"
            });
            return;
        }
#if CODEVIEW_PERFORMANCE_PROBE
        GuiPerformanceProbe.BeginDocumentAttachment();
#endif
        _document = result.Value.Document.ClaimForDisplay();
#if CODEVIEW_PERFORMANCE_PROBE
        _document = GuiPerformanceProbe.SelectDisplayDocument(result.Value.Document, _document);
#endif
        _failedPath = null;
        _encodingChoice = choice;
        _filePath = result.Value.Path.FullPath;
        _encodingName = result.Value.EncodingName.ToLowerInvariant() switch
        {
            "utf-16" => "UTF-16 LE",
            "utf-16be" => "UTF-16 BE",
            "utf-32" => "UTF-32 LE",
            "utf-32be" => "UTF-32 BE",
            "shift_jis" => "Shift_JIS（CP932）",
            _ => result.Value.EncodingName.ToUpperInvariant()
        };
        SetStatus("読み取り専用");
        Changed(nameof(Document), nameof(HasDocument), nameof(CanReopenEncoding), nameof(FilePath), nameof(EncodingName), nameof(EncodingLabel));
        // Document binding can reset the editor's caret synchronously. Restore afterwards.
        SetPosition(line, column);
        RestoreCaret?.Invoke(_line, _column);
#if CODEVIEW_PERFORMANCE_PROBE
        GuiPerformanceProbe.EndDocumentAttachment();
#endif
    }

    public void SetPosition(int line, int column)
    {
        _line = _document is null ? 1 : Math.Clamp(line, 1, _document.LineCount);
        _column = _document is null ? 1 : Math.Clamp(column, 1, _document.GetLineByNumber(_line).Length + 1);
        _position = HasDocument ? $"行 {_line}, 列 {_column}" : "";
        Changed(nameof(Position));
    }

    public void CloseFile()
    {
        _document = null;
        _failedPath = null;
        _encodingChoice = EncodingChoice.Auto;
        _line = _column = 1;
        operations.CloseFile();
        _filePath = _encodingName = _position = "";
        SetStatus("ファイルを開いて、読み始めましょう。");
        Changed(nameof(Document), nameof(HasDocument), nameof(CanReopenEncoding), nameof(FilePath), nameof(EncodingName), nameof(EncodingLabel), nameof(Position));
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _lifetime.Cancel();
        CloseFile();
        operations.Dispose();
        FileTree.Clear();
        WorkspaceChanged();
    }

    public void SetStatus(string status) { _status = status; Changed(nameof(Status)); }
    private void Changed(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
