using System.ComponentModel;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Search;

namespace CodeView.Navigation;

internal sealed class SemanticSearchWorker(FileReader files, IDocumentAdapter documents)
{
    internal WorkspaceSearchSession Start(WorkspaceSearchRequest request, SemanticOrigin origin, DocumentHandle handle,
        int offset, CancellationToken ct,
        Func<string, Func<string, LspPosition>, CancellationToken, Task<SemanticReply>> query) =>
        new(session => RunAsync(request, origin, handle, offset, session, ct, query));

    private async Task<SearchCompletion> RunAsync(WorkspaceSearchRequest request, SemanticOrigin origin, DocumentHandle handle,
        int offset, WorkspaceSearchSession session, CancellationToken ct,
        Func<string, Func<string, LspPosition>, CancellationToken, Task<SemanticReply>> query)
    {
        var reason = SearchEndReason.Completed;
        var configuration = "";
        string? notice = null;
        var partial = false;
        var total = 0;
        var returned = 0;
        var committed = 0;
        SearchWorkerException? fault = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var root = LspFilePath.Resolve(request.Root.FullPath);
            var original = LspFilePath.Resolve(origin.Path.FullPath);
            if (!LspFilePath.IsWithin(root, original) || Path.GetRelativePath(root, original)
                .Split(Path.DirectorySeparatorChar).SkipLast(1).Any(request.Exclusions.IsExcludedDirectory))
                throw new IOException("作業フォルダ内の解析対象ファイルを開いてください。");
            await VerifyOriginalAsync(origin, ct).ConfigureAwait(false);
            var text = CaptureText(handle, ct);
            var positions = new LspSourceText(text);
            var started = DateTime.UtcNow;
            var reply = await query(text, encoding => positions.PositionAt(offset, encoding), ct).ConfigureAwait(false);
            configuration = reply.Configuration;
            await VerifyOriginalAsync(origin, ct).ConfigureAwait(false);
            var results = new SemanticResultMapper(files).Map(reply, request.Root, request.Exclusions, origin, text, started, ct);
            await VerifyOriginalAsync(origin, ct).ConfigureAwait(false);
            total = results.Hits.Count;
            partial = results.IsPartial;
            notice = reply.Status ?? (partial ? "移動先を確認できない結果があります。" : null);
            reason = results.LimitReached ? SearchEndReason.LimitReached : SearchEndReason.Completed;
            foreach (var group in results.Hits.GroupBy(hit => hit.FileScanId))
            {
                foreach (var batch in group.Chunk(SearchLimits.BatchSize))
                {
                    await session.Writer.WriteAsync(new SearchEvent<SearchHit>.Hits(batch), ct).ConfigureAwait(false);
                    returned += batch.Length;
                }
                await session.Writer.WriteAsync(new SearchEvent<SearchHit>.FileCommitted(group.Key), ct).ConfigureAwait(false);
                committed = returned;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { reason = SearchEndReason.Cancelled; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException
            or Win32Exception or JsonException or ArgumentOutOfRangeException or ObjectDisposedException)
        {
            reason = SearchEndReason.Failed;
            var message = error switch
            {
                TimeoutException => "言語サーバーの応答がタイムアウトしました。",
                Win32Exception => "言語サーバーを起動できませんでした。",
                StaleSource or ObjectDisposedException => "探索元の本文が更新されました。読み直して再検索してください。",
                LspRequestException => "言語サーバーが解析要求を完了できませんでした。",
                ArgumentOutOfRangeException => "カーソル位置を原文に対応づけられませんでした。",
                _ => "言語サーバーまたは解析ファイルを確認できませんでした。設定と依存関係を確認してください。"
            };
            notice = message;
        }
        catch (Exception error)
        {
            reason = SearchEndReason.Failed;
            fault = new SearchWorkerException(Completion(), error);
        }

        var end = Completion();
        session.Finish(end);
        session.Writer.TryComplete(fault);
        if (fault is not null) throw fault;
        return end;

        SearchCompletion Completion() => new(reason, null, null, total, returned, committed,
            new(), new Dictionary<ErrorCode, long>(), [], new(configuration, partial, notice));
    }

    private string CaptureText(DocumentHandle handle, CancellationToken ct)
    {
        using var lease = documents.AcquireReadView(handle);
        if (lease.Length > SemanticResultMapper.MaximumTextChars)
            throw new InvalidDataException("本文が言語サーバーに渡せる上限を超えています。");
        return lease.ReadRangeAsString(0, checked((int)lease.Length), ct);
    }

    private async Task VerifyOriginalAsync(SemanticOrigin origin, CancellationToken ct)
    {
        var stamp = await files.CheckStampAsync(origin.Path, ct).ConfigureAwait(false);
        if (stamp.Status != OperationStatus.Success || stamp.Value != origin.Stamp)
            throw new StaleSource();
    }

    private sealed class StaleSource : IOException;
}
