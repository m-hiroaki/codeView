using System.Text;
using CodeView.Contracts;

namespace CodeView.Files;

internal enum EncodingChoice { Auto, Utf8, Cp932, Utf16Le, Utf16Be, Utf32Le, Utf32Be }

internal sealed class PreparedText : IDisposable
{
    internal FileStream Stream { get; }
    public SourcePath Path { get; }
    public SourceStamp Stamp { get; }
    public Encoding Encoding { get; }
    public int BomBytes { get; }

    internal PreparedText(FileStream stream, SourcePath path, SourceStamp stamp, Encoding encoding, int bomBytes)
        => (Stream, Path, Stamp, Encoding, BomBytes) = (stream, path, stamp, encoding, bomBytes);

    public void Dispose() => Stream.Dispose();
}

internal sealed class FileReader(ISourceAccess access)
{
    internal const int SampleBytes = 8 * 1024;
    internal const int ChunkChars = 16 * 1024;

    public OperationResult<PreparedText> Prepare(SourcePath path, EncodingChoice choice, CancellationToken ct)
    {
        var opened = access.OpenRead(path, ct);
        if (opened.Status != OperationStatus.Success)
            return OperationResult<PreparedText>.Failure(opened.Error!);
        var stream = opened.Value;
        var transferred = false;
        try
        {
            var stamp = access.GetStamp(path);
            var sample = new byte[SampleBytes];
            var length = stream.ReadAtLeast(sample, SampleBytes, false);
            var bytes = sample.AsSpan(0, length);
            var (bomChoice, bomLength) = DetectBom(bytes);
            if (IsKnownBinary(bytes) || (bomLength == 0 && choice == EncodingChoice.Auto && LooksBinary(bytes)))
                return Fail(ErrorCode.Binary);
            if (bomChoice is not null && choice != EncodingChoice.Auto && choice != bomChoice)
                return Fail(ErrorCode.DecodeFailed);

            var selected = bomChoice ?? (choice == EncodingChoice.Auto ? EncodingChoice.Utf8 : choice);
            var encoding = StrictEncoding(selected);
            if (!Validate(stream, encoding, bomLength, ct))
            {
                if (choice != EncodingChoice.Auto || bomChoice is not null)
                    return Fail(ErrorCode.DecodeFailed);
                encoding = StrictEncoding(EncodingChoice.Cp932);
                if (!Validate(stream, encoding, 0, ct))
                    return Fail(ErrorCode.DecodeFailed);
            }

            if (access.GetStamp(path) != stamp)
                return Fail(ErrorCode.ChangedDuringRead);
            stream.Position = bomLength;
            var prepared = new PreparedText(stream, path, stamp, encoding, bomLength);
            transferred = true;
            return OperationResult<PreparedText>.Success(prepared);
        }
        catch (UnauthorizedAccessException) { return Fail(ErrorCode.AccessDenied); }
        catch (IOException) { return Fail(ErrorCode.IoFailure); }
        finally
        {
            if (!transferred)
                stream.Dispose();
        }

        OperationResult<PreparedText> Fail(ErrorCode code)
            => OperationResult<PreparedText>.Failure(new(code, path.DisplayPath));
    }

    public OperationResult<long> ReadChunks(PreparedText prepared, Action<ReadOnlyMemory<char>> consume, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consume);
        try
        {
            prepared.Stream.Position = prepared.BomBytes;
            using var reader = new StreamReader(prepared.Stream, prepared.Encoding, false, 64 * 1024, true);
            var buffer = new char[ChunkChars];
            var count = 0L;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = reader.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                consume(buffer.AsMemory(0, read));
                count = checked(count + read);
            }
            if (access.GetStamp(prepared.Path) != prepared.Stamp)
                return OperationResult<long>.Failure(new(ErrorCode.ChangedDuringRead, prepared.Path.DisplayPath));
            return OperationResult<long>.Success(count);
        }
        catch (DecoderFallbackException)
        {
            return OperationResult<long>.Failure(new(ErrorCode.DecodeFailed, prepared.Path.DisplayPath));
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult<long>.Failure(new(ErrorCode.AccessDenied, prepared.Path.DisplayPath));
        }
        catch (IOException)
        {
            return OperationResult<long>.Failure(new(ErrorCode.IoFailure, prepared.Path.DisplayPath));
        }
    }

    private static bool Validate(FileStream stream, Encoding encoding, int bomBytes, CancellationToken ct)
    {
        stream.Position = bomBytes;
        using var reader = new StreamReader(stream, encoding, false, 64 * 1024, true);
        var buffer = new char[ChunkChars];
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Read(buffer, 0, buffer.Length) == 0)
                    return true;
            }
        }
        catch (DecoderFallbackException) { return false; }
    }

    private static Encoding StrictEncoding(EncodingChoice choice) => choice switch
    {
        EncodingChoice.Utf8 => new UTF8Encoding(false, true),
        EncodingChoice.Cp932 => Cp932(),
        EncodingChoice.Utf16Le => new UnicodeEncoding(false, false, true),
        EncodingChoice.Utf16Be => new UnicodeEncoding(true, false, true),
        EncodingChoice.Utf32Le => new UTF32Encoding(false, false, true),
        EncodingChoice.Utf32Be => new UTF32Encoding(true, false, true),
        _ => throw new ArgumentOutOfRangeException(nameof(choice))
    };

    private static Encoding Cp932()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static (EncodingChoice? Choice, int Length) DetectBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return (EncodingChoice.Utf32Le, 4);
        if (bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return (EncodingChoice.Utf32Be, 4);
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return (EncodingChoice.Utf8, 3);
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) return (EncodingChoice.Utf16Le, 2);
        if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) return (EncodingChoice.Utf16Be, 2);
        return (null, 0);
    }

    private static bool LooksBinary(ReadOnlySpan<byte> sample)
    {
        var control = 0;
        foreach (var b in sample)
        {
            if (b == 0) return true;
            if (b < 32 && b is not (9 or 10 or 12 or 13)) control++;
        }
        return control * 100 > sample.Length;
    }

    private static bool IsKnownBinary(ReadOnlySpan<byte> sample)
        => sample.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10 })
        || sample.StartsWith(new byte[] { 0xff, 0xd8, 0xff })
        || sample.StartsWith("GIF87a"u8) || sample.StartsWith("GIF89a"u8)
        || sample.StartsWith("%PDF-"u8) || sample.StartsWith(new byte[] { 0x50, 0x4b, 3, 4 })
        || sample.StartsWith(new byte[] { 0x7f, 0x45, 0x4c, 0x46 })
        || sample.StartsWith(new byte[] { 0xcf, 0xfa, 0xed, 0xfe })
        || sample.StartsWith(new byte[] { 0xfe, 0xed, 0xfa, 0xcf });
}
