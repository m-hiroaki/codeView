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
    SettingsSnapshot Snapshot, string? FilePath, OperationError? Warning, bool CanSave);

internal sealed class SettingsStore(SettingsLocations locations)
{
    private readonly ISourceAccess _access = new SourceAccess();

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

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static SettingsLoadResult Invalid(string? path, ErrorCode code)
        => new(SettingsSnapshot.Default, path, new OperationError(code, path), false);
}
