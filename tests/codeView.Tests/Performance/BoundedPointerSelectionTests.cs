using System.Diagnostics;
using System.Text.Json;
using CodeView.Documents;
using CodeView.Presentation;

namespace CodeView.Tests.Performance;

public sealed class BoundedPointerSelectionTests
{
    [Test]
    public void DragKeepsOriginalAnchorAcrossSlicesAndCopiesOriginalRange()
    {
        using var handle = Create(new string('x', 9000));
        using var source = new DocumentDisplaySession(handle);
        var drag = new BoundedPointerSelection();
        var first = source.CreateSlice(0, 1024);
        Assert.That(drag.Begin(source, first, 10, 1, false), Is.True);
        var next = source.CreateSlice(5000, 1024);
        Assert.That(drag.Move(source, next, 100, 1), Is.True);
        Assert.That(source.Anchor, Is.EqualTo(10));
        Assert.That(source.Caret, Is.EqualTo(next.SourceOffset + 100));
        using var copy = source.BeginCopy();
        Assert.That(copy.ReadText(CancellationToken.None), Is.EqualTo(new string('x', (int)source.SelectionLength)));
        Assert.That(drag.End(1), Is.True);
        Assert.That(drag.Move(source, next, 0, 1), Is.False);
    }

    [Test]
    public void ShiftPressPreservesExistingAnchorAndAllowsReversedSelection()
    {
        using var handle = Create("0123456789");
        using var source = new DocumentDisplaySession(handle);
        source.Select(8, 9);
        var slice = source.CreateSlice(0, 1024);
        var drag = new BoundedPointerSelection();
        Assert.That(drag.Begin(source, slice, 4, 1, true), Is.True);
        Assert.That(drag.Move(source, slice, 2, 1), Is.True);
        Assert.That((source.Anchor, source.Caret), Is.EqualTo((8L, 2L)));
        Assert.That(source.TryGetDisplaySelection(slice, out var visible), Is.True);
        Assert.That(visible.IsReversed, Is.True);
    }

    [Test]
    public void OtherPointerCannotReplaceMoveOrReleaseActiveDrag()
    {
        using var handle = Create("0123456789");
        using var source = new DocumentDisplaySession(handle);
        var slice = source.CreateSlice(0, 1024);
        var drag = new BoundedPointerSelection();
        Assert.That(drag.Begin(source, slice, 4, 1, false), Is.True);
        Assert.That(drag.Begin(source, slice, 6, 2, false), Is.False);
        Assert.That(drag.Move(source, slice, 8, 2), Is.False);
        Assert.That(drag.StepOutside(source, 2, 1, 0), Is.False);
        Assert.That(drag.End(2), Is.False);
        Assert.That(source.Caret, Is.EqualTo(4));
        Assert.That(drag.PointerId, Is.EqualTo(1));
    }

    [Test]
    public void StaleSliceCannotStartOrMoveDragAndCancelPreservesSelection()
    {
        using var handle = Create("0123456789");
        using var source = new DocumentDisplaySession(handle);
        var old = source.CreateSlice(0, 4);
        var current = source.CreateSlice(0, 4);
        var drag = new BoundedPointerSelection();
        Assert.That(drag.Begin(source, old, 1, 1, false), Is.False);
        Assert.That(drag.IsActive, Is.False);
        Assert.That(drag.Begin(source, current, 1, 1, false), Is.True);
        Assert.That(drag.Move(source, old, 3, 1), Is.False);
        Assert.That(drag.Move(source, current with { }, 3, 1), Is.False);
        Assert.That(drag.Move(source, current, 3, 1), Is.True);
        drag.Cancel();
        drag.Cancel();
        Assert.That(drag.IsActive, Is.False);
        Assert.That(drag.StepOutside(source, 1, 1, 0), Is.False);
        Assert.That((source.Anchor, source.Caret), Is.EqualTo((1L, 3L)));
        Assert.That(drag.Begin(source, current, 2, 2, false), Is.True);
    }

    [TestCase(-1)]
    [TestCase(11)]
    public void InvalidHitDoesNotAdmitOrMutateDrag(int offset)
    {
        using var handle = Create("0123456789");
        using var source = new DocumentDisplaySession(handle);
        var slice = source.CreateSlice(0, 1024);
        var drag = new BoundedPointerSelection();
        Assert.Throws<ArgumentOutOfRangeException>(() => drag.Begin(source, slice, offset, 1, false));
        Assert.That(drag.IsActive, Is.False);
        Assert.That(source.Caret, Is.Zero);
    }

