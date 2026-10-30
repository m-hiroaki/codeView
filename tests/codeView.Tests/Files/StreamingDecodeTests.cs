using System.Text;
using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Tests.Files;

public sealed class StreamingDecodeTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "codeview-streaming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    private SourcePath Put(byte[] bytes)
    {
        var path = SourcePath.Create(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".txt"), _root);
        File.WriteAllBytes(path.FullPath, bytes);
        return path;
    }

    private static Encoding EncodingFor(EncodingChoice choice) => choice switch
    {
        EncodingChoice.Cp932 => Encoding.GetEncoding(932),
        EncodingChoice.Utf16Le => new UnicodeEncoding(false, true, true),
        EncodingChoice.Utf16Be => new UnicodeEncoding(true, true, true),
        EncodingChoice.Utf32Le => new UTF32Encoding(false, true, true),
        EncodingChoice.Utf32Be => new UTF32Encoding(true, true, true),
        _ => new UTF8Encoding(true, true)
    };

    [TestCase((int)EncodingChoice.Utf8)]
    [TestCase((int)EncodingChoice.Cp932)]
    [TestCase((int)EncodingChoice.Utf16Le)]
    [TestCase((int)EncodingChoice.Utf16Be)]
    [TestCase((int)EncodingChoice.Utf32Le)]
    [TestCase((int)EncodingChoice.Utf32Be)]
    public void OneByteReadsPreserveDecoderStateAndExcludeBom(int encodingValue)
    {
        var choice = (EncodingChoice)encodingValue;
        var text = choice == EncodingChoice.Cp932 ? "日本語\r\n末尾" : "日本語😀\r\n末尾";
        var encoding = EncodingFor(choice);
        var path = Put(encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
        var source = new LimitedSource(1);
        var reader = new FileReader(source);
        using var prepared = reader.Prepare(path, choice, CancellationToken.None).Value;
        var actual = new StringBuilder();
        var result = reader.ReadChunks(prepared, chunk => actual.Append(chunk.Span), CancellationToken.None);
        Assert.That(result.Value, Is.EqualTo(text.Length));
        Assert.That(actual.ToString(), Is.EqualTo(text));
        Assert.That(source.LastStream!.LargestRead, Is.EqualTo(1));
    }

    [TestCase((int)EncodingChoice.Utf8)]
    [TestCase((int)EncodingChoice.Utf16Le)]
    [TestCase((int)EncodingChoice.Utf16Be)]
    [TestCase((int)EncodingChoice.Utf32Le)]
    [TestCase((int)EncodingChoice.Utf32Be)]
    public void SurrogatePairsAndOutputBufferBoundaryArePreserved(int encodingValue)
    {
        var choice = (EncodingChoice)encodingValue;
        var encoding = EncodingFor(choice);
        var text = new string('a', FileReader.ChunkChars - 1) + "😀" + new string('b', FileReader.ChunkChars * 5) + "\r\n終";
        var path = Put(encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
        var reader = new FileReader(new SourceAccess());
        using var prepared = reader.Prepare(path, choice, CancellationToken.None).Value;
        var actual = new StringBuilder();
        var calls = 0;
        var result = reader.ReadChunks(prepared, chunk =>
        {
            calls++;
            Assert.That(chunk.Length, Is.InRange(1, FileReader.ChunkChars));
            actual.Append(chunk.Span);
        }, CancellationToken.None);
        Assert.That(result.Value, Is.EqualTo(text.Length));
        Assert.That(calls, Is.GreaterThan(1));
        Assert.That(actual.ToString(), Is.EqualTo(text));
    }

    [TestCase((int)EncodingChoice.Utf8)]
    [TestCase((int)EncodingChoice.Cp932)]
    [TestCase((int)EncodingChoice.Utf16Le)]
    [TestCase((int)EncodingChoice.Utf16Be)]
    [TestCase((int)EncodingChoice.Utf32Le)]
    [TestCase((int)EncodingChoice.Utf32Be)]
    public void IncompleteFinalSequenceIsRejectedAndNextFileHasFreshDecoder(int encodingValue)
    {
        var choice = (EncodingChoice)encodingValue;
        var incomplete = choice switch
        {
            EncodingChoice.Cp932 => new byte[] { 0x82 },
            EncodingChoice.Utf16Le => new byte[] { 0x00, 0xd8 },
            EncodingChoice.Utf16Be => new byte[] { 0xd8, 0x00 },
            EncodingChoice.Utf32Le or EncodingChoice.Utf32Be => new byte[] { 0x00, 0x00, 0x00 },
            _ => new byte[] { 0xf0, 0x9f, 0x98 }
        };
        var source = new LimitedSource(1);
        var reader = new FileReader(source);
        var failed = reader.Prepare(Put(incomplete), choice, CancellationToken.None);
        Assert.That(failed.Error!.Code, Is.EqualTo(ErrorCode.DecodeFailed));
        Assert.That(source.LastStream!.CanRead, Is.False);
        var encoding = EncodingFor(choice);
        using var valid = reader.Prepare(Put(encoding.GetPreamble().Concat(encoding.GetBytes("次の文書")).ToArray()), choice, CancellationToken.None).Value;
        var text = new StringBuilder();
        reader.ReadChunks(valid, chunk => text.Append(chunk.Span), CancellationToken.None);
        Assert.That(text.ToString(), Is.EqualTo("次の文書"));
    }

    [Test]
    public void CancelAndConsumerFailureDoNotCorruptReusedBuffers()
    {
        var reader = new FileReader(new SourceAccess());
        var expected = new string('x', FileReader.ChunkChars * 3);
        var path = Put(Encoding.UTF8.GetBytes(expected));
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using (var prepared = reader.Prepare(path, EncodingChoice.Utf8, CancellationToken.None).Value)
            Assert.Throws<OperationCanceledException>(() => reader.ReadChunks(prepared, _ => { calls++; cancellation.Cancel(); }, cancellation.Token));
        Assert.That(calls, Is.EqualTo(1));
        var fault = new FormatException("consumer failed");
        using (var prepared = reader.Prepare(path, EncodingChoice.Utf8, CancellationToken.None).Value)
            Assert.That(Assert.Throws<FormatException>(() => reader.ReadChunks(prepared, _ => throw fault, CancellationToken.None)), Is.SameAs(fault));
        var actual = new StringBuilder();
        using var next = reader.Prepare(path, EncodingChoice.Utf8, CancellationToken.None).Value;
        Assert.That(reader.ReadChunks(next, chunk => actual.Append(chunk.Span), CancellationToken.None).Value, Is.EqualTo(expected.Length));
        Assert.That(actual.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public async Task ConcurrentReadsOnSameReaderKeepDecoderAndBuffersIsolated()
    {
        var reader = new FileReader(new SourceAccess());
        var choices = new[] { EncodingChoice.Utf8, EncodingChoice.Cp932, EncodingChoice.Utf16Le,
            EncodingChoice.Utf16Be, EncodingChoice.Utf32Le, EncodingChoice.Utf32Be };
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
        {
            var choice = choices[index % choices.Length];
            var encoding = EncodingFor(choice);
            var text = new string((char)('a' + index), 70000) + "\r\n日本語" + (choice == EncodingChoice.Cp932 ? "" : "😀");
            var path = Put(encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
            using var prepared = reader.Prepare(path, choice, CancellationToken.None).Value;
            var actual = new StringBuilder();
            Assert.That(reader.ReadChunks(prepared, chunk => actual.Append(chunk.Span), CancellationToken.None).Value,
                Is.EqualTo(text.Length));
            Assert.That(actual.ToString(), Is.EqualTo(text));
        })));
    }

    [Test]
    public void ReadIoFailureIsTypedAndOtherFilesStillDecodeCorrectly()
    {
        var source = new LimitedSource(1);
        var reader = new FileReader(source);
        using (var prepared = reader.Prepare(Put("content"u8.ToArray()), EncodingChoice.Utf8, CancellationToken.None).Value)
        {
            source.LastStream!.ThrowReads = true;
            Assert.That(reader.ReadChunks(prepared, _ => Assert.Fail("Failed reader delivered data."), CancellationToken.None).Error!.Code,
                Is.EqualTo(ErrorCode.IoFailure));
        }
        Assert.That(source.LastStream!.CanRead, Is.False);
        using var next = reader.Prepare(Put("next"u8.ToArray()), EncodingChoice.Utf8, CancellationToken.None).Value;
        var text = new StringBuilder();
        reader.ReadChunks(next, chunk => text.Append(chunk.Span), CancellationToken.None);
        Assert.That(text.ToString(), Is.EqualTo("next"));
    }

    private sealed class LimitedSource(int maxRead) : ISourceAccess
    {
        private readonly SourceAccess _source = new();
        public LimitedFileStream? LastStream { get; private set; }
        public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct)
        {
            var original = _source.OpenRead(path, ct);
            if (original.Status != OperationStatus.Success) return original;
            original.Value.Dispose();
            LastStream = new(path.FullPath, maxRead);
            return OperationResult<FileStream>.Success(LastStream);
        }
        public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => _source.InspectPath(path, ct);
        public SourceStamp GetStamp(SourcePath path) => _source.GetStamp(path);
        public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => _source.ResolveDirectory(path, ct);
        public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct) => _source.EnumerateChildren(path, ct);
    }

    private sealed class LimitedFileStream(string path, int maxRead)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan)
    {
        public int LargestRead { get; private set; }
        public bool ThrowReads { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (ThrowReads) throw new IOException("injected read failure");
            var read = base.Read(buffer, offset, Math.Min(count, maxRead));
            LargestRead = Math.Max(LargestRead, read);
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            if (ThrowReads) throw new IOException("injected read failure");
            var read = base.Read(buffer[..Math.Min(buffer.Length, maxRead)]);
            LargestRead = Math.Max(LargestRead, read);
            return read;
        }
    }
}
