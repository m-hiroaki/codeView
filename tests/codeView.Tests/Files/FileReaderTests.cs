using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Tests.Files;

public sealed class FileReaderTests
{
    private string _root = null!;
    private readonly SourceAccess _access = new();

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "codeview-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    private SourcePath Put(byte[] content, string name = "source.txt")
    {
        var path = SourcePath.Create(Path.Combine(_root, name), _root);
        File.WriteAllBytes(path.FullPath, content);
        return path;
    }

    [TestCase((int)EncodingChoice.Utf8)]
    [TestCase((int)EncodingChoice.Cp932)]
    [TestCase((int)EncodingChoice.Utf16Le)]
    [TestCase((int)EncodingChoice.Utf16Be)]
    [TestCase((int)EncodingChoice.Utf32Le)]
    [TestCase((int)EncodingChoice.Utf32Be)]
    public void ReadsSupportedEncodingsWithoutChangingSource(int choiceValue)
    {
        var choice = (EncodingChoice)choiceValue;
        const string text = "// 日本語\r\nclass C { }\n";
        Encoding encoding = choice switch
        {
            EncodingChoice.Cp932 => Encoding.GetEncoding(932),
            EncodingChoice.Utf16Le => new UnicodeEncoding(false, true),
            EncodingChoice.Utf16Be => new UnicodeEncoding(true, true),
            EncodingChoice.Utf32Le => new UTF32Encoding(false, true),
            EncodingChoice.Utf32Be => new UTF32Encoding(true, true),
            _ => new UTF8Encoding(true)
        };
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        var path = Put(bytes);
        var reader = new FileReader(_access);
        var result = reader.Prepare(path, EncodingChoice.Auto, CancellationToken.None);
        Assert.That(result.Status, Is.EqualTo(OperationStatus.Success));
        using (var prepared = result.Value)
        {
            var actual = new StringBuilder();
            var consumed = reader.ReadChunks(prepared, chunk => actual.Append(chunk.Span), CancellationToken.None);
            Assert.That(consumed.Value, Is.EqualTo(text.Length));
            Assert.That(actual.ToString(), Is.EqualTo(text));
            Assert.That(prepared.Encoding.CodePage, Is.EqualTo(encoding.CodePage));
        }
        Assert.That(SHA256.HashData(File.ReadAllBytes(path.FullPath)), Is.EqualTo(SHA256.HashData(bytes)));
        File.Delete(path.FullPath); // The reader did not retain its file handle.
    }

