using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;

namespace CodeView.Tests.Operations;

public sealed class AppOperationsTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "codeview-operations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "first.cs"), "class First { }");
        File.WriteAllText(Path.Combine(_root, "second.cs"), "class Second { }");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    private SourcePath PathOf(string name) => SourcePath.Create(name, _root);

    [Test]
    public async Task FailedReplacementKeepsOldDocumentAndCloseReleasesIt()
    {
        using var operations = new AppOperations(new FileReader(new SourceAccess()), new AvaloniaEditDocumentAdapter());
        var first = await operations.OpenFileAsync(PathOf("first.cs"), EncodingChoice.Auto);
        Assert.That(first.Status, Is.EqualTo(OperationStatus.Success));
        using var lease = first.Value.Document.AcquireReadView();
        var missing = await operations.OpenFileAsync(PathOf("missing.cs"), EncodingChoice.Auto);
        Assert.That(missing.Error!.Code, Is.EqualTo(ErrorCode.NotFound));
        Assert.That(operations.CurrentFile, Is.SameAs(first.Value));
        Assert.That(lease.ReadRange(0, 15), Is.EqualTo("class First { }"));
        operations.CloseFile();
        Assert.That(first.Value.Document.IsReleased, Is.True);
        Assert.That(operations.CurrentFile, Is.Null);
    }

    [Test]
    public async Task NewLoadCancelsOldWorkerAndCannotAdoptOldResult()
    {
        using var source = new DelayedAccess();
        using var operations = new AppOperations(new FileReader(source), new AvaloniaEditDocumentAdapter());
        var first = operations.OpenFileAsync(PathOf("first.cs"), EncodingChoice.Auto);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = operations.OpenFileAsync(PathOf("second.cs"), EncodingChoice.Auto);
        Assert.That((await first).Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That((await second).Status, Is.EqualTo(OperationStatus.Success));
        Assert.That(operations.CurrentFile!.Path.FullPath, Is.EqualTo(PathOf("second.cs").FullPath));
        Assert.That(source.MaxActive, Is.EqualTo(1));
    }

    [Test]
    public async Task ClosingDuringReadCancelsWorkerAndStopRejectsNewLoads()
    {
        using var source = new DelayedAccess();
        using var operations = new AppOperations(new FileReader(source), new AvaloniaEditDocumentAdapter());
        var first = operations.OpenFileAsync(PathOf("first.cs"), EncodingChoice.Auto);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        operations.CloseFile();
        Assert.That((await first).Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That(operations.CurrentFile, Is.Null);
        operations.Dispose();
        Assert.That((await operations.OpenFileAsync(PathOf("second.cs"), EncodingChoice.Auto)).Status,
            Is.EqualTo(OperationStatus.Cancelled));
    }

    private sealed class DelayedAccess : ISourceAccess, IDisposable
    {
        private readonly SourceAccess _source = new();
        private readonly ManualResetEventSlim _gate = new();
        private int _active;
        public int MaxActive { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _active);
            MaxActive = Math.Max(MaxActive, active);
            try
            {
                if (path.FullPath.EndsWith("first.cs", StringComparison.Ordinal))
                {
                    Entered.TrySetResult();
                    _gate.Wait(ct);
                }
                return _source.OpenRead(path, ct);
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public SourceStamp GetStamp(SourcePath path) => _source.GetStamp(path);
        public void Dispose() => _gate.Dispose();
    }
}
