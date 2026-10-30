using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeView.Documents;
using CodeView.Tests.Performance;
using TextMateSharp.Registry;

namespace CodeView.Tests.Documents;

public sealed class GrammarPerformanceTests
{
    [Test, Category("Performance")]
    public async Task MeasureBundledGrammarInitialization()
    {
        if (Environment.GetEnvironmentVariable("CODEVIEW_PERF_GRAMMARS") != "1")
            Assert.Ignore("CODEVIEW_PERF_GRAMMARS=1の時だけ測定します。");
        using var process = Process.GetCurrentProcess();
        using var memory = new ProcessMemory();
        var before = memory.ReadBytes();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var pause = GC.GetTotalPauseDuration();
        var cpu = process.TotalProcessorTime;
        var counts = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var watch = Stopwatch.StartNew();
        var registry = new Registry(new BundledGrammars());
        var initializationMs = watch.Elapsed.TotalMilliseconds;
        var results = new List<object>();
        foreach (var (path, line) in new[]
        {
            ("sample.cs", "class Sample { string value = \"text\"; } // comment"),
            ("sample.c", "int value = 42; /* comment */"),
            ("sample.cpp", "template<class T> struct Box { T value; };"),
            ("sample.json", "{\"value\": true}"),
            ("sample.xml", "<Item Value=\"text\"/>"),
            ("sample.md", "# Heading")
        })
        {
            watch.Restart();
            var grammar = registry.LoadGrammar(BundledGrammars.ScopeForPath(path));
            var loadMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var first = grammar.TokenizeLine(line, null, TimeSpan.FromSeconds(1));
            var firstTokenizeMs = watch.Elapsed.TotalMilliseconds;
            Assert.That(first.Tokens, Is.Not.Empty);
            watch.Restart();
            for (var index = 0; index < 100; index++)
                grammar.TokenizeLine(line, null, TimeSpan.FromSeconds(1));
            results.Add(new { Path = path, LoadMilliseconds = loadMs, FirstTokenizeMilliseconds = firstTokenizeMs,
                Repeated100Milliseconds = watch.Elapsed.TotalMilliseconds, MemoryAfter = memory.ReadBytes() });
        }
        var after = memory.ReadBytes();
        var report = new
        {
            FormatVersion = 1, MeasuredAtUtc = DateTimeOffset.UtcNow, Scenario = "bundled-grammar-initialization",
            Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = Environment.Version.ToString(), InitializationMilliseconds = initializationMs, Languages = results,
            AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            GcPauseMilliseconds = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
            GcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - counts[i]).ToArray(),
            MemoryMetric = memory.Metric, MemoryBefore = before, MemoryAfter = after, MemoryIncrease = after - before,
            PeakMemory = "not-sampled", GuiRendering = "not-measured", DocumentTokens = "not-measured"
        };
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "bundled-grammars.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(report));
        // Keep every loaded grammar alive through the memory observation.
        GC.KeepAlive(registry);
        Assert.That(report.MemoryIncrease, Is.LessThanOrEqualTo(64L << 20));
    }
}
