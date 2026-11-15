using System.Text;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Search;

namespace CodeView.Navigation;

internal sealed record MappedSemanticResults(IReadOnlyList<SearchHit> Hits, bool IsPartial, bool LimitReached);

internal sealed class SemanticResultMapper(FileReader files)
{
    // Bound copies passed across the process boundary and used to validate a destination.
    internal const int MaximumTextChars = 8 * 1024 * 1024;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly record struct Range(LspPosition Start, LspPosition End);

    public MappedSemanticResults Map(SemanticReply reply, SourcePath root, ExclusionPolicy policy,
        SemanticOrigin origin, string originalText, DateTime startedAtUtc, CancellationToken ct)
    {
        var canonicalRoot = LspFilePath.Resolve(root.FullPath);
        var canonicalOrigin = LspFilePath.Resolve(origin.Path.FullPath);
        var locations = new Dictionary<string, HashSet<Range>>(PathComparer);
        var incomplete = reply.IsPartial;
        var limit = false;
        var accepted = 0;
        if (reply.Locations.ValueKind == JsonValueKind.Array)
        {
            foreach (var location in reply.Locations.EnumerateArray())
            {
                if (!Add(location)) break;
            }
        }
        else if (reply.Locations.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) Add(reply.Locations);

        var hits = new List<SearchHit>(accepted);
        long fileId = 0;
        foreach (var (canonical, ranges) in locations)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(canonicalRoot, canonical);
            var target = PathComparer.Equals(canonical, canonicalOrigin) ? origin.Path : SourcePath.Create(relative, root.FullPath);
            var isOriginal = PathComparer.Equals(canonical, canonicalOrigin);
            var snapshot = isOriginal ? (originalText, origin.Stamp) : ReadText(target, startedAtUtc, ct);
            if (snapshot is not { } valid)
            {
                incomplete = true;
                continue;
            }
            var (text, stamp) = valid;
            var source = new LspSourceText(text);
            fileId++;
            foreach (var range in ranges.OrderBy(range => range.Start.Line).ThenBy(range => range.Start.Character))
            {
                if (!source.TryRange(range.Start, range.End, reply.PositionEncoding, out var mapped)
                    || range.Start.Line != range.End.Line || mapped.Length > SearchSnippet.MaxChars)
                {
                    incomplete = true;
                    continue;
                }
                var snippet = SearchSnippet.Capture(text, checked((int)mapped.Offset), checked((int)mapped.Length));
                hits.Add(new(fileId, target, mapped, snippet, stamp, origin));
            }
        }
        return new(hits, incomplete || limit, limit);

        bool Add(JsonElement location)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryReadLocation(location, out var path, out var range)
                || !TryResolveTarget(path, canonicalRoot, policy, out var target))
            {
                incomplete = true;
                return true;
            }
            if (!locations.TryGetValue(target, out var ranges))
                locations.Add(target, ranges = []);
            if (!ranges.Add(range)) return true;
            if (accepted < SearchLimits.ResultLimit)
            {
                accepted++;
                return true;
            }
            ranges.Remove(range);
            limit = true;
            return false;
        }
    }

    private (string Text, SourceStamp Stamp)? ReadText(SourcePath path, DateTime startedAtUtc, CancellationToken ct)
    {
        var prepared = files.Prepare(path, EncodingChoice.Auto, ct);
        if (prepared.Status != OperationStatus.Success) return null;
        using var input = prepared.Value;
        if (input.Stamp.LastWriteUtc > startedAtUtc) return null;
        var builder = new StringBuilder();
        try
        {
            var result = files.ReadChunks(input, chunk =>
            {
                if (chunk.Length > MaximumTextChars - builder.Length) throw new TextTooLong();
                builder.Append(chunk.Span);
            }, ct);
            return result.Status == OperationStatus.Success ? (builder.ToString(), input.Stamp) : null;
        }
        catch (TextTooLong) { return null; }
    }

    private static bool TryResolveTarget(string path, string root, ExclusionPolicy policy, out string canonical)
    {
        canonical = "";
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            canonical = LspFilePath.Resolve(path);
            if (!LspFilePath.IsWithin(root, canonical) || !File.Exists(canonical)) return false;
            var relative = Path.GetRelativePath(root, canonical);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            return !parts.Take(parts.Length - 1).Any(policy.IsExcludedDirectory);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryReadLocation(JsonElement value, out string path, out Range range)
    {
        path = "";
        range = default;
        if (value.ValueKind != JsonValueKind.Object) return false;
        var isLink = value.TryGetProperty("targetUri", out var uri);
        if (!isLink && !value.TryGetProperty("uri", out uri)) return false;
        if (uri.ValueKind != JsonValueKind.String || !Uri.TryCreate(uri.GetString(), UriKind.Absolute, out var file)
            || !file.IsFile || !string.IsNullOrEmpty(file.Query) || !string.IsNullOrEmpty(file.Fragment)) return false;
        if (!value.TryGetProperty(isLink ? "targetSelectionRange" : "range", out var span) || span.ValueKind != JsonValueKind.Object
            || !span.TryGetProperty("start", out var start) || !TryReadPosition(start, out var first)
            || !span.TryGetProperty("end", out var end) || !TryReadPosition(end, out var last)) return false;
        path = file.LocalPath;
        range = new(first, last);
        return true;
    }

    private static bool TryReadPosition(JsonElement value, out LspPosition position)
    {
        position = default;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("line", out var line)
            || line.ValueKind != JsonValueKind.Number || !line.TryGetInt32(out var row)
            || !value.TryGetProperty("character", out var character) || character.ValueKind != JsonValueKind.Number
            || !character.TryGetInt32(out var column)) return false;
        position = new(row, column);
        return true;
    }

    private sealed class TextTooLong : Exception;
}
