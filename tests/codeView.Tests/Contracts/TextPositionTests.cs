using CodeView.Contracts;

namespace CodeView.Tests.Contracts;

public sealed class TextPositionTests
{
    [TestCase(0, 1)]
    [TestCase(1, 0)]
    [TestCase(-1, 1)]
    public void RejectsNonPositivePosition(long line, long column)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TextPosition(line, column));

    [Test]
    public void RangeRejectsOverflowAndDefaultPosition()
    {
        Assert.Throws<OverflowException>(() => new TextRange(long.MaxValue, 1, new(1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextRange(0, 0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextRange(-1, 0, new(1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextRange(0, -1, new(1, 1)));
        Assert.That(new TextRange(0, 0, new(1, 1)).End, Is.Zero);
    }

    [TestCase("", 0, 1, 1)]
    [TestCase("x\r\n", 3, 2, 1)]
    [TestCase("x\ry\nz", 5, 3, 2)]
    [TestCase("a😀b\r\nが\nか\u3099", 10, 3, 3)]
    public void CursorPreservesOriginalUtf16Positions(string text, long offset, long line, long column)
    {
        for (var chunkSize = 1; chunkSize <= Math.Max(1, text.Length); chunkSize++)
        {
            var cursor = new TextCursor();
            for (var start = 0; start < text.Length; start += chunkSize)
                cursor.Advance(text.AsSpan(start, Math.Min(chunkSize, text.Length - start)));
            Assert.That(cursor.Offset, Is.EqualTo(offset));
            Assert.That(cursor.Position, Is.EqualTo(new TextPosition(line, column)));
        }
    }
}
