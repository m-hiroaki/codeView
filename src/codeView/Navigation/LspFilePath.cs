namespace CodeView.Navigation;

internal static class LspFilePath
{
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
