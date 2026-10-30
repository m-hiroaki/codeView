using CodeView.Documents;
using CodeView.Presentation;

namespace CodeView.Tests.Performance;

public sealed class TextWindowTests
{
    [TestCase(0)]
    [TestCase(16384)]
    [TestCase(100000)]
    public void WindowStaysBoundedAndMapsBackToSource(int offset)
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        var original = new string('a', 100000);
        builder.Append(original.AsMemory());
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        var window = TextWindow.Create(lease, offset);
        Assert.That(window.Text.Length, Is.LessThanOrEqualTo(TextWindow.MaxWindowChars));
        Assert.That(window.SourceOffset, Is.LessThanOrEqualTo(offset));
        Assert.That(window.SourceOffset + window.Text.Length, Is.GreaterThanOrEqualTo(offset));
        Assert.That(window.Text, Is.EqualTo(original.Substring((int)window.SourceOffset, window.Text.Length)));
        Assert.That(window.ToSourceOffset((int)(offset - window.SourceOffset)), Is.EqualTo(offset));
    }

    [TestCase("a😀bcdef", 2)]
    [TestCase("a\r\nbcdef", 2)]
    public void CursorAndEdgesDoNotSplitSurrogatesOrCrLf(string text, int target)
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(text.AsMemory());
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        for (var index = 0; index <= text.Length; index++)
        {
            var window = TextWindow.Create(lease, index, 4);
            Assert.That(window.Text, Is.Not.Empty);
            Assert.That(char.IsLowSurrogate(window.Text[0]), Is.False);
            Assert.That(char.IsHighSurrogate(window.Text[^1]), Is.False);
            Assert.That(window.Text[0], Is.Not.EqualTo('\n'));
            Assert.That(window.Text[^1], Is.Not.EqualTo('\r'));
            Assert.That(window.TargetOffset, Is.InRange(window.SourceOffset, window.SourceOffset + window.Text.Length));
        }
        Assert.That(TextWindow.Create(lease, target, 4).TargetOffset, Is.EqualTo(target - 1));
    }

    [Test]
    public void EmptyClosedAndInvalidRequestsAreHandled()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        using var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        Assert.That(TextWindow.Create(lease, 0).Text, Is.Empty);
        Assert.Throws<ArgumentOutOfRangeException>(() => TextWindow.Create(lease, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextWindow.Create(lease, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextWindow.Create(lease, 0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextWindow.Create(lease, 0, TextWindow.MaxWindowChars + 1));
        var window = TextWindow.Create(lease, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => window.ToSourceOffset(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.ToSourceOffset(1));
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => TextWindow.Create(lease, 0));
    }
}
