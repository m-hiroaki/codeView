using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
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

public sealed class TabLifecyclePerformanceTests
{
    [Test, Category("Performance")]
    public void MeasureFiveTabRetentionSwitchAndClose() => SingleThreadTestContext.Run(async () =>
    {
        var root = Environment.GetEnvironmentVariable("CODEVIEW_PERF_TAB_ROOT");
        if (string.IsNullOrEmpty(root)) Assert.Ignore("CODEVIEW_PERF_TAB_ROOTに5本文の測定ルートを指定してください。");
        var files = Directory.EnumerateFiles(root!, "*.cs").Order(StringComparer.Ordinal).Take(6).ToArray();
        Assert.That(files, Has.Length.EqualTo(5));
        var sizes = files.Select(file => new FileInfo(file).Length).ToArray();
        Assert.That(sizes, Has.All.EqualTo(50L << 20));
        var source = new CountingSource();
        using var operations = new AppOperations(new FileReader(source), new AvaloniaEditDocumentAdapter(), new DirectoryReader(source));
        var view = new MainWindowViewModel(operations, new SettingsStore(new(Path.Combine(root!, "portable.json"), Path.Combine(root!, "user.json"))), YieldAsync);
        await view.InitializeAsync([]);
        using var process = Process.GetCurrentProcess();
        using var memory = new ProcessMemory();
        var before = memory.ReadBytes();
        var peak = before;
        var heapBefore = GC.GetTotalMemory(false);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var cpu = process.TotalProcessorTime;
        var pause = GC.GetTotalPauseDuration();
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        using var cancellation = new CancellationTokenSource();
        var samples = 0;
        var sampler = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    peak = Math.Max(peak, memory.ReadBytes());
                    samples++;
                    await Task.Delay(10, cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        });
        var builds = new List<double>();
        var switches = new List<double>();
        var weak = new List<WeakReference>();
        long switchAllocated = 0;
        double closeMs = 0;
        long retainedHeap = 0, heapAfterClose = 0;
        long productionAllocated = 0;
        double productionCpu = 0, productionPause = 0;
        int[] productionCollections = [];
        int aliveAfterClose = -1;
        try
        {
            foreach (var file in files)
            {
                var watch = Stopwatch.StartNew();
                await view.OpenFilesAsync([file], OpenOrigin.Menu);
                builds.Add(watch.Elapsed.TotalMilliseconds);
                Assert.That(view.SelectedTab!.State.Status, Is.EqualTo(TabStatus.Ready));
                CaptureWeakReferences(view, weak);
            }
            Assert.That(view.Tabs, Has.Count.EqualTo(5));
            var switchStart = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 64; index++)
            {
                var selected = index % 5;
                var watch = Stopwatch.StartNew();
                view.SelectedTab = view.Tabs[selected];
                switches.Add(watch.Elapsed.TotalMilliseconds);
                Assert.That(MatchesLiveDocument(view, weak[selected * 2 + 1]), Is.True);
            }
            switchAllocated = GC.GetAllocatedBytesForCurrentThread() - switchStart;
            await view.OpenFilesAsync(files, OpenOrigin.Menu);
            Assert.That(source.OpenCount, Is.EqualTo(5), "Reopening existing tabs must not read their bodies.");
            retainedHeap = GC.GetTotalMemory(false);
            var closing = Stopwatch.StartNew();
            while (view.HasTabs) view.CloseFile();
            await view.PendingWork;
            closeMs = closing.Elapsed.TotalMilliseconds;
            Assert.That(view.Document, Is.Null);
            Assert.That(operations.Tabs.Tabs, Is.Empty);
            productionCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            productionAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
            productionPause = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
            productionCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
            CollectForDiagnosticsOnly();
            aliveAfterClose = CountAlive(weak);
            heapAfterClose = GC.GetTotalMemory(false);
        }
        finally
        {
            cancellation.Cancel();
            await sampler;
            view.Stop();
            await view.PendingWork;
        }
        peak = Math.Max(peak, memory.ReadBytes());
        var report = new
        {
            FormatVersion = 1, Scenario = "five-tab-viewmodel-lifecycle", MeasuredAtUtc = DateTimeOffset.UtcNow,
            ProductInformationalVersion = typeof(AppOperations).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            CacheCondition = Environment.GetEnvironmentVariable("CODEVIEW_PERF_CACHE") ?? "unknown",
            Files = files.Select(Path.GetFileName).ToArray(), InputBytes = sizes, BuildMilliseconds = builds,
            SwitchMilliseconds = switches, SwitchAllocatedBytes = switchAllocated, CloseMilliseconds = closeMs,
            OpenReadCount = source.OpenCount, CpuMilliseconds = productionCpu, AllocatedBytes = productionAllocated,
            GcPauseMilliseconds = productionPause, GcCollections = productionCollections,
            ManagedHeapBefore = heapBefore, ManagedHeapRetainedBeforeClose = retainedHeap, ManagedHeapAfterCloseAndDiagnosticGc = heapAfterClose,
            TrackedWeakReferences = weak.Count, AliveAfterCloseAndDiagnosticGc = aliveAfterClose,
            DiagnosticGc = "test-only; excluded from CPU/allocation/GC metrics; VM remains alive during collection",
            MemoryMetric = memory.Metric, MemoryBefore = before, SampledPeakMemory = peak, MemoryIncrease = peak - before,
            SamplingIntervalMilliseconds = 10, SampleCount = samples,
            GuiRendering = "not-measured; view model and document binding notification only", GrammarOrSearch = "not-enabled"
        };
        var label = Environment.GetEnvironmentVariable("CODEVIEW_PERF_LABEL") ?? "tab-lifecycle";
        Assert.That(label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), Is.True);
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", label + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(report));
        Assert.That(builds, Has.All.LessThanOrEqualTo(5000));
        Assert.That(productionCpu, Is.LessThanOrEqualTo(25_000));
        Assert.That(report.MemoryIncrease, Is.LessThanOrEqualTo(1536L << 20));
        Assert.That(productionPause, Is.LessThanOrEqualTo(750));
        Assert.That(switches, Has.All.LessThanOrEqualTo(100));
        Assert.That(switchAllocated, Is.LessThanOrEqualTo(1 << 20));
        Assert.That(aliveAfterClose, Is.Zero);
        GC.KeepAlive(view);
    });

    private static async Task YieldAsync() => await Task.Yield();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CaptureWeakReferences(MainWindowViewModel view, List<WeakReference> targets)
    {
        targets.Add(new(view.SelectedTab!.State.File!.Document));
        targets.Add(new(view.Document!));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool MatchesLiveDocument(MainWindowViewModel view, WeakReference target) => ReferenceEquals(view.Document, target.Target);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CountAlive(List<WeakReference> targets) => targets.Count(target => target.IsAlive);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectForDiagnosticsOnly()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class CountingSource : ISourceAccess
    {
        private readonly SourceAccess _source = new();
        public int OpenCount { get; private set; }
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct) { OpenCount++; return _source.OpenRead(path, ct); }
        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => _source.InspectPath(path, ct);
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => _source.ResolveDirectory(path, ct);
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct) => _source.EnumerateChildren(path, ct);
        public SourceStamp GetStamp(SourcePath path) => _source.GetStamp(path);
    }
}
