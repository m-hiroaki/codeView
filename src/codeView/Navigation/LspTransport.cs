using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeView.Navigation;

internal sealed class LspTransport : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 8192;
    private const int MaximumMessageBytes = 32 * 1024 * 1024;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Action<string, JsonElement> _notify;
    private readonly Func<string, JsonElement, Action<Utf8JsonWriter>?> _serverRequest;
    private readonly SemaphoreSlim _write = new(1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private long _nextId;

    internal LspTransport(Stream input, Stream output, Action<string, JsonElement> notify,
        Func<string, JsonElement, Action<Utf8JsonWriter>?> serverRequest)
    {
        _input = new BufferedStream(input, MaximumHeaderBytes);
        _output = output;
        _notify = notify;
        _serverRequest = serverRequest;
        _reader = ReadMessagesAsync();
    }

    internal bool IsClosed => _stop.IsCancellationRequested;

    internal async Task<JsonElement> RequestAsync(string method, Action<Utf8JsonWriter>? parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, response)) throw new InvalidOperationException();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            await SendAsync(method, id, parameters, linked.Token).ConfigureAwait(false);
            try
            {
                return await response.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _ = CancelRequestAsync(id);
                throw;
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    internal async Task SendNotificationAsync(string method, Action<Utf8JsonWriter>? parameters, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await SendAsync(method, null, parameters, linked.Token).ConfigureAwait(false);
    }

    private async Task CancelRequestAsync(long id)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await SendAsync("$/cancelRequest", null, writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", id);
                writer.WriteEndObject();
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private Task SendAsync(string method, long? id, Action<Utf8JsonWriter>? parameters, CancellationToken ct) =>
        WriteFrameAsync(writer =>
        {
            writer.WriteString("method", method);
            if (id.HasValue) writer.WriteNumber("id", id.Value);
            if (parameters is null) return;
            writer.WritePropertyName("params");
            parameters(writer);
        }, ct);

    private async Task WriteFrameAsync(Action<Utf8JsonWriter> fields, CancellationToken ct)
    {
        var body = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            fields(writer);
            writer.WriteEndObject();
        }
        if (body.WrittenCount > MaximumMessageBytes) throw new InvalidDataException("LSPメッセージが上限を超えました。");
        var header = Encoding.ASCII.GetBytes(FormattableString.Invariant($"Content-Length: {body.WrittenCount}\r\n\r\n"));
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            await _output.WriteAsync(header, ct).ConfigureAwait(false);
            await _output.WriteAsync(body.WrittenMemory, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A broken or partially written frame cannot be followed by another message.
            _stop.Cancel();
            throw;
        }
        finally { _write.Release(); }
    }

    private async Task ReadMessagesAsync()
    {
        Exception closed = new IOException("言語サーバーとの通信が終了しました。");
        var ct = _stop.Token;
        try
        {
            var header = new byte[MaximumHeaderBytes];
            while (true)
            {
                using var frame = await ReadFrameAsync(header, ct).ConfigureAwait(false);
                var message = frame.RootElement;
                if (message.ValueKind != JsonValueKind.Object) throw new InvalidDataException("無効なLSPメッセージです。");
                var hasId = message.TryGetProperty("id", out var id);
                if (message.TryGetProperty("method", out var methodValue) && methodValue.GetString() is { } method)
                {
                    var parameters = message.TryGetProperty("params", out var value) ? value : default;
                    if (hasId)
                        await AnswerServerRequestAsync(id, method, parameters, ct).ConfigureAwait(false);
                    else
                        _notify(method, parameters);
                    continue;
                }
                if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var requestId)
                    && _pending.TryRemove(requestId, out var response))
                {
                    if (message.TryGetProperty("result", out var result))
                        response.TrySetResult(result.Clone());
                    else
                    {
                        var details = message.TryGetProperty("error", out var error) ? error.GetRawText() : "";
                        response.TrySetException(new LspRequestException(details));
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException
            or InvalidOperationException or OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) closed = new IOException("言語サーバーの応答を読み取れませんでした。", error);
        }
        finally
        {
            _stop.Cancel();
            foreach (var (id, response) in _pending)
                if (_pending.TryRemove(id, out _)) response.TrySetException(closed);
        }
    }

    private Task AnswerServerRequestAsync(JsonElement id, string method, JsonElement parameters, CancellationToken ct)
    {
        var result = _serverRequest(method, parameters);
        return WriteFrameAsync(writer =>
        {
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            if (result is not null)
            {
                writer.WritePropertyName("result");
                result(writer);
            }
            else
            {
                writer.WriteStartObject("error");
                writer.WriteNumber("code", -32601);
                writer.WriteString("message", "Method not supported");
                writer.WriteEndObject();
            }
        }, ct);
    }

    private async Task<JsonDocument> ReadFrameAsync(byte[] header, CancellationToken ct)
    {
        var length = 0;
        do
        {
            if (length == header.Length) throw new InvalidDataException("LSPヘッダーが上限を超えました。");
            await _input.ReadExactlyAsync(header.AsMemory(length, 1), ct).ConfigureAwait(false);
            length++;
        }
        while (length < 4 || !header.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8));
        var contentLength = ParseLength(header.AsSpan(0, length));
        var payload = ArrayPool<byte>.Shared.Rent(contentLength);
        try
        {
            await _input.ReadExactlyAsync(payload.AsMemory(0, contentLength), ct).ConfigureAwait(false);
            // The document must own its input before the rented buffer is returned.
            var reader = new Utf8JsonReader(payload.AsSpan(0, contentLength));
            var document = JsonDocument.ParseValue(ref reader);
            if (!reader.Read()) return document;
            document.Dispose();
            throw new InvalidDataException("LSP応答に余分なJSONが含まれます。");
        }
        finally { ArrayPool<byte>.Shared.Return(payload, clearArray: true); }
    }

    private static int ParseLength(ReadOnlySpan<byte> header)
    {
        var value = -1;
        foreach (var line in Encoding.ASCII.GetString(header).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator < 1) throw new InvalidDataException("無効なLSPヘッダーです。");
            if (!line.AsSpan(0, separator).Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (value != -1 || !int.TryParse(line.AsSpan(separator + 1).Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out value) || value <= 0 || value > MaximumMessageBytes)
                throw new InvalidDataException("無効なLSPメッセージ長です。");
        }
        return value > 0 ? value : throw new InvalidDataException("LSPメッセージ長がありません。");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _input.Dispose();
        _output.Dispose();
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}

internal sealed class LspRequestException(string details) : IOException(details);
