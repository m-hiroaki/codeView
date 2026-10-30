using System.ComponentModel;
using AvaloniaEdit.Document;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Settings;

namespace CodeView.Presentation;

internal sealed class MainWindowViewModel(AppOperations operations, SettingsStore settingsStore) : INotifyPropertyChanged
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

    public TextDocument? Document => _document;
    public bool HasDocument => _document is not null;
    public bool CanReopenEncoding => HasDocument || _failedPath is not null;
    public string FilePath => _filePath;
    public string Status => _status;
    public string EncodingName => _encodingName;
    public string EncodingLabel => HasDocument ? _encodingName : "文字コードを指定…";
    public string Position => _position;
    public Task PendingWork => Task.WhenAll(_pump, _lateSettings);
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
                    foreach (var path in request!.Paths)
                    {
                        if (_stopping) break;
                        await LoadAsync(path);
                    }
                }
                finally { operations.Startup.CompleteOpen(request!.Id); }
            }
        }
        finally { _pumping = false; }
    }

    public async Task ReloadAsync()
    {
        if (operations.CurrentFile is not null)
            await LoadAsync(operations.CurrentFile.Path.FullPath, _encodingChoice, true);
    }

    public async Task ReopenEncodingAsync(EncodingChoice choice)
    {
        if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
        var path = operations.CurrentFile?.Path ?? _failedPath;
        if (path is not null) await LoadAsync(path.FullPath, choice, true);
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
    }

    public void SetStatus(string status) { _status = status; Changed(nameof(Status)); }
    private void Changed(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
