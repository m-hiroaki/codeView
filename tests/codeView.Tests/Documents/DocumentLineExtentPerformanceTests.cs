using System.Diagnostics;
using System.Text.Json;
using CodeView.Documents;
using CodeView.Tests.Performance;

namespace CodeView.Tests.Documents;

public sealed class DocumentLineExtentPerformanceTests
{
    [Test, Category("Performance")]
    public void CompareWorkerCompletionWithAndWithoutFinalIndexTraversal()
    {
        // Same production builder with extent collection disabled/enabled; all other paths are common.
        // Only candidate completion traverses the existing final index; no source decoding/file I/O.
        using (Build("warmup", false, 1, out _)) { }
        using (Build("warmup", true, 1, out _)) { }
        foreach (var shape in new[] { "normal", "long" })
        {
            var chunk = shape == "normal" ? string.Concat(Enumerable.Repeat(new string('x', 126) + "\r\n", 128)) : new string('x', 16384);
            // Warm both full-size shapes/paths before recording. A tiny append does not warm
            // large-rope/line-manager paths and would penalize the first baseline process sample.
            foreach (var warmCandidate in new[] { false, true, true, false })
                using (Build(chunk, warmCandidate, 3200, out _)) { }
            for (var pair = 0; pair < 3; pair++)
            {
                foreach (var candidate in pair % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    using var memory = new ProcessMemory();
                    using var process = Process.GetCurrentProcess();
                    var beforeMemory = memory.ReadBytes();
                    var beforeCpu = process.TotalProcessorTime;
                    var beforePause = GC.GetTotalPauseDuration();
                    var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                    var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    using var handle = Build(chunk, candidate, 3200, out var completionMs);
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
                    var cpu = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds;
                    var pause = (GC.GetTotalPauseDuration() - beforePause).TotalMilliseconds;
                    var afterMemory = memory.ReadBytes();
                    var gc = collections.Select((n, i) => GC.CollectionCount(i) - n).ToArray();
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        Scenario = "worker-line-extent-50MiB", Shape = shape, Pair = pair, Candidate = candidate,
                        SourceChars = handle.Length, handle.MaximumLineLength, ElapsedMilliseconds = elapsed,
                        CompletionMilliseconds = completionMs,
                        AllocatedBytes = allocated, CpuMilliseconds = cpu, GcPauseMilliseconds = pause, GcCollections = gc,
                        MemoryMetric = memory.Metric, MemoryBefore = beforeMemory, MemoryAfter = afterMemory,
                        Warmup = "Four unmeasured full-size builds per shape in ABBA order, then three measured AB/BA/AB pairs.",
                        Scope = "In-memory worker build; same production builder, optional final index traversal OFF/ON; endpoints not peak; not I/O/startup acceptance."
                    }));
                    Assert.That(handle.Length, Is.EqualTo(50 * 1024 * 1024));
                    Assert.That(handle.ClaimForDisplay().LineCount, Is.EqualTo(shape == "normal" ? 409601 : 1));
                    if (candidate) Assert.That(handle.MaximumLineLength, Is.EqualTo(shape == "normal" ? 126 : 50 * 1024 * 1024));
                    else Assert.That(handle.MaximumLineLength, Is.Null);
                }
            }
        }
    }

    private static DocumentHandle Build(string chunk, bool candidate, int count, out double completionMs)
    {
        using var builder = new DocumentBuilder(collectLineExtent: candidate);
        for (var i = 0; i < count; i++) builder.Append(chunk.AsMemory());
        var start = Stopwatch.GetTimestamp();
        var handle = builder.Complete();
        completionMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return handle;
    }
}
