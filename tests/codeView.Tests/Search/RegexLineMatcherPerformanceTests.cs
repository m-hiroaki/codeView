using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Search;

namespace CodeView.Tests.Search;

public sealed class RegexLineMatcherPerformanceTests
{
    [Test, Category("Performance")]
    public async Task MeasureBoundedRegexLinesOverImmutableChunks()
    {
        var root = Environment.GetEnvironmentVariable("CODEVIEW_PERF_REGEX_ROOT");
        if (string.IsNullOrEmpty(root)) Assert.Ignore("CODEVIEW_PERF_REGEX_ROOT指定時だけ測定します。");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(root! + ".manifest.json"));
        var singleLine = manifest.RootElement.GetProperty("layout").GetString() == "single-line";
        var path = Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories).Single();
        using var document = await Task.Run(() =>
        {
            var reader = new FileReader(new SourceAccess());
            var opened = reader.Prepare(SourcePath.Create(path, Environment.CurrentDirectory), EncodingChoice.Auto, CancellationToken.None);
            Assert.That(opened.Status, Is.EqualTo(OperationStatus.Success));
            using var file = opened.Value;
            using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
            Assert.That(reader.ReadChunks(file, builder.Append, CancellationToken.None).Status, Is.EqualTo(OperationStatus.Success));
            return builder.Complete();
        });
        using var lease = document.AcquireReadView();
        document.Dispose();
        var cases = new[]
        {
            ("marker", "CODEVIEW_MATCH", new SearchOptions(UseRegex: true)),
            ("lookbehind-marker", "(?<=// )CODEVIEW_MATCH", new SearchOptions(UseRegex: true)),
            ("whole-word-marker", "CODEVIEW_MATCH", new SearchOptions(WholeWord: true, UseRegex: true)),
            ("not-found", "CODEVIEW_ABSENT_QUERY", new SearchOptions(UseRegex: true))
        };
        var samples = new List<Sample>();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        foreach (var (name, pattern, options) in cases)
        {
            var sample = await Task.Run(() => Measure(name, pattern, options, lease));
            samples.Add(sample);
            Assert.That(sample.Hits, Is.EqualTo(singleLine || name == "not-found" ? 0 : 1));
            Assert.That(sample.SkippedLongLines, Is.EqualTo(singleLine ? 1 : 0));
            Assert.That(sample.SkippedScalarMatches, Is.Zero);
            Assert.That(sample.Characters, Is.EqualTo(lease.Length));
            if (sample.Hits > 0) Assert.That(sample.FirstHit, Is.EqualTo(new TextRange(3, 14, new(1, 4))));
        }
        var report = new
        {
            FormatVersion = 1, Scenario = "bounded-regex-line-matcher", MeasuredAtUtc = DateTimeOffset.UtcNow,
            ProductInformationalVersion = typeof(RegexLineMatcher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            InputBytes = new FileInfo(path).Length, SourceCharacters = lease.Length,
            Encoding = manifest.RootElement.GetProperty("encoding").GetString(), Layout = manifest.RootElement.GetProperty("layout").GetString(),
            CacheCondition = Environment.GetEnvironmentVariable("CODEVIEW_PERF_CACHE") ?? "unknown", Samples = samples,
            CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            IndividualGcPausesAndLohAllocations = "not-traced; allocated bytes include the bounded line buffer and Regex objects",
            DocumentBuildAndDecoding = "excluded", GuiRendering = "not-measured", WorkspaceSearch = "not-implemented"
        };
        var label = Environment.GetEnvironmentVariable("CODEVIEW_PERF_LABEL") ?? "regex-line-matcher";
        Assert.That(label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), Is.True);
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", label + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(report));
        Assert.That(samples.Select(sample => sample.Milliseconds), Has.All.LessThanOrEqualTo(2000));
        Assert.That(samples.Select(sample => sample.AllocatedBytes), Has.All.LessThanOrEqualTo(3 << 20));
        Assert.That(samples.Where(sample => sample.Hits > 0).Select(sample => sample.FirstHitMilliseconds), Has.All.LessThanOrEqualTo(50));
    }

    private static Sample Measure(string name, string pattern, SearchOptions options, DocumentReadLease lease)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var pause = GC.GetTotalPauseDuration();
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var watch = Stopwatch.StartNew();
        var matcher = RegexLineMatcher.Create(pattern, options).Value;
        long hits = 0;
        TextRange? first = null;
        double? firstMs = null;
        Func<TextRange, bool> consume = hit =>
        {
            if (hits++ == 0) { first = hit; firstMs = watch.Elapsed.TotalMilliseconds; }
            return true;
        };
        var characters = lease.VisitChunks(chunk => matcher.Push(chunk.Span, consume, CancellationToken.None), CancellationToken.None);
        matcher.Complete(consume, CancellationToken.None);
        Assert.That(matcher.Error, Is.Null);
        return new(name, characters, hits, first, firstMs, watch.Elapsed.TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - allocated, (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
            Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(),
            matcher.EvaluatedLines, matcher.SkippedLongLines, matcher.SkippedScalarMatches, matcher.BufferCapacity);
    }

    private sealed record Sample(string Name, long Characters, long Hits, TextRange? FirstHit, double? FirstHitMilliseconds,
        double Milliseconds, long AllocatedBytes, double GcPauseMilliseconds, int[] GcCollections,
        long EvaluatedLines, long SkippedLongLines, long SkippedScalarMatches, int BufferCapacity);
}
