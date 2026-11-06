using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Tests.Performance;

/// <summary>Counts bytes returned by stream reads, not OS requests or disk transfers.</summary>
internal sealed class ReadAccounting
{
    private long _bytes, _calls;
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Calls => Interlocked.Read(ref _calls);
    internal void Add(int bytes) { Interlocked.Add(ref _bytes, bytes); Interlocked.Increment(ref _calls); }
}

/// <summary>Keep SourceAccess's checked handle and concrete buffered stream; count only delegated reads.</summary>
internal sealed class CountingSourceAccess(ISourceAccess inner, ReadAccounting accounting) : ISourceAccess
{
    public OperationResult<FileStream> OpenRead(SourcePath path, CancellationToken ct)
    {
        var opened = inner.OpenRead(path, ct);
        if (opened.Status != OperationStatus.Success) return opened;
        try { return OperationResult<FileStream>.Success(new CountingReadStream(opened.Value, accounting)); }
        catch { opened.Value.Dispose(); throw; }
    }
    public SourceStamp GetStamp(SourcePath path) => inner.GetStamp(path);
    public OperationResult<SourcePath> ResolveDirectory(SourcePath path, CancellationToken ct) => inner.ResolveDirectory(path, ct);
    public IEnumerable<SourceEntry> EnumerateChildren(SourcePath path, CancellationToken ct) => inner.EnumerateChildren(path, ct);
    public OperationResult<SourceEntry> InspectPath(SourcePath path, CancellationToken ct) => inner.InspectPath(path, ct);
}

internal sealed class CountingReadStream : FileStream
{
    private readonly FileStream _inner;
    private readonly ReadAccounting _accounting;
    public CountingReadStream(FileStream inner, ReadAccounting accounting) : base(inner.SafeFileHandle, FileAccess.Read, 1, false)
        => (_inner, _accounting) = (inner, accounting);

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer));
    public override int ReadByte()
    {
        var value = _inner.ReadByte();
        _accounting.Add(value < 0 ? 0 : 1);
        return value;
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => Count(await _inner.ReadAsync(buffer, offset, count, ct));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => Count(await _inner.ReadAsync(buffer, ct));

    private int Count(int value) { _accounting.Add(value); return value; }
    protected override void Dispose(bool disposing)
    {
        try { if (disposing) _inner.Dispose(); }
        finally { base.Dispose(disposing); }
    }
    public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
