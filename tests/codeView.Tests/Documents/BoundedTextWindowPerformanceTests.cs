using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Presentation;

namespace CodeView.Tests.Documents;

public sealed class BoundedTextWindowPerformanceTests
{
    [Test, Category("Performance")]
    public async Task MeasureWindowExtractionWithoutGui()
    {
        var input = Environment.GetEnvironmentVariable("CODEVIEW_PERF_WINDOW_FILE");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("CODEVIEW_PERF_WINDOW_FILE指定時だけ測定します。");
        using var handle = await Task.Run(() =>
        {
            var reader = new FileReader(new SourceAccess());
            var result = reader.Prepare(SourcePath.Create(input!, Environment.CurrentDirectory), EncodingChoice.Auto, CancellationToken.None);
            Assert.That(result.Status, Is.EqualTo(OperationStatus.Success));
            using var prepared = result.Value;
            using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
            Assert.That(reader.ReadChunks(prepared, builder.Append, CancellationToken.None).Status, Is.EqualTo(OperationStatus.Success));
            return builder.Complete();
        });
        using var lease = handle.AcquireReadView();
        var times = new List<double>(64);
        var offsets = new List<long>(64);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        long copied = 0;
        for (var index = 0; index < 64; index++)
        {
            var target = lease.Length * index / 63;
            var start = Stopwatch.GetTimestamp();
            var window = TextWindow.Create(lease, target);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            offsets.Add(window.TargetOffset);
            copied += window.Text.Length;
            Assert.That(window.Text.Length, Is.LessThanOrEqualTo(TextWindow.MaxWindowChars));
            Assert.That(window.TargetOffset, Is.InRange(Math.Max(0, target - 1), target));
        }
        var allocationBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var report = new
        {
            FormatVersion = 1, MeasuredAtUtc = DateTimeOffset.UtcNow, Scenario = "bounded-window-extraction",
            Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = Environment.Version.ToString(), InputBytes = new FileInfo(input!).Length, SourceChars = lease.Length,
            MaxWindowChars = TextWindow.MaxWindowChars, WindowCount = times.Count, CopiedCharacters = copied,
            WindowMilliseconds = times, VisitedSourceOffsets = offsets, AllocatedBytes = allocationBytes,
            GuiLayout = "not-measured", PhysicalPaint = "not-measured", NativeGuiAcceptance = "pending"
        };
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "bounded-window.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(report));
        Assert.That(times, Has.All.LessThanOrEqualTo(50));
        Assert.That(allocationBytes, Is.LessThanOrEqualTo(4 << 20));
    }
}
