using System.Text;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Search;

namespace CodeView.Tests.Search;

internal sealed class WorkspaceSearchTests
{
    private string _root = null!;
    private readonly List<(WorkspaceSearchSession Session, CancellationTokenSource Lifetime)> _runs = new();

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "codeview-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var (session, lifetime) in _runs)
        {
            lifetime.Cancel();
            try { await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch (SearchWorkerException) { }
            while (session.Events.TryRead(out _)) { }
            try { await session.Events.Completion; } catch (SearchWorkerException) { }
            lifetime.Dispose();
        }
        _runs.Clear();
        Directory.Delete(_root, true);
    }

    private SourcePath Put(string name, string text, Encoding? encoding = null)
    {
        var path = SourcePath.Create(Path.Combine(_root, name), _root);
        File.WriteAllText(path.FullPath, text, encoding ?? new UTF8Encoding(false, true));
        return path;
    }

    private WorkspaceSearchSession Start(TestSource? source = null, string? query = "foo", bool regex = false,
        EncodingChoice encoding = EncodingChoice.Auto, bool cancelled = false)
    {
        source ??= new();
        var lifetime = new CancellationTokenSource();
        if (cancelled) lifetime.Cancel();
        var request = new WorkspaceSearchRequest(SourcePath.Create(_root, _root), new ExclusionPolicy(["skip"]),
            encoding, query, new(UseRegex: regex), 11, 22, 33);
        var session = new TextSearch(new(source), new(source)).StartWorkspace(request, lifetime.Token);
        _runs.Add((session, lifetime));
        return session;
    }

