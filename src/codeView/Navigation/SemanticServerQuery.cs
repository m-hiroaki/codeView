using System.Text.Json;

namespace CodeView.Navigation;

internal enum SemanticOperation { Definition, References }
internal readonly record struct LspPosition(int Line, int Character);
internal sealed record SemanticReply(JsonElement Locations, string PositionEncoding, string Configuration,
    bool IsPartial, string? Status = null);

internal static class SemanticServerQuery
{
    public static Task<JsonElement> RequestAsync(LspTransport transport, SemanticOperation operation, string uri,
        LspPosition position, JsonElement? projectContext, CancellationToken ct) =>
        transport.RequestAsync(operation == SemanticOperation.Definition ? "textDocument/definition" : "textDocument/references", writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("textDocument");
            writer.WriteString("uri", uri);
            if (projectContext is { } context)
            {
                writer.WritePropertyName("_vs_projectContext");
                context.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteStartObject("position");
            writer.WriteNumber("line", position.Line);
            writer.WriteNumber("character", position.Character);
            writer.WriteEndObject();
            if (operation == SemanticOperation.References)
            {
                writer.WriteStartObject("context");
                writer.WriteBoolean("includeDeclaration", false);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }, ct);
}
