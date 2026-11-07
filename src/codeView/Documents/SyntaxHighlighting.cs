using System.Diagnostics;
using System.Text;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace CodeView.Documents;

internal readonly record struct SyntaxSpan(int Offset, int Length, int Foreground);

internal sealed record SyntaxHighlighting(IReadOnlyList<SyntaxSpan> Spans, IReadOnlyDictionary<int, string> Palette)
{
    internal const int MaximumChars = 2 * 1024 * 1024;
    internal const int MaximumLineChars = 8192;
    private const int MaximumSpans = 100_000;
    private static readonly TimeSpan LineTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(5);

    internal static (SyntaxHighlighting? Highlighting, string Status) Read(
        DocumentReadLease source, string scope, IRegistryOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (source.Length > MaximumChars) return (null, "構文色なし（本文上限）");

        try
        {
            var elapsed = Stopwatch.StartNew();
            var registry = new Registry(options);
            var grammar = registry.LoadGrammar(scope);
            if (grammar is null) return (null, "構文色なし（文法なし）");

            var theme = registry.GetTheme();
            var palette = theme.GetColorMap().ToDictionary(color => theme.GetColorId(color));
            var spans = new List<SyntaxSpan>();
            var line = new StringBuilder();
            IStateStack? state = null;
            var offset = 0;
            var lineOffset = 0;
            var afterCr = false;
            string? failure = null;

            bool FinishLine()
            {
                ct.ThrowIfCancellationRequested();
                var remaining = TotalTimeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    failure = "構文色なし（処理時間上限）";
                    return false;
                }

                var text = line.ToString();
                line.Clear();
                var lineBudget = remaining < LineTimeout ? remaining : LineTimeout;
                var started = elapsed.Elapsed;
                var tokens = grammar.TokenizeLine(text, state, lineBudget);
                // This TextMateSharp interface does not expose its early-stop flag.
                if (elapsed.Elapsed - started >= lineBudget || elapsed.Elapsed >= TotalTimeout)
                {
                    failure = "構文色なし（処理時間上限）";
                    return false;
                }

                state = tokens.RuleStack;
                foreach (var token in tokens.Tokens)
                {
                    var start = token.StartIndex;
                    var end = Math.Min(token.EndIndex, text.Length);
                    if (start >= end || char.IsLowSurrogate(text[start])
                        || (end < text.Length && char.IsLowSurrogate(text[end]))) continue;

                    var foreground = 0;
                    foreach (var rule in theme.Match(token.Scopes))
                    {
                        if (rule.foreground == 0) continue;
                        foreground = rule.foreground;
                        break;
                    }

                    if (foreground == 0) continue;
                    if (spans.Count == MaximumSpans)
                    {
                        failure = "構文色なし（色範囲の上限）";
                        return false;
                    }

                    spans.Add(new(lineOffset + start, end - start, foreground));
                }
                return true;
            }

            source.VisitChunks(chunk =>
            {
                foreach (var ch in chunk.Span)
                {
                    offset++;
                    if (ch == '\n' && afterCr)
                    {
                        afterCr = false;
                        lineOffset = offset;
                        continue;
                    }

                    afterCr = ch == '\r';
                    if (ch is '\r' or '\n')
                    {
                        if (!FinishLine()) return false;
                        lineOffset = offset;
                    }
                    else
                    {
                        if (line.Length == MaximumLineChars)
                        {
                            failure = "構文色なし（長行）";
                            return false;
                        }

                        line.Append(ch);
                    }
                }
                return true;
            }, ct);
            if (failure is null && line.Length > 0) FinishLine();
            return failure is null ? (new(spans, palette), "") : (null, failure);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            ct.ThrowIfCancellationRequested();
            return (null, "構文色なし（文法読込・解析失敗）");
        }
    }
}
