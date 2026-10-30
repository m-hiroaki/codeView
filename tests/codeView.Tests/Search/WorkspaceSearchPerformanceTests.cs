using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Search;
using CodeView.Tests.Performance;

namespace CodeView.Tests.Search;

internal sealed class WorkspaceSearchPerformanceTests
{
    [Test, Category("Performance")]
    public async Task MeasureStreamingWorkspaceSearch()
    {
        var input = Environment.GetEnvironmentVariable("CODEVIEW_PERF_SEARCH_ROOT");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("CODEVIEW_PERF_SEARCH_ROOT指定時だけ検索ワーカーを測ります。");
        var root = SourcePath.Create(input!, Environment.CurrentDirectory);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(root.FullPath + ".manifest.json"));
        var expectedFiles = manifest.RootElement.GetProperty("files").GetInt64();
        var expectedBytes = manifest.RootElement.GetProperty("totalBytes").GetInt64();
        var regex = Environment.GetEnvironmentVariable("CODEVIEW_PERF_SEARCH_REGEX") == "1";
        var missing = Environment.GetEnvironmentVariable("CODEVIEW_PERF_SEARCH_MODE") != "marker";
        var query = manifest.RootElement.GetProperty(missing ? "notFoundQuery" : "literalQuery").GetString()!;
        var expectedHits = missing ? 0 : manifest.RootElement.GetProperty("expectedMatches").GetInt64();
        var cache = "unknown";
        if (Environment.GetEnvironmentVariable("CODEVIEW_PERF_CACHE") == "Warm")
        {
            var buffer = new byte[64 * 1024];
            long warmedBytes = 0;
            foreach (var path in Directory.EnumerateFiles(root.FullPath, "*", SearchOption.AllDirectories))
            {
                using var stream = File.OpenRead(path);
                int count;
                while ((count = stream.Read(buffer)) > 0) warmedBytes += count;
            }
            Assert.That(warmedBytes, Is.EqualTo(expectedBytes));
            cache = "Warm; all input bytes read immediately before measurement";
        }
        var source = new SourceAccess();
        var search = new TextSearch(new(source), new(source));
        var request = new WorkspaceSearchRequest(root, new ExclusionPolicy([]), EncodingChoice.Auto, query,
            new(UseRegex: regex), 1, 1, 1);
        using var process = Process.GetCurrentProcess();
        using var memory = new ProcessMemory();
        var before = memory.ReadBytes();
        var peak = before;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var cpu = process.TotalProcessorTime;
        var pause = GC.GetTotalPauseDuration();
        var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    peak = Math.Max(peak, memory.ReadBytes());
                    await Task.Delay(10, sampling.Token);
                }
            }
            catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        });
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pending = new Dictionary<long, long>();
        long received = 0, committed = 0, committedFiles = 0;
        double? firstHit = null, firstCommit = null;
        var validPositions = true;
        var watch = Stopwatch.StartNew();
        var session = search.StartWorkspace(request, lifetime.Token);
        SearchCompletion end;
        try
        {
            await foreach (var item in session.Events.ReadAllAsync(lifetime.Token))
            {
                switch (item)
                {
                    case SearchEvent<SearchHit>.Hits batch:
                        firstHit ??= watch.Elapsed.TotalMilliseconds;
                        foreach (var hit in batch.Items)
                        {
                            pending[hit.FileScanId] = pending.GetValueOrDefault(hit.FileScanId) + 1;
                            received++;
                            validPositions &= hit.Range.Start == new TextPosition(1, 4) && hit.Range.Offset == 3
                                && hit.Snippet.Text.AsSpan(hit.Snippet.MatchStart, hit.Snippet.MatchLength).SequenceEqual(query);
                        }
                        break;
                    case SearchEvent<SearchHit>.FileCommitted file:
                        committedFiles++;
                        if (pending.Remove(file.FileScanId, out var count))
                        { committed += count; firstCommit ??= watch.Elapsed.TotalMilliseconds; }
                        break;
                    case SearchEvent<SearchHit>.FileInvalidated file:
                        pending.Remove(file.FileScanId);
                        break;
                }
            }
            end = await session.Completion;
        }
        finally
        {
            lifetime.Cancel();
            try { await session.Completion; }
            finally { watch.Stop(); sampling.Cancel(); await sampler; }
        }
        peak = Math.Max(peak, memory.ReadBytes());
        var report = new
        {
            FormatVersion = 1, Scenario = "streaming-workspace-search", MeasuredAtUtc = DateTimeOffset.UtcNow,
            ProductInformationalVersion = typeof(TextSearch).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            CacheCondition = cache, PhysicalIo = "not-traced; validation passes included, ReadSourceBytes is logical completed second-pass input",
            Encoding = manifest.RootElement.GetProperty("encoding").GetString(), Regex = regex, Missing = missing,
            ExpectedFiles = expectedFiles, ExpectedBytes = expectedBytes, ExpectedHits = expectedHits,
            End = end, ReceivedHits = received, AdoptedCommittedHits = committed, CommittedFiles = committedFiles,
            PendingFileCount = pending.Count, FirstProvisionalMilliseconds = firstHit, FirstCommittedMilliseconds = firstCommit,
            TotalMilliseconds = watch.Elapsed.TotalMilliseconds, CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            MemoryMetric = memory.Metric, MemoryBefore = before, SampledPeakMemory = peak, MemoryIncrease = peak - before,
            GcPauseMilliseconds = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
            GcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray(),
            LastGcLohBytes = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes,
            IndividualGcPausesAndLohAllocations = "not-traced; last-GC size is not allocation total",
            GuiRendering = "not-measured; test receiver drains without UI budgets", DocumentBuild = false
        };
        var label = Environment.GetEnvironmentVariable("CODEVIEW_PERF_LABEL") ?? "workspace-search";
        Assert.That(label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), Is.True);
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", label + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(report));
        Assert.That(validPositions, Is.True);
        Assert.That(end.Reason, Is.EqualTo(expectedHits >= SearchLimits.ResultLimit ? SearchEndReason.LimitReached : SearchEndReason.Completed));
        Assert.That(received, Is.EqualTo(Math.Min(expectedHits, SearchLimits.ResultLimit)));
        Assert.That(end.Progress.FailedEntries, Is.Zero);
        if (expectedHits > 0)
        {
            Assert.That(firstHit, Is.Not.Null.And.LessThanOrEqualTo(1000));
            if (expectedHits > 1) Assert.That(firstCommit, Is.Not.Null.And.LessThanOrEqualTo(1000));
        }
        if (expectedHits < SearchLimits.ResultLimit)
        {
            Assert.That(end.Progress.VisitedFiles, Is.EqualTo(expectedFiles));
            Assert.That(committedFiles, Is.EqualTo(expectedFiles));
            Assert.That(end.Progress.ReadSourceBytes, Is.EqualTo(expectedBytes));
            Assert.That(committed, Is.EqualTo(expectedHits));
            Assert.That(pending, Is.Empty);
        }
        if (expectedFiles == 50000 && expectedBytes == 1073741824 && missing && cache.StartsWith("Warm"))
        {
            Assert.That(end.IsPartial, Is.False);
            if (regex) Assert.That(end.Progress.EvaluatedRegexLines, Is.GreaterThanOrEqualTo(expectedFiles));
            Assert.That(report.TotalMilliseconds, Is.LessThanOrEqualTo(regex ? 30000 : 15000));
            Assert.That(report.CpuMilliseconds, Is.LessThanOrEqualTo(regex ? 30000 : 15000));
            Assert.That(report.MemoryIncrease, Is.LessThanOrEqualTo(128L << 20));
            Assert.That(report.AllocatedBytes, Is.LessThanOrEqualTo(4L << 30));
            Assert.That(report.GcPauseMilliseconds, Is.LessThanOrEqualTo(500));
        }
    }
}
