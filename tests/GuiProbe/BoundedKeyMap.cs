using Avalonia.Input;

namespace CodeView.Presentation;

internal enum BoundedKeyCommand
{
    None, Left, Right, Up, Down, LineStart, LineEnd, DocumentStart, DocumentEnd,
    PageUp, PageDown, SelectAll, ClearSelection, Copy, Unsupported
}

internal readonly record struct BoundedKeyAction(BoundedKeyCommand Command, bool ExtendSelection)
{
    public bool Handled => Command != BoundedKeyCommand.None;
}

/// <summary>Explicit bindings for the keyboard-only candidate, excluded from normal builds.</summary>
internal static class BoundedKeyMap
{
    public static BoundedKeyAction Translate(Key key, KeyModifiers modifiers, bool mac)
    {
        var extend = modifiers.HasFlag(KeyModifiers.Shift);
        var basis = modifiers & ~KeyModifiers.Shift;
        var primary = mac ? KeyModifiers.Meta : KeyModifiers.Control;
        if (key == Key.C && (basis & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            return new(!extend && (basis == primary || (mac && basis == KeyModifiers.Control))
                ? BoundedKeyCommand.Copy : BoundedKeyCommand.Unsupported, false);
        if (key == Key.A && (basis & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            return new(basis == primary && !extend ? BoundedKeyCommand.SelectAll : BoundedKeyCommand.Unsupported, false);
        if (key == Key.Escape && basis == KeyModifiers.None) return new(BoundedKeyCommand.ClearSelection, false);
        var command = key switch
        {
            Key.Left when basis == KeyModifiers.None => BoundedKeyCommand.Left,
            Key.Right when basis == KeyModifiers.None => BoundedKeyCommand.Right,
            Key.Up when basis == KeyModifiers.None => BoundedKeyCommand.Up,
            Key.Down when basis == KeyModifiers.None => BoundedKeyCommand.Down,
            Key.Left when mac && basis == KeyModifiers.Meta => BoundedKeyCommand.LineStart,
            Key.Right when mac && basis == KeyModifiers.Meta => BoundedKeyCommand.LineEnd,
            Key.Up when mac && basis == KeyModifiers.Meta => BoundedKeyCommand.DocumentStart,
            Key.Down when mac && basis == KeyModifiers.Meta => BoundedKeyCommand.DocumentEnd,
            Key.Home when basis == KeyModifiers.None => BoundedKeyCommand.LineStart,
            Key.End when basis == KeyModifiers.None => BoundedKeyCommand.LineEnd,
            Key.Home when basis == KeyModifiers.Control => BoundedKeyCommand.DocumentStart,
            Key.End when basis == KeyModifiers.Control => BoundedKeyCommand.DocumentEnd,
            Key.PageUp when basis == KeyModifiers.None => BoundedKeyCommand.PageUp,
            Key.PageDown when basis == KeyModifiers.None => BoundedKeyCommand.PageDown,
            Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown
                => BoundedKeyCommand.Unsupported,
            _ => BoundedKeyCommand.None
        };
        return new(command, extend);
    }
}
