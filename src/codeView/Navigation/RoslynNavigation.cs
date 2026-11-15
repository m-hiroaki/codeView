using System.Text.Json;

namespace CodeView.Navigation;

internal sealed class RoslynNavigation(string root, string project)
{
    private TaskCompletionSource _ready = NewReadiness();
    private int _loadIssue;
    private bool _projectScopePartial;

    internal static bool SupportsProject(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx" or ".csproj";

    internal LanguageServerSession CreateSession(string executable) =>
        new(executable, ["--stdio", "--telemetryLevel", "off"], root, Observe, afterInitialized: OpenProjectAsync);

    internal async Task<SemanticReply> SearchAsync(LanguageServerSession session, SemanticOperation operation, string uri,
        long revision, string text, Func<string, LspPosition> position, CancellationToken ct) =>
        await session.UseAsync(async (transport, token) =>
        {
            await session.SynchronizeAsync(transport, uri, "csharp", revision, text, token).ConfigureAwait(false);
            var loaded = await WaitForProjectAsync(token).ConfigureAwait(false);
            var (context, label, contextIsPartial) = loaded
                ? await GetProjectContextAsync(transport, uri, token).ConfigureAwait(false) : (null, null, true);
            JsonElement locations = default;
            try
            {
                locations = await SemanticServerQuery.RequestAsync(transport, operation, uri,
                    position(session.PositionEncoding), context, token).ConfigureAwait(false);
            }
            catch (LspRequestException) when (!loaded || contextIsPartial || _projectScopePartial || Volatile.Read(ref _loadIssue) != 0)
            { }
            var status = (!loaded, _projectScopePartial, Volatile.Read(ref _loadIssue) != 0, contextIsPartial) switch
            {
                (true, _, _, _) => "C#プロジェクトの読み込みが完了していません。",
                (_, true, _, _) => "C#プロジェクトの参照関係を確認できませんでした。",
                (_, _, true, _) => "C#プロジェクトの読み込みで問題が報告されました。",
                (_, _, _, true) => "C#ファイルの解析構成を一つに確定できませんでした。",
                _ => null
            };
            return new SemanticReply(locations, session.PositionEncoding, label ?? Path.GetFileName(project), status is not null, status);
        }, ct).ConfigureAwait(false);

    private async Task OpenProjectAsync(LspTransport transport, CancellationToken ct)
    {
        Volatile.Write(ref _ready, NewReadiness());
        Volatile.Write(ref _loadIssue, 0);
        _projectScopePartial = false;
        if (!SupportsProject(project) || !Path.IsPathFullyQualified(project) || !File.Exists(project))
            throw new IOException("C#のソリューションまたはプロジェクトを指定してください。");
        if (Path.GetExtension(project).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var graph = await RoslynProjectGraph.ResolveAsync(root, project, ct).ConfigureAwait(false);
            _projectScopePartial = !graph.Complete;
            await transport.SendNotificationAsync("project/open", writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("projects");
                foreach (var file in graph.Projects) writer.WriteStringValue(new Uri(file).AbsoluteUri);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }, ct).ConfigureAwait(false);
            return;
        }

        await transport.SendNotificationAsync("solution/open", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("solution", new Uri(project).AbsoluteUri);
            writer.WriteEndObject();
        }, ct).ConfigureAwait(false);
    }

    private void Observe(string method, JsonElement parameters)
    {
        if (method == "workspace/projectInitializationComplete") Volatile.Read(ref _ready).TrySetResult();
        if (method is "window/logMessage" or "window/showMessage" && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("type", out var severity) && severity.ValueKind == JsonValueKind.Number
            && (severity.GetInt32() == 1 || severity.GetInt32() == 2 && IsProjectWarning(parameters)))
            Volatile.Write(ref _loadIssue, 1);
    }

    private static bool IsProjectWarning(JsonElement parameters) =>
        parameters.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
        && message.GetString() is { } text
        && (text.Contains("ProjectSystem]", StringComparison.Ordinal)
            || text.Contains("BuildHostProcessManager]", StringComparison.Ordinal));

    private async Task<bool> WaitForProjectAsync(CancellationToken ct)
    {
        try
        {
            await Volatile.Read(ref _ready).Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
    }

    private static async Task<(JsonElement? Context, string? Label, bool IsPartial)> GetProjectContextAsync(
        LspTransport transport, string uri, CancellationToken ct)
    {
        JsonElement result;
        try
        {
            result = await transport.RequestAsync("textDocument/_vs_getProjectContexts", writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("_vs_textDocument");
                writer.WriteString("uri", uri);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }, ct).ConfigureAwait(false);
        }
        catch (LspRequestException) { return (null, null, true); }
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("_vs_projectContexts", out var contexts) || contexts.ValueKind != JsonValueKind.Array
            || !result.TryGetProperty("_vs_defaultIndex", out var index) || !index.TryGetInt32(out var selected)
            || (uint)selected >= (uint)contexts.GetArrayLength())
            return (null, null, true);
        var context = contexts[selected];
        if (context.ValueKind != JsonValueKind.Object || !context.TryGetProperty("_vs_id", out var id)
            || id.ValueKind != JsonValueKind.String || !context.TryGetProperty("_vs_label", out var label)
            || label.ValueKind != JsonValueKind.String)
            return (null, null, true);
        var miscellaneous = !context.TryGetProperty("_vs_is_miscellaneous", out var misc) || misc.ValueKind != JsonValueKind.False;
        return (context, label.GetString(), miscellaneous || contexts.GetArrayLength() > 1);
    }

    private static TaskCompletionSource NewReadiness() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
