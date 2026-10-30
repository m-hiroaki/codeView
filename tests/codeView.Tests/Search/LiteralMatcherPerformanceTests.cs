using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Search;

namespace CodeView.Tests.Search;

public sealed class LiteralMatcherPerformanceTests
{
    [Test, Category("Performance")]
    public async Task MeasureLiteralMatchingOfImmutableChunks()
    {
        var root = Environment.GetEnvironmentVariable("CODEVIEW_PERF_LITERAL_ROOT");
        if (string.IsNullOrEmpty(root)) Assert.Ignore("CODEVIEW_PERF_LITERAL_ROOT指定時だけ測定します。");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(root! + ".manifest.json"));
        var marker = manifest.RootElement.GetProperty("literalQuery").GetString()!;
        var missing = manifest.RootElement.GetProperty("notFoundQuery").GetString()!;
        var path = Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories).Single();
        using var document = await Task.Run(() =>
        {
            var reader = new FileReader(new SourceAccess());
            var opened = reader.Prepare(SourcePath.Create(path, Environment.CurrentDirectory), EncodingChoice.Auto, CancellationToken.None);
            Assert.That(opened.Status, Is.EqualTo(OperationStatus.Success));
            using var prepared = opened.Value;
            using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
            Assert.That(reader.ReadChunks(prepared, builder.Append, CancellationToken.None).Status, Is.EqualTo(OperationStatus.Success));
            return builder.Complete();
        });
        using var lease = document.AcquireReadView();
        document.Dispose();
        var cases = new[]
        {
            ("marker", marker, new SearchOptions(), 1L),
            ("whole-word-marker", marker, new SearchOptions(WholeWord: true), 1L),
            ("not-found", missing, new SearchOptions(), 0L),
            ("max-repeated-query", new string('q', SearchLimits.QueryChars), new SearchOptions(), 0L)
        };
        var samples = new List<Sample>();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        foreach (var (name, query, options, expected) in cases)
        {
            var sample = await Task.Run(() => Measure(name, query, options, lease));
            samples.Add(sample);
            Assert.That(sample.Hits, Is.EqualTo(expected));
            Assert.That(sample.Characters, Is.EqualTo(lease.Length));
            if (expected > 0) Assert.That(sample.FirstHit, Is.EqualTo(new TextRange(3, marker.Length, new(1, 4))));
        }
        var report = new
        {
            FormatVersion = 1, Scenario = "literal-matcher-read-lease", MeasuredAtUtc = DateTimeOffset.UtcNow,
            ProductInformationalVersion = typeof(LiteralMatcher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            InputBytes = new FileInfo(path).Length, SourceCharacters = lease.Length,
            Encoding = manifest.RootElement.GetProperty("encoding").GetString(), Layout = manifest.RootElement.GetProperty("layout").GetString(),
            CacheCondition = Environment.GetEnvironmentVariable("CODEVIEW_PERF_CACHE") ?? "unknown", Samples = samples,
            CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            IndividualGcPausesAndLohAllocations = "not-traced; last-GC LOH sizes are not allocation totals",
            Workload = "literal matching and original position tracking over native snapshot reader chunks",
            DocumentBuildAndDecoding = "excluded", GuiRendering = "not-measured", WorkspaceSearch = "not-implemented"
        };
        var label = Environment.GetEnvironmentVariable("CODEVIEW_PERF_LABEL") ?? "literal-matcher";
        Assert.That(label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), Is.True);
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", label + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(report));
        Assert.That(samples.Select(sample => sample.Milliseconds), Has.All.LessThanOrEqualTo(1000));
        Assert.That(samples.Select(sample => sample.AllocatedBytes), Has.All.LessThanOrEqualTo(256 << 10));
        Assert.That(samples.Where(sample => sample.Hits > 0).Select(sample => sample.FirstHitMilliseconds), Has.All.LessThanOrEqualTo(50));
    }

    private static Sample Measure(string name, string query, SearchOptions options, DocumentReadLease lease)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var pause = GC.GetTotalPauseDuration();
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var watch = Stopwatch.StartNew();
        var matcher = LiteralMatcher.Create(query, options).Value;
        long hits = 0;
        TextRange? first = null;
        double? firstMs = null;
        bool Consume(TextRange hit)
        {
            if (hits++ == 0) { first = hit; firstMs = watch.Elapsed.TotalMilliseconds; }
            return true;
        }
        Func<TextRange, bool> consume = Consume;
        var characters = lease.VisitChunks(chunk => matcher.Push(chunk.Span, consume, CancellationToken.None), CancellationToken.None);
        matcher.Complete(consume, CancellationToken.None);
        return new(name, characters, hits, first, firstMs, watch.Elapsed.TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - allocated, (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
            Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(),
            GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes, matcher.BufferCapacity);
    }

    private sealed record Sample(string Name, long Characters, long Hits, TextRange? FirstHit, double? FirstHitMilliseconds,
        double Milliseconds, long AllocatedBytes, double GcPauseMilliseconds, int[] GcCollections, long LastGcLohBytes, int BufferCapacity);
}
