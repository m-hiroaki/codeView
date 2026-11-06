using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AvaloniaEdit.Document;
using CodeView.Documents;

namespace CodeView.Tests.Documents;

internal sealed class DocumentBuilderCopyTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(255)]
    [TestCase(256)]
    [TestCase(257)]
    [TestCase(16383)]
    [TestCase(16384)]
    [TestCase(16385)]
    public void BorrowedArrayRangeIsCopiedBeforeItIsReused(int length)
    {
        var array = Enumerable.Range(0, length + 6).Select(i => (char)('a' + i % 26)).ToArray();
        var expected = new string(array, 3, length);
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(array.AsMemory(3, length));
        Array.Fill(array, '?');
        using var handle = builder.Complete();
        var document = handle.ClaimForDisplay();
        Assert.That(document.Text, Is.EqualTo(expected));
        Assert.That(document.UndoStack.CanUndo, Is.False);
        Assert.That(document.UndoStack.CanRedo, Is.False);
    }

    [Test]
    public void ReusedChunksPreserveCrLfAndSurrogatesAcrossInsertions()
    {
        var expected = string.Concat(Enumerable.Repeat("a😀b\r\n日本\rZ\n", 100));
        var buffer = new char[7];
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        for (var offset = 0; offset < expected.Length; offset += buffer.Length)
        {
            var length = Math.Min(buffer.Length, expected.Length - offset);
            expected.AsSpan(offset, length).CopyTo(buffer);
            builder.Append(buffer.AsMemory(0, length));
            Array.Fill(buffer, '?');
        }
        using var handle = builder.Complete();
        var document = handle.ClaimForDisplay();
        var reference = new TextDocument(expected);
        Assert.That(document.Text, Is.EqualTo(expected));
        Assert.That(document.LineCount, Is.EqualTo(reference.LineCount));
        for (var offset = 0; offset <= expected.Length; offset++)
            Assert.That(document.GetLocation(offset), Is.EqualTo(reference.GetLocation(offset)), $"Offset {offset}");
    }

    [Test]
    public void StringAndNonArrayMemoryAreCopiedAndReadableAfterOwnerRelease()
    {
        const string first = "first 日本\r";
        const string second = "\n😀 last";
        using var memory = new HiddenArrayMemory(second);
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(first.AsMemory());
        builder.Append(memory.Memory);
        memory.Clear();
        var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        handle.Dispose();
        Assert.That(lease.ReadRange(0, first.Length + second.Length), Is.EqualTo(first + second));
    }

    [Test]
    public void MultipleBorrowedChunksKeepFullContentAfterTreeRebalancing()
    {
        const string pattern = "line 😀\r\n日本語\rnext\n";
        var expected = string.Create(1_048_579, pattern, static (span, value) =>
        {
            for (var index = 0; index < span.Length; index++) span[index] = value[index % value.Length];
        });
        var expectedHash = SHA256.HashData(MemoryMarshal.AsBytes(expected.AsSpan()));
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        var buffer = new char[16 * 1024];
        for (var offset = 0; offset < expected.Length; offset += buffer.Length)
        {
            var length = Math.Min(buffer.Length, expected.Length - offset);
            expected.AsSpan(offset, length).CopyTo(buffer);
            builder.Append(buffer.AsMemory(0, length));
            Array.Fill(buffer, '?');
        }
        var handle = builder.Complete();
        using var lease = handle.AcquireReadView();
        handle.Dispose();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Assert.That(lease.VisitChunks(chunk => { hash.AppendData(MemoryMarshal.AsBytes(chunk.Span)); return true; }, CancellationToken.None),
            Is.EqualTo(expected.Length));
        Assert.That(hash.GetHashAndReset(), Is.EqualTo(expectedHash));
    }

    [Test]
    public void EmptyAppendStillChecksThreadAndDisposedBuilder()
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        Task.Run(() => Assert.Throws<InvalidOperationException>(() => builder.Append(ReadOnlyMemory<char>.Empty))).GetAwaiter().GetResult();
        builder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => builder.Append(ReadOnlyMemory<char>.Empty));
    }

    private sealed class HiddenArrayMemory(string text) : MemoryManager<char>
    {
        private readonly char[] _array = text.ToCharArray();
        public override Span<char> GetSpan() => _array;
        public void Clear() => Array.Fill(_array, '?');
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) => Clear();
    }
}
