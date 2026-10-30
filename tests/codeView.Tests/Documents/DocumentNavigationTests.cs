using CodeView.Contracts;
using CodeView.Documents;

namespace CodeView.Tests.Documents;

internal sealed class DocumentNavigationTests
{
    private static DocumentHandle Body(string text)
    {
        using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
        builder.Append(text.AsMemory());
        return builder.Complete();
    }

    [Test]
    public void ExactCasePrefixAndUtf16CoordinatesProduceBoundedSelection()
    {
        using var body = Body("a😀 FOO\r\nfoo");
        var move = DocumentNavigation.Resolve(body, new(4, 3, new(1, 5)), "FOO", true);
        Assert.That(move, Is.EqualTo(new DocumentMove(new(1, 5), 4, 3, DocumentMoveWarning.None)));
        Assert.That(body.ActiveReadLeases, Is.Zero);
    }

    [TestCase(false, "foo", DocumentMoveWarning.Stale)]
    [TestCase(true, "FOO", DocumentMoveWarning.ContentMismatch)]
    public void ChangedStampOrActualTextSuppressesSelection(bool stamp, string prefix, DocumentMoveWarning warning)
    {
        using var body = Body("foo");
        var move = DocumentNavigation.Resolve(body, new(0, 3, new(1, 1)), prefix, stamp);
        Assert.That(move.SelectionLength, Is.Zero);
        Assert.That(move.Warning, Is.EqualTo(warning));
    }

    [TestCase(100, 1)]
    [TestCase(1, 100)]
    [TestCase(long.MaxValue, 1)]
    [TestCase(1, long.MaxValue)]
    public void OutOfRangePositionClampsWithoutSelecting(long line, long column)
    {
        using var body = Body("x");
        var move = DocumentNavigation.Resolve(body, new(99, 5, new(line, column)), "foo", true);
        Assert.That(move.Position, Is.EqualTo(new TextPosition(1, 2)));
        Assert.That(move.Offset, Is.EqualTo(1));
        Assert.That(move.Warning, Is.EqualTo(DocumentMoveWarning.InvalidPosition));
        Assert.That(move.SelectionLength, Is.Zero);
    }

    [TestCase(1, 1, 2)]
    [TestCase(2, 1, 3)]
    public void HalfSurrogateStartOrEndNeverCreatesSelection(int offset, int length, int column)
    {
        using var body = Body("a😀b");
        var move = DocumentNavigation.Resolve(body, new(offset, length, new(1, column)), "x", true);
        Assert.That(move.Offset, Is.EqualTo(1));
        Assert.That(move.SelectionLength, Is.Zero);
        Assert.That(move.Warning, Is.EqualTo(DocumentMoveWarning.InvalidPosition));
    }

    [Test]
    public void LongMatchOnlyChecksShortPrefixAndDoesNotClaimFullSelection()
    {
        using var body = Body(new string('a', 100000));
        var move = DocumentNavigation.Resolve(body, new(0, 100000, new(1, 1)), new string('a', 200), true);
        Assert.That(move.Warning, Is.EqualTo(DocumentMoveWarning.UnverifiedLength));
        Assert.That(move.SelectionLength, Is.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentNavigation.Resolve(body, new(0, 201, new(1, 1)), new string('a', 201), true));
    }

    [Test]
    public void EmptyAndEndZeroLengthMatchesAreValid()
    {
        using var empty = Body("");
        Assert.That(DocumentNavigation.Resolve(empty, new(0, 0, new(1, 1)), "", true).Warning, Is.EqualTo(DocumentMoveWarning.None));
        using var body = Body("a\r\n");
        Assert.That(DocumentNavigation.Resolve(body, new(3, 0, new(2, 1)), "", true),
            Is.EqualTo(new DocumentMove(new(2, 1), 3, 0, DocumentMoveWarning.None)));
    }
}
