using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;

namespace CodeView.Tests.Documents;

public sealed class DocumentDisplayPerformanceTests
{
    [Test, Category("Performance")]
    public async Task MeasureSourceSlicesAndSelectionCopyAfterDisplayClosure()
    {
        var input = Environment.GetEnvironmentVariable("CODEVIEW_PERF_DISPLAY_FILE");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("CODEVIEW_PERF_DISPLAY_FILE指定時だけ測定します。");
        using var handle = await Task.Run(() =>
        {
            var reader = new FileReader(new SourceAccess());
            var result = reader.Prepare(SourcePath.Create(input!, Environment.CurrentDirectory), EncodingChoice.Auto, default);
            Assert.That(result.Status, Is.EqualTo(OperationStatus.Success));
            using var prepared = result.Value;
            using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
            Assert.That(reader.ReadChunks(prepared, builder.Append, default).Status, Is.EqualTo(OperationStatus.Success));
            return builder.Complete();
        });
        var expectedLength = handle.Length;
        ulong expectedSum = 0;
        using (var lease = handle.AcquireReadView())
            lease.VisitChunks(chunk => { foreach (var c in chunk.Span) expectedSum += c; return true; }, default);

        var slices = new List<double>(64);
        var display = new DocumentDisplaySession(handle);
        long sliceAllocation;
        long beginCopyAllocation;
        double beginCopyMilliseconds;
        DocumentSelectionCopy copy;
        try
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 64; index++)
            {
                var start = Stopwatch.GetTimestamp();
                var slice = display.CreateSlice(display.Length * index / 63, 1024);
                slices.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                Assert.That(display.TryMoveFromDisplay(slice, slice.TargetDisplayOffset, extendSelection: index != 0), Is.True);
            }
            sliceAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.That(display.SelectionStart, Is.Zero);
            Assert.That(display.SelectionLength, Is.EqualTo(expectedLength));
            var beforeBeginCopy = GC.GetAllocatedBytesForCurrentThread();
            var beginCopyStart = Stopwatch.GetTimestamp();
            copy = display.BeginCopy();
            beginCopyMilliseconds = Stopwatch.GetElapsedTime(beginCopyStart).TotalMilliseconds;
            beginCopyAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeBeginCopy;
        }
        finally { display.Dispose(); }
        handle.Dispose();
        Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
        using var writer = new ChecksumWriter();
        using var process = Process.GetCurrentProcess();
        var beforeCpu = process.TotalProcessorTime;
        var beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
        var beforePause = GC.GetTotalPauseDuration();
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var copyStart = Stopwatch.GetTimestamp();
        using (copy) copy.WriteTo(writer, CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
        var cpu = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds;
        var pause = (GC.GetTotalPauseDuration() - beforePause).TotalMilliseconds;
        var report = new
        {
            FormatVersion = 1, MeasuredAtUtc = DateTimeOffset.UtcNow,
            Scenario = "source-display-selection-copy", InputBytes = new FileInfo(input!).Length,
            SourceChars = expectedLength, ExpectedChecksum = expectedSum, CopiedChars = writer.Characters, Checksum = writer.Sum,
            SliceChars = 1024, SliceCount = slices.Count, SliceMilliseconds = slices, SliceAllocatedBytes = sliceAllocation,
            BeginCopyMilliseconds = beginCopyMilliseconds, BeginCopyAllocatedBytes = beginCopyAllocation,
            CopyMilliseconds = elapsed, CopyCpuMilliseconds = cpu, CopyAllocatedBytes = allocatedBytes,
            CopyGcPauseMilliseconds = pause, CopyGcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(),
            MaxWriteChars = writer.MaxWrite, ReadLeasesAfterCopy = handle.ActiveReadLeases,
            OriginalHandleReleased = handle.IsReleased, DisplayClosedBeforeCopy = true,
            GuiLayout = "not-measured", Clipboard = "not-measured", OsMemory = "not-measured", IndividualGcPauses = "not-traced"
        };
        var label = Environment.GetEnvironmentVariable("CODEVIEW_PERF_LABEL") ?? "source-display-copy";
        Assert.That(label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), Is.True);
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", label + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(report));
        Assert.Multiple(() =>
        {
            Assert.That(writer.Characters, Is.EqualTo(expectedLength));
            Assert.That(writer.Sum, Is.EqualTo(expectedSum));
            Assert.That(writer.MaxWrite, Is.LessThanOrEqualTo(DocumentReadLease.ChunkChars));
            Assert.That(handle.ActiveReadLeases, Is.Zero);
            Assert.That(slices, Has.All.LessThanOrEqualTo(50));
            Assert.That(sliceAllocation, Is.LessThanOrEqualTo(4 << 20));
            Assert.That(beginCopyMilliseconds, Is.LessThanOrEqualTo(50));
            Assert.That(beginCopyAllocation, Is.LessThanOrEqualTo(1 << 20));
        });
        if (report.InputBytes <= 50L << 20)
            Assert.Multiple(() =>
            {
                Assert.That(elapsed, Is.LessThanOrEqualTo(1000));
                Assert.That(cpu, Is.LessThanOrEqualTo(1000));
                Assert.That(allocatedBytes, Is.LessThanOrEqualTo(256 << 10));
            });
    }

    private sealed class ChecksumWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public long Characters { get; private set; }
        public ulong Sum { get; private set; }
        public int MaxWrite { get; private set; }
        public override void Write(char[] buffer, int index, int count)
        {
            Characters += count;
            MaxWrite = Math.Max(MaxWrite, count);
            foreach (var c in buffer.AsSpan(index, count)) Sum += c;
        }
    }
}
