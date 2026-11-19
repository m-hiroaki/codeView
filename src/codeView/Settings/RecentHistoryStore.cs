using System.Security.Cryptography;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Settings;

internal sealed record RecentItem(string FullPath, bool IsFolder, DateTimeOffset OpenedAt)
{
    public string Name => Path.GetFileName(FullPath) is { Length: > 0 } name ? name : FullPath;
    public string Kind => IsFolder ? "フォルダ" : "ファイル";
    public string DateLabel
    {
        get
        {
            var date = OpenedAt.LocalDateTime.Date;
            var today = DateTime.Today;
            return date == today ? "今日" : date == today.AddDays(-1) ? "昨日" : date.ToString("d");
        }
    }

    public string ShortPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length == 0) return FullPath;
            var relative = Path.GetRelativePath(home, FullPath);
            return relative == "." ? "~" : Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? FullPath : "~" + Path.DirectorySeparatorChar + relative;
        }
    }
}

internal sealed class RecentHistoryStore(string path)
{
    internal const int MaximumItems = 6;
    internal const int MaximumBytes = 64 * 1024;
    private const int SchemaVersion = 1;
    private readonly string _directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
    private readonly string _path = Path.GetFullPath(path);
    private readonly SourceAccess _files = new();
    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task<IReadOnlyList<RecentItem>> LoadAsync(CancellationToken ct) =>
        (await ReadAsync(ct).ConfigureAwait(false)).Items;

    public Task<IReadOnlyList<RecentItem>> RecordAsync(IReadOnlyList<RecentItem> opened,
        IReadOnlyList<string> openSources, string workspace, CancellationToken ct) =>
        UpdateAsync(old => opened.Reverse().Concat(old).OrderByDescending(item => item.OpenedAt)
            .Where(item => JsonEncodedText.Encode(item.FullPath).EncodedUtf8Bytes.Length <= MaximumBytes - 256)
            .DistinctBy(item => item.FullPath, PathComparer).Take(MaximumItems).ToArray(), openSources, workspace, ct);

    public Task<IReadOnlyList<RecentItem>> RemoveAsync(string? itemPath, IReadOnlyList<string> openSources,
        string workspace, CancellationToken ct) =>
        UpdateAsync(old => itemPath is null ? [] : old.Where(item => !PathComparer.Equals(item.FullPath, itemPath)).ToArray(),
            openSources, workspace, ct);

    private async Task<IReadOnlyList<RecentItem>> UpdateAsync(Func<RecentItem[], RecentItem[]> update,
        IReadOnlyList<string> openSources, string workspace, CancellationToken ct)
    {
        var lockPath = Path.ChangeExtension(_path, ".lock");
        EnsurePlainDirectory();
        var destination = SettingsStore.ResolveExistingLinks(_path).Normalize();
        var lockDestination = SettingsStore.ResolveExistingLinks(lockPath).Normalize();
        var workspacePrefix = workspace.Length == 0 ? "" : SettingsStore.ResolveExistingLinks(workspace).Normalize();
        if (workspacePrefix.Length > 0 && !Path.EndsInDirectorySeparator(workspacePrefix))
            workspacePrefix += Path.DirectorySeparatorChar;
        if ((workspacePrefix.Length > 0 && destination.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
            || openSources.Select(source => SettingsStore.ResolveExistingLinks(source).Normalize()).Any(source =>
                string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, lockDestination, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("履歴の保存先が開いているファイルまたはフォルダに含まれます。");

        Directory.CreateDirectory(_directory);
        EnsurePlainDirectory();
        await using var writeLock = await AcquireLockAsync(lockPath, ct).ConfigureAwait(false);
        var (old, hash) = await ReadAsync(ct).ConfigureAwait(false);
        var items = update(old);
        if (items.SequenceEqual(old)) return old;
        var json = Serialize(items);
        while (json.Length > MaximumBytes)
        {
            items = items[..^1];
            json = Serialize(items);
        }

        var temporary = Path.Combine(_directory, ".codeview.recent-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                created = true;
                await stream.WriteAsync(json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            if ((await ReadAsync(ct).ConfigureAwait(false)).Hash != hash)
                throw new IOException("履歴が外部で変更されました。");
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: hash is not null);
            created = false;
            return items;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<FileStream> AcquireLockAsync(string lockPath, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                EnsurePlainDirectory();
                if (SettingsStore.Exists(lockPath))
                    using (OpenPlainFile(lockPath, ct)) { }
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    if (SourceAccess.HasSingleLink(stream.SafeFileHandle) && IsPlainFile(lockPath)) return stream;
                    throw new InvalidDataException("履歴の排他制御ファイルが不正です。");
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException) when (attempt < 7)
            {
                await Task.Delay(30, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<(RecentItem[] Items, string? Hash)> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsurePlainDirectory();
        if (!SettingsStore.Exists(_path)) return ([], null);
        await using var stream = OpenPlainFile(_path, ct);
        var bytes = new byte[MaximumBytes + 1];
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct).ConfigureAwait(false);
        if (count > MaximumBytes) throw new InvalidDataException("履歴ファイルが上限を超えています。");
        var data = bytes.AsMemory(0, count);
        using var json = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 4 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number) || number != SchemaVersion
            || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() > MaximumItems)
            throw new InvalidDataException("履歴ファイルの形式または版を確認できません。");

        var entries = new List<RecentItem>(items.GetArrayLength());
        var paths = new HashSet<string>(PathComparer);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 3
                || !item.TryGetProperty("path", out var itemPath) || itemPath.ValueKind != JsonValueKind.String
                || ReadString(itemPath) is not { } fullPath || fullPath.Contains('\0') || !Path.IsPathFullyQualified(fullPath)
                || !item.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                || ReadString(kind) is not ("file" or "folder")
                || !item.TryGetProperty("openedAt", out var time) || time.ValueKind != JsonValueKind.String
                || !time.TryGetDateTimeOffset(out var openedAt))
                throw new InvalidDataException("履歴項目を読み取れません。");
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
            if (paths.Add(fullPath)) entries.Add(new(fullPath, kind.GetString() == "folder", openedAt));
        }
        return (entries.OrderByDescending(item => item.OpenedAt).ToArray(), Convert.ToHexString(SHA256.HashData(data.Span)));
    }

    private static string? ReadString(JsonElement element)
    {
        try { return element.GetString(); }
        catch (InvalidOperationException error)
        {
            throw new InvalidDataException("履歴項目の文字列を読み取れません。", error);
        }
    }

    private FileStream OpenPlainFile(string file, CancellationToken ct)
    {
        if (!IsPlainFile(file)) throw new InvalidDataException("履歴に使うファイルが不正です。");
        var opened = _files.OpenRead(SourcePath.Create(file, _directory), ct);
        if (opened.Status != OperationStatus.Success) throw new IOException("履歴に使うファイルを読み取れません。");
        var stream = opened.Value;
        if (SourceAccess.HasSingleLink(stream.SafeFileHandle)) return stream;
        stream.Dispose();
        throw new InvalidDataException("履歴に使うファイルに複数のリンクがあります。");
    }

    private void EnsurePlainDirectory() => SettingsStore.EnsurePlainDirectories(SettingsStore.NormalizeSystemPath(_directory));

    private static bool IsPlainFile(string file) =>
        (File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;

    private static byte[] Serialize(IEnumerable<RecentItem> items)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", SchemaVersion);
            writer.WriteStartArray("items");
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("path", item.FullPath);
                writer.WriteString("kind", item.IsFolder ? "folder" : "file");
                writer.WriteString("openedAt", item.OpenedAt.ToUniversalTime());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return bytes.ToArray();
    }
}
