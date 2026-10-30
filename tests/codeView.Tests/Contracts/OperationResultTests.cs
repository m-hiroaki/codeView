using CodeView.Contracts;
using CodeView.Operations;

namespace CodeView.Tests.Contracts;

public sealed class OperationResultTests
{
    [Test]
    public void OnlySuccessfulResultsExposeTheirValue()
    {
        Assert.That(OperationResult<int>.Success(42).Value, Is.EqualTo(42));
        var failure = OperationResult<int>.Failure(new(ErrorCode.NotFound));
        Assert.That(failure.Error!.Code, Is.EqualTo(ErrorCode.NotFound));
        Assert.Throws<InvalidOperationException>(() => _ = failure.Value);
        Assert.Throws<InvalidOperationException>(() => _ = OperationResult<int>.Cancelled().Value);
        Assert.Throws<ArgumentNullException>(() => OperationResult<string>.Success(null!));
    }

    [Test]
    public void OldEpochsAndClosedOwnersCannotAdoptResults()
    {
        var identity = new OperationIdentity();
        Assert.That(identity.CanAdopt(0, true, false), Is.False);
        var oldEpoch = identity.Advance();
        Assert.That(identity.CanAdopt(oldEpoch, true, false), Is.True);
        var current = identity.Advance();
        Assert.That(identity.CanAdopt(oldEpoch, true, false), Is.False);
        Assert.That(identity.CanAdopt(current, false, false), Is.False);
        Assert.That(identity.CanAdopt(current, true, true), Is.False);
        Assert.That(identity.CanAdopt(current, true, false), Is.True);
    }
}
