using Avalonia.Input;
using CodeView.Presentation;

namespace CodeView.Tests.Performance;

public sealed class BoundedKeyMapTests
{
    [TestCase(Key.Left, KeyModifiers.None, false, BoundedKeyCommand.Left, false)]
    [TestCase(Key.Right, KeyModifiers.Shift, true, BoundedKeyCommand.Right, true)]
    [TestCase(Key.Up, KeyModifiers.Shift, false, BoundedKeyCommand.Up, true)]
    [TestCase(Key.Down, KeyModifiers.None, true, BoundedKeyCommand.Down, false)]
    [TestCase(Key.Home, KeyModifiers.None, false, BoundedKeyCommand.LineStart, false)]
    [TestCase(Key.End, KeyModifiers.Shift, true, BoundedKeyCommand.LineEnd, true)]
    [TestCase(Key.Home, KeyModifiers.Control, false, BoundedKeyCommand.DocumentStart, false)]
    [TestCase(Key.End, KeyModifiers.Control | KeyModifiers.Shift, true, BoundedKeyCommand.DocumentEnd, true)]
    [TestCase(Key.Left, KeyModifiers.Meta, true, BoundedKeyCommand.LineStart, false)]
    [TestCase(Key.Right, KeyModifiers.Meta | KeyModifiers.Shift, true, BoundedKeyCommand.LineEnd, true)]
    [TestCase(Key.Up, KeyModifiers.Meta, true, BoundedKeyCommand.DocumentStart, false)]
    [TestCase(Key.Down, KeyModifiers.Meta | KeyModifiers.Shift, true, BoundedKeyCommand.DocumentEnd, true)]
    [TestCase(Key.PageUp, KeyModifiers.None, true, BoundedKeyCommand.PageUp, false)]
    [TestCase(Key.PageDown, KeyModifiers.Shift, false, BoundedKeyCommand.PageDown, true)]
    [TestCase(Key.A, KeyModifiers.Control, false, BoundedKeyCommand.SelectAll, false)]
    [TestCase(Key.A, KeyModifiers.Meta, true, BoundedKeyCommand.SelectAll, false)]
    [TestCase(Key.Escape, KeyModifiers.None, true, BoundedKeyCommand.ClearSelection, false)]
    [TestCase(Key.C, KeyModifiers.Meta, true, BoundedKeyCommand.CopyUnavailable, false)]
    [TestCase(Key.C, KeyModifiers.Control, false, BoundedKeyCommand.CopyUnavailable, false)]
    [TestCase(Key.Left, KeyModifiers.Control, false, BoundedKeyCommand.Unsupported, false)]
    [TestCase(Key.Right, KeyModifiers.Alt | KeyModifiers.Shift, true, BoundedKeyCommand.Unsupported, true)]
    [TestCase(Key.Up, KeyModifiers.Meta, false, BoundedKeyCommand.Unsupported, false)]
    [TestCase(Key.A, KeyModifiers.Control, true, BoundedKeyCommand.Unsupported, false)]
    [TestCase(Key.End, (KeyModifiers)int.MaxValue, false, BoundedKeyCommand.Unsupported, true)]
    [TestCase(Key.O, KeyModifiers.Meta, true, BoundedKeyCommand.None, false)]
    [TestCase(Key.W, KeyModifiers.Control, false, BoundedKeyCommand.None, false)]
    [TestCase(Key.F, KeyModifiers.Meta | KeyModifiers.Shift, true, BoundedKeyCommand.None, true)]
    public void MapsCandidateBindingsWithoutFallingBackToLocalDocument(
        Key key, KeyModifiers modifiers, bool mac, object command, bool extend)
    {
        var action = BoundedKeyMap.Translate(key, modifiers, mac);
        Assert.That(action.Command, Is.EqualTo(command));
        Assert.That(action.ExtendSelection, Is.EqualTo(extend));
        Assert.That(action.Handled, Is.EqualTo((BoundedKeyCommand)command != BoundedKeyCommand.None));
    }
}
