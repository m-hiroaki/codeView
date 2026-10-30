using System.Text;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class MainWindowViewModelTests
{
    private static async Task YieldTreeAsync() => await Task.Yield();
    [Test]
    public void ShutdownWaitsForManualRefreshAndCannotRestoreWorkspace() => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-refresh-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "first");
        Directory.CreateDirectory(folder);
        using var source = new CodeView.Tests.Operations.WorkspaceTests.ControlledSource();
        using var operations = new AppOperations(new FileReader(source), new AvaloniaEditDocumentAdapter(), new DirectoryReader(source));
        var viewModel = new MainWindowViewModel(operations,
            new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))), YieldTreeAsync);
        Task refresh = Task.CompletedTask;
        try
        {
            await viewModel.InitializeAsync([folder]);
            source.DelayResolution = true;
            refresh = viewModel.RefreshWorkspaceAsync();
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.Stop();
            var pending = viewModel.PendingWork;
            Assert.That(pending.IsCompleted, Is.False);
            Assert.That(viewModel.HasWorkspace, Is.False);
            source.Release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await refresh;
            Assert.That(viewModel.HasWorkspace, Is.False);
        }
        finally
        {
            source.Release.Set();
            viewModel.Stop();
            await refresh;
            await viewModel.PendingWork;
            Directory.Delete(root, true);
        }
    });

    [Test]
    public void QueuedFolderWaitsForSettingsAndMixedInputUsesFirstFolder() => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-folder-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "skip"));
        Directory.CreateDirectory(Path.Combine(root, "second"));
        using var operations = new AppOperations(new FileReader(new SourceAccess()), new AvaloniaEditDocumentAdapter(), new DirectoryReader(new SourceAccess()));
        var portable = Path.Combine(root, "settings.json");
        var viewModel = new MainWindowViewModel(operations, new SettingsStore(new(portable, Path.Combine(root, "user.json"))), YieldTreeAsync);
        try
        {
            await File.WriteAllTextAsync(portable, "{\"schemaVersion\":1,\"excludedDirectoryNames\":[\"skip\"]}");
            var file = Path.Combine(root, "body.cs");
            await File.WriteAllTextAsync(file, "class Body { }");
            await viewModel.OpenFilesAsync([root, file, Path.Combine(root, "second")], OpenOrigin.Menu);
            Assert.That(operations.Workspace, Is.Null);
            Assert.That(viewModel.Document, Is.Null);
            await viewModel.InitializeAsync([]);
            Assert.That(viewModel.WorkspacePath, Is.EqualTo(root));
            Assert.That(viewModel.CanSearchWorkspace, Is.True);
            Assert.That(operations.Workspace!.RootListing!.Entries.Select(e => e.Name), Does.Not.Contain("skip"));
            Assert.That(viewModel.Document!.Text, Is.EqualTo("class Body { }"));
            Assert.That(viewModel.Status, Does.Contain("後続のフォルダ1件"));
            var row = viewModel.FileTree.Rows.First(row => row.Name == "body.cs");
            viewModel.CloseFile();
            await viewModel.ActivateTreeRowAsync(row);
            Assert.That(viewModel.Document!.Text, Is.EqualTo("class Body { }"));
            viewModel.IsTreeVisible = false;
            Assert.That(viewModel.IsTreeVisible, Is.False);
            Assert.That(viewModel.FileTree.Rows, Is.Not.Empty);
            viewModel.IsTreeVisible = true;
            var document = viewModel.Document;
            var before = operations.Workspace.RootListing.Entries.Count;
            await File.WriteAllTextAsync(Path.Combine(root, "added.cs"), "// added");
            await viewModel.RefreshWorkspaceAsync();
            Assert.That(operations.Workspace!.RootListing!.Entries, Has.Count.EqualTo(before + 1));
            Assert.That(viewModel.Document, Is.SameAs(document));
            await viewModel.OpenFilesAsync([Path.Combine(root, "second")], OpenOrigin.Menu);
            Assert.That(viewModel.WorkspacePath, Is.EqualTo(Path.Combine(root, "second")));
            Assert.That(viewModel.WorkspaceSummary, Is.EqualTo("フォルダ: 0項目"));
            viewModel.CloseWorkspace();
            Assert.That(viewModel.HasWorkspace, Is.False);
            Assert.That(viewModel.FileTree.Rows, Is.Empty);
            await viewModel.ActivateTreeRowAsync(row);
            Assert.That(viewModel.CanSearchWorkspace, Is.False);
            Assert.That(viewModel.Document, Is.SameAs(document));
            await viewModel.OpenFilesAsync([root], OpenOrigin.Menu);
            viewModel.CloseFile();
            Assert.That(viewModel.HasWorkspace, Is.True);
        }
        finally { viewModel.Stop(); await viewModel.PendingWork; Directory.Delete(root, true); }
    });

    [TestCase(EncodingChoice.Utf8)]
    [TestCase(EncodingChoice.Cp932)]
    [TestCase(EncodingChoice.Utf16Le)]
    [TestCase(EncodingChoice.Utf16Be)]
    [TestCase(EncodingChoice.Utf32Le)]
    [TestCase(EncodingChoice.Utf32Be)]
    public void ManualEncodingRetriesFailedOpenAndReloadPreservesChoice(EncodingChoice choice) => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-encoding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var operations = new AppOperations(new FileReader(new SourceAccess()), new AvaloniaEditDocumentAdapter(), new DirectoryReader(new SourceAccess()));
        var viewModel = new MainWindowViewModel(operations,
            new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))), YieldTreeAsync);
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = choice switch
            {
                EncodingChoice.Utf8 => new UTF8Encoding(false, true),
                EncodingChoice.Cp932 => Encoding.GetEncoding(932),
                EncodingChoice.Utf16Le => new UnicodeEncoding(false, false, true),
                EncodingChoice.Utf16Be => new UnicodeEncoding(true, false, true),
                EncodingChoice.Utf32Le => new UTF32Encoding(false, false, true),
                EncodingChoice.Utf32Be => new UTF32Encoding(true, false, true),
                _ => throw new ArgumentOutOfRangeException(nameof(choice))
            };
            var file = Path.Combine(root, "source.txt");
            var original = encoding.GetBytes("日本語\r\n末尾");
            await File.WriteAllBytesAsync(file, original);
            Assert.That(viewModel.CanReopenEncoding, Is.False);
            await viewModel.InitializeAsync([file]);
            Assert.That(viewModel.CanReopenEncoding, Is.True);
            await viewModel.ReopenEncodingAsync(choice);
            Assert.That(viewModel.Document!.Text, Is.EqualTo("日本語\r\n末尾"));
            Assert.That(viewModel.EncodingName, Is.EqualTo(choice switch
            {
                EncodingChoice.Cp932 => "Shift_JIS（CP932）",
                EncodingChoice.Utf16Le => "UTF-16 LE",
                EncodingChoice.Utf16Be => "UTF-16 BE",
                EncodingChoice.Utf32Le => "UTF-32 LE",
                EncodingChoice.Utf32Be => "UTF-32 BE",
                _ => "UTF-8"
            }));
            viewModel.SetPosition(2, 3);
            (int Line, int Column) restored = default;
            viewModel.RestoreCaret += (line, column) => restored = (line, column);
            await viewModel.ReloadAsync();
            Assert.That(viewModel.Document.Text, Is.EqualTo("日本語\r\n末尾"));
            Assert.That(restored, Is.EqualTo((2, 3)));
            Assert.That(await File.ReadAllBytesAsync(file), Is.EqualTo(original));
            await File.WriteAllBytesAsync(file, encoding.GetBytes("短"));
            await viewModel.ReloadAsync();
            Assert.That(viewModel.Document.Text, Is.EqualTo("短"));
            Assert.That(restored, Is.EqualTo((1, 2)));
            await File.WriteAllBytesAsync(file, []);
            await viewModel.ReloadAsync();
            Assert.That(viewModel.Document.Text, Is.Empty);
            Assert.That(restored, Is.EqualTo((1, 1)));
            viewModel.CloseFile();
            Assert.That(viewModel.CanReopenEncoding, Is.False);
            await viewModel.ReopenEncodingAsync(choice);
            Assert.That(viewModel.Document, Is.Null);
        }
        finally { viewModel.Stop(); Directory.Delete(root, true); }
    });

    [Test]
    public void FailedEncodingChangePreservesBodyPositionAndPreviousChoice() => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-encoding-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var operations = new AppOperations(new FileReader(new SourceAccess()), new AvaloniaEditDocumentAdapter(), new DirectoryReader(new SourceAccess()));
        var viewModel = new MainWindowViewModel(operations,
            new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))), YieldTreeAsync);
        try
        {
            var file = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(file, "abc\n日本語", new UTF8Encoding(true));
            await viewModel.InitializeAsync([file]);
            viewModel.SetPosition(int.MaxValue, int.MaxValue);
            var previous = viewModel.Document;
            await viewModel.ReopenEncodingAsync(EncodingChoice.Utf16Be);
            Assert.That(viewModel.Document, Is.SameAs(previous));
            Assert.That(viewModel.EncodingName, Is.EqualTo("UTF-8"));
            Assert.That(viewModel.Position, Is.EqualTo("行 2, 列 4"));
            Assert.That(viewModel.Status, Does.Contain("文字コード"));
            await viewModel.ReloadAsync();
            Assert.That(viewModel.Document, Is.Not.SameAs(previous));
            Assert.That(viewModel.Document!.Text, Is.EqualTo("abc\n日本語"));
            viewModel.SetPosition(int.MinValue, int.MinValue);
            Assert.That(viewModel.Position, Is.EqualTo("行 1, 列 1"));
            await viewModel.ReopenEncodingAsync(EncodingChoice.Auto);
            Assert.That(viewModel.EncodingName, Is.EqualTo("UTF-8"));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => viewModel.ReopenEncodingAsync((EncodingChoice)999));
        }
        finally { viewModel.Stop(); Directory.Delete(root, true); }
    });

    [Test]
    public void StartupPathsMenuOpenReloadAndCloseUseTheSameReadonlyPipeline() => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-viewmodel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var operations = new AppOperations(new FileReader(new SourceAccess()), new AvaloniaEditDocumentAdapter(), new DirectoryReader(new SourceAccess()));
        var viewModel = new MainWindowViewModel(operations,
            new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))), YieldTreeAsync);
        try
        {
            var file = Path.Combine(root, "source.cs");
            await File.WriteAllTextAsync(file, "class Original { }");
            await viewModel.InitializeAsync([file]);
            Assert.That(viewModel.HasDocument, Is.True);
            Assert.That(viewModel.Document!.Text, Is.EqualTo("class Original { }"));
            Assert.That(viewModel.FilePath, Is.EqualTo(file));
            Assert.That(viewModel.EncodingName, Is.EqualTo("UTF-8"));
            await viewModel.InitializeAsync([Path.Combine(root, "ignored.cs")]);
            Assert.That(viewModel.Document.Text, Is.EqualTo("class Original { }"));
            await viewModel.OpenFilesAsync([Path.Combine(root, "missing.cs")], OpenOrigin.Menu);
            Assert.That(viewModel.Document.Text, Is.EqualTo("class Original { }"));
            Assert.That(viewModel.Status, Does.Contain("見つかりません"));
            await File.WriteAllTextAsync(file, "class Reloaded { }");
            await viewModel.ReloadAsync();
            Assert.That(viewModel.Document.Text, Is.EqualTo("class Reloaded { }"));
            viewModel.CloseFile();
            Assert.That(viewModel.Document, Is.Null);
            Assert.That(viewModel.FilePath, Is.Empty);
            Assert.That(operations.CurrentFile, Is.Null);
            viewModel.Stop();
            await viewModel.OpenFilesAsync([file], OpenOrigin.Menu);
            Assert.That(viewModel.Document, Is.Null);
            await viewModel.PendingWork;
        }
        finally
        {
            viewModel.Stop();
            Directory.Delete(root, true);
        }
    });
}
