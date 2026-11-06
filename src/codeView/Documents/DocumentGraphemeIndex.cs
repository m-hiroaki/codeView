using System.Buffers;
using System.Globalization;

namespace CodeView.Documents;

/// <summary>
/// Opt-in experiment. Stores only proven restart boundaries, never source text or a lease.
/// A query must use the same live snapshot lease that built the index.
/// </summary>
internal sealed class DocumentGraphemeIndex
{
    internal const int WindowChars = 64 * 1024;
    internal const int MaximumStorageBytes = 1024 * 1024;
    private const int MaximumCheckpoints = (MaximumStorageBytes - 256) / sizeof(long);
    private readonly WeakReference<DocumentReadLease> _source;
    private readonly long[] _checkpoints;
    private readonly int _count;

    private DocumentGraphemeIndex(DocumentReadLease source, long[] checkpoints, int count,
        long indexedLength, long length)
    {
        _source = new(source);
        _checkpoints = checkpoints;
        _count = count;
        IndexedLength = indexedLength;
        Length = length;
    }

    public long Length { get; }
    public long IndexedLength { get; }
    public bool IsComplete => IndexedLength == Length;
    // Includes a conservative allowance for the index, weak reference and array headers.
    public int StorageBytes => checked(_checkpoints.Length * sizeof(long) + 256);
    internal int CheckpointCount => _count;

    public static DocumentGraphemeIndex Build(DocumentReadLease source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();
        var length = source.Length;
        var stride = Math.Max(4096L, (length + MaximumCheckpoints - 2) / (MaximumCheckpoints - 1));
        var checkpoints = new long[checked((int)(length / stride + 1))];
        var count = 1; // Offset zero is always a boundary, including the empty source.
        long position = 0, nextCheckpoint = stride;
        var buffer = ArrayPool<char>.Shared.Rent(WindowChars);
        try
        {
            while (position < length)
            {
                ct.ThrowIfCancellationRequested();
                var available = Fill(source, position, buffer, ct);
                var eof = position + available == length;
                var consumed = 0;
                while (consumed < available)
                {
                    ct.ThrowIfCancellationRequested();
                    var size = StringInfo.GetNextTextElementLength(buffer.AsSpan(consumed, available - consumed));
                    // The final cluster of a partial window can continue in the next window.
                    if (!eof && consumed + size == available) break;
                    consumed += size;
                    var boundary = position + consumed;
                    if (boundary >= nextCheckpoint)
                    {
                        checkpoints[count++] = boundary;
                        nextCheckpoint = boundary + stride;
                    }
                }
                if (consumed == 0) break; // Explicit unresolved suffix: a cluster exceeds the window.
                position += consumed;
            }
            ct.ThrowIfCancellationRequested();
            return new(source, checkpoints, count, position, length);
        }
        finally { ArrayPool<char>.Shared.Return(buffer, clearArray: true); }
    }

    /// <summary>Exact offset yields equal boundaries. False means the bounded replay cannot decide.</summary>
    public bool TryGetBounds(DocumentReadLease source, long offset, out long before, out long after,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();
        if (!_source.TryGetTarget(out var original) || !ReferenceEquals(original, source))
            throw new ArgumentException("索引の構築元と同じ読取Leaseが必要です。", nameof(source));
        _ = source.Length; // Reject a disposed lease even for offset zero.
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Length);
        before = after = 0;
        if (offset > IndexedLength) return false;
        var found = Array.BinarySearch(_checkpoints, 0, _count, offset);
        var start = _checkpoints[found >= 0 ? found : ~found - 1];
        if (start == offset) { before = after = start; return true; }
        var buffer = ArrayPool<char>.Shared.Rent(WindowChars);
        try
        {
            var available = Fill(source, start, buffer, ct);
            var eof = start + available == Length;
            var consumed = 0;
            while (consumed < available)
            {
                ct.ThrowIfCancellationRequested();
                var size = StringInfo.GetNextTextElementLength(buffer.AsSpan(consumed, available - consumed));
                if (!eof && consumed + size == available) return false;
                var end = start + consumed + size;
                if (end >= offset)
                {
                    before = end == offset ? end : start + consumed;
                    after = end;
                    return true;
                }
                consumed += size;
            }
            return false;
        }
        finally { ArrayPool<char>.Shared.Return(buffer, clearArray: true); }
    }

    private static int Fill(DocumentReadLease source, long offset, char[] buffer, CancellationToken ct)
    {
        var count = (int)Math.Min(WindowChars, source.Length - offset);
        for (var copied = 0; copied < count;)
        {
            ct.ThrowIfCancellationRequested();
            var size = Math.Min(DocumentReadLease.ChunkChars, count - copied);
            source.CopyRangeInto(offset + copied, buffer.AsSpan(copied, size));
            copied += size;
        }
        // A partial scalar is not enough lookahead to establish the previous cluster's end.
        if (offset + count < source.Length && count > 0 && char.IsHighSurrogate(buffer[count - 1])) count--;
        return count;
    }
}
