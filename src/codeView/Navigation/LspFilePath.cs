namespace CodeView.Navigation;

internal static class LspFilePath
{
    internal static bool TryFromUri(string? value, out string path)
    {
        path = "";
        const string filePrefix = "file:///";
        var drive = filePrefix.Length;
        // Roslyn can percent-encode the drive colon. System.Uri needs it literally to recognize a DOS path.
        if (OperatingSystem.IsWindows() && value is not null && value.Length > drive + 4
            && value.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase) && char.IsAsciiLetter(value[drive])
            && value.AsSpan(drive + 1, 4).Equals("%3A/", StringComparison.OrdinalIgnoreCase))
            value = string.Concat(value.AsSpan(0, drive + 1), ":", value.AsSpan(drive + 4));
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile
            || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        path = uri.LocalPath;
        return Path.IsPathFullyQualified(path);
    }

    // A server may canonicalize parent symlinks, for example /var to /private/var on macOS.
    internal static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var remaining = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < remaining.Length; i++)
        {
            current = Path.Combine(current, remaining[i]);
            FileSystemInfo entry = i + 1 < remaining.Length || Directory.Exists(current)
                ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null)
                current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return current;
    }

    internal static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
