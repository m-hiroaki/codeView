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
    private bool _pumping;
    private bool _stopping;
    private bool _initialized;
    private Task _pump = Task.CompletedTask;
    private Task _lateSettings = Task.CompletedTask;

    public TextDocument? Document => _document;
    public bool HasDocument => _document is not null;
    public string FilePath => _filePath;
    public string Status => _status;
    public string EncodingName => _encodingName;
    public string Position => _position;
    public Task PendingWork => Task.WhenAll(_pump, _lateSettings);
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? FatalFailure;

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
        if (operations.CurrentFile is not null) await LoadAsync(operations.CurrentFile.Path.FullPath);
    }

    private async Task LoadAsync(string input)
    {
        SourcePath path;
        try { path = SourcePath.Create(input, _baseDirectory); }
        catch (ArgumentException) { SetStatus("パスを確認してください。"); return; }
        SetStatus("読み込み中… " + path.DisplayPath);
        var result = await operations.OpenFileAsync(path, EncodingChoice.Auto);
        if (_stopping || result.Status == OperationStatus.Cancelled) return;
        if (result.Status == OperationStatus.Failure)
        {
            SetStatus(result.Error!.Code switch
            {
                ErrorCode.Binary => "バイナリのため表示できません。",
                ErrorCode.DecodeFailed => "文字コードを判定できません。",
                ErrorCode.NotFound => "ファイルが見つかりません。",
                ErrorCode.AccessDenied => "ファイルを読む権限がありません。",
                ErrorCode.NotRegularFile => "通常のファイルを指定してください。",
                ErrorCode.BrokenLink => "リンク先のファイルが見つかりません。",
                ErrorCode.ChangedDuringRead => "読込中にファイルが更新されました。再読み込みしてください。",
                _ => "ファイルを読み込めませんでした。"
            });
            return;
        }
        _document = result.Value.Document.ClaimForDisplay();
        _filePath = result.Value.Path.FullPath;
        _encodingName = result.Value.EncodingName.ToUpperInvariant();
        _position = "行 1, 列 1";
        SetStatus("読み取り専用");
        Changed(nameof(Document), nameof(HasDocument), nameof(FilePath), nameof(EncodingName), nameof(Position));
    }

    public void SetPosition(int line, int column)
    {
        _position = HasDocument ? $"行 {line}, 列 {column}" : "";
        Changed(nameof(Position));
    }

    public void CloseFile()
    {
        _document = null;
        operations.CloseFile();
        _filePath = _encodingName = _position = "";
        SetStatus("ファイルを開いて、読み始めましょう。");
        Changed(nameof(Document), nameof(HasDocument), nameof(FilePath), nameof(EncodingName), nameof(Position));
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
