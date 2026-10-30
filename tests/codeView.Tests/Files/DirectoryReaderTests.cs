using CodeView.Contracts;
using CodeView.Files;
using CodeView.Settings;

namespace CodeView.Tests.Files;

public sealed class DirectoryReaderTests
{
    private string _root = null!;
    private readonly SourceAccess _source = new();
    private static ExclusionPolicy DefaultPolicy() => new(SettingsSnapshot.Default.ExcludedDirectoryNames);
    private SourcePath PathOf(string relative = "") => SourcePath.Create(Path.Combine(_root, relative), _root);

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "codeview-directories-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    [Test]
    public async Task ReadsOnlyDirectChildrenWithDirectoriesFirstAndSharedExclusions()
    {
        Directory.CreateDirectory(Path.Combine(_root, "b-folder", "not-enumerated"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, "a-folder"));
        File.WriteAllText(Path.Combine(_root, "Z.cs"), "z");
        File.WriteAllText(Path.Combine(_root, "a.cs"), "a");
        File.WriteAllText(Path.Combine(_root, "obj"), "A file with an excluded directory name is retained.");
        var reader = new DirectoryReader(_source);
        var listing = await reader.ReadChildrenAsync(PathOf(), DefaultPolicy(), CancellationToken.None);
        Assert.That(listing.IsComplete, Is.True);
        Assert.That(listing.Entries.Select(e => e.Name), Is.EqualTo(new[] { "a-folder", "b-folder", "a.cs", "obj", "Z.cs" }));
        Assert.That(listing.Entries.Any(e => e.Name == "not-enumerated"), Is.False);
        Assert.That(listing.Entries, Is.InstanceOf<System.Collections.ObjectModel.ReadOnlyCollection<SourceEntry>>());
    }

    [Test]
    public async Task EmptyMissingAndFileRootsAreDistinct()
    {
        var reader = new DirectoryReader(_source);
        var empty = await reader.ReadChildrenAsync(PathOf(), DefaultPolicy(), CancellationToken.None);
        Assert.That(empty.IsComplete, Is.True);
        Assert.That(empty.Entries, Is.Empty);
        var missing = await reader.ReadChildrenAsync(PathOf("missing"), DefaultPolicy(), CancellationToken.None);
        Assert.That(missing.IsComplete, Is.False);
        Assert.That(missing.Error!.Code, Is.EqualTo(ErrorCode.NotFound));
        Assert.That((await reader.ResolveRootAsync(PathOf("missing"), CancellationToken.None)).Error!.Code, Is.EqualTo(ErrorCode.NotFound));
        File.WriteAllText(Path.Combine(_root, "file.cs"), "content");
        Assert.That((await reader.ResolveRootAsync(PathOf("file.cs"), CancellationToken.None)).Error!.Code, Is.EqualTo(ErrorCode.NotRegularFile));
    }

