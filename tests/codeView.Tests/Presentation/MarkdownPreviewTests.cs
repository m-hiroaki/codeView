using Avalonia.Input;
using CodeView.Presentation;

namespace CodeView.Tests.Presentation;

internal sealed class MarkdownPreviewTests
{
    [TestCase(KeyModifiers.Control)]
    [TestCase(KeyModifiers.Meta)]
    public void ViewShortcutRequiresPAndOnlyThePlatformModifier(KeyModifiers modifier)
    {
        var gesture = MainWindow.CreateMarkdownViewGesture(modifier);
        var otherModifier = modifier == KeyModifiers.Control ? KeyModifiers.Meta : KeyModifiers.Control;

        Assert.Multiple(() =>
        {
            Assert.That(Matches(Key.P, modifier), Is.True);
            Assert.That(Matches(Key.P, KeyModifiers.None), Is.False);
            Assert.That(Matches(Key.P, otherModifier), Is.False);
            Assert.That(Matches(Key.P, modifier | KeyModifiers.Shift), Is.False);
            Assert.That(Matches(Key.P, modifier | KeyModifiers.Alt), Is.False);
            Assert.That(Matches(Key.M, modifier | KeyModifiers.Shift), Is.False);
        });

        bool Matches(Key key, KeyModifiers modifiers) =>
            gesture.Matches(new KeyEventArgs { Key = key, KeyModifiers = modifiers });
    }

    [TestCase("README.md", 0, true)]
    [TestCase("README.MARKDOWN", 128 * 1024, true)]
    [TestCase("README.markdown", 128 * 1024 + 1, false)]
    [TestCase("README.md", -1, false)]
    [TestCase("README.md.txt", 128, false)]
    [TestCase(null, 128, false)]
    public void PreviewRequiresMarkdownWithinTheDocumentBudget(string? path, int length, bool expected) =>
        Assert.That(MainWindow.CanPreviewMarkdown(path, length), Is.EqualTo(expected));

    [Test]
    public void ResolvesLocalMarkdownLinksWithEscapingAndFragments()
    {
        var directory = Path.GetFullPath("docs #1");
        var document = Path.Combine(directory, "guide", "README.md");
        var target = Path.Combine(directory, "日本語 note", "Makefile");

        Assert.Multiple(() =>
        {
            Assert.That(MainWindow.ResolveMarkdownFileLink(document, "../%E6%97%A5%E6%9C%AC%E8%AA%9E%20note/Makefile#usage"), Is.EqualTo(target));
            Assert.That(MainWindow.ResolveMarkdownFileLink(document, new Uri(target).AbsoluteUri), Is.EqualTo(target));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("#section")]
    [TestCase("https://example.com/README.md")]
    [TestCase("//example.com/README.md")]
    [TestCase("file://example.com/README.md")]
    [TestCase("mailto:reader@example.com")]
    public void LeavesAnchorsAndExternalLinksToTheViewer(string? url) =>
        Assert.That(MainWindow.ResolveMarkdownFileLink(Path.GetFullPath("README.md"), url), Is.Null);

    [Test]
    public void ResolvesPercentEncodedRelativeImageFromDocumentDirectory()
    {
        var folder = Path.GetFullPath("example");
        var path = Path.Combine(folder, "README.md");
        Assert.That(LocalMarkdownImages.Resolve(path, "docs/my%20image.png"),
            Is.EqualTo(Path.Combine(folder, "docs", "my image.png")));
    }

    [TestCase("https://example.com/test.png")]
    [TestCase("//example.com/test.png")]
    [TestCase("file://example.com/test.png")]
    [TestCase("data:image/png;base64,AAAA")]
    public void RejectsNonLocalImageRequests(string url) =>
        Assert.That(LocalMarkdownImages.Resolve(Path.GetFullPath("README.md"), url), Is.Null);

    [Test]
    public void PlantUmlInvokesSandboxedLocalJavaWithoutShell()
    {
        var jar = Path.GetFullPath("path with spaces/plantuml.jar");
        var start = new PlantUmlPreview(jar, "java").CreateStartInfo();
        Assert.Multiple(() =>
        {
            Assert.That(start.UseShellExecute, Is.False);
            Assert.That(start.RedirectStandardInput && start.RedirectStandardOutput, Is.True);
            Assert.That(start.Environment["PLANTUML_SECURITY_PROFILE"], Is.EqualTo("SANDBOX"));
            Assert.That(start.ArgumentList, Does.Contain(jar).And.Contain("-tsvg").And.Contain("-pipe"));
        });
    }

    [TestCase(0)]
    [TestCase(PlantUmlPreview.MaximumSourceChars + 1)]
    public async Task PlantUmlRejectsEmptyOrOversizeInput(int length) =>
        Assert.That(await new PlantUmlPreview("not-installed.jar", "not-installed").RenderAsync(new string('a', length), default),
            Is.Null);

    [Test]
    public async Task PlantUmlUnavailableExecutableReturnsSourceFallbackSignal()
    {
        var renderer = new PlantUmlPreview("not-installed.jar", Path.GetFullPath("nonexistent-java-executable"));
        Assert.That(await renderer.RenderAsync("@startuml\nA -> B\n@enduml", default), Is.Null);
    }
}
