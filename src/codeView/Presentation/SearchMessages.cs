using CodeView.Search;

namespace CodeView.Presentation;

internal static class SearchMessages
{
    public static string RegexLimits { get; } =
        $"行ごとに照合します。{SearchLimits.RegexLineChars:N0} UTF-16単位を超える行は未評価です。"
        + $"照合がタイムアウト（{SearchLimits.RegexTimeout.TotalMilliseconds:N0} ms）すると検索を中断します。";

    public static string InvalidQuery(string? query, SearchOptions options)
    {
        if (string.IsNullOrEmpty(query)) return "検索語を入力してください。";
        if (query.Length > SearchLimits.QueryChars)
            return $"検索語が長すぎます。上限は {SearchLimits.QueryChars:N0} UTF-16単位です。";
        if (options.UseRegex) return "正規表現の構文が不正です。";
        if (query.AsSpan().IndexOfAny('\r', '\n') >= 0) return "通常の検索語に改行は指定できません。";
        return "検索語に不正なUnicode文字列が含まれています。";
    }
}
