using CodeView.Presentation;

namespace CodeView.Tests.Performance;

public sealed class StartupTimelineTests
{
    private static readonly string[] Stages =
    [
        "main-entered", "builder-configured", "app-initialize-start", "app-initialize-end",
        "framework-start", "window-constructor-start", "window-xaml-loaded",
        "probe-attach-start", "probe-attach-end", "window-constructor-end", "framework-end", "window-opened"
    ];

    [Test]
    public void CompleteTimelineHasBoundedOrderedCheckpointsAndIndependentSnapshots()
    {
        var timeline = new StartupTimeline();
        Assert.That(timeline.Snapshot(), Is.Empty);
        timeline.Record(Stages[0], 0);
        var prefix = timeline.Snapshot();
        for (var i = 1; i < Stages.Length; i++) timeline.Record(Stages[i], i * 10);
        Assert.That(timeline.Snapshot().Select(x => x.Stage), Is.EqualTo(Stages));
        Assert.That(timeline.Snapshot()[^1].Milliseconds, Is.EqualTo(110));
        Assert.That(prefix.Count, Is.EqualTo(1));
        Assert.Throws<InvalidOperationException>(() => timeline.Record("window-opened", 120));
        Assert.That(timeline.Snapshot().Count, Is.EqualTo(12));
    }

    [TestCase(-1)]
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    public void InvalidTimeDoesNotAdvanceTimeline(double time)
    {
        var timeline = new StartupTimeline();
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.Record(Stages[0], time));
        Assert.That(timeline.Snapshot(), Is.Empty);
        timeline.Record(Stages[0], 0);
    }

    [Test]
    public void SkippedDuplicateAndDecreasingCheckpointsCannotCorruptPriorState()
    {
        var timeline = new StartupTimeline();
        Assert.Throws<InvalidOperationException>(() => timeline.Record(Stages[1], 0));
        timeline.Record(Stages[0], 10);
        Assert.Throws<InvalidOperationException>(() => timeline.Record(Stages[0], 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.Record(Stages[1], 9));
        timeline.Record(Stages[1], 10); // Equal timestamps are a valid clock-resolution boundary.
        Assert.That(timeline.Snapshot().Select(x => x.Milliseconds), Is.EqualTo(new[] { 10d, 10d }));
    }
}
