using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Navigation;

internal sealed record CppIndexedFile(string RelativePath, SourceStamp Stamp, int CodePage, ErrorCode? Error,
    IReadOnlyList<CppDefinition> Definitions)
{
    internal long EstimatedBytes => 192L + 2L * RelativePath.Length
        + Definitions.Sum(item => 192L + 2L * (item.Name.Length + (item.Qualifier?.Length ?? 0)));
}

internal sealed class CppDefinitionSnapshot(Dictionary<string, CppIndexedFile> files, bool enumerationComplete)
{
    internal IReadOnlyDictionary<string, CppIndexedFile> Files => files;
    internal bool EnumerationComplete => enumerationComplete;
    internal bool IsComplete { get; } = enumerationComplete && files.Values.All(file => file.Error is null);
}

internal sealed class CppDefinitionCache
{
    internal const int MaximumEntries = 100_000;
    internal const int MaximumFiles = 50_000;
    internal const long MaximumFileBytes = 8L * 1024 * 1024;
    internal const long MaximumSourceBytes = 1024L * 1024 * 1024;
    internal const int MaximumDefinitions = 250_000;
    internal const int MaximumRecordBytes = 64 * 1024;
    internal const long MaximumCacheBytes = 64L * 1024 * 1024;
    internal const long MaximumEstimatedBytes = 48L * 1024 * 1024;
    private const long MaximumTotalBytes = 256L * 1024 * 1024;
    private const string SnapshotName = "definitions.jsonl";
    private const string Kind = "cpp-definitions";
    private const int FormatVersion = 1;
    private const int DeclarationVersion = 1;

    internal static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _cacheRoot;
    private readonly string _workspace;
    private readonly string _identity;
    private readonly string _directory;
    private string SnapshotPath => Path.Combine(_directory, SnapshotName);

    internal CppDefinitionCache(string cacheRoot, SourcePath root, EncodingChoice encoding, IEnumerable<string> excluded)
    {
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _workspace = Path.TrimEndingDirectorySeparator(root.FullPath);
        var normalizedRoot = OperatingSystem.IsWindows() ? _workspace.ToUpperInvariant() : _workspace;
        var identity = new StringBuilder();
        Append(normalizedRoot);
        Append(encoding.ToString());
        Append(DeclarationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var item in excluded.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) Append(item);
        _identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
        _directory = Path.Combine(_cacheRoot, _identity);

        void Append(string value) => identity.Append(value.Length).Append(':').Append(value);
    }

    internal bool Exists => File.Exists(SnapshotPath);

    internal FileStream BeginUpdate()
    {
        var canonicalCache = LspFilePath.Resolve(_cacheRoot);
        if (LspFilePath.IsWithin(LspFilePath.Resolve(_workspace), canonicalCache))
            throw new IOException("作業フォルダ内のタグキャッシュには書き込めません。");
        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(_directory);
        if (!PathComparer.Equals(LspFilePath.Resolve(_directory), Path.Combine(canonicalCache, _identity)))
            throw new IOException("タグキャッシュのディレクトリがリンクです。");
        return OpenLock(_directory);
    }

