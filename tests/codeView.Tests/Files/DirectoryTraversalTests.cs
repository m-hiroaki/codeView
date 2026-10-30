using System.Collections;
using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Tests.Files;

public sealed class DirectoryTraversalTests
{
    private static readonly SourcePath Root = SourcePath.Create(Path.Combine(Path.GetTempPath(), "codeview-logical-root"), Path.GetTempPath());
    private static SourcePath PathOf(string name) => SourcePath.Create(name, Root.FullPath);
    private static SourceEntry Entry(string name, SourceEntryKind kind = SourceEntryKind.FileCandidate, bool link = false) =>
        new(PathOf(name), Path.GetFileName(name), kind, link);

    [Test]
    public void WalkIsLazyDepthFirstAndDisposesOnEarlyStop()
    {
        var source = new FakeSource(path => path == Root
            ? new[] { Entry("child", SourceEntryKind.Directory), Entry("later.cs") }
            : new[] { Entry("child/first.cs"), Entry("child/second.cs") });
        var walk = new DirectoryReader(source).WalkFiles(Root, new([]), CancellationToken.None);
        Assert.That(source.Moves, Is.Zero);
        using (var iterator = walk.GetEnumerator())
        {
            Assert.That(source.Moves, Is.Zero);
            Assert.That(iterator.MoveNext(), Is.True);
            Assert.That(iterator.Current.Path, Is.EqualTo(PathOf("child/first.cs")));
            Assert.That(source.Active, Is.EqualTo(2));
            Assert.That(source.Moves, Is.EqualTo(2));
        }
        Assert.That(source.Active, Is.Zero);
        Assert.That(source.Disposed, Is.EqualTo(2));
    }

    [Test]
    public void ExcludedDirectoriesLinksAndSpecialEntriesAreNeverEntered()
    {
        var source = new FakeSource(_ => new[]
        {
            Entry("bin", SourceEntryKind.Directory), Entry("linked-dir", SourceEntryKind.Directory, true),
            Entry("linked.cs", link: true), Entry("special", SourceEntryKind.Other), Entry("kept.cs")
        });
        var events = new DirectoryReader(source).WalkFiles(Root, new(["bin"]), CancellationToken.None).ToArray();
        Assert.That(events.Select(e => e.Kind), Is.EqualTo(new[]
        {
            DirectoryWalkKind.SkippedDirectory, DirectoryWalkKind.SkippedLink, DirectoryWalkKind.SkippedLink,
            DirectoryWalkKind.SkippedSpecialEntry, DirectoryWalkKind.File
        }));
        Assert.That(source.Entered, Is.EqualTo(new[] { Root.FullPath }));
    }

    [Test]
    public void BranchFailureIsReportedAndOtherBranchesContinue()
    {
        var source = new FakeSource(path => path == Root
            ? new[] { Entry("denied", SourceEntryKind.Directory), Entry("kept.cs") }
            : throw new UnauthorizedAccessException());
        var events = new DirectoryReader(source).WalkFiles(Root, new([]), CancellationToken.None).ToArray();
        Assert.That(events[0].Kind, Is.EqualTo(DirectoryWalkKind.Failed));
        Assert.That(events[0].Error!.Code, Is.EqualTo(ErrorCode.AccessDenied));
        Assert.That(events[0].IsRoot, Is.False);
        Assert.That(events[1].Path, Is.EqualTo(PathOf("kept.cs")));
        Assert.That(source.Active, Is.Zero);
    }

    [Test]
    public async Task MidEnumerationFailurePreservesPartialTreeAndRootFailureIsExplicit()
    {
        var source = new FakeSource(_ => FailAfterFirst());
        var reader = new DirectoryReader(source);
        var listing = await reader.ReadChildrenAsync(Root, new([]), CancellationToken.None);
        Assert.That(listing.Entries.Single().Name, Is.EqualTo("first.cs"));
        Assert.That(listing.IsComplete, Is.False);
        Assert.That(listing.Error!.Code, Is.EqualTo(ErrorCode.IoFailure));
        var events = reader.WalkFiles(Root, new([]), CancellationToken.None).ToArray();
        Assert.That(events[0].Kind, Is.EqualTo(DirectoryWalkKind.File));
        Assert.That(events[1].Kind, Is.EqualTo(DirectoryWalkKind.Failed));
        Assert.That(events[1].IsRoot, Is.True);
        Assert.That(source.Active, Is.Zero);

        static IEnumerable<SourceEntry> FailAfterFirst()
        {
            yield return Entry("first.cs");
            throw new IOException("enumeration failed");
        }
    }

