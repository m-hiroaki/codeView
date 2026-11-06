using System.Diagnostics;
using System.Text.Json;
using AvaloniaEdit.Document;
using CodeView.Documents;
using CodeView.Tests.Performance;

namespace CodeView.Tests.Documents;

public sealed class DocumentLineExtentPerformanceTests
{
    [Test, Category("Performance")]
    public void CompareWorkerCompletionWithAndWithoutFinalIndexTraversal()
    {
        // The baseline reproduces the previous builder (same insertion, undo, snapshot and handoff).
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
                        Scope = "In-memory worker build; final index traversal compared to previous builder; endpoints not peak; not I/O/startup acceptance."
                    }));
                    Assert.That(handle.Length, Is.EqualTo(50 * 1024 * 1024));
                    Assert.That(handle.ClaimForDisplay().LineCount, Is.EqualTo(shape == "normal" ? 409601 : 1));
                    if (candidate) Assert.That(handle.MaximumLineLength, Is.EqualTo(shape == "normal" ? 126 : 50 * 1024 * 1024));
                }
            }
        }
    }

    private static DocumentHandle Build(string chunk, bool candidate, int count, out double completionMs)
    {
        if (candidate)
        {
            using var builder = new DocumentBuilder();
            for (var i = 0; i < count; i++) builder.Append(chunk.AsMemory());
            var start = Stopwatch.GetTimestamp();
            var handle = builder.Complete();
            completionMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return handle;
        }
        using var previous = new PreviousDocumentBuilder();
        for (var i = 0; i < count; i++) previous.Append(chunk.AsMemory());
        var previousStart = Stopwatch.GetTimestamp();
        var previousHandle = previous.Complete();
        completionMs = Stopwatch.GetElapsedTime(previousStart).TotalMilliseconds;
        return previousHandle;
    }

    // Previous builder implementation, including owner/range checks; current handle layout is common.
    private sealed class PreviousDocumentBuilder : IDisposable
    {
        private TextDocument? _document = new();
        private readonly int _ownerThread = Environment.CurrentManagedThreadId;
        public PreviousDocumentBuilder() => _document!.UndoStack.SizeLimit = 0;
        public void Append(ReadOnlyMemory<char> chunk)
        {
            VerifyOwner();
            var document = _document ?? throw new ObjectDisposedException(nameof(PreviousDocumentBuilder));
            _ = checked(document.TextLength + chunk.Length);
            document.Insert(document.TextLength, chunk.ToString());
        }
        public DocumentHandle Complete()
        {
            VerifyOwner();
            var document = _document ?? throw new ObjectDisposedException(nameof(PreviousDocumentBuilder));
            document.UndoStack.ClearAll();
            document.SetOwnerThread(null!);
            _document = null;
            return new DocumentHandle(document, 0);
        }
        public void Dispose() => _document = null;
        private void VerifyOwner()
        {
            if (Environment.CurrentManagedThreadId != _ownerThread)
                throw new InvalidOperationException("文書構築中に所有スレッドを変更できません。");
        }
    }
}
