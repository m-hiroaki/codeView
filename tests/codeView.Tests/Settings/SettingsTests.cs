using System.Text;
using CodeView.Settings;

namespace CodeView.Tests.Settings;

public sealed class SettingsTests
{
    private static SettingsValidation Validate(string json) => SettingsValidator.Validate(Encoding.UTF8.GetBytes(json));

    [TestCase("{}")]
    [TestCase("{\"schemaVersion\":1}")]
    public void MissingNamesUseDefaults(string json)
    {
        var result = Validate(json);
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Snapshot.ExcludedDirectoryNames, Is.EqualTo(new[] { ".git", "bin", "obj", "node_modules" }));
    }

    [Test]
    public void ExplicitEmptyNamesAndExactDuplicatesArePreservedCorrectly()
    {
        Assert.That(Validate("{\"excludedDirectoryNames\":[]}").Snapshot.ExcludedDirectoryNames, Is.Empty);
        var result = Validate("{\"excludedDirectoryNames\":[\"bin\",\"bin\",\"Bin\",\"日本語\"]}");
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Snapshot.ExcludedDirectoryNames, Is.EqualTo(new[] { "bin", "Bin", "日本語" }));
    }

    [TestCase("{")]
    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("{\"schemaVersion\":2}")]
    [TestCase("{\"schemaVersion\":\"1\"}")]
    [TestCase("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [TestCase("{\"excludedDirectoryNames\":null}")]
    [TestCase("{\"excludedDirectoryNames\":[1]}")]
    [TestCase("{\"excludedDirectoryNames\":[\"../src\"]}")]
    [TestCase("{\"excludedDirectoryNames\":[\"*.tmp\"]}")]
    [TestCase("{\"excludedDirectoryNames\":[\"\"]}")]
    public void InvalidSettingsReturnDefaultsWithoutSavePermission(string json)
    {
        var result = Validate(json);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.CanSave, Is.False);
        Assert.That(result.Snapshot, Is.SameAs(SettingsSnapshot.Default));
    }

    [Test]
    public void UnknownPropertiesCanBeReadButNotResaved()
    {
        var result = Validate("{\"future\":42}");
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.CanSave, Is.False);
    }

    [Test]
    public void BoundsIncludeExactByteAndScalarLimits()
    {
        var exact = Encoding.UTF8.GetBytes("{}" + new string(' ', SettingsValidator.MaxBytes - 2));
        Assert.That(SettingsValidator.Validate(exact).IsValid, Is.True);
        Assert.That(SettingsValidator.Validate(exact.Concat(new byte[] { 32 }).ToArray()).IsValid, Is.False);
        var name = string.Concat(Enumerable.Repeat("😀", 255));
        Assert.That(Validate($"{{\"excludedDirectoryNames\":[\"{name}\"]}}").IsValid, Is.True);
        Assert.That(Validate($"{{\"excludedDirectoryNames\":[\"{name}a\"]}}").IsValid, Is.False);
        var names = Enumerable.Range(0, 256).Select(i => $"\"dir{i}\"");
        Assert.That(Validate("{\"excludedDirectoryNames\":[" + string.Join(',', names) + "]}").IsValid, Is.True);
        Assert.That(Validate("{\"excludedDirectoryNames\":[" + string.Join(',', names) + ",\"extra\"]}").IsValid, Is.False);
    }

    [Test]
    public void Utf8BomIsAcceptedAndInvalidUtf8IsRejected()
    {
        Assert.That(SettingsValidator.Validate(new byte[] { 0xef, 0xbb, 0xbf, 123, 125 }).IsValid, Is.True);
        Assert.That(SettingsValidator.Validate(new byte[] { 123, 34, 0xff, 34, 58, 49, 125 }).IsValid, Is.False);
    }

    [Test]
    public void SnapshotDoesNotShareMutableInput()
    {
        var names = new[] { "bin" };
        var settings = new SettingsSnapshot(names);
        names[0] = "changed";
        Assert.That(settings.ExcludedDirectoryNames[0], Is.EqualTo("bin"));
    }
}