    [Test]
    public void EmptyAndBomOnlyFilesProduceEmptyText()
    {
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 0xef, 0xbb, 0xbf } })
        {
            var reader = new FileReader(_access);
            using var prepared = reader.Prepare(Put(bytes), EncodingChoice.Auto, CancellationToken.None).Value;
            Assert.That(reader.ReadChunks(prepared, _ => Assert.Fail("Empty file emitted a chunk."), CancellationToken.None).Value, Is.Zero);
        }
    }

    [Test]
    public void RejectsBinaryAndInvalidExplicitEncoding()
    {
        var reader = new FileReader(_access);
        var binary = Put(new byte[] { 0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10 });
        Assert.That(reader.Prepare(binary, EncodingChoice.Auto, CancellationToken.None).Error!.Code, Is.EqualTo(ErrorCode.Binary));
        Assert.That(reader.Prepare(binary, EncodingChoice.Cp932, CancellationToken.None).Error!.Code, Is.EqualTo(ErrorCode.Binary));
        Assert.That(reader.Prepare(Put(new byte[] { 0xc3 }), EncodingChoice.Utf8, CancellationToken.None).Error!.Code,
            Is.EqualTo(ErrorCode.DecodeFailed));
    }

    [Test]
    public void MultibyteCharactersSurviveChunkBoundariesAndNoFinalNewline()
    {
        var text = new string('x', 65535) + "😀日本語\r\n" + new string('y', 65535) + "終";
        var reader = new FileReader(_access);
        using var prepared = reader.Prepare(Put(Encoding.UTF8.GetBytes(text)), EncodingChoice.Auto, CancellationToken.None).Value;
        var builder = new StringBuilder();
        Assert.That(reader.ReadChunks(prepared, chunk => builder.Append(chunk.Span), CancellationToken.None).Status,
            Is.EqualTo(OperationStatus.Success));
        Assert.That(builder.ToString(), Is.EqualTo(text));
    }

    [Test]
    public void ManualUtf16CanRetryBinaryHeuristicAndBomMismatchIsRejected()
    {
        var reader = new FileReader(_access);
        var noBom = Put(new UnicodeEncoding(false, false).GetBytes("abc"));
        Assert.That(reader.Prepare(noBom, EncodingChoice.Auto, CancellationToken.None).Error!.Code, Is.EqualTo(ErrorCode.Binary));
        using var manual = reader.Prepare(noBom, EncodingChoice.Utf16Le, CancellationToken.None).Value;
        var withBom = Put(new byte[] { 0xff, 0xfe, 0x61, 0 }, "bom.txt");
        Assert.That(reader.Prepare(withBom, EncodingChoice.Utf8, CancellationToken.None).Error!.Code, Is.EqualTo(ErrorCode.DecodeFailed));
    }

    [Test]
    public void ChangedAndCancelledReadsDoNotReturnSuccess()
    {
        var path = Put(Encoding.UTF8.GetBytes("initial"));
        var reader = new FileReader(_access);
        using var prepared = reader.Prepare(path, EncodingChoice.Auto, CancellationToken.None).Value;
        File.WriteAllText(path.FullPath, "changed and longer");
        Assert.That(reader.ReadChunks(prepared, _ => { }, CancellationToken.None).Error!.Code, Is.EqualTo(ErrorCode.ChangedDuringRead));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.Prepare(path, EncodingChoice.Auto, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => reader.ReadChunks(prepared, _ => { }, cancelled.Token));
    }

    [Test]
    public void DirectoryAndMissingFileAreTypedFailures()
    {
        Assert.That(_access.OpenRead(SourcePath.Create(_root, _root), CancellationToken.None).Error!.Code,
            Is.EqualTo(ErrorCode.NotRegularFile));
        Assert.That(_access.OpenRead(SourcePath.Create("missing", _root), CancellationToken.None).Error!.Code,
            Is.EqualTo(ErrorCode.NotFound));
    }

    [Test]
    public void FileLinkCanBeOpenedExplicitly()
    {
        var target = Put(Encoding.UTF8.GetBytes("target"));
        var link = Path.Combine(_root, "link.txt");
        File.CreateSymbolicLink(link, target.FullPath);
        var opened = _access.OpenRead(SourcePath.Create(link, _root), CancellationToken.None);
        Assert.That(opened.Status, Is.EqualTo(OperationStatus.Success));
        opened.Value.Dispose();
    }

    [Test]
    public void BrokenFileLinkIsReportedAndTargetChangesAffectStamp()
    {
        var link = Path.Combine(_root, "missing-link.txt");
        File.CreateSymbolicLink(link, Path.Combine(_root, "not-found.txt"));
        Assert.That(_access.OpenRead(SourcePath.Create(link, _root), CancellationToken.None).Error!.Code,
            Is.EqualTo(ErrorCode.BrokenLink));

        var target = Put(Encoding.UTF8.GetBytes("target"));
        var validLink = Path.Combine(_root, "valid-link.txt");
        File.CreateSymbolicLink(validLink, target.FullPath);
        var linkedPath = SourcePath.Create(validLink, _root);
        var before = _access.GetStamp(linkedPath);
        Assert.That(before.LengthBytes, Is.EqualTo(6));
        File.WriteAllText(target.FullPath, "longer target");
        Assert.That(_access.GetStamp(linkedPath), Is.Not.EqualTo(before));
    }

    [Test]
    public async Task MacFifoIsRejectedWithoutWaitingForAWriter()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Ignore("Darwinの特殊ファイル検証。Windowsは別のファイル種別試験で確認する。");
        var fifo = Path.Combine(_root, "fifo");
        using var command = Process.Start(new ProcessStartInfo("/usr/bin/mkfifo")
        {
            UseShellExecute = false,
            ArgumentList = { fifo }
        })!;
        await command.WaitForExitAsync();
        Assert.That(command.ExitCode, Is.Zero);
        var result = await Task.Run(() => _access.OpenRead(SourcePath.Create(fifo, _root), CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(result.Error!.Code, Is.EqualTo(ErrorCode.NotRegularFile));
    }
}
