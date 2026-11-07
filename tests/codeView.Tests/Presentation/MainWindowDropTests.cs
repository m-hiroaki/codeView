using Avalonia.Input;
using Avalonia.Platform.Storage;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class MainWindowDropTests
{
    [Test]
    public void DropWaitsForSettingsCopiesNativeValuesAndKeepsSourcesUnchanged() => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-drop-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var file1 = Path.Combine(first, "日本語 空白 e\u0301 +%# $.cs");
        var file2 = Path.Combine(second, "second.cs");
        await File.WriteAllTextAsync(file1, "class First { }");
        await File.WriteAllTextAsync(file2, "class Second { }");
        var bytes1 = await File.ReadAllBytesAsync(file1);
        var bytes2 = await File.ReadAllBytesAsync(file2);
        var source = new SourceAccess();
        using var operations = new AppOperations(new FileReader(source), new AvaloniaEditDocumentAdapter(), new DirectoryReader(source));
        var view = new MainWindowViewModel(operations, new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))), YieldAsync);
        try
        {
            var data = FileDropInputTests.Files(first, file1, second, file2);
            var accepted = 0;
            await view.OpenDropAsync(data, () => accepted++);
            Assert.That(accepted, Is.EqualTo(1));
            Assert.That(operations.Workspace, Is.Null);
            Assert.That(view.Document, Is.Null);
            Assert.That(operations.Startup.OutstandingCount, Is.EqualTo(1));
            foreach (var item in data.Items)
                item.Set<IStorageItem>(DataFormat.File, () => throw new InvalidOperationException("Native values are no longer available."));
            await view.InitializeAsync([]);
            Assert.That(view.WorkspacePath, Is.EqualTo(first));
            Assert.That(view.FilePath, Is.EqualTo(file2));
            Assert.That(view.Document!.Text, Is.EqualTo("class Second { }"));
            Assert.That(view.Status, Does.Contain("後続のフォルダ1件"));
            Assert.That(operations.Startup.OutstandingCount, Is.Zero);
            Assert.That(await File.ReadAllBytesAsync(file1), Is.EqualTo(bytes1));
            Assert.That(await File.ReadAllBytesAsync(file2), Is.EqualTo(bytes2));
            view.Stop();
            await view.OpenDropAsync(data);
            Assert.That(view.Document, Is.Null);
        }
        finally { view.Stop(); await view.PendingWork; Directory.Delete(root, true); }
    });

    [Test]
    public void RejectedDropDoesNotPartiallyOpenOrChangeExistingBody() => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-drop-reject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "body.cs");
        await File.WriteAllTextAsync(file, "class Body { }");
        var source = new SourceAccess();
        using var operations = new AppOperations(new FileReader(source), new AvaloniaEditDocumentAdapter(), new DirectoryReader(source));
        var view = new MainWindowViewModel(operations, new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))), YieldAsync);
        try
        {
            await view.InitializeAsync([file]);
            var body = view.Document;
            var data = FileDropInputTests.Files(root);
            data.Add(DataTransferItem.CreateFile(FileDropInputTests.StorageItem(new Uri("https://example.com/source.cs"))));
            var accepted = false;
            await view.OpenDropAsync(data, () => accepted = true);
            Assert.That(accepted, Is.False);
            Assert.That(view.Document, Is.SameAs(body));
            Assert.That(view.HasWorkspace, Is.False);
            Assert.That(view.Status, Does.Contain("ローカル"));
            var text = new DataTransfer();
            text.Add(DataTransferItem.CreateText(file));
            await view.OpenDropAsync(text);
            Assert.That(view.Document, Is.SameAs(body));
            var large = new DataTransfer();
            for (var index = 0; index < 257; index++) large.Add(DataTransferItem.CreateText("ignored"));
            await view.OpenDropAsync(large);
            Assert.That(view.Status, Does.Contain("上限"));
            Assert.That(view.Document!.Text, Is.EqualTo("class Body { }"));
            Assert.That(operations.Startup.OutstandingCount, Is.Zero);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("class Body { }"));
        }
        finally { view.Stop(); await view.PendingWork; Directory.Delete(root, true); }
    });

    private static async Task YieldAsync() => await Task.Yield();
}
