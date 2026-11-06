using System.Buffers.Binary;

namespace CodeView.Tests.Performance;

internal sealed class ProcessIoTests
{
    [Test]
    public void OwnProcessCountersAreAvailableMonotonicAndMayRemainZero()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) Assert.Ignore("Windows/macOS accounting only.");
        var io = new ProcessIo();
        var before = io.Read();
        var after = io.Read();
        var delta = ProcessIo.Difference(before, after);
        Assert.That(delta.Metric, Is.EqualTo(io.Metric));
        Assert.That(ProcessIo.Difference(after, after).ReadBytes, Is.Zero);
    }

    [Test]
    public void MacFieldsUseVerifiedV2OffsetsAndUnsignedLimits()
    {
        var bytes = new byte[160];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(136, 8), 99); // Last child field is not disk I/O.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(144, 8), ulong.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(152, 8), 123);
        var sample = ProcessIo.ParseMac(bytes);
        Assert.That(sample.ReadBytes, Is.EqualTo(ulong.MaxValue));
        Assert.That(sample.WrittenBytes, Is.EqualTo(123));
    }

    [TestCase(0)]
    [TestCase(159)]
    [TestCase(161)]
    public void InvalidNativeBufferLengthIsRejected(int length) => Assert.Throws<ArgumentException>(() => ProcessIo.ParseMac(new byte[length]));

    [Test]
    public void CounterResetOrMetricChangeCannotBecomeUnsignedOverflow()
    {
        var before = new ProcessIoSample("one", 10, 20);
        Assert.Throws<InvalidOperationException>(() => ProcessIo.Difference(before, new("one", 9, 20)));
        Assert.Throws<InvalidOperationException>(() => ProcessIo.Difference(before, new("one", 10, 19)));
        Assert.Throws<InvalidOperationException>(() => ProcessIo.Difference(before, new("two", 10, 20)));
        Assert.That(ProcessIo.Difference(new("one", 0, 0), new("one", ulong.MaxValue, ulong.MaxValue)).ReadBytes, Is.EqualTo(ulong.MaxValue));
    }
}
