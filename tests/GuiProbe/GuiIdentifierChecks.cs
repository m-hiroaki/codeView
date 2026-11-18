using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private async Task CheckIdentifierClicksAsync(Action<bool, string> require, CancellationToken ct)
    {
        var area = _editor.TextArea;
        var view = area.TextView;
        var search = _viewModel.InFileSearch;
        view.EnsureVisualLines();
        using var pointer = new Pointer(100009, PointerType.Mouse, true);
        Point At(int offset) => view.GetVisualPosition(new TextViewPosition(_editor.Document.GetLocation(offset)),
            VisualYPosition.LineMiddle) - view.ScrollOffset;
        Point Root(Point point) => view.TranslatePoint(point, _window)!.Value;
        void Raise(PointerEventArgs args)
        {
            _scriptedRowPointer = args;
            try { view.RaiseEvent(args); }
            finally { _scriptedRowPointer = null; }
        }
        void Click(Point start, Point end)
        {
            Raise(new PointerPressedEventArgs(view, pointer, _window, Root(start), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            if (start != end)
                Raise(new PointerEventArgs(InputElement.PointerMovedEvent, view, pointer, _window, Root(end), 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
            Raise(new PointerReleasedEventArgs(view, pointer, _window, Root(end), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        }

        var inside = At(7) + new Vector(1, 0);
        Click(inside, inside);
        await search.PendingWork.WaitAsync(ct);
        require(search.IdentifierMatches.Select(match => _editor.Document.GetText((int)match.Offset, (int)match.Length))
            .SequenceEqual(new[] { "Target", "Target", "Target" }) && !search.IsVisible && search.Query.Length == 0,
            "identifier-click-highlights-whole-word");

        Click(inside, At(11));
        await search.PendingWork.WaitAsync(ct);
        require(search.IdentifierMatches.Count == 3 && !area.Selection.IsEmpty, "identifier-click-preserves-drag-selection");
        var blank = new Point(view.Bounds.Width - 5, inside.Y);
        Click(blank, blank);
        require(search.IdentifierMatches.Count == 0, "identifier-click-blank-clears");
    }
}