    [Test]
    public void HitAndEdgeStepKeepSurrogatesCrLfAndOriginalAnchor()
    {
        using var handle = Create("a😀\r\n" + new string('x', 100));
        using var source = new DocumentDisplaySession(handle);
        var slice = source.CreateSlice(0, 1024);
        var drag = new BoundedPointerSelection();
        drag.Begin(source, slice, 2, 1, false);
        Assert.That(source.Anchor, Is.EqualTo(1));
        drag.Move(source, slice, 4, 1);
        Assert.That(source.Caret, Is.EqualTo(3));
        Assert.That(drag.StepOutside(source, 1, 1, 0), Is.True);
        Assert.That(source.Caret, Is.EqualTo(36));
        Assert.That(source.Anchor, Is.EqualTo(1));
    }

    [TestCase("")]
    [TestCase("a")]
    public void EdgeStepsStopAtDocumentBoundsWithoutClearingAnchor(string text)
    {
        using var handle = Create(text);
        using var source = new DocumentDisplaySession(handle);
        var slice = source.CreateSlice(0, 1024);
        var drag = new BoundedPointerSelection();
        drag.Begin(source, slice, 0, 1, false);
        Assert.That(drag.StepOutside(source, 1, -1, 0), Is.False);
        drag.StepOutside(source, 1, 1, 0);
        Assert.That(source.Caret, Is.EqualTo(text.Length));
        Assert.That(drag.StepOutside(source, 1, 1, 0), Is.False);
        Assert.That(source.Anchor, Is.Zero);
        Assert.That(drag.IsActive, Is.True);
    }

    [Test]
    public void VerticalStepTakesPriorityAndRetainsPreferredColumn()
    {
        using var handle = Create("123456\nx\n123456");
        using var source = new DocumentDisplaySession(handle);
        var slice = source.CreateSlice(0, 1024);
        var drag = new BoundedPointerSelection();
        drag.Begin(source, slice, 5, 1, false);
        Assert.That(drag.StepOutside(source, 1, 1, 1), Is.True);
        Assert.That(source.Position.Column, Is.EqualTo(2));
        Assert.That(drag.StepOutside(source, 1, 0, 1), Is.True);
        Assert.That(source.Position.Column, Is.EqualTo(6));
        Assert.That(source.Anchor, Is.EqualTo(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => drag.StepOutside(source, 1, 2, 0));
    }

    [Test, Category("Performance")]
    public void MeasureOneHundredThousandDragCycles()
    {
        using var handle = Create(new string('x', 1024));
        using var source = new DocumentDisplaySession(handle);
        var slice = source.CreateSlice(0, 1024);
        var drag = new BoundedPointerSelection();
        for (var index = 0; index < 1000; index++)
        { drag.Begin(source, slice, 10, 1, false); drag.Move(source, slice, 900, 1); drag.End(1); }
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var pause = GC.GetTotalPauseDuration();
        var counts = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var index = 0; index < 100000; index++)
        { drag.Begin(source, slice, 10, 1, false); drag.Move(source, slice, 900, 1); drag.End(1); }
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var pauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
        var collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - counts[i]).ToArray();
        var diagnostics = new Dictionary<string, long>();
        void Check(string name, Action action)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 100000; index++) action();
            diagnostics[name] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Check("offset-conversion", () => slice.ToSourceOffset(10));
        Check("source-move", () => source.MoveCaret(10));
        Check("display-move", () => source.TryMoveFromDisplay(slice, 900));
        Check("drag-cycles", () => { drag.Begin(source, slice, 10, 1, false); drag.Move(source, slice, 900, 1); drag.End(1); });
        var report = new { Scenario = "100000-source-drag-cycles", MeasuredAtUtc = DateTimeOffset.UtcNow,
            Milliseconds = elapsed, CpuMilliseconds = cpuMs, AllocatedBytes = allocated,
            GcPauseMilliseconds = pauseMs, GcCollections = collections, ActiveReadLeases = handle.ActiveReadLeases,
            DiagnosticAllocations = diagnostics, PointerActive = drag.IsActive, source.Anchor, source.Caret,
            NativeInput = "not-measured", Rendering = "not-measured", OsMemory = "not-measured" };
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "pointer-cycles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        Assert.Multiple(() =>
        {
            Assert.That(elapsed, Is.LessThanOrEqualTo(1000));
            Assert.That(cpuMs, Is.LessThanOrEqualTo(1000));
            Assert.That(allocated, Is.LessThanOrEqualTo(1 << 20));
            Assert.That(pauseMs, Is.LessThanOrEqualTo(100));
            Assert.That(drag.IsActive, Is.False);
            Assert.That((source.Anchor, source.Caret), Is.EqualTo((10L, 900L)));
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
