using CodeView.Contracts;

namespace CodeView.Documents;

internal enum DocumentMoveWarning { None, Stale, InvalidPosition, ContentMismatch, UnverifiedLength }
internal sealed record DocumentMove(TextPosition Position, long Offset, int SelectionLength, DocumentMoveWarning Warning);

internal static class DocumentNavigation
{
    public static DocumentMove Resolve(DocumentHandle handle, TextRange range, string prefix, bool stampsMatch)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefix.Length, 200);
        var document = handle.ClaimForDisplay();
        var line = (int)Math.Min(range.Start.Line, document.LineCount);
        var sourceLine = document.GetLineByNumber(line);
        var column = Math.Min(range.Start.Column, (long)sourceLine.Length + 1);
        var offset = range.Start.Line > document.LineCount ? document.TextLength : checked((int)(sourceLine.Offset + column - 1));
        if (!Boundary(offset)) offset--;
        var location = document.GetLocation(offset);
        if (location.Line < 1 || location.Column < 1) throw new OverflowException("本文位置を表示モデルで表現できません。");
        var position = new TextPosition(location.Line, location.Column);
        var valid = range.Offset == offset && range.End <= document.TextLength && position == range.Start
            && Boundary((int)Math.Min(range.End, document.TextLength));
        var warning = !valid ? DocumentMoveWarning.InvalidPosition : !stampsMatch ? DocumentMoveWarning.Stale
            : prefix.Length > range.Length || !document.GetText(offset, Math.Min(prefix.Length, document.TextLength - offset)).AsSpan().SequenceEqual(prefix)
                ? DocumentMoveWarning.ContentMismatch
            : range.Length != prefix.Length ? DocumentMoveWarning.UnverifiedLength : DocumentMoveWarning.None;
        return new(position, offset, warning == DocumentMoveWarning.None ? checked((int)range.Length) : 0, warning);

        bool Boundary(int index) => index <= 0 || index >= document.TextLength
            || !(char.IsHighSurrogate(document.GetCharAt(index - 1)) && char.IsLowSurrogate(document.GetCharAt(index)));
    }
}
