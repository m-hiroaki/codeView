using CodeView.Presentation;
using CodeView.Documents;

namespace CodeView.Tests.Performance;

public sealed class ScrollProbePlanTests
{
    [Test]
    public void NormalProductBuildDoesNotIncludeProbe()
    {
        Assert.That(typeof(MainWindowViewModel).Assembly.GetType("CodeView.Presentation.GuiPerformanceProbe"), Is.Null);
        Assert.That(typeof(MainWindowViewModel).Assembly.GetType("CodeView.Presentation.TextWindow"), Is.Null);
        Assert.That(typeof(MainWindowViewModel).Assembly.GetType("CodeView.Presentation.GuiNavigationPlan"), Is.Null);
        Assert.That(typeof(MainWindowViewModel).Assembly.GetType("CodeView.Presentation.BoundedKeyMap"), Is.Null);
    }

    [Test]
    public void SourceSelectionPlanIncludesReverseFullAndCrossWindowSelection()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(new string('a', 4096).AsMemory());
        using var handle = builder.Complete();
        using var display = new DocumentDisplaySession(handle);
        for (var step = 0; step < GuiNavigationPlan.StepCount; step++)
        {
            GuiNavigationPlan.Apply(display, step);
            if (step == 2) Assert.That((display.Anchor, display.Caret), Is.EqualTo((4096, 0)));
            if (step == 3) Assert.That((display.Anchor, display.Caret), Is.EqualTo((0, 4096)));
            if (step == 62) Assert.That((display.Anchor, display.Caret), Is.EqualTo((0, 1856)));
            var slice = display.CreateSlice(display.Caret, 1024);
            Assert.That(display.TryGetDisplaySelection(slice, out var projected), Is.True);
            Assert.That(projected.CaretOffset, Is.Not.Null);
            Assert.That(projected.Start + projected.Length, Is.LessThanOrEqualTo(slice.Text.Length));
        }
        Assert.That(display.SelectionLength, Is.EqualTo(4096));
    }

    [Test]
    public void InvalidSourcePlanStepDoesNotMoveCaret()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append("abc".AsMemory());
        using var handle = builder.Complete();
        using var display = new DocumentDisplaySession(handle);
        display.MoveCaret(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => GuiNavigationPlan.Apply(display, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GuiNavigationPlan.Apply(display, 64));
        Assert.Throws<ArgumentNullException>(() => GuiNavigationPlan.Apply(null!, 0));
        Assert.That(display.Caret, Is.EqualTo(1));
    }

    [Test]
    public void NormalPlanIncludesContinuousScrollAndEndOfFile()
    {
        Assert.That(ScrollProbePlan.Target(0, 1_000_000, 80), Is.EqualTo((1, 1)));
        Assert.That(ScrollProbePlan.Target(31, 1_000_000, 80), Is.EqualTo((249, 1)));
        Assert.That(ScrollProbePlan.Target(63, 1_000_000, 80), Is.EqualTo((1_000_000, 1)));
    }

    [Test]
    public void EmptyShortAndExtremeDocumentsStayInRange()
    {
        Assert.That(ScrollProbePlan.Target(63, 1, 0), Is.EqualTo((1, 1)));
        Assert.That(ScrollProbePlan.Target(31, 2, 3), Is.EqualTo((2, 1)));
        Assert.That(ScrollProbePlan.Target(63, int.MaxValue, 0), Is.EqualTo((int.MaxValue, 1)));
        Assert.That(ScrollProbePlan.Target(63, 1, int.MaxValue - 1), Is.EqualTo((1, int.MaxValue)));
    }

    [TestCase(-1, 1, 0)]
    [TestCase(64, 1, 0)]
    [TestCase(0, 0, 0)]
    [TestCase(0, 1, -1)]
    [TestCase(0, 1, int.MaxValue)]
    public void InvalidPlanInputsFail(int step, int lines, int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrollProbePlan.Target(step, lines, length));
}
