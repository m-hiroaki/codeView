using System.Text;
using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Tests.Performance;

internal sealed class ReadAccountingTests
{
    private string _root = null!;
    [SetUp] public void SetUp() { _root = Path.Combine(Path.GetTempPath(), "codeview-io-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TearDown] public void TearDown() => Directory.Delete(_root, true);

    [Test]
    public void BenchmarkReferenceEncodingIsExplicitAndStrict()
    {
        var cp932 = CodeView.Tests.Files.IoPerformanceTests.ReferenceEncoding("cp932");
        Assert.That(cp932.CodePage, Is.EqualTo(932));
        Assert.That(cp932.GetString([0x82, 0xa0]), Is.EqualTo("あ"));
        Assert.Throws<DecoderFallbackException>(() => cp932.GetString([0x82]));
        Assert.Throws<DecoderFallbackException>(() => CodeView.Tests.Files.IoPerformanceTests.ReferenceEncoding("utf-8").GetString([0xe3, 0x81]));
        Assert.Throws<ArgumentException>(() => CodeView.Tests.Files.IoPerformanceTests.ReferenceEncoding("unknown"));
    }

    [Test]
    public void ArraySpanByteAndSeekCountActualReturnedBytesOnce()
    {
        var path = Path.Combine(_root, "source.txt");
        File.WriteAllBytes(path, "0123456789"u8.ToArray());
        var counts = new ReadAccounting();
        using var inner = File.OpenRead(path);
        using var stream = new CountingReadStream(inner, counts);
        Assert.That(stream.Read(new byte[3], 0, 3), Is.EqualTo(3));
        Span<byte> span = stackalloc byte[2];
        Assert.That(stream.Read(span), Is.EqualTo(2));
        Assert.That(stream.ReadByte(), Is.EqualTo((int)'5'));
        Assert.That(counts.Bytes, Is.EqualTo(6));
        Assert.That(counts.Calls, Is.EqualTo(3));
        stream.Seek(0, SeekOrigin.Begin);
        Assert.That(stream.Position, Is.Zero);
        Assert.That(stream.Length, Is.EqualTo(10));
        Assert.That(stream.Read(new byte[20]), Is.EqualTo(10));
        Assert.That(stream.ReadByte(), Is.EqualTo(-1));
        Assert.That(counts.Bytes, Is.EqualTo(16));
        Assert.That(counts.Calls, Is.EqualTo(5));
    }

    [Test]
    public async Task BothAsyncOverloadsAndZeroLengthReadAreCountedOnce()
    {
        var path = Path.Combine(_root, "source.txt");
        File.WriteAllBytes(path, "0123456789"u8.ToArray());
        var counts = new ReadAccounting();
        await using var stream = new CountingReadStream(File.OpenRead(path), counts);
        Assert.That(await stream.ReadAsync(new byte[3], 0, 3, CancellationToken.None), Is.EqualTo(3));
        Assert.That(await stream.ReadAsync(new byte[4].AsMemory()), Is.EqualTo(4));
        Assert.That(stream.Read(Span<byte>.Empty), Is.Zero);
        Assert.That(counts.Bytes, Is.EqualTo(7));
        Assert.That(counts.Calls, Is.EqualTo(3));
    }

    [Test]
    public void DisposingWrapperClosesTheOriginalHandleAndDoesNotCountFailedReads()
    {
        var path = Path.Combine(_root, "source.txt");
        File.WriteAllText(path, "data");
        var inner = File.OpenRead(path);
        var counts = new ReadAccounting();
        var stream = new CountingReadStream(inner, counts);
        stream.Dispose();
        stream.Dispose();
        Assert.That(inner.SafeFileHandle.IsClosed, Is.True);
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        Assert.That(counts.Bytes, Is.Zero);
        File.Delete(path);
    }

    [Test]
    public void AccessFailureIsForwardedWithoutWrappingOrOpeningAnotherFile()
    {
        var counts = new ReadAccounting();
        var access = new CountingSourceAccess(new SourceAccess(), counts);
        var result = access.OpenRead(SourcePath.Create("absent.txt", _root), CancellationToken.None);
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.NotFound));
        Assert.That(counts.Bytes, Is.Zero);
        Assert.That(counts.Calls, Is.Zero);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(8191)]
    [TestCase(8192)]
    [TestCase(8193)]
    [TestCase(65537)]
    public void Utf8ReaderCountsSampleValidationAndConsumptionAndPreservesBytes(int bytes)
    {
        var path = SourcePath.Create("source.txt", _root);
        var original = Enumerable.Repeat((byte)'a', bytes).ToArray();
        File.WriteAllBytes(path.FullPath, original);
        var counts = new ReadAccounting();
        var reader = new FileReader(new CountingSourceAccess(new SourceAccess(), counts));
        using (var prepared = reader.Prepare(path, EncodingChoice.Utf8, CancellationToken.None).Value)
        {
            Assert.That(counts.Bytes, Is.EqualTo(Math.Min(bytes, 8192) + (long)bytes));
            var consumed = reader.ReadChunks(prepared, _ => { }, CancellationToken.None);
            Assert.That(consumed.Value, Is.EqualTo(bytes));
            Assert.That(counts.Bytes, Is.EqualTo(Math.Min(bytes, 8192) + 2L * bytes));
        }
        Assert.That(File.ReadAllBytes(path.FullPath), Is.EqualTo(original));
    }

    [Test]
    public void AutomaticCp932AlsoCountsTheFailedUtf8ValidationRead()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = SourcePath.Create("source.txt", _root);
        var raw = Encoding.GetEncoding(932).GetBytes(new string('あ', 50_000));
        File.WriteAllBytes(path.FullPath, raw);
        var counts = new ReadAccounting();
        var reader = new FileReader(new CountingSourceAccess(new SourceAccess(), counts));
        using var prepared = reader.Prepare(path, EncodingChoice.Auto, CancellationToken.None).Value;
        Assert.That(prepared.Encoding.CodePage, Is.EqualTo(932));
        var afterPrepare = counts.Bytes;
        Assert.That(afterPrepare, Is.EqualTo(8192L + 65536 + raw.Length));
        Assert.That(reader.ReadChunks(prepared, _ => { }, CancellationToken.None).Value, Is.EqualTo(50_000));
        Assert.That(counts.Bytes - afterPrepare, Is.EqualTo(raw.Length));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(9000)]
    public void BomIsIncludedInSampleButNotCountedAsDecodedPayload(int characters)
    {
        var path = SourcePath.Create("bom.txt", _root);
        var bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Enumerable.Repeat((byte)'a', characters)).ToArray();
        File.WriteAllBytes(path.FullPath, bytes);
        var counts = new ReadAccounting();
        var reader = new FileReader(new CountingSourceAccess(new SourceAccess(), counts));
        using var prepared = reader.Prepare(path, EncodingChoice.Auto, CancellationToken.None).Value;
        Assert.That(prepared.BomBytes, Is.EqualTo(3));
        Assert.That(counts.Bytes, Is.EqualTo(Math.Min(bytes.Length, 8192) + (long)characters));
        Assert.That(reader.ReadChunks(prepared, _ => { }, CancellationToken.None).Value, Is.EqualTo(characters));
        Assert.That(counts.Bytes, Is.EqualTo(Math.Min(bytes.Length, 8192) + 2L * characters));
    }
}
