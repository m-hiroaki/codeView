using System.Text.Json;

namespace CodeView.Navigation;

internal sealed class ClangdNavigation(string root, string? database, string cacheRoot)
{
    private readonly ClangdCompilationDatabase _compilation = new(root, database, cacheRoot);
    private List<string> _arguments = [];
    private int _indexState;
    private Diagnostics? _lastDiagnostics;

    private sealed record Diagnostics(string Uri, int Version, bool HasErrors);

    public LanguageServerSession CreateSession(string executable)
    {
        _arguments = ["--background-index", "--enable-config=false", "--log=error"];
        if (BundledLanguageServers.ClangdResourceDirectory(executable) is { } resources)
            _arguments.Add("--resource-dir=" + resources);
        _arguments.Add(""); // The active compilation database is selected on first search.
        return new(executable, _arguments, LspFilePath.Resolve(root), Observe, WriteInitialization);
    }

    public async Task<SemanticReply> SearchAsync(LanguageServerSession session, SemanticOperation operation, string uri,
        string languageId, long revision, string text, Func<string, LspPosition> position, CancellationToken ct)
    {
        var source = LspFilePath.Resolve(new Uri(uri).LocalPath);
        uri = new Uri(source).AbsoluteUri;
        var configurationChanged = await _compilation.RefreshAsync(ct).ConfigureAwait(false);
        _arguments[^1] = "--compile-commands-dir=" + _compilation.CacheDirectory;
        if (configurationChanged)
            await session.RestartAsync(ct).ConfigureAwait(false);
        return await session.UseAsync(async (transport, token) =>
        {
            await session.SynchronizeAsync(transport, uri, languageId, revision, text, token).ConfigureAwait(false);
            var indexWasReady = Volatile.Read(ref _indexState) == 2;
            var documentWasReady = HasCurrentDiagnostics(uri, session.DocumentVersion);
            var result = await SemanticServerQuery.RequestAsync(transport, operation, uri,
                position(session.PositionEncoding), null, token).ConfigureAwait(false);
            var reason = !_compilation.HasCommand(source) ? "このファイルのコンパイル条件を確認できませんでした。"
                : !_compilation.IsComplete ? "コンパイルデータベースに未確認の構成があります。"
                : !indexWasReady || Volatile.Read(ref _indexState) != 2 ? "clangdの索引は準備中、または状態が不明です。"
                : !documentWasReady || !HasCurrentDiagnostics(uri, session.DocumentVersion) ? "現在の本文の解析状態を確認できませんでした。"
                : null;
            return new SemanticReply(result, session.PositionEncoding, _compilation.Description, reason is not null, reason);
        }, ct).ConfigureAwait(false);
    }

    private bool HasCurrentDiagnostics(string uri, int version) =>
        Volatile.Read(ref _lastDiagnostics) is { HasErrors: false } diagnostics
        && diagnostics.Uri == uri && diagnostics.Version == version;

    private void WriteInitialization(Utf8JsonWriter writer)
    {
        Volatile.Write(ref _indexState, 0);
        Volatile.Write(ref _lastDiagnostics, null);
        writer.WriteStartObject();
        writer.WriteEndObject();
    }

    private void Observe(string method, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return;
        if (method == "$/progress" && parameters.TryGetProperty("token", out var token)
            && token.ValueKind == JsonValueKind.String && token.GetString() == "backgroundIndexProgress"
            && parameters.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String)
        {
            Volatile.Write(ref _indexState, kind.GetString() == "end" ? 2 : 1);
            return;
        }
        if (method == "textDocument/publishDiagnostics" && parameters.TryGetProperty("uri", out var uri)
            && uri.ValueKind == JsonValueKind.String && parameters.TryGetProperty("version", out var version)
            && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number)
            && parameters.TryGetProperty("diagnostics", out var diagnostics) && diagnostics.ValueKind == JsonValueKind.Array)
        {
            var errors = diagnostics.EnumerateArray().Any(diagnostic =>
                diagnostic.ValueKind == JsonValueKind.Object && diagnostic.TryGetProperty("severity", out var severity)
                && severity.ValueKind == JsonValueKind.Number && severity.TryGetInt32(out var level) && level == 1);
            Volatile.Write(ref _lastDiagnostics, new Diagnostics(uri.GetString()!, number, errors));
        }
    }
}
