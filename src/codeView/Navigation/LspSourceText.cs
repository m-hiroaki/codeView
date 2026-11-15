using System.Text;
using CodeView.Contracts;

namespace CodeView.Navigation;

internal sealed class LspSourceText
{
    private readonly string _text;
    private readonly int[] _lineStarts;

    internal LspSourceText(string text)
    {
        _text = text;
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
            else if (text[index] != '\n') continue;
            starts.Add(index + 1);
        }
        _lineStarts = starts.ToArray();
    }

    internal LspPosition PositionAt(int offset, string encoding)
    {
        if ((uint)offset > (uint)_text.Length || !IsScalarBoundary(offset))
            throw new ArgumentOutOfRangeException(nameof(offset));
        var line = Array.BinarySearch(_lineStarts, offset);
        if (line < 0) line = ~line - 1;
        var end = EndOfLine(line);
        if (offset > end) throw new ArgumentOutOfRangeException(nameof(offset));
        var prefix = _text.AsSpan(_lineStarts[line], offset - _lineStarts[line]);
        var column = encoding switch
        {
            "utf-8" => Encoding.UTF8.GetByteCount(prefix),
            "utf-16" => prefix.Length,
            "utf-32" => CountRunes(prefix),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        };
        return new(line, column);
    }

    internal bool TryRange(LspPosition start, LspPosition end, string encoding, out TextRange range)
    {
        range = default;
        if (!TryOffset(start, encoding, out var first) || !TryOffset(end, encoding, out var last) || last <= first)
            return false;
        range = new(first, last - first, new(start.Line + 1L, first - _lineStarts[start.Line] + 1L));
        return true;
    }

    private bool TryOffset(LspPosition location, string encoding, out int offset)
    {
        offset = 0;
        if ((uint)location.Line >= (uint)_lineStarts.Length || location.Character < 0) return false;
        var start = _lineStarts[location.Line];
        var text = _text.AsSpan(start, EndOfLine(location.Line) - start);
        if (encoding == "utf-16")
        {
            if (location.Character > text.Length || !IsScalarBoundary(start + location.Character)) return false;
            offset = start + location.Character;
            return true;
        }
        if (encoding is not ("utf-8" or "utf-32")) return false;
        var consumed = 0;
        var remaining = location.Character;
        while (remaining > 0 && Rune.DecodeFromUtf16(text[consumed..], out var rune, out var length) == System.Buffers.OperationStatus.Done)
        {
            remaining -= encoding == "utf-8" ? rune.Utf8SequenceLength : 1;
            consumed += length;
        }
        if (remaining != 0) return false;
        offset = start + consumed;
        return true;
    }

    private int EndOfLine(int line)
    {
        if (line + 1 == _lineStarts.Length) return _text.Length;
        var end = _lineStarts[line + 1] - 1;
        return _text[end] == '\n' && end > 0 && _text[end - 1] == '\r' ? end - 1 : end;
    }

    private bool IsScalarBoundary(int offset) => offset == 0 || offset == _text.Length
        || !char.IsLowSurrogate(_text[offset]) || !char.IsHighSurrogate(_text[offset - 1]);

    private static int CountRunes(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }
}
