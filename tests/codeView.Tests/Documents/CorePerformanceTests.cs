using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Tests.Performance;

namespace CodeView.Tests.Documents;

/// <summary>Opt-in measurements, never used as proof of GUI rendering performance.</summary>
public sealed class CorePerformanceTests
{
    [Test, Category("Performance")]
    public async Task MeasureReadBuildAndSnapshot()
    {
        var input = Environment.GetEnvironmentVariable("CODEVIEW_PERF_FILE");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("CODEVIEW_PERF_FILEで生成済みデータを指定した時だけ性能測定します。");
        var copies = int.TryParse(Environment.GetEnvironmentVariable("CODEVIEW_PERF_COPIES"), out var count) ? count : 1;
        Assert.That(copies, Is.InRange(1, 5));
        var label = Environment.GetEnvironmentVariable("CODEVIEW_PERF_LABEL") ?? "core";
        Assert.That(label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), Is.True);
        var path = SourcePath.Create(input!, Environment.CurrentDirectory);
        var source = new SourceAccess();
        var reader = new FileReader(source);
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        using var memory = new ProcessMemory();
        var memoryBefore = memory.ReadBytes();
        var peakMemory = memoryBefore;
        var heapBefore = GC.GetTotalMemory(false);
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var pauseBefore = GC.GetTotalPauseDuration();
        var collectionsBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        using var sampling = new CancellationTokenSource();
        var samples = 0;
        var sampler = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    peakMemory = Math.Max(peakMemory, memory.ReadBytes());
                    samples++;
                    await Task.Delay(10, sampling.Token);
                }
            }
            catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        });
        using var documents = new DocumentCollection();
        var times = new List<double>();
        var snapshotTimes = new List<double>();
        var snapshotAllocated = new List<long>();
        try
        {
            for (var index = 0; index < copies; index++)
            {
                var watch = Stopwatch.StartNew();
                var handle = await Task.Run(() =>
                {
                    var result = reader.Prepare(path, EncodingChoice.Auto, CancellationToken.None);
                    Assert.That(result.Status, Is.EqualTo(OperationStatus.Success));
                    using var prepared = result.Value;
                    using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
                    var read = reader.ReadChunks(prepared, builder.Append, CancellationToken.None);
                    Assert.That(read.Status, Is.EqualTo(OperationStatus.Success));
                    return builder.Complete();
                });
                watch.Stop();
                documents.Add(handle);
                times.Add(watch.Elapsed.TotalMilliseconds);
                var allocationStart = GC.GetAllocatedBytesForCurrentThread();
                watch.Restart();
                using var lease = handle.AcquireReadView();
                watch.Stop();
                snapshotTimes.Add(watch.Elapsed.TotalMilliseconds);
                snapshotAllocated.Add(GC.GetAllocatedBytesForCurrentThread() - allocationStart);
                Assert.That(lease.ReadRange(0, 32), Does.Contain("CODEVIEW_MATCH"));
            }
        }
        finally
        {
            sampling.Cancel();
            await sampler;
        }

        peakMemory = Math.Max(peakMemory, memory.ReadBytes());
        var gc = GC.GetGCMemoryInfo();
        var report = new
        {
            MeasuredAtUtc = DateTimeOffset.UtcNow,
            FormatVersion = 2, Scenario = "read-build-snapshot", Label = label,
            Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = Environment.Version.ToString(), CacheCondition = Environment.GetEnvironmentVariable("CODEVIEW_PERF_CACHE") ?? "unknown",
            InputBytes = new FileInfo(path.FullPath).Length, Copies = copies, BuildMilliseconds = times,
            SnapshotMilliseconds = snapshotTimes, SnapshotAllocatedBytes = snapshotAllocated,
            CpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocatedBefore,
            ManagedHeapBefore = heapBefore, ManagedHeapAfter = GC.GetTotalMemory(false),
            MemoryMetric = memory.Metric, MemoryBefore = memoryBefore, SampledPeakMemory = peakMemory,
            MemoryIncrease = peakMemory - memoryBefore,
            SamplingIntervalMilliseconds = 10, SampleCount = samples,
            GcPauseTotalMilliseconds = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds,
            GcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collectionsBefore[i]).ToArray(),
            LastGcHeapBytes = gc.HeapSizeBytes, LastGcFragmentationBytes = gc.FragmentedBytes,
            LastGcLohBytes = gc.GenerationInfo[3].SizeAfterBytes,
            IndividualGcPauses = "not-traced", GuiRendering = "not-measured"
        };
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", $"{label}-{copies}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(report));
        if (report.InputBytes <= 50L * 1024 * 1024)
        {
            Assert.That(times, Has.All.LessThanOrEqualTo(5000));
            Assert.That(report.MemoryIncrease, Is.LessThanOrEqualTo(copies == 1 ? 512L << 20 : 1536L << 20));
            Assert.That(report.GcPauseTotalMilliseconds, Is.LessThanOrEqualTo(150 * copies));
        }
        Assert.That(snapshotTimes, Has.All.LessThanOrEqualTo(50));
        Assert.That(snapshotAllocated, Has.All.LessThanOrEqualTo(1 << 20));
    }

    private sealed class DocumentCollection : List<DocumentHandle>, IDisposable
    {
        public void Dispose()
        {
            foreach (var document in this) document.Dispose();
        }
    }
}
