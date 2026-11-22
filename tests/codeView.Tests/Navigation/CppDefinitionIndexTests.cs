using CodeView.Contracts;
using CodeView.Files;
using CodeView.Navigation;

namespace CodeView.Tests.Navigation;

internal sealed class CppDefinitionIndexTests
{
    [Test]
    public async Task RestoresWithoutWritingAndUpdatesOnlyChangedSources()
    {
        using var fixture = new Fixture();
        fixture.Write("a.c", "int old_name() {}");
        fixture.Write("b.hpp", "struct B { void member() {} };");
        fixture.Write("excluded/hidden.cpp", "void hidden() {}");
        fixture.Write("ignore.txt", "void ignored() {}");
        using (var index = fixture.Open())
        {
            await index.RestoreAsync();
            Assert.That(fixture.Cache.Exists, Is.False);
            Assert.That(index.TryGet("old_name", out _, out _, out _), Is.False);
            Assert.That(await index.UpdateAsync(false, null, fixture.Token), Is.EqualTo(new CppIndexUpdate(2, 2, 3, true)));
            Assert.That(index.TryGet("hidden", out _, out _, out _), Is.False);
        }
        var saved = File.ReadAllBytes(fixture.Snapshot);
        using var reopened = fixture.Open();
        await reopened.RestoreAsync();
        Assert.That(reopened.TryGet("member", out var hits, out var complete, out var generation), Is.True);
        Assert.That(hits.Single().Definition.Qualifier, Is.EqualTo("B"));
        Assert.That(complete && reopened.IsCurrent(generation), Is.True);
        Assert.That(File.ReadAllBytes(fixture.Snapshot), Is.EqualTo(saved));

        fixture.Write("a.c", "int replaced_name() {}");
        File.Move(fixture.Source("b.hpp"), fixture.Source("renamed.hpp"));
        fixture.Write("added.cpp", "void added() {}");
        await WaitFor(() => !reopened.IsCurrent(generation), fixture.Token);
        Assert.That(await reopened.UpdateAsync(false, null, fixture.Token), Is.EqualTo(new CppIndexUpdate(3, 3, 4, true)));
        fixture.Write("added.cpp", "void updated() {}");
        File.Delete(fixture.Source("a.c"));
        Assert.That(await reopened.UpdateAsync(false, null, fixture.Token), Is.EqualTo(new CppIndexUpdate(2, 1, 3, true)));
        Assert.That(reopened.TryGet("updated", out _, out _, out _), Is.True);
        Assert.That(reopened.TryGet("replaced_name", out _, out _, out _), Is.False);
        Assert.That(await reopened.UpdateAsync(true, null, fixture.Token), Is.EqualTo(new CppIndexUpdate(2, 2, 3, true)));
    }

