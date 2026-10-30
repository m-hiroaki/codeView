using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;

namespace CodeView.Tests.Documents;

public sealed class DocumentScanPerformanceTests
{
    [Test, Category("Performance")]
    public async Task MeasureImmutableReadViewScan()
    {
        var input = Environment.GetEnvironmentVariable("CODEVIEW_PERF_SCAN_FILE");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("CODEVIEW_PERF_SCAN_FILE指定時だけ走査を測定します。");
        using var handle = await Task.Run(() =>
        {
            var reader = new FileReader(new SourceAccess());
            var prepared = reader.Prepare(SourcePath.Create(input!, Environment.CurrentDirectory), EncodingChoice.Auto, CancellationToken.None);
            Assert.That(prepared.Status, Is.EqualTo(OperationStatus.Success));
            using var file = prepared.Value;
            using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
            Assert.That(reader.ReadChunks(file, builder.Append, CancellationToken.None).Status, Is.EqualTo(OperationStatus.Success));
            return builder.Complete();
        });
        using var lease = handle.AcquireReadView();
        handle.Dispose();
        var scans = new List<ScanSample>();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var pause = GC.GetTotalPauseDuration();
        var counts = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        for (var index = 0; index < 3; index++)
            scans.Add(Measure(lease, stopEarly: false));
        var early = Measure(lease, stopEarly: true);
        var report = new
        {
            FormatVersion = 1, MeasuredAtUtc = DateTimeOffset.UtcNow, Scenario = "immutable-read-view-scan",
            Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = Environment.Version.ToString(), InputBytes = new FileInfo(input!).Length,
            SourceChars = lease.Length, Scans = scans, EarlyStop = early,
            CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            GcPauseMilliseconds = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
            GcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - counts[i]).ToArray(),
            UiAttached = false, LiveDocumentReleased = handle.IsReleased,
            Workload = "read every UTF-16 unit and sum values; not literal/regex matching",
            ReaderBatching = "reader-native-chunks",
            BuildCost = "excluded; see read-build-snapshot measurements", GuiRendering = "not-measured"
        };
        var label = Environment.GetEnvironmentVariable("CODEVIEW_PERF_LABEL") ?? "document-scan";
        Assert.That(label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), Is.True);
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", label + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(report));
        Assert.That(scans.Select(s => s.Characters), Has.All.EqualTo(lease.Length));
        Assert.That(scans.Select(s => s.Checksum).Distinct().Count(), Is.EqualTo(1));
        Assert.That(early.Chunks, Is.EqualTo(1));
        Assert.That(early.Characters, Is.InRange(1, DocumentReadLease.ChunkChars));
        if (report.InputBytes <= 50L << 20)
        {
            Assert.That(scans.Select(s => s.TotalMilliseconds), Has.All.LessThanOrEqualTo(1000));
            Assert.That(scans.Select(s => s.FirstChunkMilliseconds), Has.All.LessThanOrEqualTo(50));
            Assert.That(scans.Select(s => s.AllocatedBytes), Has.All.LessThanOrEqualTo(256 << 10));
        }
    }

    private static ScanSample Measure(DocumentReadLease lease, bool stopEarly)
    {
        double first = -1;
        var chunks = 0;
        ulong sum = 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var characters = lease.VisitChunks(chunk =>
        {
            if (chunks++ == 0) first = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            foreach (var c in chunk.Span) sum += c;
            return !stopEarly;
        }, CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return new(characters, chunks, sum, first, elapsed, bytes);
    }

    private sealed record ScanSample(long Characters, int Chunks, ulong Checksum, double FirstChunkMilliseconds,
        double TotalMilliseconds, long AllocatedBytes);
}
