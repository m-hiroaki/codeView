using AvaloniaEdit.Document;
using CodeView.Documents;
using CodeView.Presentation;

namespace CodeView.Tests.Performance;

public sealed class SourceLineNumbersTests
{
    [TestCase(1L, 1L, 1, 1, 1L)]
    [TestCase(123L, 1000L, 5, 1, 123L)]
    [TestCase(123L, 1000L, 5, 5, 127L)]
    [TestCase(long.MaxValue, long.MaxValue, 1, 1, long.MaxValue)]
    [TestCase(long.MaxValue - 9, long.MaxValue, 10, 10, long.MaxValue)]
    public void MapsDisplayRowsWithoutOverflow(long first, long total, int count, int row, long expected)
    {
        var numbers = new SourceLineNumbers(first, total, count);
        Assert.That(numbers.IsValid, Is.True);
        Assert.That(numbers.GetSourceLine(row), Is.EqualTo(expected));
    }

    [TestCase(1L, 2)]
    [TestCase(9L, 2)]
    [TestCase(10L, 2)]
    [TestCase(99L, 2)]
    [TestCase(100L, 3)]
    [TestCase(999999L, 6)]
    [TestCase(long.MaxValue, 19)]
    public void ReservesWidthForWholeSourceRatherThanCurrentSlice(long total, int digits)
    {
        Assert.That(new SourceLineNumbers(1, total, 1).WidthInDigits, Is.EqualTo(digits));
        Assert.That(new SourceLineNumbers(total, total, 1).WidthInDigits, Is.EqualTo(digits));
    }

    [TestCase(0L, 1L, 1)]
    [TestCase(2L, 1L, 1)]
    [TestCase(1L, 1L, 0)]
    [TestCase(1L, 1L, 2)]
    [TestCase(long.MaxValue, long.MaxValue, 2)]
    public void RejectsInvalidMapBeforeRendering(long first, long total, int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new SourceLineNumbers(first, total, count));

    [Test]
    public void DefaultAndOutOfRangeRowsCannotProduceLabels()
    {
        var disconnected = default(SourceLineNumbers);
        Assert.That(disconnected.IsValid, Is.False);
        Assert.Throws<InvalidOperationException>(() => disconnected.GetSourceLine(1));
        var numbers = new SourceLineNumbers(10, 15, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => numbers.GetSourceLine(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => numbers.GetSourceLine(4));
    }

    [TestCase("")]
    [TestCase("first\r\nsecond\rthird\nfourth\r\n")]
    [TestCase("😀注文\r\n短\n長い😀line\n")]
    public void EverySliceRowMatchesOriginalDocumentLineIncludingTrailingEmptyLine(string text)
    {
        using var builder = new DocumentBuilder();
        builder.Append(text.AsMemory());
        using var handle = builder.Complete();
        using var source = new DocumentDisplaySession(handle);
        var original = handle.ClaimForDisplay();
        for (var target = 0; target <= text.Length; target++)
        {
            var slice = source.CreateSlice(target, 8);
            var display = new TextDocument(slice.Text);
            var numbers = new SourceLineNumbers(slice.SourceStart.Line, original.LineCount, display.LineCount);
            foreach (var line in display.Lines)
            {
                var originalOffset = checked((int)(slice.SourceOffset + line.Offset));
                Assert.That(numbers.GetSourceLine(line.LineNumber), Is.EqualTo(original.GetLocation(originalOffset).Line),
                    $"target={target}, slice={slice.SourceOffset}, displayLine={line.LineNumber}");
            }
        }
    }
}
