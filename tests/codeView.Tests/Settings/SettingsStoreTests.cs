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
