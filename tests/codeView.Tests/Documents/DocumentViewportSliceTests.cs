using System.Diagnostics;
using System.Text.Json;
using AvaloniaEdit.Document;
using CodeView.Documents;

namespace CodeView.Tests.Documents;

public sealed class DocumentViewportSliceTests
{
    [TestCase("\n", 0)]
    [TestCase("\r", 80)]
    [TestCase("\r\n", 999)]
    public void NormalLinesFillRequestedRowsAndKeepOriginalCoordinates(string newline, int targetLine)
    {
        var text = string.Join(newline, Enumerable.Range(1, 1000).Select(n => $"// 注文😀 {n:0000}"));
        using var handle = Create(text);
        var document = handle.ClaimForDisplay();
        using var source = new DocumentDisplaySession(handle);
        var target = document.GetLineByNumber(targetLine + 1).Offset;
        source.Select(1, 3);
        var slice = source.CreateViewportSlice(target, 64, 80);
        var display = new TextDocument(slice.Text);
        Assert.Multiple(() =>
        {
            Assert.That(display.LineCount, Is.GreaterThanOrEqualTo(80));
            Assert.That(slice.Text.Length, Is.GreaterThan(64));
            Assert.That(slice.Text.Length, Is.LessThanOrEqualTo(DocumentDisplaySession.MaximumSliceChars));
            Assert.That(slice.Text, Is.EqualTo(text.Substring((int)slice.SourceOffset, slice.Text.Length)));
            Assert.That(slice.ToSourceOffset(slice.TargetDisplayOffset), Is.EqualTo(target));
            Assert.That((source.Anchor, source.Caret), Is.EqualTo((1L, 3L)));
            Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
        });
        foreach (var line in display.Lines)
            Assert.That(document.GetLocation((int)slice.SourceOffset + line.Offset).Line,
                Is.EqualTo(slice.SourceStart.Line + line.LineNumber - 1));
    }

    [TestCase("")]
    [TestCase("a")]
    [TestCase("a\r\n")]
    [TestCase("😀\r\n短\n")]
    public void ShortDocumentsAndTrailingEmptyLineRetainAllText(string text)
    {
        using var handle = Create(text);
        using var source = new DocumentDisplaySession(handle);
        var slice = source.CreateViewportSlice(text.Length, 64, 80);
        Assert.That(slice.Text, Is.EqualTo(text));
        Assert.That(slice.SourceOffset, Is.Zero);
        Assert.That(slice.TargetOffset, Is.EqualTo(text.Length));
    }

    [TestCase(0)]
    [TestCase(4000)]
    [TestCase(8000)]
    public void LongLineKeepsOriginalCharacterBoundInsteadOfExpandingToFillRows(int offset)
    {
        var text = new string('x', 8000) + "😀\r\nlast";
        using var handle = Create(text);
        using var source = new DocumentDisplaySession(handle);
        var old = source.CreateSlice(offset, 1024);
        var current = source.CreateViewportSlice(offset, 1024, 80);
        Assert.That(current, Is.EqualTo(old));
        Assert.That(current.Text.Length, Is.LessThanOrEqualTo(1024));
        Assert.That(source.TryMoveFromDisplay(old, 0), Is.False);
        Assert.That(source.TryMoveFromDisplay(current, current.TargetDisplayOffset), Is.True);
    }

    [Test]
    public void TotalBudgetAndPerLineBudgetAreIndependent()
    {
        var text = string.Join('\n', Enumerable.Repeat(new string('x', 1024), 100));
        using var handle = Create(text);
        using var source = new DocumentDisplaySession(handle);
        Assert.That(source.CreateViewportSlice(0, 1024, 2).Text.Length, Is.EqualTo(2050));
        Assert.That(source.CreateViewportSlice(0, 1024, 80).Text.Length, Is.LessThanOrEqualTo(1024));
    }

    [TestCase(1)]
    [TestCase(512)]
    public void MinimumAndMaximumRowRequestsRemainBounded(int rows)
    {
        using var handle = Create(string.Concat(Enumerable.Repeat("a\r\n", 1000)));
        using var source = new DocumentDisplaySession(handle);
        var slice = source.CreateViewportSlice(1500, 4, rows);
        Assert.That(new TextDocument(slice.Text).LineCount, Is.GreaterThanOrEqualTo(rows));
        Assert.That(slice.Text.Length, Is.LessThanOrEqualTo(DocumentDisplaySession.MaximumSliceChars));
        Assert.That(slice.TargetOffset, Is.EqualTo(1500));
    }

    [TestCase(-1, 1024, 80)]
    [TestCase(4, 1024, 80)]
    [TestCase(0, 3, 80)]
    [TestCase(0, 16385, 80)]
    [TestCase(0, 1024, 0)]
    [TestCase(0, 1024, 513)]
    public void InvalidRequestsDoNotInvalidateCurrentSlice(int offset, int chars, int rows)
    {
        using var handle = Create("abc");
        using var source = new DocumentDisplaySession(handle);
        var current = source.CreateSlice(0, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.CreateViewportSlice(offset, chars, rows));
        Assert.That(source.TryMoveFromDisplay(current, 0), Is.True);
    }

    [Test]
    public void RequestedTargetDoesNotSplitCrLfOrSurrogateAndClosedSessionRejectsRequests()
    {
        using var handle = Create("a😀\r\nb");
        using var source = new DocumentDisplaySession(handle);
        Assert.That(source.CreateViewportSlice(2, 64, 80).TargetOffset, Is.EqualTo(1));
        Assert.That(source.CreateViewportSlice(4, 64, 80).TargetOffset, Is.EqualTo(3));
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.CreateViewportSlice(0, 64, 80));
    }

    [Test, Category("Performance")]
    public void MeasureViewportSlices()
    {
        using var handle = Create(string.Join('\n', Enumerable.Range(0, 100000).Select(n => $"// line {n:000000}")));
        using var source = new DocumentDisplaySession(handle);
        for (var n = 0; n < 100; n++) source.CreateViewportSlice(n * 1000, 1024, 80);
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var pause = GC.GetTotalPauseDuration();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var largest = 0;
        for (var n = 0; n < 1000; n++)
            largest = Math.Max(largest, source.CreateViewportSlice(source.Length * n / 999, 1024, 80).Text.Length);
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var pauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "viewport-slices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Milliseconds = watch.Elapsed.TotalMilliseconds,
            CpuMilliseconds = cpuMs, AllocatedBytes = allocated, GcPauseMilliseconds = pauseMs, LargestSliceChars = largest,
            handle.ActiveReadLeases }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        Assert.Multiple(() =>
        {
            Assert.That(watch.Elapsed.TotalMilliseconds, Is.LessThanOrEqualTo(1000));
            Assert.That(cpuMs, Is.LessThanOrEqualTo(1000));
            Assert.That(allocated, Is.LessThanOrEqualTo(40 << 20));
            Assert.That(pauseMs, Is.LessThanOrEqualTo(100));
            Assert.That(largest, Is.LessThanOrEqualTo(DocumentDisplaySession.MaximumSliceChars));
            Assert.That(handle.ActiveReadLeases, Is.EqualTo(1));
        });
    }

    private static DocumentHandle Create(string text)
    {
        using var builder = new DocumentBuilder();
        builder.Append(text.AsMemory());
        return builder.Complete();
    }
}
