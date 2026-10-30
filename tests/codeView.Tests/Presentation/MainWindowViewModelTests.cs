using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

public sealed class MainWindowViewModelTests
{
    [Test]
    public void StartupPathsMenuOpenReloadAndCloseUseTheSameReadonlyPipeline() => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-viewmodel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var operations = new AppOperations(new FileReader(new SourceAccess()), new AvaloniaEditDocumentAdapter());
        var viewModel = new MainWindowViewModel(operations,
            new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))));
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
