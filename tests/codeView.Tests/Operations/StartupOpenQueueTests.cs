using CodeView.Contracts;
using CodeView.Operations;
using CodeView.Settings;

namespace CodeView.Tests.Operations;

public sealed class StartupOpenQueueTests
{
    private static SettingsLoadResult Defaults() => new(SettingsSnapshot.Default, null, null, true);

    [Test]
    public void RequestsWaitForExactlyOneSettingsCompletionAndRemainFifo()
    {
        var queue = new StartupOpenQueue();
        var a = queue.Submit(new[] { "a.cs" }, OpenOrigin.CommandLine).Value;
        var b = queue.Submit(new[] { "b.cs" }, OpenOrigin.Drop).Value;
        Assert.That(queue.TryTake(out _), Is.False);
        Assert.That(queue.CompleteSettings(Defaults()), Is.True);
        Assert.That(queue.CompleteSettings(Defaults()), Is.False);
        var c = queue.Submit(new[] { "c.cs" }, OpenOrigin.Menu).Value;
        foreach (var expected in new[] { a, b, c })
        {
            Assert.That(queue.TryTake(out var request), Is.True);
            Assert.That(request!.Id, Is.EqualTo(expected.Id));
            Assert.That(queue.CompleteOpen(request.Id), Is.True);
            Assert.That(queue.CompleteOpen(request.Id), Is.False);
        }
        Assert.That(queue.TryTake(out _), Is.False);
        Assert.That(queue.OutstandingCount, Is.Zero);
    }

    [Test]
    public void StartedRequestsStillConsumeBudgetUntilCompletion()
    {
        var queue = new StartupOpenQueue();
        queue.CompleteSettings(Defaults());
        for (var i = 0; i < StartupOpenQueue.MaxRequests; i++)
            Assert.That(queue.Submit(new[] { $"{i}.cs" }, OpenOrigin.Menu).Status, Is.EqualTo(OperationStatus.Success));
        Assert.That(queue.TryTake(out var active), Is.True);
        Assert.That(queue.Submit(new[] { "extra.cs" }, OpenOrigin.Drop).Error!.Code, Is.EqualTo(ErrorCode.QueueFull));
        Assert.That(queue.CompleteOpen(active!.Id), Is.True);
        Assert.That(queue.Submit(new[] { "extra.cs" }, OpenOrigin.Drop).Status, Is.EqualTo(OperationStatus.Success));
    }

    [Test]
    public void PathBudgetRejectsWholeRequestsAndInputIsCopied()
    {
        var queue = new StartupOpenQueue();
        var paths = new[] { "original.cs" };
        var first = queue.Submit(paths, OpenOrigin.Menu).Value;
        paths[0] = "changed.cs";
        Assert.That(first.Paths[0], Is.EqualTo("original.cs"));
        Assert.That(queue.Submit(Enumerable.Repeat("x", 256).ToArray(), OpenOrigin.Drop).Status, Is.EqualTo(OperationStatus.Failure));
        Assert.That(queue.OutstandingCount, Is.EqualTo(1));
        Assert.That(queue.CompleteOpen(first.Id), Is.False, "A request still waiting must not release budget.");
    }

    [Test]
    public void CharacterBudgetHasExactBoundary()
    {
        var queue = new StartupOpenQueue();
        Assert.That(queue.Submit(new[] { new string('x', StartupOpenQueue.MaxPathChars) }, OpenOrigin.Menu).Status,
            Is.EqualTo(OperationStatus.Success));
        Assert.That(queue.Submit(new[] { "x" }, OpenOrigin.Menu).Error!.Code, Is.EqualTo(ErrorCode.QueueFull));
    }

    [Test]
    public void ExactPathLimitIsAcceptedAndReleasedOnlyWhenTheRequestFinishes()
    {
        var queue = new StartupOpenQueue();
        var paths = Enumerable.Range(0, StartupOpenQueue.MaxPaths).Select(i => $"{i}.cs").ToArray();
        var request = queue.Submit(paths, OpenOrigin.Drop).Value;
        Assert.That(queue.Submit(["extra.cs"], OpenOrigin.Menu).Error!.Code, Is.EqualTo(ErrorCode.QueueFull));
        queue.CompleteSettings(Defaults());
        Assert.That(queue.TryTake(out var started), Is.True);
        Assert.That(started, Is.SameAs(request));
        Assert.That(queue.Submit(["extra.cs"], OpenOrigin.Menu).Error!.Code, Is.EqualTo(ErrorCode.QueueFull));
        Assert.That(queue.CompleteOpen(request.Id), Is.True);
        Assert.That(queue.Submit(paths, OpenOrigin.Drop).Status, Is.EqualTo(OperationStatus.Success));
    }

    [Test]
    public void EmptyAndOversizedRequestsDoNotPartiallyReservePaths()
    {
        var queue = new StartupOpenQueue();
        Assert.That(queue.Submit([], OpenOrigin.Menu).Error!.Code, Is.EqualTo(ErrorCode.InvalidPath));
        Assert.That(queue.Submit(["a.cs"], (OpenOrigin)999).Error!.Code, Is.EqualTo(ErrorCode.InvalidPath));
        Assert.That(queue.Submit(Enumerable.Repeat("x", StartupOpenQueue.MaxPaths + 1).ToArray(), OpenOrigin.Drop).Error!.Code,
            Is.EqualTo(ErrorCode.QueueFull));
        Assert.That(queue.OutstandingCount, Is.Zero);
        Assert.That(queue.Submit(Enumerable.Repeat("x", StartupOpenQueue.MaxPaths).ToArray(), OpenOrigin.Drop).Status,
            Is.EqualTo(OperationStatus.Success));
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("bad\0.cs")]
    public void InvalidRequestsDoNotReserveBudget(string path)
    {
        var queue = new StartupOpenQueue();
        Assert.That(queue.Submit(new[] { path }, OpenOrigin.Menu).Error!.Code, Is.EqualTo(ErrorCode.InvalidPath));
        Assert.That(queue.OutstandingCount, Is.Zero);
    }

    [Test]
    public void StopDropsWaitingRequestsAndIgnoresLateSettings()
    {
        var queue = new StartupOpenQueue();
        queue.Submit(new[] { "a.cs" }, OpenOrigin.CommandLine);
        queue.Stop();
        queue.Stop();
        Assert.That(queue.CompleteSettings(Defaults()), Is.False);
        Assert.That(queue.TryTake(out _), Is.False);
        Assert.That(queue.Submit(new[] { "b.cs" }, OpenOrigin.Menu).Status, Is.EqualTo(OperationStatus.Cancelled));
        Assert.That(queue.OutstandingCount, Is.Zero);
    }
}
