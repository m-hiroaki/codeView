using System.Text;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class MainWindowViewModelTests
{
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
        using var operations = new AppOperations(new FileReader(new SourceAccess()), new AvaloniaEditDocumentAdapter());
        var viewModel = new MainWindowViewModel(operations,
            new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))));
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
        using var operations = new AppOperations(new FileReader(new SourceAccess()), new AvaloniaEditDocumentAdapter());
        var viewModel = new MainWindowViewModel(operations,
            new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))));
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
