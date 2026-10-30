namespace CodeView.Tests;

public sealed class ProcessExitStatusTests
{
    [Test]
    public void SuccessfulLifetimeAndNoFailureReturnZero() =>
        Assert.That(new ProcessExitStatus().Resolve(0), Is.Zero);

    [Test]
    public void RecordedFailureSurvivesLaterSuccessfulLifetimeShutdown()
    {
        var status = new ProcessExitStatus();
        status.MarkFailed();
        Assert.That(status.Resolve(0), Is.EqualTo(1));
        status.MarkFailed();
        Assert.That(status.Resolve(0), Is.EqualTo(1));
    }

    [TestCase(1)]
    [TestCase(42)]
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    [TestCase(int.MaxValue)]
    public void ExplicitLifetimeFailureIsPreserved(int code)
    {
        var status = new ProcessExitStatus();
        Assert.That(status.Resolve(code), Is.EqualTo(code));
        status.MarkFailed();
        Assert.That(status.Resolve(code), Is.EqualTo(code));
    }

    [Test]
    public async Task WorkerFailureCanBeObservedByExitResolution()
    {
        var status = new ProcessExitStatus();
        await Task.Run(status.MarkFailed);
        Assert.That(status.Resolve(0), Is.EqualTo(1));
    }
}
