using CodeView.Presentation;

namespace CodeView.Tests.Performance;

public sealed class RowWheelAccumulatorTests
{
    [Test]
    public void FractionalTrackpadDeltasAccumulateInsteadOfDisappearing()
    {
        var wheel = new RowWheelAccumulator();
        Assert.That(wheel.Consume(0, -.125, false), Is.EqualTo((0L, 0L)));
        Assert.That(wheel.Consume(0, -.125, false), Is.EqualTo((0L, 0L)));
        Assert.That(wheel.Consume(0, -.125, false), Is.EqualTo((1L, 0L)));
        Assert.That(wheel.Consume(-.03125, 0, false), Is.EqualTo((0L, 0L)));
        Assert.That(wheel.Consume(-.03125, 0, false), Is.EqualTo((0L, 1L)));
    }

    [Test]
    public void DiagonalAndShiftWheelsUseIndependentLogicalAxes()
    {
        var wheel = new RowWheelAccumulator();
        Assert.That(wheel.Consume(-1, -1, false), Is.EqualTo((3L, 16L)));
        Assert.That(wheel.Consume(0, -1, true), Is.EqualTo((0L, 16L)));
        Assert.That(wheel.Consume(1, -1, true), Is.EqualTo((0L, 0L)));
        Assert.That(wheel.Consume(1, 1, false), Is.EqualTo((-3L, -16L)));
    }

    [TestCase(double.NaN, 0)]
    [TestCase(0, double.PositiveInfinity)]
    [TestCase(double.NegativeInfinity, 0)]
    public void NonfiniteInputDoesNotConsumePendingFractions(double x, double y)
    {
        var wheel = new RowWheelAccumulator();
        wheel.Consume(-.03125, -.25, false);
        Assert.That(wheel.Consume(x, y, false), Is.EqualTo((0L, 0L)));
        Assert.That(wheel.Consume(-.03125, -.125, false), Is.EqualTo((1L, 1L)));
    }

    [Test]
    public void HugeEventsAreCappedAndDoNotQueueUnboundedFutureWork()
    {
        var wheel = new RowWheelAccumulator();
        Assert.That(wheel.Consume(-double.MaxValue, -double.MaxValue, false), Is.EqualTo((512L, 4096L)));
        Assert.That(wheel.Consume(0, 0, false), Is.EqualTo((0L, 0L)));
        wheel.Consume(-.03125, -.25, false);
        wheel.Reset();
        Assert.That(wheel.Consume(0, 0, false), Is.EqualTo((0L, 0L)));
    }
}
