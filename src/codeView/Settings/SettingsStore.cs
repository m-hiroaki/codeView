using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Settings;

internal sealed record SettingsLocations(string PortableFile, string UserFile)
{
    public static SettingsLocations ForApplication(string applicationDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        if (OperatingSystem.IsMacOS())
        {
            var appBundle = new DirectoryInfo(directory);
            while (appBundle is not null && !appBundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                appBundle = appBundle.Parent;
            if (appBundle?.Parent is not null)
                directory = appBundle.Parent.FullName;
        }

        var userDirectory = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "codeView")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codeView");
        return new(Path.Combine(directory, "codeview.settings.json"), Path.Combine(userDirectory, "codeview.settings.json"));
    }
}

internal sealed record SettingsLoadResult(
    SettingsSnapshot Snapshot, string? FilePath, OperationError? Warning, bool CanSave)
{
    internal string? LoadedHash { get; init; }
}

internal sealed class SettingsStore(SettingsLocations locations, ISourceAccess? sourceAccess = null)
{
    private readonly ISourceAccess _access = sourceAccess ?? new SourceAccess();
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken ct)
    {
        string? selected = null;
        try
        {
            // Do not use File.Exists: it can turn access errors into false and change precedence.
            ct.ThrowIfCancellationRequested();
            selected = locations.PortableFile;
            if (!Exists(selected))
            {
                selected = locations.UserFile;
                if (!Exists(selected))
                    return new(SettingsSnapshot.Default, null, null, true);
            }

            var attributes = File.GetAttributes(selected);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                return Invalid(selected, ErrorCode.InvalidSettings);

            var path = SourcePath.Create(selected, Path.GetDirectoryName(Path.GetFullPath(selected))!);
            var opened = _access.OpenRead(path, ct);
            if (opened.Status != OperationStatus.Success)
                return Invalid(selected, opened.Error!.Code);
            await using var stream = opened.Value;
            var bytes = new byte[SettingsValidator.MaxBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                count += read;
            }

            var validated = SettingsValidator.Validate(bytes.AsMemory(0, count));
            return validated.IsValid
                ? new(validated.Snapshot, selected, null, validated.CanSave)
                    { LoadedHash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, count))) }
                : Invalid(selected, ErrorCode.InvalidSettings);
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid(selected, ErrorCode.AccessDenied);
        }
        catch (IOException)
        {
            return Invalid(selected, ErrorCode.IoFailure);
        }
    }

    internal async Task<OperationResult<SettingsLoadResult>> SaveAsync(SettingsLoadResult loaded,
        SettingsSnapshot snapshot, IReadOnlyList<string> openSources, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!loaded.CanSave || loaded.Warning is not null)
            return SaveFailure(ErrorCode.InvalidSettings, loaded.FilePath);

        var selected = loaded.FilePath ?? locations.UserFile;
        if (selected != locations.PortableFile && selected != locations.UserFile)
            return SaveFailure(ErrorCode.InvalidSettings, selected);
        if (snapshot.ExcludedDirectoryNames.Count > SettingsValidator.MaxNames ||
            snapshot.ExcludedDirectoryNames.Any(name => !SettingsValidator.IsDirectoryName(name)))
            return SaveFailure(ErrorCode.InvalidSettings, selected);
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes, new() { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", snapshot.SchemaVersion);
            writer.WriteStartArray("excludedDirectoryNames");
            foreach (var name in snapshot.ExcludedDirectoryNames) writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var json = bytes.ToArray();
        var validated = SettingsValidator.Validate(json);
        if (!validated.IsValid)
            return SaveFailure(ErrorCode.InvalidSettings, selected);

        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var fullPath = Path.GetFullPath(selected);
            var destination = ResolveExistingLinks(fullPath).Normalize();
            if (openSources.Any(path => string.Equals(ResolveExistingLinks(path).Normalize(), destination,
                    StringComparison.OrdinalIgnoreCase)))
                return SaveFailure(ErrorCode.InvalidSettings, selected);

            var directory = Path.GetDirectoryName(fullPath)!;
            // Resolve system-managed Darwin aliases such as /var before checking the rest.
            EnsurePlainDirectories(NormalizeSystemPath(directory));
            if (!await MatchesLoadedFileAsync(loaded, fullPath, ct).ConfigureAwait(false))
                return SaveFailure(ErrorCode.ChangedDuringRead, selected);
            Directory.CreateDirectory(directory);
            EnsurePlainDirectories(NormalizeSystemPath(directory));

            var candidate = Path.Combine(directory, ".codeview.settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                temporary = candidate;
                await stream.WriteAsync(json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            EnsurePlainDirectories(NormalizeSystemPath(directory));
            if (!await MatchesLoadedFileAsync(loaded, fullPath, ct).ConfigureAwait(false))
                return SaveFailure(ErrorCode.ChangedDuringRead, selected);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: loaded.FilePath is not null);
            temporary = null;
            return OperationResult<SettingsLoadResult>.Success(new(validated.Snapshot, selected, null, true)
                { LoadedHash = Convert.ToHexString(SHA256.HashData(json)) });
        }
        catch (UnauthorizedAccessException) { return SaveFailure(ErrorCode.AccessDenied, selected); }
        catch (IOException) { return SaveFailure(ErrorCode.IoFailure, selected); }
        catch (Win32Exception) { return SaveFailure(ErrorCode.IoFailure, selected); }
        finally
        {
            try
            {
                if (temporary is not null) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { _saveGate.Release(); }
        }
    }

    private async Task<bool> MatchesLoadedFileAsync(SettingsLoadResult loaded, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var current = Exists(locations.PortableFile) ? locations.PortableFile
            : Exists(locations.UserFile) ? locations.UserFile : null;
        if (current != loaded.FilePath) return false;
        if (current is null) return true;
        var info = new FileInfo(path);
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            return false;
        var result = _access.OpenRead(SourcePath.Create(path, Path.GetDirectoryName(path)!), ct);
        if (result.Status != OperationStatus.Success) return false;
        await using var existing = result.Value;
        if (!SourceAccess.HasSingleLink(existing.SafeFileHandle)) return false;
        var content = new byte[SettingsValidator.MaxBytes + 1];
        var count = await existing.ReadAtLeastAsync(content, content.Length, false, ct).ConfigureAwait(false);
        return count <= SettingsValidator.MaxBytes &&
            loaded.LoadedHash == Convert.ToHexString(SHA256.HashData(content.AsSpan(0, count)));
    }

    private static void EnsurePlainDirectories(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (!Exists(directory.FullName)) continue;
            if ((directory.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("設定の親フォルダを確認できません。");
        }
    }

    private static string ResolveExistingLinks(string path)
    {
        path = NormalizeSystemPath(Path.GetFullPath(path));
        var root = Path.GetPathRoot(path)!;
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            root = Path.Combine(root, parts[i]);
            if (!Exists(root)) continue;
            FileSystemInfo entry = i == parts.Length - 1 ? new FileInfo(root) : new DirectoryInfo(root);
            if (entry.LinkTarget is not null)
                root = entry.ResolveLinkTarget(true)?.FullName ?? throw new IOException("参照先を確認できません。");
        }
        return NormalizeSystemPath(root);
    }

    private static string NormalizeSystemPath(string path) =>
        OperatingSystem.IsMacOS() && (path.StartsWith("/var/", StringComparison.Ordinal) ||
            path.StartsWith("/tmp/", StringComparison.Ordinal) || path is "/var" or "/tmp") ? "/private" + path : path;

    private static OperationResult<SettingsLoadResult> SaveFailure(ErrorCode code, string? path) =>
        OperationResult<SettingsLoadResult>.Failure(new(code, path));

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static SettingsLoadResult Invalid(string? path, ErrorCode code)
        => new(SettingsSnapshot.Default, path, new OperationError(code, path), false);
}
