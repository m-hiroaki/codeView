using Avalonia.Input;
using Avalonia.Platform.Storage;
using CodeView.Contracts;
using CodeView.Operations;

namespace CodeView.Presentation;

/// <summary>Extracts paths during the drop callback; never retains native transfer/storage objects.</summary>
internal static class FileDropInput
{
    public static bool CanOfferCopy(IDataTransfer data, DragDropEffects allowedEffects) =>
        (allowedEffects & DragDropEffects.Copy) != 0
        && data.Items.Count is > 0 and <= StartupOpenQueue.MaxPaths
        && data.Contains(DataFormat.File);

    public static OperationResult<IReadOnlyList<string>> Read(IDataTransfer data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var items = data.Items;
        if (items.Count > StartupOpenQueue.MaxPaths) return Fail(ErrorCode.QueueFull);
        if (items.Count == 0) return Fail(ErrorCode.InvalidPath);
        var paths = new List<string>(items.Count);
        var chars = 0L;
        foreach (var item in items)
        {
            // Do not read text/URI-list formats as paths or materialize all native file values first.
            var path = item.TryGetFile()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) return Fail(ErrorCode.InvalidPath);
            chars += path.Length;
            if (chars > StartupOpenQueue.MaxPathChars) return Fail(ErrorCode.QueueFull);
            paths.Add(path);
        }
        return OperationResult<IReadOnlyList<string>>.Success(paths.AsReadOnly());

        static OperationResult<IReadOnlyList<string>> Fail(ErrorCode code) =>
            OperationResult<IReadOnlyList<string>>.Failure(new(code));
    }
}
