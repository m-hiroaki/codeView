namespace CodeView.Contracts;

internal sealed record SourcePath
{
    public string FullPath { get; }
    public string DisplayPath { get; }

    private SourcePath(string fullPath, string displayPath)
        => (FullPath, DisplayPath) = (fullPath, displayPath);

    public static SourcePath Create(string path, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        if (path.Contains('\0'))
            throw new ArgumentException("パスにNULは使用できません。", nameof(path));
        if (!Path.IsPathFullyQualified(baseDirectory))
            throw new ArgumentException("基準ディレクトリは絶対パスで指定してください。", nameof(baseDirectory));

        return new(Path.GetFullPath(path, baseDirectory), path);
    }
}

internal readonly record struct SourceStamp(long LengthBytes, DateTime LastWriteUtc);
