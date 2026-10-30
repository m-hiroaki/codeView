using CodeView.Presentation;

namespace CodeView.Tests.Performance;

public sealed class ScrollProbePlanTests
{
    [Test]
    public void NormalProductBuildDoesNotIncludeProbe() =>
        Assert.That(typeof(MainWindowViewModel).Assembly.GetType("CodeView.Presentation.GuiPerformanceProbe"), Is.Null);

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
