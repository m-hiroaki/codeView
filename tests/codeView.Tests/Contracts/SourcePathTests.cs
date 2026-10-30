using CodeView.Contracts;

namespace CodeView.Tests.Contracts;

public sealed class SourcePathTests
{
    [Test]
    public void ResolvesRelativeComponentsButPreservesUnicodeAndDisplayPath()
    {
        var basis = Path.GetTempPath();
        const string original = "src/../か\u3099.cs";
        var path = SourcePath.Create(original, basis);
        Assert.That(path.FullPath, Is.EqualTo(Path.Combine(basis, "か\u3099.cs")));
        Assert.That(path.DisplayPath, Is.EqualTo(original));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("file\0.cs")]
    public void RejectsInvalidInput(string input)
        => Assert.Catch<ArgumentException>(() => SourcePath.Create(input, Path.GetTempPath()));

    [Test]
    public void RejectsRelativeBaseDirectory()
        => Assert.Throws<ArgumentException>(() => SourcePath.Create("a.cs", "relative"));
}
