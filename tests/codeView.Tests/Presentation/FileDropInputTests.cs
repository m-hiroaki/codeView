using System.Reflection;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CodeView.Contracts;
using CodeView.Operations;
using CodeView.Presentation;

namespace CodeView.Tests.Presentation;

internal sealed class FileDropInputTests
{
    internal static DataTransfer Files(params string[] paths)
    {
        var data = new DataTransfer();
        foreach (var path in paths) data.Add(DataTransferItem.CreateFile(StorageItem(new Uri(path))));
        return data;
    }

    [Test]
    public void LocalPathsKeepOrderUnicodeAndLiteralShellCharacters()
    {
        var paths = new[] { Path.Combine(Path.GetTempPath(), "日本語 空白 e\u0301 +%# `file` $(echo text).cs"), Path.Combine(Path.GetTempPath(), "folder") };
        var result = FileDropInput.Read(Files(paths));
        Assert.That(result.Status, Is.EqualTo(OperationStatus.Success));
        Assert.That(result.Value, Is.EqualTo(paths));
        Assert.That(((IList<string>)result.Value).IsReadOnly, Is.True);
    }

    [TestCase(DragDropEffects.Copy, true)]
    [TestCase(DragDropEffects.Move, false)]
    [TestCase(DragDropEffects.Link, false)]
    [TestCase(DragDropEffects.None, false)]
    [TestCase(DragDropEffects.Copy | DragDropEffects.Move, true)]
    public void PreviewOnlyOffersCopyAndDoesNotReadFileValues(DragDropEffects effects, bool expected)
    {
        var item = new DataTransferItem();
        item.Set<IStorageItem>(DataFormat.File, () => throw new InvalidOperationException("Preview must not retrieve file values."));
        var data = new DataTransfer();
        data.Add(item);
        Assert.That(FileDropInput.CanOfferCopy(data, effects), Is.EqualTo(expected));
    }

    [TestCase("https://example.com/source.cs")]
    [TestCase("content://provider/source.cs")]
    [TestCase("file:///tmp/invalid%00name.cs")]
    public void UnsupportedOrInvalidPathRejectsWholeDrop(string uri)
    {
        var data = Files(Path.Combine(Path.GetTempPath(), "valid.cs"));
        data.Add(DataTransferItem.CreateFile(StorageItem(new Uri(uri))));
        var result = FileDropInput.Read(data);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InvalidPath));
    }

    [Test]
    public void TextAndEmptyInputNeverBecomeOpenRequests()
    {
        var data = new DataTransfer();
        Assert.That(FileDropInput.Read(data).Error!.Code, Is.EqualTo(ErrorCode.InvalidPath));
        data.Add(DataTransferItem.CreateText("/tmp/not-a-file-drop.cs"));
        Assert.That(FileDropInput.CanOfferCopy(data, DragDropEffects.Copy), Is.False);
        Assert.That(FileDropInput.Read(data).Error!.Code, Is.EqualTo(ErrorCode.InvalidPath));
        data.Add(DataTransferItem.CreateFile(StorageItem(new Uri(Path.Combine(Path.GetTempPath(), "valid.cs")))));
        Assert.That(FileDropInput.Read(data).Error!.Code, Is.EqualTo(ErrorCode.InvalidPath));
    }

    [TestCase(255, true)]
    [TestCase(256, true)]
    [TestCase(257, false)]
    public void ItemLimitIsCheckedBeforeReadingValues(int count, bool accepted)
    {
        var reads = 0;
        var data = new DataTransfer();
        for (var index = 0; index < count; index++)
        {
            var item = new DataTransferItem();
            item.Set<IStorageItem>(DataFormat.File, () => { reads++; return StorageItem(new Uri(Path.Combine(Path.GetTempPath(), "source.cs"))); });
            data.Add(item);
        }
        Assert.That(FileDropInput.Read(data).Status, Is.EqualTo(accepted ? OperationStatus.Success : OperationStatus.Failure));
        Assert.That(reads, Is.EqualTo(accepted ? count : 0));
    }

    [TestCase(-1, true)]
    [TestCase(0, true)]
    [TestCase(1, false)]
    public void PathCharacterBudgetIsAppliedAtomically(int difference, bool accepted)
    {
        var prefix = Path.GetPathRoot(Path.GetTempPath())!;
        var length = StartupOpenQueue.MaxPathChars / 8;
        var paths = Enumerable.Range(0, 8).Select(index => prefix + new string('x', length - prefix.Length + (index == 0 ? difference : 0))).ToArray();
        var result = FileDropInput.Read(Files(paths));
        Assert.That(result.Status, Is.EqualTo(accepted ? OperationStatus.Success : OperationStatus.Failure));
        if (!accepted) Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.QueueFull));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PickerSelectionUsesTheSamePathValidation(bool includeInvalidItem)
    {
        var path = Path.Combine(Path.GetTempPath(), "日本語 #.cs");
        using var file = StorageItem(new Uri(path));
        using var remote = StorageItem(new Uri("content://provider/remote.cs"));
        IReadOnlyList<IStorageItem> selected = includeInvalidItem ? [file, remote] : [file];
        var result = FileDropInput.ReadPaths(selected, item => item);
        Assert.That(result.Status, Is.EqualTo(includeInvalidItem ? OperationStatus.Failure : OperationStatus.Success));
        if (includeInvalidItem) Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.InvalidPath));
        else Assert.That(result.Value, Is.EqualTo(new[] { path }));
    }

    internal static IStorageItem StorageItem(Uri path)
    {
        var item = DispatchProxy.Create<IStorageItem, StorageProxy>();
        ((StorageProxy)item).Path = path;
        return item;
    }

    // The platform interface has a compiler-only non-implementation marker. A runtime proxy
    // is a test double only; product code consumes the platform-owned storage item.
    public class StorageProxy : DispatchProxy
    {
        public Uri Path { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            "get_Path" => Path,
            "Dispose" => null,
            _ => throw new InvalidOperationException("No storage operations during extraction.")
        };
    }
}
