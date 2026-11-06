namespace CodeView.Presentation;

internal sealed record RowVisualCase(string Name, string Text, int Start, int Columns, bool Control = false, bool Capture = false);

/// <summary>Small, fixed, single-paragraph references; never used to waive large-file budgets.</summary>
internal static class RowVisualCases
{
    public static IReadOnlyList<RowVisualCase> All { get; } = Array.AsReadOnly<RowVisualCase>([
        new("ascii-control", "0123456789abcdef", 4, 8, Control: true),
        new("unicode-full-control", "a\t日本語 e\u0301 👩‍💻 🇯🇵 אבג سلام", 0, 512, Control: true, Capture: true),
        new("tab-phase", "a\tb\tc end", 1, 8, Capture: true),
        new("combining-left", "Ae\u0301B", 2, 2, Capture: true),
        new("combining-right", "Ae\u0301B", 0, 2),
        new("zwj-left", "A👩‍💻B", 3, 4),
        new("zwj-right", "A👩‍💻B", 0, 3),
        new("regional-left", "A🇯🇵B", 3, 3),
        new("hangul-left", "A\u1100\u1161\u11A8B", 2, 3),
        new("indic-left", "Aक्‍षB", 2, 4),
        new("bidi-embedding", "A\u202Babc אבג\u202C Z", 3, 7, Capture: true),
        new("arabic-joining", "xx سلام yy", 4, 4)
    ]);
}