    private static async Task<List<SearchEvent<SearchHit>>> Drain(WorkspaceSearchSession session)
    {
        var events = new List<SearchEvent<SearchHit>>();
        async Task Read()
        {
            await foreach (var item in session.Events.ReadAllAsync()) events.Add(item);
        }
        await Read().WaitAsync(TimeSpan.FromSeconds(5));
        return events;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MultipleFilesReturnOriginalPositionsAndOrderedCommit(bool regex)
    {
        var first = Put("one.cs", "😀 FOO\r\nfoo");
        var second = Put("two.cs", "no\rfoo");
        var source = new TestSource { Entries = [Entry(first), Entry(second)] };
        var session = Start(source, regex: regex);
        var events = await Drain(session);
        var end = await session.Completion;
        var hits = events.OfType<SearchEvent<SearchHit>.Hits>().SelectMany(e => e.Items).ToArray();
        Assert.That(hits.Select(h => h.Range.Start), Is.EqualTo(new[] { new TextPosition(1, 4), new(2, 1), new(2, 1) }));
        Assert.That(hits.Select(h => h.Range.Offset), Is.EqualTo(new long[] { 3, 8, 3 }));
        foreach (var hit in hits)
        {
            Assert.That(hit.Snippet.Text.Substring(hit.Snippet.MatchStart, hit.Snippet.MatchLength), Is.EqualTo(hit == hits[0] ? "FOO" : "foo"));
            var commit = events.FindIndex(e => e is SearchEvent<SearchHit>.FileCommitted c && c.FileScanId == hit.FileScanId);
            var batch = events.FindIndex(e => e is SearchEvent<SearchHit>.Hits h && h.Items.Contains(hit));
            Assert.That(commit, Is.GreaterThan(batch));
        }
        Assert.That(end.Reason, Is.EqualTo(SearchEndReason.Completed));
        Assert.That(end.IsPartial, Is.False);
        Assert.That(end.GeneratedHits, Is.EqualTo(3));
        Assert.That(end.ReturnedHits, Is.EqualTo(3));
        Assert.That(end.CommittedHits, Is.EqualTo(3));
        Assert.That(end.UncommittedFileScanId, Is.Null);
        Assert.That(end.Progress.VisitedFiles, Is.EqualTo(2));
        Assert.That(end.Progress.CommittedFiles, Is.EqualTo(2));
        Assert.That(end.Progress.ReadSourceBytes, Is.EqualTo(new FileInfo(first.FullPath).Length + new FileInfo(second.FullPath).Length));
        Assert.That(session.LatestProgress, Is.EqualTo(end.Progress));
        if (regex) Assert.That(end.Progress.EvaluatedRegexLines, Is.EqualTo(4));
        Assert.That(source.Streams.All(stream => !stream.CanRead), Is.True);
    }

    [Test]
    public async Task ChangedFileInvalidatesEarlierHitsAndNextFileStillCommits()
    {
        var first = Put("changed.cs", "foo");
        var second = Put("next.cs", "foo");
        var source = new TestSource { Entries = [Entry(first), Entry(second)] };
        source.Stamp = (path, count, stamp) => path == first && count == 3 ? stamp with { LengthBytes = stamp.LengthBytes + 1 } : stamp;
        var session = Start(source);
        var events = await Drain(session);
        var end = await session.Completion;
        Assert.That(events[0], Is.TypeOf<SearchEvent<SearchHit>.Hits>());
        Assert.That(events[1], Is.EqualTo(new SearchEvent<SearchHit>.FileInvalidated(1, new(ErrorCode.ChangedDuringRead, first.DisplayPath))));
        Assert.That(events.Last(), Is.EqualTo(new SearchEvent<SearchHit>.FileCommitted(2)));
        Assert.That(end.GeneratedHits, Is.EqualTo(2));
        Assert.That(end.CommittedHits, Is.EqualTo(1));
        Assert.That(end.Progress.FailedEntries, Is.EqualTo(1));
        Assert.That(end.IsPartial, Is.True);
        Assert.That(end.ErrorCounts[ErrorCode.ChangedDuringRead], Is.EqualTo(1));
    }

    [Test]
    public async Task RootFailureIsFatalButChildFailureIsPartialAndContinues()
    {
        var path = Put("ok.cs", "foo");
        var source = new TestSource { Entries = [new(SourcePath.Create(Path.Combine(_root, "bad"), _root), "bad", SourceEntryKind.Directory, false), Entry(path)] };
        source.Enumerate = requested => requested.FullPath == _root ? source.Entries : throw new UnauthorizedAccessException();
        var session = Start(source);
        var events = await Drain(session);
        var end = await session.Completion;
        Assert.That(end.Reason, Is.EqualTo(SearchEndReason.Completed));
        Assert.That(end.IsPartial, Is.True);
        Assert.That(end.CommittedHits, Is.EqualTo(1));
        Assert.That(events.Last(), Is.TypeOf<SearchEvent<SearchHit>.FileCommitted>());
        var rootFailure = Start(new TestSource { Enumerate = _ => throw new UnauthorizedAccessException() });
        Assert.That(await Drain(rootFailure), Is.Empty);
        var rootEnd = await rootFailure.Completion;
        Assert.That(rootEnd.Reason, Is.EqualTo(SearchEndReason.Failed));
        Assert.That(rootEnd.Error!.Code, Is.EqualTo(ErrorCode.AccessDenied));
    }

    [Test]
    public async Task ExclusionsLinksBinaryAndInvalidEncodingAreDistinguished()
    {
        Directory.CreateDirectory(Path.Combine(_root, "skip"));
        File.WriteAllText(Path.Combine(_root, "skip", "hidden.cs"), "foo");
        var target = Put("target.cs", "foo");
        File.CreateSymbolicLink(Path.Combine(_root, "link.cs"), target.FullPath);
        File.WriteAllBytes(Path.Combine(_root, "binary.bin"), [137, 80, 78, 71, 13, 10, 26, 10]);
        File.WriteAllBytes(Path.Combine(_root, "bad.txt"), [0x61, 0xFF]);
        var session = Start(encoding: EncodingChoice.Utf8);
        await Drain(session);
        var end = await session.Completion;
        Assert.That(end.CommittedHits, Is.EqualTo(1));
        Assert.That(end.Progress.ExcludedEntries, Is.EqualTo(3));
        Assert.That(end.Progress.FailedEntries, Is.EqualTo(1));
        Assert.That(end.ErrorCounts[ErrorCode.DecodeFailed], Is.EqualTo(1));
    }

    [Test]
    public async Task ErrorSamplesAreBoundedWithoutLosingCounts()
    {
        var paths = Enumerable.Range(0, 45).Select(i => Entry(SourcePath.Create(Path.Combine(_root, i + ".cs"), _root))).ToArray();
        var session = Start(new TestSource { Entries = paths, OpenError = ErrorCode.AccessDenied });
        Assert.That(await Drain(session), Is.Empty);
        var end = await session.Completion;
        Assert.That(end.ErrorSamples.Count, Is.EqualTo(20));
        Assert.That(end.ErrorCounts[ErrorCode.AccessDenied], Is.EqualTo(45));
        Assert.That(end.Progress.FailedEntries, Is.EqualTo(45));
    }

    [TestCase(9999, false)]
    [TestCase(10000, true)]
    [TestCase(10001, true)]
    public async Task GeneratedLimitStopsWithoutCommittingEvenAtExactTotal(int count, bool limited)
    {
        Put("many.cs", string.Concat(Enumerable.Repeat("foo ", count)));
        var session = Start();
        var events = await Drain(session);
        var end = await session.Completion;
        Assert.That(end.GeneratedHits, Is.EqualTo(Math.Min(count, 10000)));
        Assert.That(end.ReturnedHits, Is.EqualTo(end.GeneratedHits));
        Assert.That(end.CommittedHits, Is.EqualTo(limited ? 0 : count));
        Assert.That(end.Reason, Is.EqualTo(limited ? SearchEndReason.LimitReached : SearchEndReason.Completed));
        Assert.That(end.UncommittedFileScanId, limited ? Is.EqualTo(1) : Is.Null);
        Assert.That(events.OfType<SearchEvent<SearchHit>.Hits>().All(batch => batch.Items.Count <= 128), Is.True);
        Assert.That(events.OfType<SearchEvent<SearchHit>.FileCommitted>().Count(), Is.EqualTo(limited ? 0 : 1));
    }

    [Test]
    public async Task InvalidatedResultsDoNotRefundGeneratedBudget()
    {
        var first = Put("changed.cs", string.Concat(Enumerable.Repeat("foo ", 5000)));
        var next = Put("next.cs", string.Concat(Enumerable.Repeat("foo ", 5001)));
        var source = new TestSource { Entries = [Entry(first), Entry(next)] };
        source.Stamp = (path, count, stamp) => path == first && count == 3 ? stamp with { LengthBytes = stamp.LengthBytes + 1 } : stamp;
        var session = Start(source);
        var events = await Drain(session);
        var end = await session.Completion;
        Assert.That(end.GeneratedHits, Is.EqualTo(10000));
        Assert.That(end.Reason, Is.EqualTo(SearchEndReason.LimitReached));
        Assert.That(end.CommittedHits, Is.Zero);
        Assert.That(end.UncommittedFileScanId, Is.EqualTo(2));
        Assert.That(events.OfType<SearchEvent<SearchHit>.FileInvalidated>().Single().FileScanId, Is.EqualTo(1));
    }

    [Test]
    public async Task FullQueueCanBeCancelledWithoutDrainingOrWritingTerminalEvent()
    {
        Put("many.cs", string.Concat(Enumerable.Repeat("foo ", 20000)));
        var source = new TestSource();
        var session = Start(source);
        async Task WaitForFull()
        {
            while (session.Events.Count != 8) await Task.Delay(1);
        }
        await WaitForFull().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(session.Completion.IsCompleted, Is.False);
        _runs.Last().Lifetime.Cancel();
        var end = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(end.Reason, Is.EqualTo(SearchEndReason.Cancelled));
        Assert.That(end.UncommittedFileScanId, Is.EqualTo(1));
        Assert.That(session.Events.Count, Is.EqualTo(8));
        Assert.That(source.Streams.Single().CanRead, Is.False);
        var events = await Drain(session);
        Assert.That(events.OfType<SearchEvent<SearchHit>.Hits>().Sum(batch => batch.Items.Count), Is.EqualTo(end.ReturnedHits));
        Assert.That(end.GeneratedHits, Is.GreaterThanOrEqualTo(end.ReturnedHits));
    }

    [Test]
    public async Task WorkerCompletionDoesNotMeanQueuedEventsWereDrained()
    {
        Put("one.cs", "foo");
        var session = Start();
        var end = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(end.Reason, Is.EqualTo(SearchEndReason.Completed));
        Assert.That(session.Events.Count, Is.EqualTo(2));
        Assert.That(session.Events.Completion.IsCompleted, Is.False);
        await Drain(session);
        await session.Events.Completion;
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("foo\n")]
    public async Task InvalidQueryDoesNotEnumerateOrOpenSource(string? query)
    {
        var source = new TestSource { Enumerate = _ => throw new AssertionException("条件エラーで走査した") };
        var session = Start(source, query);
        Assert.That(await Drain(session), Is.Empty);
        var end = await session.Completion;
        Assert.That(end.Reason, Is.EqualTo(SearchEndReason.Failed));
        Assert.That(end.Error!.Code, Is.EqualTo(ErrorCode.InvalidPattern));
        Assert.That(end.Progress.VisitedFiles, Is.Zero);
    }

    [Test]
    public async Task CancelledBeforeStartStillCompletesWriter()
    {
        var session = Start(new TestSource { Enumerate = _ => throw new AssertionException("取消後の走査") }, cancelled: true);
        Assert.That(await Drain(session), Is.Empty);
        Assert.That((await session.Completion).Reason, Is.EqualTo(SearchEndReason.Cancelled));
        Assert.That(session.Terminal, Is.Not.Null);
    }

    [Test]
    public async Task InvalidRegexDoesNotStartTraversal()
    {
        var source = new TestSource { Enumerate = _ => throw new AssertionException("不正な式で走査した") };
        var session = Start(source, "[", regex: true);
        Assert.That(await Drain(session), Is.Empty);
        var end = await session.Completion;
        Assert.That(end.Error!.Code, Is.EqualTo(ErrorCode.InvalidPattern));
        Assert.That(end.Reason, Is.EqualTo(SearchEndReason.Failed));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EmptyRootCompletesWithoutInventingResults(bool regex)
    {
        var session = Start(regex: regex);
        Assert.That(await Drain(session), Is.Empty);
        var end = await session.Completion;
        Assert.That(end.IsPartial, Is.False);
        Assert.That(end.Progress, Is.EqualTo(new SearchProgress()));
        Assert.That(end.GeneratedHits, Is.Zero);
        Assert.That(end.UncommittedFileScanId, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UnexpectedOrUnrelatedCancellationFaultCarriesUncommittedId(bool cancellation)
    {
        var path = Put("one.cs", "foo");
        var source = new TestSource { Entries = [Entry(path)] };
        source.Stamp = (_, count, stamp) => count == 3
            ? throw (cancellation ? new OperationCanceledException() : new InvalidOperationException("injected")) : stamp;
        var session = Start(source);
        SearchWorkerException? failure = null;
        try { await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (SearchWorkerException error) { failure = error; }
        Assert.That(failure, Is.Not.Null);
        Assert.That(failure!.Completion, Is.SameAs(session.Terminal));
        Assert.That(failure.Completion.Reason, Is.EqualTo(SearchEndReason.Failed));
        Assert.That(failure.Completion.Error!.Code, Is.EqualTo(ErrorCode.Unexpected));
        Assert.That(failure.Completion.UncommittedFileScanId, Is.EqualTo(1));
        Assert.That(failure.Completion.ReturnedHits, Is.EqualTo(1));
        Assert.That(source.Streams.Single().CanRead, Is.False);
    }

    [Test]
    public async Task UnrelatedCancellationIsNotHiddenByConcurrentRequestCancellation()
    {
        var path = Put("one.cs", "foo");
        var source = new TestSource { Entries = [Entry(path)] };
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        source.Stamp = (_, count, stamp) =>
        {
            if (count != 3) return stamp;
            reached.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            throw new OperationCanceledException(); // Not the request's token.
        };
        var session = Start(source);
        try
        {
            Assert.That(await Task.Run(() => reached.Wait(TimeSpan.FromSeconds(5))), Is.True);
            _runs.Last().Lifetime.Cancel();
        }
        finally { release.Set(); }
        SearchWorkerException? failure = null;
        try { await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (SearchWorkerException error) { failure = error; }
        Assert.That(failure, Is.Not.Null);
        Assert.That(failure!.Completion.Reason, Is.EqualTo(SearchEndReason.Failed));
        Assert.That(failure.Completion.UncommittedFileScanId, Is.EqualTo(1));
    }

    [Test]
    public async Task RegexSkippedLinesAndScalarMatchesRemainPartialAfterFileCommit()
    {
        Put("lines.cs", new string('a', SearchLimits.RegexLineChars + 1) + "\n😀\nfoo");
        var session = Start(query: ".", regex: true);
        var events = await Drain(session);
        var end = await session.Completion;
        Assert.That(end.Reason, Is.EqualTo(SearchEndReason.Completed));
        Assert.That(end.IsPartial, Is.True);
        Assert.That(end.Progress.SkippedLongLines, Is.EqualTo(1));
        Assert.That(end.Progress.SkippedScalarMatches, Is.EqualTo(2));
        Assert.That(end.Progress.EvaluatedRegexLines, Is.EqualTo(2));
        Assert.That(events.Last(), Is.TypeOf<SearchEvent<SearchHit>.FileCommitted>());
        Assert.That(end.CommittedHits, Is.EqualTo(3));
    }

    [Test]
    public async Task Cp932IsValidatedThenDecodedWithoutChangingOriginalCoordinates()
    {
        Put("japanese.cs", "日本語\r\nfoo", Encoding.GetEncoding(932));
        var session = Start();
        var events = await Drain(session);
        var hit = events.OfType<SearchEvent<SearchHit>.Hits>().Single().Items.Single();
        Assert.That(hit.Range, Is.EqualTo(new TextRange(5, 3, new(2, 1))));
        Assert.That((await session.Completion).IsPartial, Is.False);
    }

    [Test]
    public async Task RegexTimeoutStopsSessionAndLeavesEarlierMatchesUncommitted()
    {
        Put("timeout.cs", "foo\n" + new string('a', 30000) + "!");
        var session = Start(query: "^foo$|^(a+)+$", regex: true);
        var events = await Drain(session);
        var end = await session.Completion;
        Assert.That(end.Reason, Is.EqualTo(SearchEndReason.Failed));
        Assert.That(end.Error!.Code, Is.EqualTo(ErrorCode.RegexTimedOut));
        Assert.That(end.UncommittedFileScanId, Is.EqualTo(1));
        Assert.That(end.GeneratedHits, Is.EqualTo(1));
        Assert.That(end.CommittedHits, Is.Zero);
        Assert.That(events.OfType<SearchEvent<SearchHit>.FileCommitted>(), Is.Empty);
        Assert.That(end.Progress.EvaluatedRegexLines, Is.EqualTo(1));
    }

    [Test]
    public async Task ReadFailureInvalidatesOnlyThatFileAndReleasesItsStream()
    {
        var path = Put("read-failure.cs", "foo" + new string(' ', 100000));
        var stream = new FailingStream(path.FullPath);
        var source = new TestSource { Entries = [Entry(path)], OpenStream = _ => stream };
        source.Stamp = (_, count, stamp) => { if (count == 2) stream.FailAfterNextRead = true; return stamp; };
        var session = Start(source);
        var events = await Drain(session);
        var end = await session.Completion;
        Assert.That(end.Reason, Is.EqualTo(SearchEndReason.Completed));
        Assert.That(end.IsPartial, Is.True);
        Assert.That(end.GeneratedHits, Is.EqualTo(1));
        Assert.That(end.CommittedHits, Is.Zero);
        Assert.That(events.Last(), Is.EqualTo(new SearchEvent<SearchHit>.FileInvalidated(1, new(ErrorCode.IoFailure, path.DisplayPath))));
        Assert.That(end.UncommittedFileScanId, Is.Null);
        Assert.That(stream.CanRead, Is.False);
    }

    private static SourceEntry Entry(SourcePath path) => new(path, Path.GetFileName(path.FullPath), SourceEntryKind.FileCandidate, false);

    private sealed class TestSource : ISourceAccess
    {
        private readonly SourceAccess _inner = new();
        private readonly Dictionary<SourcePath, int> _stamps = new();
        public IReadOnlyList<SourceEntry>? Entries { get; init; }
        public Func<SourcePath, IEnumerable<SourceEntry>>? Enumerate { get; set; }
        public Func<SourcePath, int, SourceStamp, SourceStamp>? Stamp { get; set; }
        public ErrorCode? OpenError { get; init; }
        public Func<SourcePath, FileStream>? OpenStream { get; init; }
        public List<FileStream> Streams { get; } = new();
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct)
        {
            if (OpenError is { } code) return OperationResult<FileStream>.Failure(new(code, path.DisplayPath));
            var result = OpenStream is null ? _inner.OpenRead(path, ct) : OperationResult<FileStream>.Success(OpenStream(path));
            if (result.Status == OperationStatus.Success) Streams.Add(result.Value);
            return result;
        }
        public SourceStamp GetStamp(SourcePath path)
        {
            var count = _stamps[path] = _stamps.GetValueOrDefault(path) + 1;
            var stamp = _inner.GetStamp(path);
            return Stamp?.Invoke(path, count, stamp) ?? stamp;
        }
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => _inner.ResolveDirectory(path, ct);
        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => _inner.InspectPath(path, ct);
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct)
            => Enumerate?.Invoke(path) ?? Entries ?? _inner.EnumerateChildren(path, ct);
    }

    private sealed class FailingStream(string path) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
    {
        private bool _fail;
        public bool FailAfterNextRead { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_fail) throw new IOException("injected");
            var result = base.Read(buffer, offset, count);
            if (FailAfterNextRead) _fail = true;
            return result;
        }
    }
}
