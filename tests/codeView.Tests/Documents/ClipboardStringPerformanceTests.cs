using System.Diagnostics;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;

namespace CodeView.Tests.Documents;

public sealed class ClipboardStringPerformanceTests
{
    [Test, Category("Performance")]
    public async Task MeasureOneFinalStringAfterSourceClosure()
    {
        var input = Environment.GetEnvironmentVariable("CODEVIEW_PERF_CLIPBOARD_FILE");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("CODEVIEW_PERF_CLIPBOARD_FILE指定時だけ測定します。");
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
        var length = handle.Length;
        ulong expected = 0;
        using (var lease = handle.AcquireReadView())
            lease.VisitChunks(chunk => { foreach (var value in chunk.Span) expected += value; return true; }, default);
        DocumentSelectionCopy copy;
        using (var source = new DocumentDisplaySession(handle)) { source.SelectAll(); copy = source.BeginCopy(); }
        handle.Dispose();
        using (copy)
        {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var pause = GC.GetTotalPauseDuration();
            var counts = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            var text = copy.ReadText(CancellationToken.None);
            copy.Dispose();
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
            var cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var gcPause = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
            var collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - counts[i]).ToArray();
            ulong checksum = 0;
            foreach (var value in text.AsSpan()) checksum += value;
            var report = new
            {
                FormatVersion = 1, MeasuredAtUtc = DateTimeOffset.UtcNow, Scenario = "final-clipboard-string",
                InputBytes = new FileInfo(input!).Length, SourceChars = length, ResultChars = text.Length,
                ExpectedChecksum = expected, ResultChecksum = checksum,
                Milliseconds = elapsed, CpuMilliseconds = cpuMilliseconds, AllocatedBytes = allocated,
                GcPauseMilliseconds = gcPause, GcCollections = collections, ActiveReadLeases = handle.ActiveReadLeases,
                SourceClosedBeforeRead = handle.IsReleased, IntermediateWholeString = false,
                OsClipboard = "not-measured", OsMemory = "not-measured", IndividualGcPauses = "not-traced"
            };
            var label = Environment.GetEnvironmentVariable("CODEVIEW_PERF_LABEL") ?? "clipboard-string";
            Assert.That(label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), Is.True);
            var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", label + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.AddTestAttachment(output);
            Assert.Multiple(() =>
            {
                Assert.That(text.Length, Is.EqualTo(length));
                Assert.That(checksum, Is.EqualTo(expected));
                Assert.That(handle.ActiveReadLeases, Is.Zero);
                Assert.That(allocated, Is.LessThanOrEqualTo(length * 2 + (1 << 20)));
                if (report.InputBytes <= 50L << 20)
                {
                    Assert.That(elapsed, Is.LessThanOrEqualTo(1000));
                    Assert.That(cpuMilliseconds, Is.LessThanOrEqualTo(1000));
                }
            });
        }
    }
}
