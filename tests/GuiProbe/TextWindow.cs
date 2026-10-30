using System.Globalization;
using CodeView.Documents;

namespace CodeView.Presentation;

/// <summary>Evaluation-only source slice. This is not a complete document navigation model.</summary>
internal sealed record TextWindow(long SourceOffset, long TargetOffset, string Text)
{
    internal const int MaxWindowChars = 16 * 1024;

    internal static int ParseWindowChars(string? value)
    {
        if (value is null) return MaxWindowChars;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size is < 4 or > MaxWindowChars)
            throw new ArgumentException("表示量には4〜16,384の整数を指定してください。", nameof(value));
        return size;
    }

    internal static TextWindow Create(DocumentReadLease source, long targetOffset, int maxChars = MaxWindowChars)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxChars, MaxWindowChars);
        var length = source.Length;
        ArgumentOutOfRangeException.ThrowIfNegative(targetOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetOffset, length);
        if (SplitsBoundary(targetOffset)) targetOffset--;
        var start = Math.Clamp(targetOffset - maxChars / 4, 0, Math.Max(0, length - maxChars));
        if (SplitsBoundary(start)) start++;
        var end = Math.Min(length, start + maxChars);
        if (SplitsBoundary(end)) end--;
        return new(start, targetOffset, source.ReadRange(start, checked((int)(end - start))));

        bool SplitsBoundary(long offset)
        {
            if (offset == 0 || offset == length) return false;
            var pair = source.ReadRange(offset - 1, 2);
            return (char.IsHighSurrogate(pair[0]) && char.IsLowSurrogate(pair[1])) || pair is "\r\n";
        }
    }

    internal long ToSourceOffset(int displayOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(displayOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(displayOffset, Text.Length);
        return SourceOffset + displayOffset;
    }
}
