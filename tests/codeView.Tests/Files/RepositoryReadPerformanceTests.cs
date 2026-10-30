using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Files;
using CodeView.Settings;
using CodeView.Tests.Performance;

namespace CodeView.Tests.Files;

public sealed class RepositoryReadPerformanceTests
{
    [Test, Category("Performance")]
    public async Task MeasureStrictRepositoryRead()
    {
        var input = Environment.GetEnvironmentVariable("CODEVIEW_PERF_READ_ROOT");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("CODEVIEW_PERF_READ_ROOT指定時だけ厳格デコード読取を測ります。");
        var path = SourcePath.Create(input!, Environment.CurrentDirectory);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(path.FullPath + ".manifest.json"));
        var expectedFiles = manifest.RootElement.GetProperty("files").GetInt64();
        var expectedBytes = manifest.RootElement.GetProperty("totalBytes").GetInt64();
        var encoding = manifest.RootElement.GetProperty("encoding").GetString();
        var version = typeof(SourceAccess).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var source = new SourceAccess();
        var directory = new DirectoryReader(source);
        var reader = new FileReader(source);
        using var process = Process.GetCurrentProcess();
        using var memory = new ProcessMemory();
        var before = memory.ReadBytes();
        var peak = before;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var cpu = process.TotalProcessorTime;
        var pause = GC.GetTotalPauseDuration();
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        using var stop = new CancellationTokenSource();
        var samples = 0;
        var sampler = Task.Run(async () =>
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
        long files = 0, bytes = 0, characters = 0, failures = 0;
        double firstChunkMs = -1;
        var watch = Stopwatch.StartNew();
        try
        {
            await Task.Run(() =>
            {
                // One consumer and no retained bodies or per-file tasks.
                Action<ReadOnlyMemory<char>> consume = chunk =>
                {
                    if (firstChunkMs < 0) firstChunkMs = watch.Elapsed.TotalMilliseconds;
                    characters += chunk.Length;
                };
                foreach (var entry in directory.WalkFiles(path, new(SettingsSnapshot.Default.ExcludedDirectoryNames), CancellationToken.None))
                {
                    if (entry.Kind == DirectoryWalkKind.Failed) { failures++; continue; }
                    if (entry.Kind != DirectoryWalkKind.File) continue;
                    var preparation = reader.Prepare(entry.Path, EncodingChoice.Auto, CancellationToken.None);
                    if (preparation.Status != OperationStatus.Success) { failures++; continue; }
                    using var prepared = preparation.Value;
                    var result = reader.ReadChunks(prepared, consume, CancellationToken.None);
                    if (result.Status != OperationStatus.Success) { failures++; continue; }
                    files++;
                    bytes += prepared.Stamp.LengthBytes;
                }
            });
        }
        finally
        {
            watch.Stop();
            stop.Cancel();
            await sampler;
        }
        peak = Math.Max(peak, memory.ReadBytes());
        var gc = GC.GetGCMemoryInfo();
        var report = new
        {
            FormatVersion = 1, MeasuredAtUtc = DateTimeOffset.UtcNow, Scenario = "strict-repository-read",
            ProductInformationalVersion = version, Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            CacheCondition = Environment.GetEnvironmentVariable("CODEVIEW_PERF_CACHE") ?? "unknown", PhysicalIo = "not-traced",
            ExpectedFiles = expectedFiles, ExpectedBytes = expectedBytes, Encoding = encoding,
            Files = files, InputBytes = bytes, DecodedCharacters = characters, Failures = failures,
            TotalMilliseconds = watch.Elapsed.TotalMilliseconds, FirstChunkMilliseconds = firstChunkMs,
            AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            MemoryMetric = memory.Metric, MemoryBefore = before, SampledPeakMemory = peak, MemoryIncrease = peak - before,
            SamplingIntervalMilliseconds = 10, SampleCount = samples,
            GcPauseMilliseconds = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
            GcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(),
            LastGcLohBytes = gc.GenerationInfo[3].SizeAfterBytes, IndividualGcPauses = "not-traced",
            DocumentBuild = false, SearchMatching = false, GuiRendering = "not-measured"
        };
        var label = Environment.GetEnvironmentVariable("CODEVIEW_PERF_LABEL") ?? "repository-read";
        Assert.That(label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), Is.True);
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", label + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(report));
        Assert.That(files, Is.EqualTo(expectedFiles));
        Assert.That(bytes, Is.EqualTo(expectedBytes));
        Assert.That(failures, Is.Zero);
        if (expectedFiles == 50000 && encoding == "utf-8")
        {
            Assert.Multiple(() =>
            {
                Assert.That(report.TotalMilliseconds, Is.LessThanOrEqualTo(10000));
                Assert.That(report.CpuMilliseconds, Is.LessThanOrEqualTo(10000));
                Assert.That(firstChunkMs, Is.InRange(0, 1000));
                Assert.That(report.MemoryIncrease, Is.LessThanOrEqualTo(128L << 20));
                Assert.That(report.AllocatedBytes, Is.LessThanOrEqualTo(4L << 30));
                Assert.That(report.GcPauseMilliseconds, Is.LessThanOrEqualTo(500));
            });
        }
    }
}
