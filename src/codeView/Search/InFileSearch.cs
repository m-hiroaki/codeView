using CodeView.Contracts;
using CodeView.Documents;

namespace CodeView.Search;

internal readonly record struct InFileScanResult(bool ReachedEnd, OperationError? Error,
    long SkippedLongLines = 0, long SkippedScalarMatches = 0)
{
    public bool IsPartial => Error is not null || SkippedLongLines != 0 || SkippedScalarMatches != 0;
}

internal readonly record struct InFileMove(TextRange? Match, bool Wrapped, InFileScanResult Scan);

internal static class InFileSearch
{
    public static InFileScanResult Scan(DocumentReadLease lease, string query, SearchOptions options,
        Func<TextRange, bool> consume, CancellationToken ct)
    {
        if (options.UseRegex)
        {
            var created = RegexLineMatcher.Create(query, options);
            if (created.Status != OperationStatus.Success) return new(false, created.Error);
            var matcher = created.Value;
            try
            {
                lease.VisitChunks(chunk => matcher.Push(chunk.Span, consume, ct), ct);
                var end = !matcher.IsFinished && matcher.Complete(consume, ct);
                return new(end, matcher.Error, matcher.SkippedLongLines, matcher.SkippedScalarMatches);
            }
            finally { matcher.Abort(); }
        }

        var literal = LiteralMatcher.Create(query, options);
        if (literal.Status != OperationStatus.Success) return new(false, literal.Error);
        var search = literal.Value;
        try
        {
            lease.VisitChunks(chunk => search.Push(chunk.Span, consume, ct), ct);
            return new(!search.IsFinished && search.Complete(consume, ct), null);
        }
        finally { search.Abort(); }
    }

    // Match from the original prefix to preserve non-overlap, line anchors and word boundaries.
    // Stop as soon as the nearest directional match is known. Wrapping needs at most one pass.
    public static InFileMove Find(DocumentReadLease lease, string query, SearchOptions options, long boundary,
        bool previous, bool excludeBoundary, CancellationToken ct)
    {
        TextRange? first = null, last = null, nearest = null;
        var scan = Scan(lease, query, options, match =>
        {
            first ??= match;
            last = match;
            if (previous)
            {
                if (match.Offset < boundary) nearest = match;
                else if (nearest is not null) return false;
            }
            else if (match.Offset > boundary || (!excludeBoundary && match.Offset == boundary))
            {
                nearest = match;
                return false;
            }
            return true;
        }, ct);
        var wrapped = nearest is null && scan.ReachedEnd;
        var target = nearest ?? (wrapped ? previous ? last : first : null);
        return new(target, wrapped && target is not null, scan);
    }

    public static int LowerBound(IReadOnlyList<TextRange> matches, long offset)
    {
        var left = 0;
        var right = matches.Count;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (matches[middle].Offset < offset) left = middle + 1;
            else right = middle;
        }
        return left;
    }
}