    [Test]
    public async Task LinksAreVisibleButNotWalkedAndExplicitRootLinkCanBeResolved()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "source.cs"), "text");
        Directory.CreateSymbolicLink(Path.Combine(_root, "folder-link"), real);
        Directory.CreateSymbolicLink(Path.Combine(real, "cycle"), _root);
        File.CreateSymbolicLink(Path.Combine(_root, "file-link.cs"), Path.Combine(real, "source.cs"));
        File.CreateSymbolicLink(Path.Combine(_root, "broken-link.cs"), Path.Combine(_root, "absent.cs"));
        var reader = new DirectoryReader(_source);
        var listing = await reader.ReadChildrenAsync(PathOf(), DefaultPolicy(), CancellationToken.None);
        Assert.That(listing.Entries.Where(e => e.IsLink).Select(e => e.Name),
            Is.EquivalentTo(new[] { "folder-link", "file-link.cs", "broken-link.cs" }));
        var walked = reader.WalkFiles(PathOf(), DefaultPolicy(), CancellationToken.None).ToArray();
        Assert.That(walked.Where(e => e.Kind == DirectoryWalkKind.File).Select(e => e.Path.FullPath),
            Is.EqualTo(new[] { Path.Combine(real, "source.cs") }));
        Assert.That(walked.Count(e => e.Kind == DirectoryWalkKind.SkippedLink), Is.EqualTo(4));
        var blocked = await reader.ReadChildrenAsync(PathOf("folder-link"), DefaultPolicy(), CancellationToken.None);
        Assert.That(blocked.IsComplete, Is.False);
        var resolved = await reader.ResolveRootAsync(PathOf("folder-link"), CancellationToken.None);
        Assert.That(resolved.Value.FullPath, Is.EqualTo(real));
        Assert.That(reader.WalkFiles(resolved.Value, DefaultPolicy(), CancellationToken.None)
            .Count(e => e.Kind == DirectoryWalkKind.File), Is.EqualTo(1));
    }

    [Test]
    public async Task BrokenAndCyclicRootLinksFailWithoutWalking()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "broken"), Path.Combine(_root, "missing"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "cycle-a"), Path.Combine(_root, "cycle-b"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "cycle-b"), Path.Combine(_root, "cycle-a"));
        var reader = new DirectoryReader(_source);
        Assert.That((await reader.ResolveRootAsync(PathOf("broken"), CancellationToken.None)).Error!.Code, Is.EqualTo(ErrorCode.BrokenLink));
        Assert.That((await reader.ResolveRootAsync(PathOf("cycle-a"), CancellationToken.None)).Error!.Code, Is.EqualTo(ErrorCode.BrokenLink));
    }

    [Test]
    public async Task MacPermissionFailureIsReportedAndSiblingFilesRemainWalkable()
    {
        if (OperatingSystem.IsMacOS() && Environment.UserName != "root")
        {
            var denied = Path.Combine(_root, "denied");
            Directory.CreateDirectory(denied);
            File.WriteAllText(Path.Combine(denied, "hidden.cs"), "not readable");
            File.WriteAllText(Path.Combine(_root, "kept.cs"), "readable");
            File.SetUnixFileMode(denied, UnixFileMode.None);
            try
            {
                var reader = new DirectoryReader(_source);
                var listing = await reader.ReadChildrenAsync(PathOf("denied"), DefaultPolicy(), CancellationToken.None);
                Assert.That(listing.IsComplete, Is.False);
                Assert.That(listing.Error!.Code, Is.EqualTo(ErrorCode.AccessDenied));
                var events = reader.WalkFiles(PathOf(), DefaultPolicy(), CancellationToken.None).ToArray();
                Assert.That(events.Count(e => e.Kind == DirectoryWalkKind.Failed), Is.EqualTo(1));
                Assert.That(events.Single(e => e.Kind == DirectoryWalkKind.File).Path.FullPath, Is.EqualTo(PathOf("kept.cs").FullPath));
            }
            finally { File.SetUnixFileMode(denied, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
        else Assert.Ignore("macOSの非root利用者で実際のアクセス拒否を確認する試験。");
    }

    [Test]
    public async Task PreCancelledWorkDoesNotReadDirectory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reader = new DirectoryReader(_source);
        Assert.That(async () => await reader.ReadChildrenAsync(PathOf(), DefaultPolicy(), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(async () => await reader.ResolveRootAsync(PathOf(), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.Throws<OperationCanceledException>(() => reader.WalkFiles(PathOf(), DefaultPolicy(), cancellation.Token).ToArray());
        await Task.CompletedTask;
    }

    [Test]
    public void ExclusionsAreCopiedAndCaseRulesAreExplicit()
    {
        var names = new[] { "bin", ".git" };
        var exact = new ExclusionPolicy(names);
        var insensitive = new ExclusionPolicy(names, caseSensitive: false);
        names[0] = "changed";
        Assert.That(exact.IsExcludedDirectory("bin"), Is.True);
        Assert.That(exact.IsExcludedDirectory("BIN"), Is.False);
        Assert.That(insensitive.IsExcludedDirectory("BIN"), Is.True);
        Assert.That(exact.IsExcludedDirectory("changed"), Is.False);
        Assert.That(exact.IsExcludedDirectory("binary"), Is.False);
        Assert.Throws<ArgumentException>(() => new ExclusionPolicy([""]));
        Assert.Throws<ArgumentNullException>(() => new ExclusionPolicy(null!));
    }
}
