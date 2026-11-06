using CodeView.Presentation;

namespace CodeView.Tests.Presentation;

public sealed class ShutdownTests
{
    [Test]
    public async Task CompletedWorkStopsBeforeItIsObserved()
    {
        var stopped = false;
        var failures = 0;
        await MainWindow.StopAndWaitAsync(() => stopped = true, () =>
        {
            Assert.That(stopped, Is.True);
            return Task.CompletedTask;
        }, () => failures++);
        Assert.That(failures, Is.Zero);
    }

    [Test]
    public async Task StopFailureStillWaitsForActualWork()
    {
        var pending = new TaskCompletionSource();
        var failures = 0;
        var wait = MainWindow.StopAndWaitAsync(() => throw new InvalidOperationException(),
            () => pending.Task, () => failures++);
        Assert.That(failures, Is.EqualTo(1));
        Assert.That(wait.IsCompleted, Is.False);
        pending.SetResult();
        await wait;
        Assert.That(failures, Is.EqualTo(1));
    }

    [Test]
    public async Task PendingWorkCollectionFailureIsRecorded()
    {
        var failures = 0;
        await MainWindow.StopAndWaitAsync(() => { }, () => throw new InvalidOperationException(), () => failures++);
        Assert.That(failures, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WorkerFailureIncludingTimeoutExceptionIsNotTheWaitDeadline(bool timeout)
    {
        var failures = 0;
        Exception error = timeout ? new TimeoutException() : new InvalidOperationException();
        await MainWindow.StopAndWaitAsync(() => { }, () => Task.FromException(error), () => failures++);
        Assert.That(failures, Is.EqualTo(1));
    }

    [Test]
    public async Task UnrelatedCancellationIsFailure()
    {
        var failures = 0;
        await MainWindow.StopAndWaitAsync(() => { }, () => Task.FromCanceled(new CancellationToken(true)), () => failures++);
        Assert.That(failures, Is.EqualTo(1));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task DeadlineEndsWaitingButObservesLateFailure(int completion)
    {
        var clock = new ControlledClock();
        var pending = new TaskCompletionSource();
        var failures = 0;
        var wait = MainWindow.StopAndWaitAsync(() => { }, () => pending.Task, () => Interlocked.Increment(ref failures), clock);
        Assert.That(wait.IsCompleted, Is.False);
        Assert.That(clock.Timer!.Due, Is.EqualTo(TimeSpan.FromSeconds(2)));
        clock.Timer.Fire();
        await wait;
        Assert.That(pending.Task.IsCompleted, Is.False, "The deadline must not pretend the worker stopped.");
        Assert.That(failures, Is.Zero);
        Assert.That(clock.Timer.Disposed, Is.True);
        if (completion == 1) pending.SetException(new InvalidOperationException());
        else if (completion == 2) pending.SetCanceled(new CancellationToken(true));
        else pending.SetResult();
        Assert.That(failures, Is.EqualTo(completion == 0 ? 0 : 1));
    }

    [Test]
    public async Task WorkCompletedBeforeDeadlineDisposesTimer()
    {
        var clock = new ControlledClock();
        var pending = new TaskCompletionSource();
        var failures = 0;
        var wait = MainWindow.StopAndWaitAsync(() => { }, () => pending.Task, () => failures++, clock);
        pending.SetResult();
        await wait;
        clock.Timer!.Fire();
        Assert.That(clock.Timer.Disposed, Is.True);
        Assert.That(failures, Is.Zero);
    }

    private sealed class ControlledClock : TimeProvider
    {
        public ControlledTimer? Timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.That(period, Is.EqualTo(Timeout.InfiniteTimeSpan));
            return Timer = new(callback, state, dueTime);
        }
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; } = due;
        public bool Disposed { get; private set; }
        public void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
