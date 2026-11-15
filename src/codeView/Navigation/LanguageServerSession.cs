using System.Diagnostics;
using System.Text.Json;

namespace CodeView.Navigation;

internal enum SemanticLanguage { CSharp, Cpp }

/// <summary>Owns one local language server for one workspace and configuration.</summary>
internal sealed class LanguageServerSession(
    string executable, IReadOnlyList<string> arguments, string workspace,
    Action<string, JsonElement> notify, Action<Utf8JsonWriter>? initializationOptions = null,
    Func<LspTransport, CancellationToken, Task>? afterInitialized = null,
    TimeSpan? requestTimeout = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _closing = new();
    private Process? _process;
    private LspTransport? _transport;
    private Task? _drain;
    private int _version;
    private string? _openDocument;
    private long _openRevision;

    internal string PositionEncoding { get; private set; } = "utf-16";

    internal async Task<T> UseAsync<T>(Func<LspTransport, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _closing.Token);
        deadline.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_transport is null || _transport.IsClosed || _process?.HasExited != false)
                {
                    await StopAsync().ConfigureAwait(false);
                    await StartAsync(token).ConfigureAwait(false);
                }
                return await action(_transport!, token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_closing.IsCancellationRequested)
        {
            throw new TimeoutException("言語サーバーの応答がタイムアウトしました。");
        }
    }

    // Called only from inside UseAsync. Reopening supports both incremental and full-sync servers.
    internal async Task SynchronizeAsync(LspTransport transport, string uri, string languageId, long revision,
        string text, CancellationToken ct)
    {
        if (_openDocument == uri && _openRevision == revision) return;
        if (_openDocument is { } previous)
        {
            await transport.SendNotificationAsync("textDocument/didClose", writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("textDocument");
                writer.WriteString("uri", previous);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }, ct).ConfigureAwait(false);
            _openDocument = null;
        }
        await transport.SendNotificationAsync("textDocument/didOpen", writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("textDocument");
            writer.WriteString("uri", uri);
            writer.WriteString("languageId", languageId);
            writer.WriteNumber("version", checked(++_version));
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, ct).ConfigureAwait(false);
        _openDocument = uri;
        _openRevision = revision;
    }

    private async Task StartAsync(CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new IOException("言語サーバーの実行ファイルを指定してください。");
        var options = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) options.ArgumentList.Add(argument);
        _process = Process.Start(options) ?? throw new IOException("言語サーバーを起動できませんでした。");
        _drain = _process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            _transport = new(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream, notify, Respond);
            var result = await _transport.RequestAsync("initialize", WriteInitialization, ct).ConfigureAwait(false);
            if (!result.TryGetProperty("capabilities", out var capabilities))
                throw new InvalidDataException("言語サーバーの機能情報がありません。");
            PositionEncoding = capabilities.TryGetProperty("positionEncoding", out var encoding) ? encoding.GetString() ?? "utf-16" : "utf-16";
            if (PositionEncoding is not ("utf-8" or "utf-16" or "utf-32"))
                throw new InvalidDataException("言語サーバーの位置エンコーディングに対応していません。");
            await _transport.SendNotificationAsync("initialized", WriteEmptyObject, ct).ConfigureAwait(false);
            if (afterInitialized is not null) await afterInitialized(_transport, ct).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void WriteInitialization(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("processId", Environment.ProcessId);
        writer.WriteString("rootUri", new Uri(workspace).AbsoluteUri);
        writer.WritePropertyName("workspaceFolders");
        WriteFolders(writer);
        writer.WriteStartObject("capabilities");
        writer.WriteStartObject("general");
        writer.WriteStartArray("positionEncodings");
        writer.WriteStringValue("utf-16");
        writer.WriteStringValue("utf-8");
        writer.WriteStringValue("utf-32");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteStartObject("window");
        writer.WriteBoolean("workDoneProgress", true);
        writer.WriteEndObject();
        writer.WriteStartObject("workspace");
        writer.WriteBoolean("workspaceFolders", true);
        writer.WriteBoolean("configuration", true);
        writer.WriteBoolean("applyEdit", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
        if (initializationOptions is not null)
        {
            writer.WritePropertyName("initializationOptions");
            initializationOptions(writer);
        }
        writer.WriteEndObject();
    }

    private void WriteFolders(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("uri", new Uri(workspace).AbsoluteUri);
        writer.WriteString("name", Path.GetFileName(workspace));
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    private Action<Utf8JsonWriter>? Respond(string method, JsonElement parameters) => method switch
    {
        "window/workDoneProgress/create" or "client/registerCapability" or "client/unregisterCapability"
            => static writer => writer.WriteNullValue(),
        "workspace/workspaceFolders" => WriteFolders,
        "workspace/configuration" => writer =>
        {
            writer.WriteStartArray();
            if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array)
                foreach (var _ in items.EnumerateArray()) writer.WriteNullValue();
            writer.WriteEndArray();
        },
        "workspace/applyEdit" => static writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("applied", false);
            writer.WriteEndObject();
        },
        _ => null
    };

    private static void WriteEmptyObject(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    }

    private async Task StopAsync()
    {
        var process = _process;
        if (process is null) return;
        var transport = _transport;
        var drain = _drain;
        _process = null;
        _transport = null;
        _drain = null;
        _openDocument = null;
        try
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (transport is { IsClosed: false } && !process.HasExited)
            {
                try
                {
                    await transport.RequestAsync("shutdown", null, grace.Token).ConfigureAwait(false);
                    await transport.SendNotificationAsync("exit", null, grace.Token).ConfigureAwait(false);
                    await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
            }
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
                if (drain is not null)
                {
                    // A process descendant can inherit stderr even after the server exits.
                    process.StandardError.Dispose();
                    try { await drain.ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or ObjectDisposedException) { }
                }
            }
            finally { process.Dispose(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
