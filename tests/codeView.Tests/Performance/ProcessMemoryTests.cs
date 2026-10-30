namespace CodeView.Tests.Performance;

public sealed class ProcessMemoryTests
{
    [Test]
    public void CurrentProcessReturnsPositiveMeasurement()
    {
        using var memory = new ProcessMemory();
        Assert.That(memory.ReadBytes(), Is.GreaterThan(0));
        Assert.That(memory.Metric, Is.Not.Empty);
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void UnavailableOrInvalidMeasurementFails(long value) =>
        Assert.Throws<InvalidOperationException>(() => ProcessMemory.Validate(value));

    [Test]
    public void PositiveBoundaryIsPreserved() => Assert.That(ProcessMemory.Validate(1), Is.EqualTo(1));
}
