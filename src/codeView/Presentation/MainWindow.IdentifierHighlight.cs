using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.Document;

namespace CodeView.Presentation;

internal sealed partial class MainWindow
{
    private void ConfigureIdentifierClicks(TextEditor editor)
    {
        const double dragThreshold = 4;
        var area = editor.TextArea;
        var view = area.TextView;
        IPointer? pressed = null;
        Point origin = default;
        long epoch = 0;

        area.AddHandler(PointerPressedEvent, (_, args) =>
        {
            pressed = null;
            if (args.Pointer.Type != PointerType.Mouse || args.ClickCount != 1 || args.KeyModifiers != KeyModifiers.None
                || !args.GetCurrentPoint(view).Properties.IsLeftButtonPressed) return;
            origin = args.GetPosition(view);
            if (!new Rect(view.Bounds.Size).Contains(origin)) return;
            epoch = _viewModel.ViewEpoch;
            pressed = args.Pointer;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        area.AddHandler(PointerMovedEvent, (_, args) =>
        {
            if (ReferenceEquals(pressed, args.Pointer) && HasDragged(args.GetPosition(view))) pressed = null;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        area.PointerCaptureLost += (_, args) =>
        {
            if (ReferenceEquals(pressed, args.Pointer)) pressed = null;
        };
        area.AddHandler(PointerReleasedEvent, (_, args) =>
        {
            if (!ReferenceEquals(pressed, args.Pointer) || args.InitialPressMouseButton != MouseButton.Left) return;
            pressed = null;
            var point = args.GetPosition(view);
            if (HasDragged(point) || args.KeyModifiers != KeyModifiers.None || epoch != _viewModel.ViewEpoch
                || editor.Document is not { } document || !ReferenceEquals(document, _viewModel.Document)) return;
#if CODEVIEW_PERFORMANCE_PROBE
            if (GuiPerformanceProbe.HasInteractiveDocument || GuiPerformanceProbe.HasContextualRows) return;
#endif
            // A character hit uses the preceding caret boundary rather than rounding to the nearest one.
            var location = new Rect(view.Bounds.Size).Contains(point)
                ? view.GetPositionFloor(point + view.ScrollOffset) : null;
            var offset = -1;
            if (location is { } position && document.GetLineByNumber(position.Line) is { } line
                && position.Column <= line.Length)
                offset = document.GetOffset(new TextLocation(position.Line, position.Column));
            _viewModel.InFileSearch.HighlightIdentifier(document, offset,
                Path.GetExtension(_viewModel.FilePath).Equals(".cs", StringComparison.OrdinalIgnoreCase));
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        bool HasDragged(Point point) =>
            Math.Abs(point.X - origin.X) > dragThreshold || Math.Abs(point.Y - origin.Y) > dragThreshold;
    }
}
