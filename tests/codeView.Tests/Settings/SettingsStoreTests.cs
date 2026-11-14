using CodeView.Contracts;
using CodeView.Settings;
using System.Diagnostics;

namespace CodeView.Tests.Settings;

public sealed class SettingsStoreTests
{
    private string _root = null!;
    private SettingsLocations _locations = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "codeview-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _locations = new(Path.Combine(_root, "portable.json"), Path.Combine(_root, "user.json"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    [Test]
    public async Task NoFileUsesDefaultsAndDoesNotCreateSettings()
    {
        var result = await new SettingsStore(_locations).LoadAsync(CancellationToken.None);
        Assert.That(result.FilePath, Is.Null);
        Assert.That(result.Warning, Is.Null);
        Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty);
    }

    [Test]
    public async Task PortableFileHasPrecedenceAndInvalidPortableDoesNotFallBack()
    {
        await File.WriteAllTextAsync(_locations.UserFile, "{\"excludedDirectoryNames\":[]}");
        var store = new SettingsStore(_locations);
        Assert.That((await store.LoadAsync(CancellationToken.None)).Snapshot.ExcludedDirectoryNames, Is.Empty);
        await File.WriteAllTextAsync(_locations.PortableFile, "invalid");
        var invalid = await store.LoadAsync(CancellationToken.None);
        Assert.That(invalid.FilePath, Is.EqualTo(_locations.PortableFile));
        Assert.That(invalid.Warning!.Code, Is.EqualTo(ErrorCode.InvalidSettings));
        Assert.That(invalid.Snapshot, Is.SameAs(SettingsSnapshot.Default));
        Assert.That(await File.ReadAllTextAsync(_locations.PortableFile), Is.EqualTo("invalid"));
    }

    [Test]
    public async Task OversizedFilesAreRejected()
    {
        await File.WriteAllBytesAsync(_locations.PortableFile, new byte[SettingsValidator.MaxBytes + 1]);
        var result = await new SettingsStore(_locations).LoadAsync(CancellationToken.None);
        Assert.That(result.Warning!.Code, Is.EqualTo(ErrorCode.InvalidSettings));
        Assert.That(result.CanSave, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExplicitSaveUsesUserLocationOrReplacesLoadedPortableFile(bool portable)
    {
        if (portable) await File.WriteAllTextAsync(_locations.PortableFile, "{}");
        var store = new SettingsStore(_locations);
        var loaded = await store.LoadAsync(default);
        var first = await store.SaveAsync(loaded, new(["除外", "bin"]), [], default);
        Assert.That(first.Status, Is.EqualTo(OperationStatus.Success));
        Assert.That(first.Value.FilePath, Is.EqualTo(portable ? _locations.PortableFile : _locations.UserFile));
        var second = await store.SaveAsync(first.Value, new([]), [], default);
        Assert.That(second.Status, Is.EqualTo(OperationStatus.Success));
        Assert.That((await store.LoadAsync(default)).Snapshot.ExcludedDirectoryNames, Is.Empty);
        Assert.That(Directory.GetFiles(_root), Has.Length.EqualTo(1));
    }

    [TestCase("{\"schemaVersion\":2}", false)]
    [TestCase("{\"future\":true}", false)]
    [TestCase("{\"schemaVersion\":1}", true)]
    public async Task SavePreservesSettingsAfterExternalChangeOrUnknownSchema(string current, bool changeAfterLoad)
    {
        var store = new SettingsStore(_locations);
        await File.WriteAllTextAsync(_locations.PortableFile,
            changeAfterLoad ? "{\"schemaVersion\":1,\"excludedDirectoryNames\":[]}" : current);
        var loaded = await store.LoadAsync(default);
        if (changeAfterLoad) await File.WriteAllTextAsync(_locations.PortableFile, current);
        var result = await store.SaveAsync(loaded, new(["bin"]), [], default);
        Assert.That(result.Error?.Code, Is.EqualTo(changeAfterLoad ? ErrorCode.ChangedDuringRead : ErrorCode.InvalidSettings));
        Assert.That(await File.ReadAllTextAsync(_locations.PortableFile), Is.EqualTo(current));
        Assert.That(Directory.GetFiles(_root), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task RejectsOpenSettingsFileInvalidNamesAndNewPortableFile()
    {
        var store = new SettingsStore(_locations);
        var defaults = await store.LoadAsync(default);
        Assert.That((await store.SaveAsync(defaults, new(["bad/name"]), [], default)).Error?.Code,
            Is.EqualTo(ErrorCode.InvalidSettings));
        await File.WriteAllTextAsync(_locations.PortableFile, "{}");
        Assert.That((await store.SaveAsync(defaults, new(["bin"]), [], default)).Error?.Code,
            Is.EqualTo(ErrorCode.ChangedDuringRead));
        var loaded = await store.LoadAsync(default);
        Assert.That((await store.SaveAsync(loaded, new(["bin"]), [_locations.PortableFile], default)).Error?.Code,
            Is.EqualTo(ErrorCode.InvalidSettings));
        Assert.That(await File.ReadAllTextAsync(_locations.PortableFile), Is.EqualTo("{}"));
    }

    [Test]
    public async Task HardLinkedSettingsCannotBeReplaced()
    {
        await File.WriteAllTextAsync(_locations.PortableFile, "{}");
        var alias = Path.Combine(_root, "source.cs");
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo("fsutil.exe")
            { UseShellExecute = false, ArgumentList = { "hardlink", "create", alias, _locations.PortableFile } })!;
            await process.WaitForExitAsync();
            Assert.That(process.ExitCode, Is.Zero);
        }
        else
        {
            using var process = Process.Start(new ProcessStartInfo("/bin/ln")
            { UseShellExecute = false, ArgumentList = { _locations.PortableFile, alias } })!;
            await process.WaitForExitAsync();
            Assert.That(process.ExitCode, Is.Zero);
        }
        var store = new SettingsStore(_locations);
        var loaded = await store.LoadAsync(default);
        Assert.That((await store.SaveAsync(loaded, new(["bin"]), [], default)).Status, Is.EqualTo(OperationStatus.Failure));
        Assert.That(await File.ReadAllTextAsync(alias), Is.EqualTo("{}"));
        Assert.That(await File.ReadAllTextAsync(_locations.PortableFile), Is.EqualTo("{}"));
    }

    [Test]
    public async Task LinkedSourceAndLinkedSettingsDirectoryCannotBeWritten()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("設定のシンボリックリンクをDarwinで検証。");
        await File.WriteAllTextAsync(_locations.PortableFile, "{}");
        var alias = Path.Combine(_root, "source.cs");
        File.CreateSymbolicLink(alias, _locations.PortableFile);
        var store = new SettingsStore(_locations);
        var loaded = await store.LoadAsync(default);
        Assert.That((await store.SaveAsync(loaded, new(["bin"]), [alias], default)).Error?.Code,
            Is.EqualTo(ErrorCode.InvalidSettings));
        var link = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(link, _root);
        var linkedStore = new SettingsStore(new(Path.Combine(link, "portable.json"), _locations.UserFile));
        var linked = await linkedStore.LoadAsync(default);
        Assert.That((await linkedStore.SaveAsync(linked, new(["bin"]), [], default)).Status,
            Is.EqualTo(OperationStatus.Failure));
        Assert.That(await File.ReadAllTextAsync(_locations.PortableFile), Is.EqualTo("{}"));
    }

    [Test]
    public async Task ReplacementBlockedByWindowsShareModeLeavesFileAndCleansTemporary()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windowsの置換失敗を検証。");
        await File.WriteAllTextAsync(_locations.PortableFile, "{}");
        var store = new SettingsStore(_locations);
        var loaded = await store.LoadAsync(default);
        await using var held = new FileStream(_locations.PortableFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.That((await store.SaveAsync(loaded, new(["bin"]), [], default)).Status,
            Is.EqualTo(OperationStatus.Failure));
        Assert.That(await File.ReadAllTextAsync(_locations.PortableFile), Is.EqualTo("{}"));
        Assert.That(Directory.GetFiles(_root), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task TemporaryFileCannotBeCreatedInReadOnlyPortableDirectory()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Ignore("Darwinのディレクトリ書込権限で検証。");
            return;
        }
        var dir = Directory.CreateDirectory(Path.Combine(_root, "portable")).FullName;
        var path = Path.Combine(dir, "settings.json");
        await File.WriteAllTextAsync(path, "{}");
        var store = new SettingsStore(new(path, _locations.UserFile));
        var loaded = await store.LoadAsync(default);
        var previousMode = File.GetUnixFileMode(dir);
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.That((await store.SaveAsync(loaded, new(["skip"]), [], default)).Status, Is.EqualTo(OperationStatus.Failure));
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("{}"));
            Assert.That(Directory.GetFileSystemEntries(dir), Is.EqualTo(new[] { path }));
            Assert.That(File.Exists(_locations.UserFile), Is.False);
        }
        finally { File.SetUnixFileMode(dir, previousMode); }
    }

    [Test]
    public void CancelledLoadDoesNotReturnSuccess()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await new SettingsStore(_locations).LoadAsync(source.Token));
    }

    [Test]
    public async Task MacFifoSettingsCannotBlockStartup()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Ignore("DarwinのFIFO設定ファイルの検証。");
        using var command = Process.Start(new ProcessStartInfo("/usr/bin/mkfifo")
        {
            UseShellExecute = false,
            ArgumentList = { _locations.PortableFile }
        })!;
        await command.WaitForExitAsync();
        var result = await Task.Run(() => new SettingsStore(_locations).LoadAsync(CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(result.Warning!.Code, Is.EqualTo(ErrorCode.NotRegularFile));
        Assert.That(result.CanSave, Is.False);
    }
}
