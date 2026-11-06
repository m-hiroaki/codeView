using CodeView.Documents;

namespace CodeView.Tests.Documents;

public sealed class DocumentLineExtentTests
{
    [TestCase("", 0)]
    [TestCase("a\r\nbbb\rc\n", 3)]
    [TestCase("日本語😀\t", 6)]
    [TestCase("\r\n\r\n", 0)]
    [TestCase("abcdef", 6)]
    public void CompletedIndexProvidesMaximumLogicalUtf16Length(string text, int expected)
    {
        for (var chunk = 1; chunk <= Math.Max(1, text.Length); chunk++)
        {
            using var builder = new DocumentBuilder(collectLineExtent: true);
            for (var start = 0; start < text.Length; start += chunk)
                builder.Append(text.AsMemory(start, Math.Min(chunk, text.Length - start)));
            using var handle = builder.Complete();
            Assert.That(handle.MaximumLineLength, Is.EqualTo(expected), "chunk=" + chunk);
            using var session = new DocumentDisplaySession(handle);
            Assert.That(session.MaximumLineLength, Is.EqualTo(expected));
            Assert.That(handle.ClaimForDisplay().Text, Is.EqualTo(text));
        }
    }

    [Test]
    public async Task ExtentIsAvailableAfterWorkerOwnershipTransferAndClose()
    {
        using var handle = await Task.Run(() =>
        {
            using var builder = new DocumentBuilder(collectLineExtent: true);
            builder.Append(("short\r\n" + new string('x', 1024 * 1024) + "\r\nend").AsMemory());
            return builder.Complete();
        });
        Assert.That(handle.MaximumLineLength, Is.EqualTo(1024 * 1024));
        using var session = new DocumentDisplaySession(handle);
        handle.Dispose();
        Assert.That(handle.MaximumLineLength, Is.EqualTo(1024 * 1024));
        Assert.That(session.MaximumLineLength, Is.EqualTo(1024 * 1024));
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = session.MaximumLineLength);
    }

    [Test]
    public void FailedCompletionAndWrongThreadDoNotCreateAnExtentOrTransferOwnership()
    {
        using var builder = new DocumentBuilder(collectLineExtent: true);
        builder.Append("retained".AsMemory());
        Task.Run(() => Assert.Throws<InvalidOperationException>(() => builder.Complete())).GetAwaiter().GetResult();
        using var handle = builder.Complete();
        Assert.That(handle.MaximumLineLength, Is.EqualTo(8));
        Assert.Throws<ObjectDisposedException>(() => builder.Complete());
        Assert.Throws<ObjectDisposedException>(() => builder.Append("later".AsMemory()));
    }

    [Test]
    public void DefaultAdapterSkipsExtentAndMissingExtentIsNotAnEmptyLineClaim()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append("non-empty".AsMemory());
        using var handle = builder.Complete();
        Assert.That(handle.MaximumLineLength, Is.Null);
        using var session = new DocumentDisplaySession(handle);
        Assert.Throws<InvalidOperationException>(() => _ = session.MaximumLineLength);
        Assert.That(session.CreateRowProjection(1, 16).Text, Is.EqualTo("non-empty"));
    }

    [Test]
    public void ExplicitAdapterCollectsTheExtentOnItsWorkerBuilder()
    {
        using var builder = new AvaloniaEditDocumentAdapter(collectLineExtent: true).CreateBuilder();
        builder.Append("wide\r\nx".AsMemory());
        using var handle = builder.Complete();
        Assert.That(handle.MaximumLineLength, Is.EqualTo(4));
    }
}
