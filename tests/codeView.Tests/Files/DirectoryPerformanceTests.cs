using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Settings;
using CodeView.Tests.Performance;

namespace CodeView.Tests.Files;

public sealed class DirectoryPerformanceTests
{
    [Test, Category("Performance")]
    public async Task MeasureLazyDirectoryEnumeration()
    {
        var input = Environment.GetEnvironmentVariable("CODEVIEW_PERF_DIRECTORY");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("CODEVIEW_PERF_DIRECTORY指定時だけ列挙を測定します。");
        var requested = SourcePath.Create(input!, Environment.CurrentDirectory);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(requested.FullPath + ".manifest.json"));
        var expectedFiles = manifest.RootElement.GetProperty("files").GetInt64();
        var manifestBytes = manifest.RootElement.GetProperty("totalBytes").GetInt64();
        var seed = manifest.RootElement.GetProperty("seed").GetInt32();
        var layout = manifest.RootElement.GetProperty("layout").GetString();
        var reader = new DirectoryReader(new SourceAccess());
        var policy = new ExclusionPolicy(SettingsSnapshot.Default.ExcludedDirectoryNames);
        var productVersion = typeof(SourceAccess).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var resolved = await reader.ResolveRootAsync(requested, CancellationToken.None);
        Assert.That(resolved.Status, Is.EqualTo(OperationStatus.Success));
        using var memory = new ProcessMemory();
        using var process = Process.GetCurrentProcess();
        var before = memory.ReadBytes();
        var peak = before;
        var samples = 0;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var cpu = process.TotalProcessorTime;
        var pause = GC.GetTotalPauseDuration();
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        using var stop = new CancellationTokenSource();
        var sampling = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    peak = Math.Max(peak, memory.ReadBytes());
                    samples++;
                    await Task.Delay(10, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        var watch = Stopwatch.StartNew();
        DirectoryListing rootListing;
        DirectoryListing? childListing = null;
        double rootMs;
        double? childMs = null;
        double firstMs = -1;
        double walkMs;
        long files = 0, failures = 0, skipped = 0;
        try
        {
            rootListing = await reader.ReadChildrenAsync(resolved.Value, policy, CancellationToken.None);
            rootMs = watch.Elapsed.TotalMilliseconds;
            var child = rootListing.Entries.FirstOrDefault(e => e.Kind == SourceEntryKind.Directory && !e.IsLink);
            if (child is not null)
            {
                watch.Restart();
                childListing = await reader.ReadChildrenAsync(child.Path, policy, CancellationToken.None);
                childMs = watch.Elapsed.TotalMilliseconds;
            }
            watch.Restart();
            await Task.Run(() =>
            {
                foreach (var entry in reader.WalkFiles(resolved.Value, policy, CancellationToken.None))
                {
                    if (entry.Kind == DirectoryWalkKind.File)
                    {
                        if (files == 0) firstMs = watch.Elapsed.TotalMilliseconds;
                        files++;
                    }
                    else if (entry.Kind == DirectoryWalkKind.Failed) failures++;
                    else skipped++;
                }
            });
            walkMs = watch.Elapsed.TotalMilliseconds;
        }
        finally
        {
            stop.Cancel();
            await sampling;
        }
        peak = Math.Max(peak, memory.ReadBytes());
        var report = new
        {
            FormatVersion = 1, MeasuredAtUtc = DateTimeOffset.UtcNow, Scenario = "lazy-directory-enumeration",
            Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = Environment.Version.ToString(), CacheCondition = Environment.GetEnvironmentVariable("CODEVIEW_PERF_CACHE") ?? "unknown",
            ProductInformationalVersion = productVersion,
            ExpectedFiles = expectedFiles, ManifestTotalBytes = manifestBytes, Seed = seed, Layout = layout,
            RootChildren = rootListing.Entries.Count, ChildChildren = childListing?.Entries.Count,
            RootChildrenMilliseconds = rootMs, ChildChildrenMilliseconds = childMs,
            FirstFileMilliseconds = firstMs, WalkMilliseconds = walkMs, Files = files, Failures = failures, Skipped = skipped,
            MemoryMetric = memory.Metric, MemoryBefore = before, SampledPeakMemory = peak, MemoryIncrease = peak - before,
            SamplingIntervalMilliseconds = 10, SampleCount = samples,
            AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            GcPauseMilliseconds = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
            GcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(),
            FileBodiesRead = false, RetainedAllPaths = false, GuiRendering = "not-measured"
        };
        var label = Environment.GetEnvironmentVariable("CODEVIEW_PERF_LABEL") ?? "directory-enumeration";
        Assert.That(label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), Is.True);
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", label + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(report));
        Assert.That(rootListing.IsComplete, Is.True);
        Assert.That(childListing?.IsComplete ?? true, Is.True);
        Assert.That(files, Is.EqualTo(expectedFiles));
        Assert.That(failures, Is.Zero);
        Assert.That(skipped, Is.Zero);
        if (expectedFiles == 50000)
        {
            Assert.That(rootMs, Is.LessThanOrEqualTo(1000));
            if (childMs is not null) Assert.That(childMs.Value, Is.LessThanOrEqualTo(1000));
            Assert.That(firstMs, Is.InRange(0, 1000));
            Assert.That(walkMs, Is.LessThanOrEqualTo(5000));
            Assert.That(report.CpuMilliseconds, Is.LessThanOrEqualTo(5000));
            Assert.That(report.MemoryIncrease, Is.LessThanOrEqualTo(64L << 20));
        }
        GC.KeepAlive(rootListing);
        GC.KeepAlive(childListing);
    }
}
