using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class StickyScrollSettingsTests
{
    [Test]
    public void PersistsLatestToggleAcrossRestartAndPreservesExclusionChanges() => SingleThreadTestContext.Run(async () =>
    {
        using var fixture = new SettingsFixture();
        var view = fixture.CreateWindow();
        await view.InitializeAsync([]);
        Assert.That(view.StickyScrollEnabled, Is.True);
        Assert.That(File.Exists(fixture.Locations.UserFile), Is.False);

        var exclusions = view.SaveExcludedNamesAsync(["custom"]);
        var firstToggle = view.ToggleStickyScrollAsync();
        Assert.That(view.StickyScrollEnabled, Is.False);
        _ = view.ToggleStickyScrollAsync();
        var latestToggle = view.ToggleStickyScrollAsync();
        await Task.WhenAll(exclusions, firstToggle, latestToggle);
        await view.SaveExcludedNamesAsync(["retained"]);

        var restarted = fixture.CreateWindow();
        await restarted.InitializeAsync([]);
        Assert.That(restarted.StickyScrollEnabled, Is.False);
        Assert.That(restarted.EditableSettings?.ExcludedDirectoryNames, Is.EqualTo(new[] { "retained" }));
        await restarted.ToggleStickyScrollAsync();

        var reenabled = fixture.CreateWindow();
        await reenabled.InitializeAsync([]);
        Assert.That(reenabled.StickyScrollEnabled, Is.True);
        Assert.That(reenabled.EditableSettings?.ExcludedDirectoryNames, Is.EqualTo(new[] { "retained" }));
        Assert.That(await File.ReadAllTextAsync(fixture.Locations.UserFile), Does.Not.Contain("stickyScrollEnabled"));
    });

    [Test]
    public void KeepsSessionToggleWhenTheSettingsFileCannotBeSaved() => SingleThreadTestContext.Run(async () =>
    {
        using var fixture = new SettingsFixture();
        const string before = """{"excludedDirectoryNames":["custom"],"future":12}""";
        await File.WriteAllTextAsync(fixture.Locations.UserFile, before);
        var view = fixture.CreateWindow();
        await view.InitializeAsync([]);
        await view.ToggleStickyScrollAsync();
        Assert.That(view.StickyScrollEnabled, Is.False);
        Assert.That(view.Status, Does.Contain("保存できません"));
        Assert.That(await File.ReadAllTextAsync(fixture.Locations.UserFile), Is.EqualTo(before));
    });

    [TestCase("""{"stickyScrollEnabled":false}""", false, true)]
    [TestCase("""{"stickyScrollEnabled":true}""", true, true)]
    [TestCase("""{"stickyScrollEnabled":"false"}""", true, false)]
    [TestCase("""{"stickyScrollEnabled":true,"stickyScrollEnabled":false}""", true, false)]
    public void ReadsOnlyBooleanValues(string json, bool enabled, bool valid)
    {
        var parsed = SettingsValidator.Validate(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.That(parsed.IsValid, Is.EqualTo(valid));
        Assert.That(parsed.Snapshot.StickyScrollEnabled, Is.EqualTo(enabled));
    }

    private sealed class SettingsFixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("codeview-sticky-settings-");
        private readonly List<MainWindowViewModel> _windows = [];
        public SettingsLocations Locations => new(Path.Combine(_root.FullName, "portable.json"), Path.Combine(_root.FullName, "user.json"));

        public MainWindowViewModel CreateWindow()
        {
            var access = new SourceAccess();
            var operations = new AppOperations(new(access), new AvaloniaEditDocumentAdapter(), new(access));
            var view = new MainWindowViewModel(operations, new SettingsStore(Locations), () => Task.CompletedTask);
            _windows.Add(view);
            return view;
        }

        public void Dispose()
        {
            foreach (var window in _windows) window.Stop();
            _root.Delete(recursive: true);
        }
    }
}
