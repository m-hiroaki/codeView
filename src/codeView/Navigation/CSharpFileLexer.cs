using System.Runtime.ExceptionServices;
using System.Text;
using CodeView.Contracts;
using CodeView.Files;

namespace CodeView.Navigation;

internal readonly record struct CSharpFileLexResult(bool CanCommit, CSharpLexError LexError, OperationError? ReadError);

/// <summary>Connects the declaration lexer to strict source decoding on one serialized worker.</summary>
internal sealed class CSharpFileLexer(FileReader files)
{
    private readonly CSharpLexer _lexer = new();
    private bool _reading;

    // The caller owns source. Tokens are provisional; discard the whole file when CanCommit is false.
    public CSharpFileLexResult Read(PreparedText source, Func<CSharpToken, bool> consume, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(consume);
        if (_reading) throw new InvalidOperationException("字句読取中に再入できません。");
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(source.Path.FullPath), ".cs", StringComparison.OrdinalIgnoreCase))
            return new(false, CSharpLexError.Unsupported, null);
        _reading = true;
        try
        {
            if (_lexer.IsFinished) _lexer.Reset();
            var read = files.ReadChunks(source, chunk =>
            {
                if (!_lexer.Push(chunk.Span, Accept, ct)) throw new StopLexing();
            }, ct);
            if (read.Status != OperationStatus.Success) return new(false, _lexer.Error, read.Error);
            // ReadChunks verifies the original stamp before EOF can finalize the last token.
            return new(_lexer.Complete(consume, ct), _lexer.Error, null);
        }
        catch (StopLexing) { return new(false, _lexer.Error, null); }
        catch (ConsumerFailure error)
        {
            ExceptionDispatchInfo.Throw(error.InnerException!);
            throw;
        }
        finally { _lexer.Abort(); _reading = false; }

        bool Accept(CSharpToken token)
        {
            try { return consume(token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // FileReader classifies these exceptions as source failures, not consumer failures.
                throw new ConsumerFailure(error);
            }
        }
    }

    private sealed class StopLexing : Exception;
    private sealed class ConsumerFailure(Exception inner) : Exception("字句通知の処理に失敗しました。", inner);
}
