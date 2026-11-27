using Avalonia.Media;
using Avalonia.Media.Imaging;
using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;

namespace CodeView.Presentation;

internal sealed class LocalMarkdownImages(string documentPath) : IMarkViewExtension, IImageLoader
{
    private const int MaximumImageBytes = 8 * 1024 * 1024;

    public void Register(AvaloniaRenderer renderer)
    {
        renderer.ImageLoaders.Clear();
        renderer.ImageLoaders.Add(this);
    }

    public bool CanLoad(string url) => Resolve(documentPath, url) is not null;

    public Task<IImage?> LoadAsync(string url, CancellationToken ct = default) => Task.Run<IImage?>(() =>
    {
        var path = Resolve(documentPath, url);
        if (path is null) return null;
        try
        {
            ct.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            if (stream.Length > MaximumImageBytes) return null;
            return Bitmap.DecodeToWidth(stream, 1200);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }, ct);

    internal static Uri DocumentUri(string path) => new(new Uri(path).AbsoluteUri);

    internal static string? Resolve(string documentPath, string url)
    {
        if (url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith(@"\\", StringComparison.Ordinal)
            || !Uri.TryCreate(DocumentUri(documentPath), url, out var uri) || !uri.IsFile
            || uri.IsUnc || uri.Host.Length != 0) return null;
        return uri.LocalPath;
    }
}