    internal CppDefinitionSnapshot? Read(CancellationToken ct)
    {
        if (!Exists) return null;
        try
        {
            if ((File.GetAttributes(SnapshotPath) & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, MaximumRecordBytes, FileOptions.SequentialScan);
            if (stream.Length > MaximumCacheBytes) return null;
            return ReadSnapshot(stream, ct);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private CppDefinitionSnapshot ReadSnapshot(Stream stream, CancellationToken ct)
    {
        var files = new Dictionary<string, CppIndexedFile>(PathComparer);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[MaximumRecordBytes];
        var lineNumber = 0;
        var definitions = 0;
        var bytes = 0L;
        var estimated = 0L;
        CppIndexedFile? pending = null;
        List<CppDefinition>? items = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var length = 0;
            int value;
            while ((value = stream.ReadByte()) >= 0 && value != '\n')
            {
                if (length == buffer.Length - 1) throw InvalidCache();
                buffer[length++] = (byte)value;
            }
            if (value < 0 || length == 0 || (bytes += length + 1) > MaximumCacheBytes) throw InvalidCache();
            using var record = JsonDocument.Parse(buffer.AsMemory(0, length));
            var row = record.RootElement;
            var recordKind = RequiredString(row, "record", 16);
            if (lineNumber++ == 0)
            {
                if (recordKind != "header" || row.GetProperty("format").GetInt32() != FormatVersion
                    || row.GetProperty("parser").GetInt32() != DeclarationVersion
                    || RequiredString(row, "kind", 32) != Kind || RequiredString(row, "identity", 64) != _identity)
                    throw InvalidCache();
            }
            else if (recordKind == "file")
            {
                CommitPending();
                if (files.Count >= MaximumFiles) throw InvalidCache();
                var relative = RequiredString(row, "path", MaximumRecordBytes);
                if (!IsRelativeSource(relative)) throw InvalidCache();
                var size = row.GetProperty("size").GetInt64();
                var ticks = row.GetProperty("modified").GetInt64();
                var codePage = row.GetProperty("encoding").GetInt32();
                var reason = row.GetProperty("error");
                ErrorCode? error = reason.ValueKind == JsonValueKind.Null ? null : reason.GetInt32() is var code
                    && Enum.IsDefined((ErrorCode)code) ? (ErrorCode)code : throw InvalidCache();
                if (size < 0 || error is null && (size > MaximumFileBytes || codePage is not (65001 or 932 or 1200 or 1201 or 12000 or 12001)))
                    throw InvalidCache();
                items = [];
                pending = new(relative, new(size, new(ticks, DateTimeKind.Utc)), codePage, error, items);
                Reserve(192L + 2L * relative.Length);
            }
            else if (recordKind == "definition")
            {
                if (pending is not { Error: null } file || ++definitions > MaximumDefinitions) throw InvalidCache();
                var name = RequiredString(row, "name", IdentifierExtractor.MaximumChars);
                var qualifier = row.GetProperty("qualifier").ValueKind == JsonValueKind.Null
                    ? null : RequiredString(row, "qualifier", IdentifierExtractor.MaximumChars);
                var declarationKind = (DeclarationKind)row.GetProperty("kind").GetInt32();
                var offset = row.GetProperty("offset").GetInt64();
                var line = row.GetProperty("line").GetInt64();
                var column = row.GetProperty("column").GetInt64();
                var hasBody = row.GetProperty("body").GetBoolean();
                if (!Enum.IsDefined(declarationKind) || offset < 0 || offset > file.Stamp.LengthBytes - name.Length
                    || line < 1 || column < 1 || line > offset + 1 || column > offset + 1
                    || hasBody && declarationKind != DeclarationKind.Function) throw InvalidCache();
                Reserve(192L + 2L * (name.Length + (qualifier?.Length ?? 0)));
                items!.Add(new(name, declarationKind, qualifier, new(offset, name.Length, new(line, column)), hasBody));
            }
            else if (recordKind == "footer")
            {
                CommitPending();
                if (row.GetProperty("files").GetInt32() != files.Count || row.GetProperty("definitions").GetInt32() != definitions
                    || RequiredString(row, "hash", 64) != Convert.ToHexString(hash.GetHashAndReset()) || stream.ReadByte() != -1)
                    throw InvalidCache();
                return new(files, row.GetProperty("complete").GetBoolean());
            }
            else throw InvalidCache();
            buffer[length] = (byte)'\n';
            hash.AppendData(buffer.AsSpan(0, length + 1));
        }

        void CommitPending()
        {
            if (pending is not null && !files.TryAdd(pending.RelativePath, pending)) throw InvalidCache();
            pending = null;
        }

        void Reserve(long amount)
        {
            estimated += amount;
            if (estimated > MaximumEstimatedBytes) throw InvalidCache();
        }
    }

    internal void Save(CppDefinitionSnapshot snapshot, Func<bool> canPublish, CancellationToken ct)
    {
        var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                WriteSnapshot(stream, snapshot, ct);
            using var quota = OpenLock(_cacheRoot);
            ReserveDiskSpace(new FileInfo(temporary).Length, ct);
            ct.ThrowIfCancellationRequested();
            if (!canPublish()) throw new IOException("タグ作成中に作業フォルダが変更されました。");
            File.Move(temporary, SnapshotPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private void WriteSnapshot(Stream stream, CppDefinitionSnapshot snapshot, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        Begin("header");
        writer.WriteNumber("format", FormatVersion);
        writer.WriteNumber("parser", DeclarationVersion);
        writer.WriteString("kind", Kind);
        writer.WriteString("identity", _identity);
        End();
        var count = 0;
        foreach (var file in snapshot.Files.Values)
        {
            Begin("file");
            writer.WriteString("path", file.RelativePath);
            writer.WriteNumber("size", file.Stamp.LengthBytes);
            writer.WriteNumber("modified", file.Stamp.LastWriteUtc.Ticks);
            writer.WriteNumber("encoding", file.CodePage);
            if (file.Error is { } error) writer.WriteNumber("error", (int)error);
            else writer.WriteNull("error");
            End();
            foreach (var definition in file.Definitions)
            {
                Begin("definition");
                writer.WriteString("name", definition.Name);
                writer.WriteString("qualifier", definition.Qualifier);
                writer.WriteNumber("kind", (int)definition.Kind);
                writer.WriteNumber("offset", definition.Range.Offset);
                writer.WriteNumber("line", definition.Range.Start.Line);
                writer.WriteNumber("column", definition.Range.Start.Column);
                writer.WriteBoolean("body", definition.HasBody);
                End();
                count++;
            }
        }
        Begin("footer");
        writer.WriteNumber("files", snapshot.Files.Count);
        writer.WriteNumber("definitions", count);
        writer.WriteBoolean("complete", snapshot.EnumerationComplete);
        writer.WriteString("hash", Convert.ToHexString(hash.GetHashAndReset()));
        End();

        void Begin(string record)
        {
            ct.ThrowIfCancellationRequested();
            buffer.Clear();
            writer.Reset(buffer);
            writer.WriteStartObject();
            writer.WriteString("record", record);
        }

        void End()
        {
            writer.WriteEndObject();
            writer.Flush();
            if (buffer.WrittenCount >= MaximumRecordBytes || stream.Length + buffer.WrittenCount + 1 > MaximumCacheBytes)
                throw new IOException("タグキャッシュのサイズ上限に達しました。");
            stream.Write(buffer.WrittenSpan);
            stream.WriteByte((byte)'\n');
            hash.AppendData(buffer.WrittenSpan);
            hash.AppendData("\n"u8);
        }
    }

    private void ReserveDiskSpace(long replacementLength, CancellationToken ct)
    {
        var candidates = new DirectoryInfo(_cacheRoot).EnumerateDirectories()
            .Where(entry => entry.Name.Length == 64 && entry.Name.All(char.IsAsciiHexDigit)
                && (entry.Attributes & FileAttributes.ReparsePoint) == 0 && !PathComparer.Equals(entry.FullName, _directory))
            .Select(entry => new FileInfo(Path.Combine(entry.FullName, SnapshotName)))
            .Where(entry => entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(entry => entry.LastWriteTimeUtc).ToArray();
        var total = replacementLength + candidates.Sum(entry => entry.Length);
        foreach (var entry in candidates)
        {
            if (total <= MaximumTotalBytes) return;
            ct.ThrowIfCancellationRequested();
            try
            {
                using var writeLock = OpenLock(entry.DirectoryName!);
                var size = entry.Length;
                entry.Delete();
                total -= size;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (total > MaximumTotalBytes) throw new IOException("タグキャッシュの合計容量上限に達しました。");
    }

    private static FileStream OpenLock(string path) =>
        new(Path.Combine(path, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private bool IsRelativeSource(string path)
    {
        if (Path.IsPathRooted(path) || !CppDeclarationScanner.IncludesPath(path)) return false;
        var full = Path.GetFullPath(path, _workspace);
        return LspFilePath.IsWithin(_workspace, full) && PathComparer.Equals(path, Path.GetRelativePath(_workspace, full));
    }

    private static string RequiredString(JsonElement row, string property, int maxChars) =>
        row.GetProperty(property).GetString() is { Length: > 0 } value && value.Length <= maxChars && !value.Contains('\0')
            ? value : throw InvalidCache();

    private static InvalidDataException InvalidCache() => new("タグキャッシュの形式が不正です。");
}