    [Test]
    public void CancellationAndUnexpectedFailureDisposeAllActiveFrames()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FakeSource(path => path == Root
            ? new[] { Entry("child", SourceEntryKind.Directory) } : new[] { Entry("child/file.cs") });
        using (var iterator = new DirectoryReader(source).WalkFiles(Root, new([]), cancellation.Token).GetEnumerator())
        {
            Assert.That(iterator.MoveNext(), Is.True);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => iterator.MoveNext());
        }
        Assert.That(source.Active, Is.Zero);
        Assert.That(source.Disposed, Is.EqualTo(2));

        var failed = new FakeSource(path => path == Root
            ? new[] { Entry("child", SourceEntryKind.Directory) } : throw new InvalidOperationException("bug"));
        Assert.Throws<InvalidOperationException>(() => new DirectoryReader(failed).WalkFiles(Root, new([]), CancellationToken.None).ToArray());
        Assert.That(failed.Active, Is.Zero);
    }

    [Test]
    public void DeepLogicalTreeUsesExplicitFramesAndNoRecursion()
    {
        const int depth = 10000;
        var source = new FakeSource(path =>
        {
            var current = path == Root ? 0 : int.Parse(Path.GetFileName(path.FullPath));
            return current == depth ? new[] { Entry("last.cs") }
                : new[] { Entry((current + 1).ToString(), SourceEntryKind.Directory) };
        });
        Assert.That(new DirectoryReader(source).WalkFiles(Root, new([]), CancellationToken.None).Single().Path,
            Is.EqualTo(PathOf("last.cs")));
        Assert.That(source.MaxActive, Is.EqualTo(depth + 1));
        Assert.That(source.Active, Is.Zero);
        Assert.That(source.Disposed, Is.EqualTo(depth + 1));
    }

    [Test]
    public void RootFailureAndCancellationDuringMoveDoNotBecomeSuccessfulFileNotifications()
    {
        var missing = new FakeSource(_ => throw new DirectoryNotFoundException());
        var failure = new DirectoryReader(missing).WalkFiles(Root, new([]), CancellationToken.None).Single();
        Assert.That(failure.Kind, Is.EqualTo(DirectoryWalkKind.Failed));
        Assert.That(failure.IsRoot, Is.True);
        Assert.That(failure.Error!.Code, Is.EqualTo(ErrorCode.NotFound));
        using var cancellation = new CancellationTokenSource();
        var cancelled = new FakeSource(_ => CancelDuringMove());
        Assert.Throws<OperationCanceledException>(() => new DirectoryReader(cancelled).WalkFiles(Root, new([]), cancellation.Token).ToArray());
        Assert.That(cancelled.Active, Is.Zero);
        Assert.That(cancelled.Disposed, Is.EqualTo(1));

        IEnumerable<SourceEntry> CancelDuringMove()
        {
            cancellation.Cancel();
            yield return Entry("late.cs");
        }
    }

    [Test]
    public async Task TreeNameOrderIsStableWithCaseTies()
    {
        var source = new FakeSource(_ => new[] { Entry("b"), Entry("a"), Entry("A"), Entry("z-dir", SourceEntryKind.Directory) });
        var listing = await new DirectoryReader(source).ReadChildrenAsync(Root, new([]), CancellationToken.None);
        Assert.That(listing.Entries.Select(e => e.Name), Is.EqualTo(new[] { "z-dir", "A", "a", "b" }));
    }

    private sealed class FakeSource(Func<SourcePath, IEnumerable<SourceEntry>> children) : ISourceAccess
    {
        public int Active { get; private set; }
        public int MaxActive { get; private set; }
        public int Moves { get; private set; }
        public int Disposed { get; private set; }
        public List<string> Entered { get; } = [];
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct) => throw new InvalidOperationException("No file bodies during enumeration.");
        public SourceStamp GetStamp(SourcePath path) => throw new InvalidOperationException();
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => OperationResult<SourcePath>.Success(path);
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct) => new TrackingEnumerable(this, path, children);

        private sealed class TrackingEnumerable(FakeSource owner, SourcePath path, Func<SourcePath, IEnumerable<SourceEntry>> children) : IEnumerable<SourceEntry>
        {
            public IEnumerator<SourceEntry> GetEnumerator()
            {
                owner.Entered.Add(path.FullPath);
                var inner = children(path).GetEnumerator();
                owner.Active++;
                owner.MaxActive = Math.Max(owner.MaxActive, owner.Active);
                return new TrackingEnumerator(owner, inner);
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private sealed class TrackingEnumerator(FakeSource owner, IEnumerator<SourceEntry> inner) : IEnumerator<SourceEntry>
        {
            private bool _disposed;
            public SourceEntry Current => inner.Current;
            object IEnumerator.Current => Current;
            public bool MoveNext() { owner.Moves++; return inner.MoveNext(); }
            public void Reset() => throw new NotSupportedException();
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                inner.Dispose();
                owner.Active--;
                owner.Disposed++;
            }
        }
    }
}
