using System.Diagnostics;
using System.Text.Json;
using CodeView.Documents;
using CodeView.Tests.Performance;

namespace CodeView.Tests.Documents;

public sealed class RowProjectionPerformanceTests
{
    [Test, Category("Performance")]
    public void FiftyMiBMixedLinesKeepProjectionWorkBounded()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(new string('x', 50 * 1024 * 1024).AsMemory());
        builder.Append(("\r\n" + string.Join("\r\n", Enumerable.Repeat(new string('y', 2048), 49))).AsMemory());
        using var handle = builder.Complete();
        using var view = new DocumentDisplaySession(handle);
        view.Select(0, 1);
        var elapsed = new double[64];
        using var memory = new ProcessMemory();
        var memoryBefore = memory.ReadBytes();
        var pauseBefore = GC.GetTotalPauseDuration();
        var collectionsBefore = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var step = 0; step < elapsed.Length; step++)
        {
            view.SetViewport(1 + step % 2, step % 16 * 8);
            var started = Stopwatch.GetTimestamp();
            var projection = view.CreateRowProjection(48, 512);
            elapsed[step] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.That(projection.Rows.Length, Is.EqualTo(48));
            Assert.That(projection.Text.Length, Is.LessThanOrEqualTo(16384));
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var pause = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) }
            .Select((value, index) => value - collectionsBefore[index]).ToArray();
        var memoryAfter = memory.ReadBytes();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Scenario = "row-projection-50MiB-mixed", SourceChars = view.Length, Steps = 64, Rows = 48,
            AllocatedBytes = allocated, CpuMilliseconds = cpu, MaximumProjectionMilliseconds = elapsed.Max(),
            MemoryMetric = memory.Metric, MemoryBefore = memoryBefore, MemoryAfter = memoryAfter,
            GcPauseTotalMilliseconds = pause, GcCollections = collections,
            DisplayBudgetChars = 16384, Scope = "UI-free core; source construction excluded; assertions included in allocation."
        }));
        Assert.That((view.Anchor, view.Caret), Is.EqualTo((0L, 1L)));
        Assert.That(elapsed.Max(), Is.LessThanOrEqualTo(50));
        Assert.That(allocated, Is.LessThanOrEqualTo(4 * 1024 * 1024));
    }
}
