using CodeView.Presentation;

namespace CodeView.Tests.Presentation;

public sealed class UiFailureTests
{
    [Test]
    public void SuccessfulActionDoesNotReportFailure()
    {
        var operations = 0;
        var failures = 0;
        MainWindow.RunUiAction(() => operations++, () => failures++);
        Assert.That(operations, Is.EqualTo(1));
        Assert.That(failures, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedActionReportsOnceIncludingUnexpectedCancellation(bool canceled)
    {
        var failures = 0;
        MainWindow.RunUiAction(() =>
        {
            if (canceled) throw new OperationCanceledException();
            throw new InvalidOperationException();
        }, () => failures++);
        Assert.That(failures, Is.EqualTo(1));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void FailureIsRecordedBeforeNotificationAndCloseIsNeverSkipped(int notificationFault)
    {
        var events = new List<string>();
        MainWindow.ReportUiFailure(() => events.Add("failed"), () =>
        {
            events.Add("notification");
            if (notificationFault == 1) throw new InvalidOperationException();
            if (notificationFault == 2) throw new OperationCanceledException();
        }, () => events.Add("close"));
        Assert.That(events, Is.EqualTo(new[] { "failed", "notification", "close" }));
    }

    [Test]
    public void CloseFailureIsNotSilentlyConvertedIntoSuccessfulShutdown()
    {
        var failure = new InvalidOperationException("close");
        var observed = Assert.Throws<InvalidOperationException>(() => MainWindow.ReportUiFailure(
            () => { }, () => throw new Exception("notify"), () => throw failure));
        Assert.That(observed, Is.SameAs(failure));
    }
}
