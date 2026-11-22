using CodeView.Contracts;
using CodeView.Files;
using CodeView.Search;

namespace CodeView.Navigation;

internal static class CppDefinitionSearch
{
    internal static string? QualifierAt(ReadOnlySpan<char> source, int nameStart)
    {
        var parts = new Stack<string>();
        var prefix = source[..nameStart].TrimEnd();
        if (prefix.EndsWith("~")) prefix = prefix[..^1].TrimEnd();
        while (prefix.EndsWith("::"))
        {
            prefix = prefix[..^2].TrimEnd();
            if (prefix.EndsWith(">"))
            {
                var depth = 1;
                var position = prefix.Length - 1;
                while (position > 0 && depth > 0)
                    depth += prefix[--position] switch { '>' => 1, '<' => -1, _ => 0 };
                if (depth != 0) break;
                prefix = prefix[..position].TrimEnd();
            }
            if (prefix.IsEmpty || !IdentifierExtractor.TryExtract(prefix, prefix.Length, prefix.Length, 0, false,
                    out var part, out var start, out _)) break;
            parts.Push(part);
            prefix = prefix[..start].TrimEnd();
        }
        return parts.Count == 0 ? null : string.Join("::", parts);
    }

    internal static WorkspaceSearchSession Start(WorkspaceSearchRequest request, FileReader files, CppDefinitionIndex? index,
        SemanticOrigin? origin, string? qualifier,
        Func<WorkspaceSearchSession> simple, Func<WorkspaceSearchSession>? semantic, Func<bool>? hasCompilationDatabase,
        CancellationToken ct) => new(session => RunAsync(session, request, files, index, origin, qualifier,
            simple, semantic, hasCompilationDatabase, ct));

    private static async Task<SearchCompletion> RunAsync(WorkspaceSearchSession output, WorkspaceSearchRequest request,
        FileReader files, CppDefinitionIndex? index, SemanticOrigin? origin, string? qualifier,
        Func<WorkspaceSearchSession> simple, Func<WorkspaceSearchSession>? semantic, Func<bool>? hasCompilationDatabase,
        CancellationToken ct)
    {
        string? notice = null;
        SearchCompletion result;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (semantic is not null && hasCompilationDatabase?.Invoke() == true)
            {
                result = await RelayAsync(semantic(), output, ct).ConfigureAwait(false);
                if (result.Reason != SearchEndReason.Failed || result.ReturnedHits != 0) return Finish(result);
                notice = "clangdでの定義検索に失敗しました。検索方法を切り替えました。";
            }
            else if (semantic is not null)
                notice = "コンパイルデータベースを確認できなかったため、検索方法を切り替えました。";
            if (index is not null && origin is not null && request.Query is { } name
                && index.TryGet(name, out var matches, out var complete, out var generation))
            {
                var hits = Verify(files, request, name, qualifier, matches, origin, generation, ct);
                if (hits is not null && index.IsCurrent(generation))
                {
                    var limited = matches.Count >= SearchLimits.ResultLimit;
                    return Finish(await PublishAsync(output, hits, complete, limited, notice, ct).ConfigureAwait(false));
                }
                index.Invalidate();
                notice = "タグ作成後にファイルが変更されました。タグを更新できます。";
            }
            result = await RelayAsync(simple(), output, ct).ConfigureAwait(false);
            return Finish(result with { DefinitionNotice = notice });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Finish(Completion(SearchEndReason.Cancelled, 0));
        }
        catch (Exception error)
        {
            var failure = new SearchWorkerException(Completion(SearchEndReason.Failed, 0), error);
            output.Finish(failure.Completion);
            output.Writer.TryComplete(failure);
            throw;
        }

        SearchCompletion Finish(SearchCompletion completion)
        {
            output.Finish(completion);
            output.Writer.TryComplete();
            return completion;
        }
    }

    private static async Task<SearchCompletion> RelayAsync(WorkspaceSearchSession worker, WorkspaceSearchSession output,
        CancellationToken ct)
    {
        try
        {
            await foreach (var item in worker.Events.ReadAllAsync(ct).ConfigureAwait(false))
            {
                output.Publish(worker.LatestProgress);
                await output.Writer.WriteAsync(item, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        return await worker.Completion.ConfigureAwait(false);
    }

    private static List<SearchHit>? Verify(FileReader files, WorkspaceSearchRequest request, string name, string? qualifier,
        IReadOnlyList<CppDefinitionIndex.Match> matches, SemanticOrigin origin, long generation, CancellationToken ct)
    {
        var reference = files.CheckStamp(origin.Path, ct);
        if (reference.Status != OperationStatus.Success || reference.Value != origin.Stamp) return null;
        var root = LspFilePath.Resolve(request.Root.FullPath);
        var hits = new List<SearchHit>();
        var checkedPaths = new Dictionary<string, SourcePath>(CppDefinitionCache.PathComparer);
        foreach (var match in matches.OrderByDescending(item => Rank(item.Definition, qualifier)).Take(SearchLimits.ResultLimit))
        {
            ct.ThrowIfCancellationRequested();
            var file = match.File;
            if (!checkedPaths.TryGetValue(file.RelativePath, out var path))
            {
                path = SourcePath.Create(file.RelativePath, request.Root.FullPath);
                try
                {
                    if (!CppDefinitionCache.PathComparer.Equals(LspFilePath.Resolve(path.FullPath),
                            Path.GetFullPath(file.RelativePath, root))) return null;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
                var actual = files.CheckStamp(path, ct);
                if (actual.Status != OperationStatus.Success || actual.Value != file.Stamp) return null;
                checkedPaths.Add(file.RelativePath, path);
            }
            var definition = match.Definition;
            var label = SimpleSymbolNavigator.Label(definition.Kind);
            var context = definition.Qualifier is { } scope ? "  ·  " + scope : "";
            var snippet = SearchSnippet.Capture(name + "  ·  " + label + context, 0, name.Length);
            // One file group per result keeps the candidate ordering across source files.
            hits.Add(new(hits.Count + 1L, path, definition.Range, snippet, file.Stamp, origin, generation));
        }
        return hits;
    }

    private static int Rank(CppDefinition definition, string? qualifier)
    {
        var scope = definition.Qualifier;
        var score = qualifier is null || scope is null ? 0
            : scope.Equals(qualifier, StringComparison.Ordinal) ? 4
            : scope.EndsWith("::" + qualifier, StringComparison.Ordinal) ? 2 : 0;
        return score + (definition.HasBody ? 1 : 0);
    }

    private static async Task<SearchCompletion> PublishAsync(WorkspaceSearchSession output, List<SearchHit> hits,
        bool complete, bool limited, string? notice, CancellationToken ct)
    {
        var reason = limited ? SearchEndReason.LimitReached : SearchEndReason.Completed;
        foreach (var hit in hits)
        {
            await output.Writer.WriteAsync(new SearchEvent<SearchHit>.Hits([hit]), ct).ConfigureAwait(false);
            await output.Writer.WriteAsync(new SearchEvent<SearchHit>.FileCommitted(hit.FileScanId), ct).ConfigureAwait(false);
        }
        return Completion(reason, hits.Count, !complete) with
        {
            DefinitionSource = "C/C++の定義タグ",
            DefinitionNotice = notice
        };
    }

    private static SearchCompletion Completion(SearchEndReason reason, int count, bool partial = false) =>
        new(reason, null, null, count, count, count, new(FailedEntries: partial ? 1 : 0), new Dictionary<ErrorCode, long>(), []);
}
