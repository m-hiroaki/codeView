using System.Buffers;
using AvaloniaEdit.Document;

namespace CodeView.Documents;

internal interface IDocumentAdapter
{
    DocumentBuilder CreateBuilder();
    DocumentReadLease AcquireReadView(DocumentHandle document);
}

/// <summary>Initial AvaloniaEdit candidate; production adoption still requires Phase 2 measurements.</summary>
internal sealed class AvaloniaEditDocumentAdapter : IDocumentAdapter
{
    public DocumentBuilder CreateBuilder() => new();
    public DocumentReadLease AcquireReadView(DocumentHandle document) => document.AcquireReadView();
}

internal sealed class DocumentBuilder : IDisposable
{
    private TextDocument? _document = new();
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;

    internal DocumentBuilder() => _document!.UndoStack.SizeLimit = 0;

    public void Append(ReadOnlyMemory<char> chunk)
    {
        VerifyOwner();
        var document = _document ?? throw new ObjectDisposedException(nameof(DocumentBuilder));
        _ = checked(document.TextLength + chunk.Length);
        // Only this bounded chunk is transiently copied, never the whole decoded source.
        document.Insert(document.TextLength, chunk.ToString());
    }

    public DocumentHandle Complete()
    {
        VerifyOwner();
        var document = _document ?? throw new ObjectDisposedException(nameof(DocumentBuilder));
        document.UndoStack.ClearAll();
        document.SetOwnerThread(null!);
        _document = null;
        return new DocumentHandle(document);
    }

    public void Dispose() => _document = null;

    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("文書構築中に所有スレッドを変更できません。");
    }
}

internal sealed class DocumentHandle : IDisposable
{
    private TextDocument? _document;
    private int _readLeases;
    private readonly object _gate = new();

    internal DocumentHandle(TextDocument document)
    {
        _document = document;
        // CreateSnapshot is documented thread-safe even after ownership is released.
        Length = document.CreateSnapshot().TextLength;
    }

    public long Length { get; }
    public int ActiveReadLeases => Volatile.Read(ref _readLeases);
    public bool IsReleased { get { lock (_gate) return _document is null; } }

    // This concrete type stays within Documents and Presentation; Operations uses the handle only.
    public TextDocument ClaimForDisplay()
    {
        lock (_gate)
        {
            var document = _document ?? throw new ObjectDisposedException(nameof(DocumentHandle));
            document.SetOwnerThread(Thread.CurrentThread);
            return document;
        }
    }

    internal DocumentReadLease AcquireReadView()
    {
        lock (_gate)
        {
            var document = _document ?? throw new ObjectDisposedException(nameof(DocumentHandle));
            var snapshot = document.CreateSnapshot();
            Interlocked.Increment(ref _readLeases);
            return new DocumentReadLease(snapshot, () => Interlocked.Decrement(ref _readLeases));
        }
    }

    public void Dispose()
    {
        // Existing leases retain only their immutable snapshot, not a live UI document.
        lock (_gate) _document = null;
    }
}

internal sealed class DocumentReadLease(ITextSource snapshot, Action release) : IDisposable
{
    internal const int ChunkChars = 16 * 1024;
    private ITextSource? _snapshot = snapshot;
    private Action? _release = release;

    public long Length => (_snapshot ?? throw new ObjectDisposedException(nameof(DocumentReadLease))).TextLength;

    public string ReadRange(long offset, int length)
    {
        var source = _snapshot ?? throw new ObjectDisposedException(nameof(DocumentReadLease));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > source.TextLength || length > source.TextLength - offset)
            throw new ArgumentOutOfRangeException(nameof(length));
        return source.GetText(checked((int)offset), length);
    }

    /// <summary>Copies an original-source range on the calling worker without a whole-range string.</summary>
    public void CopyRangeTo(long offset, long length, TextWriter destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var source = _snapshot ?? throw new ObjectDisposedException(nameof(DocumentReadLease));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > source.TextLength || length > source.TextLength - offset)
            throw new ArgumentOutOfRangeException(nameof(length));
        ct.ThrowIfCancellationRequested();
        if (length == 0) return;
        using var reader = source.CreateReader(checked((int)offset), checked((int)length));
        var buffer = ArrayPool<char>.Shared.Rent(ChunkChars);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var count = reader.Read(buffer, 0, ChunkChars);
                if (count == 0) return;
                ct.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, count);
                ct.ThrowIfCancellationRequested();
            }
        }
        finally { ArrayPool<char>.Shared.Return(buffer, clearArray: true); }
    }

    /// <summary>
    /// Visits the immutable source on the calling worker. Chunk memory is borrowed until the callback returns.
    /// Return false to stop after a chunk; the result counts all characters delivered, including that chunk.
    /// </summary>
    public long VisitChunks(Func<ReadOnlyMemory<char>, bool> consume, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consume);
        var source = _snapshot ?? throw new ObjectDisposedException(nameof(DocumentReadLease));
        ct.ThrowIfCancellationRequested();
        using var reader = source.CreateReader();
        var buffer = new char[ChunkChars];
        var delivered = 0L;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Preserve the reader's short chunks; coalescing measured slower for the current source.
            var count = reader.Read(buffer, 0, buffer.Length);
            if (count == 0) return delivered;
            ct.ThrowIfCancellationRequested();
            delivered = checked(delivered + count);
            var keepGoing = consume(buffer.AsMemory(0, count));
            ct.ThrowIfCancellationRequested();
            if (!keepGoing) return delivered;
        }
    }

    public void Dispose()
    {
        _snapshot = null;
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
