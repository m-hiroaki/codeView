namespace CodeView.Navigation;

internal static class BundledLanguageServers
{
    internal static (string? Roslyn, string? Clangd) Resolve(string appDirectory)
    {
        if (OperatingSystem.IsWindows())
            return (Existing(appDirectory, "language-servers", "roslyn", "Microsoft.CodeAnalysis.LanguageServer.exe"),
                Existing(appDirectory, "language-servers", "clangd", "bin", "clangd.exe"));
        if (!OperatingSystem.IsMacOS()) return (null, null);

        var contents = Path.GetFullPath(Path.Combine(appDirectory, ".."));
        var clangd = Existing(contents, "Helpers", "clangd");
        return (Existing(contents, "Helpers", "Roslyn.app", "Contents", "MacOS", "Microsoft.CodeAnalysis.LanguageServer"),
            clangd is not null && ClangdResourceDirectory(clangd) is not null ? clangd : null);
    }

    internal static string? ClangdResourceDirectory(string executable)
    {
        if (!OperatingSystem.IsMacOS() || Path.GetDirectoryName(executable) is not { } directory
            || !Path.GetFileName(directory).Equals("Helpers", StringComparison.Ordinal)) return null;
        var resources = Path.GetFullPath(Path.Combine(directory, "..", "Resources", "clangd"));
        return File.Exists(Path.Combine(resources, "include", "stddef.h")) ? resources : null;
    }

    private static string? Existing(string root, params string[] parts)
    {
        var path = Path.Combine(root, Path.Combine(parts));
        return File.Exists(path) ? path : null;
    }
}
