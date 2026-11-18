using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using CodeView.Operations;

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
        var markers = _window.FindControl<IdentifierScrollMarkers>("IdentifierMarkers")!;
        require(view.BackgroundRenderers.OfType<InFileMatchRenderer>().Any() && markers.TrackBounds is null,
            "identifier-highlights-short-document-without-rail");

        Click(inside, At(11));
        await search.PendingWork.WaitAsync(ct);
        require(search.IdentifierMatches.Count == 3 && !area.Selection.IsEmpty, "identifier-click-preserves-drag-selection");
        var blank = new Point(view.Bounds.Width - 5, inside.Y);
        Click(blank, blank);
        require(search.IdentifierMatches.Count == 0, "identifier-click-blank-clears");

        var original = _viewModel.FilePath;
        var fixture = Path.Combine(Path.GetDirectoryName(original)!, "identifier-scroll-markers.txt");
        await File.WriteAllTextAsync(fixture, string.Join('\n',
            Enumerable.Range(1, 201).Select(line => line is 1 or 101 or 201 ? "needle" : "filler")), ct);
        try
        {
            await _viewModel.OpenFilesAsync([fixture], OpenOrigin.Menu);
            await Frame(ct);
            await Frame(ct);
            view.EnsureVisualLines();
            var first = At(1) + new Vector(1, 0);
            Click(first, first);
            await search.PendingWork.WaitAsync(ct);
            await Frame(ct);
            var track = markers.TrackBounds;
            require(track is { Height: > 2 } && markers.HasMarkerAt(1, 201)
                && markers.HasMarkerAt(101, 201) && markers.HasMarkerAt(201, 201),
                "identifier-rail-spans-first-middle-and-last-line");
            var target = markers.TranslatePoint(track!.Value.Center, _window);
            require(!markers.IsHitTestVisible && target is { } p && _window.InputHitTest(p) is Visual hit
                && hit.GetSelfAndVisualAncestors().OfType<ScrollBar>().Any(), "identifier-rail-preserves-scrollbar-hit");
            search.IsVisible = true;
            search.Query = "needle";
            await search.PendingWork.WaitAsync(ct);
            require(search.Matches.Count == 3 && search.IdentifierMatches.Count == 3
                && view.BackgroundRenderers.OfType<InFileMatchRenderer>().Count() == 1,
                "identifier-and-explicit-search-coexist");

            search.IsVisible = false;
            await Frame(ct);
            area.Focus();
            var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            RaiseSearchKey(area, escape);
            if (!escape.Handled || search.IdentifierMatches.Count != 0 || markers.TrackBounds is not null)
                Console.Error.WriteLine($"Identifier Escape handled={escape.Handled} focused={area.IsKeyboardFocusWithin} matches={search.IdentifierMatches.Count} track={markers.TrackBounds}.");
            require(escape.Handled && search.IdentifierMatches.Count == 0 && markers.TrackBounds is null,
                "identifier-escape-clears-highlights-and-rail");
        }
        finally
        {
            _viewModel.CloseFile();
            await _viewModel.OpenFilesAsync([original], OpenOrigin.Menu);
            File.Delete(fixture);
            await Frame(ct);
        }
    }
}
