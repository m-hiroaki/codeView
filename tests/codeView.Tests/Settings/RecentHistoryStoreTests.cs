using CodeView.Settings;

namespace CodeView.Tests.Settings;

internal sealed class RecentHistoryStoreTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;
    private RecentHistoryStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("codeview-recent-");
        _path = Path.Combine(_root.FullName, "user", "codeview.recent.json");
        _store = new(_path);
    }

    [TearDown]
    public void TearDown() => _root.Delete(recursive: true);

    [Test]
    public async Task LoadsWithoutWritingAndPersistsNewestDistinctItemsAcrossInstances()
    {
        Assert.That(await _store.LoadAsync(default), Is.Empty);
        Assert.That(_root.GetFileSystemInfos(), Is.Empty);
        var items = Enumerable.Range(0, 8).Select(index => Entry(Path.Combine($"folder{index}", "Source.cs"), index)).ToArray();
        await _store.RecordAsync(items, [], "", default);
        var reopened = items[5] with { OpenedAt = items[7].OpenedAt.AddSeconds(1) };
        var folder = Entry("workspace", 12) with { IsFolder = true };
        await new RecentHistoryStore(_path).RecordAsync([reopened, folder], [], "", default);

        var persisted = await _store.LoadAsync(default);
        Assert.That(persisted, Is.EqualTo(new[] { folder, reopened, items[7], items[6], items[4], items[3] }));
        Assert.That(persisted.Skip(1).Select(item => item.Name), Is.All.EqualTo("Source.cs"));
        await _store.RemoveAsync(reopened.FullPath, [], "", default);
        Assert.That(await _store.LoadAsync(default), Does.Not.Contain(reopened));
        await _store.RemoveAsync(null, [], "", default);
        Assert.That(await new RecentHistoryStore(_path).LoadAsync(default), Is.Empty);
    }

    [Test]
    public async Task ConcurrentWritersMergeSuccessfully()
    {
        var first = Entry("one.cs", 1);
        var second = Entry("two.cs", 2);
        await Task.WhenAll(_store.RecordAsync([first], [], "", default),
            new RecentHistoryStore(_path).RecordAsync([second], [], "", default));
        Assert.That(await _store.LoadAsync(default), Is.EqualTo(new[] { second, first }));
    }

    [TestCase("invalid json")]
    [TestCase("{\"version\":2,\"items\":[]}")]
    [TestCase("{\"version\":1,\"items\":[{\"path\":\"\\uD800\",\"kind\":\"file\",\"openedAt\":\"2026-11-19T00:00:00Z\"}]}")]
    public async Task PreservesInvalidOrUnknownFiles(string input)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, input);
        var error = Assert.CatchAsync<Exception>(async () => await _store.RecordAsync([Entry("one", 1)], [], "", default));
        Assert.That(error, Is.InstanceOf<InvalidDataException>().Or.InstanceOf<System.Text.Json.JsonException>());
        Assert.That(await File.ReadAllTextAsync(_path), Is.EqualTo(input));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(_path)!, "*.tmp"), Is.Empty);
    }

    [Test]
    public async Task RejectsOversizedFilesAndPreservesOpenSourcesAndWorkspace()
    {
        Assert.ThrowsAsync<IOException>(async () => await _store.RecordAsync([Entry("one", 1)], [], _root.FullName, default));
        Assert.ThrowsAsync<IOException>(async () => await _store.RecordAsync([Entry("one", 1)], [_path], "", default));
        Assert.That(_root.GetFileSystemInfos(), Is.Empty);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllBytesAsync(_path, new byte[RecentHistoryStore.MaximumBytes + 1]);
        Assert.ThrowsAsync<InvalidDataException>(async () => await _store.LoadAsync(default));
        Assert.That(new FileInfo(_path).Length, Is.EqualTo(RecentHistoryStore.MaximumBytes + 1));
    }

    [Test]
    public async Task WritesRemainWithinSizeLimitForLongEscapedPaths()
    {
        var items = Enumerable.Range(0, 6).Select(index => Entry(new string('文', 3000) + index, index)).ToArray();
        var written = await _store.RecordAsync(items, [], "", default);
        Assert.That(written, Is.EqualTo(items.Reverse().Take(3)));
        Assert.That(await _store.LoadAsync(default), Is.EqualTo(written));
        Assert.That(new FileInfo(_path).Length, Is.LessThanOrEqualTo(RecentHistoryStore.MaximumBytes));
    }

    private RecentItem Entry(string path, int seconds) => new(Path.Combine(_root.FullName, path), false,
        DateTimeOffset.UnixEpoch.AddSeconds(seconds));
}
