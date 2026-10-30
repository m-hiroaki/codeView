using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Settings;
using CodeView.Tests.Performance;

namespace CodeView.Tests.Presentation;

public sealed class FileTreePerformanceTests
{
    [Test, Category("Performance")]
    public void MeasureLogicalTreeProjection() => SingleThreadTestContext.Run(async () =>
    {
        var input = Environment.GetEnvironmentVariable("CODEVIEW_PERF_DIRECTORY");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("CODEVIEW_PERF_DIRECTORY指定時だけ測定します。");
        var path = SourcePath.Create(input!, Environment.CurrentDirectory);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(path.FullPath + ".manifest.json"));
        var source = new SourceAccess();
        using var operations = new AppOperations(new FileReader(source), new AvaloniaEditDocumentAdapter(), new DirectoryReader(source));
        using var memory = new ProcessMemory();
        using var process = Process.GetCurrentProcess();
        var before = memory.ReadBytes();
        var peak = before;
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var pause = GC.GetTotalPauseDuration();
        var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var yields = 0;
        var maxBatch = 0;
        var view = new FileTreeViewModel(async () =>
        {
            yields++;
            peak = Math.Max(peak, memory.ReadBytes());
            await Task.Yield();
        });
        view.Rows.CollectionChanged += (_, args) => maxBatch = Math.Max(maxBatch, args.NewItems?.Count ?? 0);
        var watch = Stopwatch.StartNew();
        var opened = await operations.OpenFolderAsync(path, SettingsSnapshot.Default);
        Assert.That(opened.Status, Is.EqualTo(OperationStatus.Success));
        var rootMs = watch.Elapsed.TotalMilliseconds;
        peak = Math.Max(peak, memory.ReadBytes());
        watch.Restart();
        await view.BindAsync(opened.Value.Tree);
        var projectionMs = watch.Elapsed.TotalMilliseconds;
        var rootRows = view.Rows.Count;
        var child = view.Rows.FirstOrDefault(row => row.CanExpand);
        double? childMs = null;
        var childRows = 0;
        if (child is not null)
        {
            watch.Restart();
            await view.ExpandAsync(child);
            childMs = watch.Elapsed.TotalMilliseconds;
            Assert.That(child.Node.Status, Is.EqualTo(TreeLoadStatus.Loaded));
            childRows = view.Rows.Count - rootRows;
            peak = Math.Max(peak, memory.ReadBytes());
            view.Collapse(child);
            Assert.That(view.Rows.Count, Is.EqualTo(rootRows));
        }
        peak = Math.Max(peak, memory.ReadBytes());
        var report = new
        {
            FormatVersion = 1, Scenario = "logical-tree-projection", MeasuredAtUtc = DateTimeOffset.UtcNow,
            ProductInformationalVersion = typeof(AppOperations).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            CacheCondition = Environment.GetEnvironmentVariable("CODEVIEW_PERF_CACHE") ?? "unknown",
            ExpectedRepositoryFiles = manifest.RootElement.GetProperty("files").GetInt64(), Layout = manifest.RootElement.GetProperty("layout").GetString(),
            RootOpenMilliseconds = rootMs, RootProjectionMilliseconds = projectionMs, ChildExpandMilliseconds = childMs,
            RootRows = rootRows, ChildRows = childRows, MaxAddBatch = maxBatch, ProjectionYields = yields,
            CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            GcPauseMilliseconds = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
            GcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray(),
            MemoryMetric = memory.Metric, MemoryBefore = before, SampledPeakMemory = peak, MemoryIncrease = peak - before,
            Sampling = "phase boundaries and projection yields; not a continuous peak",
            GuiRendering = "not-measured; serialized test context", FileBodiesRead = false
        };
        var label = Environment.GetEnvironmentVariable("CODEVIEW_PERF_LABEL") ?? "tree-projection";
        Assert.That(label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), Is.True);
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", label + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(report));
        Assert.That(rootRows, Is.EqualTo(opened.Value.RootListing!.Entries.Count));
        Assert.That(maxBatch, Is.LessThanOrEqualTo(256));
        Assert.That(rootMs + projectionMs, Is.LessThanOrEqualTo(1000));
        if (childMs is not null) Assert.That(childMs, Is.LessThanOrEqualTo(1000));
        Assert.That(report.CpuMilliseconds, Is.LessThanOrEqualTo(1000));
        Assert.That(report.MemoryIncrease, Is.LessThanOrEqualTo(64L << 20));
        operations.CloseWorkspace();
        view.Clear();
        await operations.PendingTreeWork;
        Assert.That(view.Rows, Is.Empty);
    });
}
