using System.Buffers;
using System.Globalization;
using System.Text;
using CodeView.Search;

namespace CodeView.Navigation;

/// <summary>Extracts one source spelling, without normalization or retaining the supplied text.</summary>
internal static class IdentifierExtractor
{
    internal const int MaximumChars = 4096;
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly",
        "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct",
        "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
        "ushort", "using", "virtual", "void", "volatile", "while"
    };

    public static bool TryExtract(ReadOnlySpan<char> text, int caret, int selectionStart, int selectionLength,
        bool isCSharp, out string name, out int start, out int length)
    {
        name = ""; start = length = 0;
        if ((uint)caret > text.Length || (uint)selectionStart > text.Length || selectionLength < 0
            || selectionLength > text.Length - selectionStart) return false;
        if (!UnicodeWordBoundary.IsScalarBoundary(text, caret)
            || !UnicodeWordBoundary.IsScalarBoundary(text, selectionStart)
            || !UnicodeWordBoundary.IsScalarBoundary(text, selectionStart + selectionLength)) return false;
        var left = selectionStart;
        var right = selectionStart + selectionLength;
        if (selectionLength == 0)
        {
            left = caret;
            // '@' belongs to the source range, but not the name used for matching.
            if (isCSharp && left < text.Length && text[left] == '@') left++;
            right = left;
            while (left > 0 && Rune.DecodeLastFromUtf16(text[..left], out var value, out var size) == OperationStatus.Done
                && UnicodeWordBoundary.IsWord(value))
            {
                left -= size;
                if (right - left > MaximumChars) return false;
            }
            while (right < text.Length && Rune.DecodeFromUtf16(text[right..], out var value, out var size) == OperationStatus.Done
                && UnicodeWordBoundary.IsWord(value))
            {
                right += size;
                if (right - left > MaximumChars) return false;
            }
            if (isCSharp && left > 0 && text[left - 1] == '@') left--;
            if (isCSharp && ((left > 0 && text[left - 1] is '\\' or '@')
                || (right < text.Length && text[right] == '\\'))) return false;
        }
        var spelling = text[left..right];
        var escaped = isCSharp && !spelling.IsEmpty && spelling[0] == '@';
        var identifier = escaped ? spelling[1..] : spelling;
        if (identifier.IsEmpty || identifier.Length > MaximumChars
            || Rune.DecodeFromUtf16(identifier, out var first, out _) != OperationStatus.Done
            || !(first.Value == '_' || Rune.GetUnicodeCategory(first) is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber)) return false;
        for (var index = 0; index < identifier.Length;)
        {
            if (Rune.DecodeFromUtf16(identifier[index..], out var value, out var size) != OperationStatus.Done
                || !UnicodeWordBoundary.IsWord(value)) return false;
            index += size;
        }
        var candidate = identifier.ToString();
        if (isCSharp && !escaped && Reserved.Contains(candidate)) return false;
        name = candidate; start = left; length = right - left;
        return true;
    }
}
