using System.Collections.ObjectModel;

namespace CodeView.Settings;

internal sealed class SettingsSnapshot
{
    public static SettingsSnapshot Default { get; } = new([".git", "bin", "obj", "node_modules"]);
    public int SchemaVersion => 1;
    public ReadOnlyCollection<string> ExcludedDirectoryNames { get; }

    internal SettingsSnapshot(IEnumerable<string> excludedDirectoryNames)
        => ExcludedDirectoryNames = Array.AsReadOnly(excludedDirectoryNames.ToArray());
}
