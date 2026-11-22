using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeView.Navigation;

internal sealed class ClangdCompilationDatabase(string root, string? selected, string cacheRoot)
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private HashSet<string> _sources = new(PathComparer);
    private (string? Path, long Length, DateTime Changed)? _snapshot;
    private readonly string _canonicalRoot = LspFilePath.Resolve(root);
    private readonly string _cacheNamespace = Path.Combine(cacheRoot,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root + "\n" + selected))));
    public string CacheDirectory { get; private set; } = "";
    public string Description => _snapshot?.Path is { } path ? Path.GetRelativePath(root, path) : "コンパイル条件未設定";
    public bool IsComplete { get; private set; }
    internal bool HasCommands => _sources.Count != 0;
    public bool HasCommand(string path) => _sources.Contains(path);

    // clangd writes index files beside the database. Copy it out of the source folder, keeping the
    // original compile directories and source paths, so the cache can be reused across app launches.
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        var source = FindDatabase(root, selected);
        var snapshot = (source?.FullName, source?.Length ?? -1, source?.LastWriteTimeUtc ?? default);
        if (_snapshot == snapshot) return false;
        var sources = new HashSet<string>(PathComparer);
        var complete = source is not null;
        Directory.CreateDirectory(_cacheNamespace);
        var temporary = Path.Combine(_cacheNamespace, Guid.NewGuid().ToString("N") + ".tmp");
        string generation;
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                if (source is not null)
                {
                    await using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var commands = await JsonDocument.ParseAsync(input, cancellationToken: ct).ConfigureAwait(false);
                    if (commands.RootElement.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("コンパイルデータベースの形式が不正です。");
                    foreach (var entry in commands.RootElement.EnumerateArray())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!TryReadPaths(entry, source.DirectoryName!, out var directory, out var file))
                        {
                            complete = false;
                            continue;
                        }
                        var hasArguments = entry.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Array
                            && arguments.GetArrayLength() > 0 && arguments.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String);
                        var hasCommand = entry.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(command.GetString());
                        if (!(hasArguments || hasCommand) || !File.Exists(file) || !LspFilePath.IsWithin(_canonicalRoot, file))
                        {
                            complete = false;
                            continue;
                        }
                        if (!sources.Add(file)) complete = false;
                        writer.WriteStartObject();
                        writer.WriteString("directory", directory);
                        writer.WriteString("file", file);
                        if (hasArguments)
                        {
                            writer.WritePropertyName("arguments");
                            arguments.WriteTo(writer);
                        }
                        else
                            writer.WriteString("command", command.GetString());
                        if (entry.TryGetProperty("output", out var target) && target.ValueKind == JsonValueKind.String)
                            writer.WriteString("output", target.GetString());
                        writer.WriteEndObject();
                    }
                }
                writer.WriteEndArray();
            }
            await using (var input = File.OpenRead(temporary))
                generation = Path.Combine(_cacheNamespace, Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false)));
            Directory.CreateDirectory(generation);
            var output = Path.Combine(generation, "compile_commands.json");
            if (!File.Exists(output)) File.Move(temporary, output, overwrite: true);
        }
        finally { File.Delete(temporary); }
        _sources = sources;
        var changed = _snapshot.HasValue && CacheDirectory != generation;
        CacheDirectory = generation;
        _snapshot = snapshot;
        IsComplete = complete && sources.Count > 0;
        return changed;
    }

    internal static FileInfo? FindDatabase(string root, string? selected)
    {
        if (selected is not null)
            return File.Exists(selected) ? new FileInfo(selected) : null;
        foreach (var parent in new[] { root, Path.Combine(root, "build") })
        {
            var candidate = Path.Combine(parent, "compile_commands.json");
            if (File.Exists(candidate)) return new FileInfo(candidate);
        }
        return null;
    }

    private static bool TryReadPaths(JsonElement entry, string databaseDirectory, out string directory, out string file)
    {
        directory = file = "";
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("directory", out var directoryValue) || directoryValue.ValueKind != JsonValueKind.String
            || !entry.TryGetProperty("file", out var fileValue) || fileValue.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(directoryValue.GetString()) || string.IsNullOrWhiteSpace(fileValue.GetString()))
            return false;
        try
        {
            directory = LspFilePath.Resolve(Path.GetFullPath(directoryValue.GetString()!, databaseDirectory));
            file = LspFilePath.Resolve(Path.GetFullPath(fileValue.GetString()!, directory));
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