    [Test]
    public async Task KeepsValidSnapshotWhenAnotherWriterHoldsTheLockOrTheOperationIsCancelled()
    {
        using var fixture = new Fixture();
        fixture.Write("a.cpp", "void function() {}");
        using var index = fixture.Open();
        await index.UpdateAsync(false, null, fixture.Token);
        var original = File.ReadAllBytes(fixture.Snapshot);
        using (fixture.Cache.BeginUpdate())
        using (var second = fixture.Open())
            Assert.ThrowsAsync<IOException>(async () => await second.UpdateAsync(true, null, fixture.Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.That(async () => await index.UpdateAsync(true, null, cancelled.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(File.ReadAllBytes(fixture.Snapshot), Is.EqualTo(original));
    }

    [Test]
    public async Task TreatsCorruptionAndUnverifiedChangesAsUnavailableAndRebuildsManually()
    {
        using var fixture = new Fixture();
        fixture.Write("a.c", "int name() {}");
        using (var index = fixture.Open()) await index.UpdateAsync(false, null, fixture.Token);
        fixture.Write("a.c", "int renamed() {}");
        using var changed = fixture.Open();
        await changed.RestoreAsync();
        Assert.That(changed.TryGet("name", out _, out _, out _), Is.False);
        changed.Dispose();

        File.AppendAllText(fixture.Snapshot, "trailing bytes");
        using var recovered = fixture.Open();
        await recovered.RestoreAsync();
        Assert.That(recovered.TryGet("name", out _, out _, out _), Is.False);
        Assert.That(await recovered.UpdateAsync(false, null, fixture.Token), Is.EqualTo(new CppIndexUpdate(1, 1, 1, true)));
        Assert.That(recovered.TryGet("renamed", out _, out _, out _), Is.True);
    }

    [Test]
    public async Task DoesNotPublishACompleteIndexWhenAFileIsOversizedOrMalformed()
    {
        using var fixture = new Fixture();
        fixture.Write("ok.c", "void ok() {}");
        fixture.Write("broken.hpp", "void provisional() {} void malformed() {");
        using (var stream = File.Create(fixture.Source("large.cpp"))) stream.SetLength(CppDefinitionCache.MaximumFileBytes + 1);
        using var index = fixture.Open();
        Assert.That(await index.UpdateAsync(false, null, fixture.Token), Is.EqualTo(new CppIndexUpdate(3, 2, 1, false)));
        Assert.That(index.TryGet("ok", out _, out var complete, out _), Is.True);
        Assert.That(complete, Is.False);
        Assert.That(index.TryGet("provisional", out _, out _, out _), Is.False);
        Assert.That(fixture.Cache.Read(fixture.Token)!.Files["large.cpp"].Error, Is.EqualTo(ErrorCode.UnsupportedSize));
    }

    [Test]
    public async Task AFolderSwitchDuringCollectionKeepsThePreviouslyCommittedCache()
    {
        using var fixture = new Fixture();
        fixture.Write("a.c", "int initial() {}");
        using (var initial = fixture.Open()) await initial.UpdateAsync(false, null, fixture.Token);
        var saved = File.ReadAllBytes(fixture.Snapshot);
        for (var i = 0; i < 70; i++) fixture.Write($"generated_{i}.cpp", $"void method_{i}() {{}}");
        using var endingWorkspace = new CancellationTokenSource();
        using var index = fixture.Open(endingWorkspace.Token);
        Assert.That(async () => await index.UpdateAsync(true, _ => endingWorkspace.Cancel(), fixture.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(File.ReadAllBytes(fixture.Snapshot), Is.EqualTo(saved));
        Assert.That(index.TryGet("method_0", out _, out _, out _), Is.False);
    }

    [Test]
    public async Task PrunesOlderCacheEntriesWhenCommittedDiskSpaceIsFull()
    {
        using var fixture = new Fixture();
        fixture.Write("a.cpp", "void function() {}");
        var old = Enumerable.Range(0, 4).Select(i => fixture.WriteOldCache(i)).ToArray();
        using var index = fixture.Open();
        await index.UpdateAsync(false, null, fixture.Token);
        Assert.That(File.Exists(old[0]), Is.False);
        Assert.That(old.Skip(1).All(File.Exists), Is.True);
        Assert.That(index.TryGet("function", out _, out _, out _), Is.True);
    }

    private static async Task WaitFor(Func<bool> ready, CancellationToken ct)
    {
        while (!ready()) await Task.Delay(25, ct);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("codeview-index-");
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(10));
        private readonly SourceAccess _access = new();
        private readonly SourcePath _root;
        private readonly string _cache;
        internal Fixture()
        {
            _root = SourcePath.Create(Directory.CreateDirectory(Path.Combine(_temporary.FullName, "source")).FullName,
                _temporary.FullName);
            _cache = Path.Combine(_temporary.FullName, "cache");
            Cache = new(_cache, _root, EncodingChoice.Auto, ["excluded"]);
        }

        internal CppDefinitionCache Cache { get; }
        internal string Snapshot => Directory.GetFiles(_cache, "definitions.jsonl", SearchOption.AllDirectories).Single();
        internal CancellationToken Token => _lifetime.Token;
        internal CppDefinitionIndex Open(CancellationToken? lifetime = null) => new(_root, new(["excluded"]), EncodingChoice.Auto,
            new(_access), new(_access), Cache, lifetime ?? Token);
        internal string Source(string name) => Path.Combine(_root.FullPath, name);
        internal void Write(string path, string text)
        {
            var target = Source(path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text);
        }

        internal string WriteOldCache(int rank)
        {
            var directory = Directory.CreateDirectory(Path.Combine(_cache, rank.ToString("X64")));
            var path = Path.Combine(directory.FullName, "definitions.jsonl");
            using (var stream = File.Create(path)) stream.SetLength(CppDefinitionCache.MaximumCacheBytes);
            File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddDays(rank + 1));
            return path;
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            _temporary.Delete(recursive: true);
        }
    }
}
