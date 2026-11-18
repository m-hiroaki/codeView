using System.Diagnostics;
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
        Point At(int offset) => EditorPointAt(offset);
        void Click(Point start, Point end) => RaiseIdentifierGesture(start, end);

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
    private readonly List<object> _identifierSamples = [];

    private async Task MeasureIdentifierAsync(string scenario, int offset, bool horizontal, CancellationToken ct)
    {
        var search = _viewModel.InFileSearch;
        var view = (IScrollable)_editor.TextArea.TextView;
        using var process = Process.GetCurrentProcess();
        int Count() => search.IdentifierMatches.Count;
        void Reset()
        {
            search.ClearIdentifier();
            view.Offset = default;
        }

        Reset();
        await Frame(ct);
        try
        {
            var inactive = await ScrollAsync();
            view.Offset = default;
            await Frame(ct);
            var point = EditorPointAt(offset) + new Vector(1, 0);
            var watch = Stopwatch.StartNew();
            var first = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnMatches()
            {
                if (Count() > 0) first.TrySetResult(watch.Elapsed.TotalMilliseconds);
            }

            process.Refresh();
            var cpu = process.TotalProcessorTime;
            var managedBefore = GC.GetTotalMemory(false);
            var residentBefore = ResidentBytes();
            search.IdentifierMatchesChanged += OnMatches;
            double firstUpdate;
            try
            {
                RaiseIdentifierGesture(point, point);
                await search.PendingWork.WaitAsync(ct);
                if (Count() == 0) throw new InvalidOperationException($"No identifier matches in {scenario}.");
                firstUpdate = await first.Task.WaitAsync(ct);
            }
            finally { search.IdentifierMatchesChanged -= OnMatches; }
            await Frame(ct);
            var scan = new
            {
                FirstUpdateMilliseconds = firstUpdate,
                SettledMilliseconds = watch.Elapsed.TotalMilliseconds,
                CpuMilliseconds = CpuElapsed(cpu),
                ManagedDeltaBytes = GC.GetTotalMemory(false) - managedBefore,
                ResidentDeltaBytes = ResidentBytes() - residentBefore
            };
            var capped = _window.FindControl<TextBlock>("IdentifierStatus")!.IsEffectivelyVisible;
            if (capped != (Count() == CodeView.Search.SearchLimits.ResultLimit))
                throw new InvalidOperationException($"Incorrect identifier limit status in {scenario}.");
            var active = await ScrollAsync();
            _identifierSamples.Add(new
            {
                Scenario = scenario, DocumentLength = _editor.Document.TextLength, Matches = Count(),
                Scan = scan, InactiveScroll = inactive, ActiveScroll = active
            });
        }
        finally { Reset(); }

        double CpuElapsed(TimeSpan start)
        {
            process.Refresh();
            return (process.TotalProcessorTime - start).TotalMilliseconds;
        }

        long ResidentBytes()
        {
            process.Refresh();
            return process.WorkingSet64;
        }

        async Task<object> ScrollAsync()
        {
            var samples = new double[24];
            var watch = Stopwatch.StartNew();
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            var movement = 0;
            for (var i = 1; i <= samples.Length; i++)
            {
                var before = view.Offset;
                view.Offset = horizontal ? new Vector(i * 120, 0) : new Vector(0, i * 24);
                await Frame(ct);
                samples[i - 1] = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                if (view.Offset != before) movement++;
            }
            if (movement != samples.Length)
                throw new InvalidOperationException($"Identifier scrolling did not reach all positions in {scenario}.");
            Array.Sort(samples);
            return new
            {
                FrameMedianMilliseconds = samples[samples.Length / 2], FrameMaxMilliseconds = samples[^1],
                CpuMilliseconds = CpuElapsed(cpu), ResidentBytes = ResidentBytes()
            };
        }
    }

    private Point EditorPointAt(int offset)
    {
        var view = _editor.TextArea.TextView;
        view.EnsureVisualLines();
        return view.GetVisualPosition(new TextViewPosition(_editor.Document.GetLocation(offset)), VisualYPosition.LineMiddle)
            - view.ScrollOffset;
    }

    private void RaiseIdentifierGesture(Point start, Point end)
    {
        var view = _editor.TextArea.TextView;
        using var pointer = new Pointer(100009, PointerType.Mouse, true);
        Point Root(Point point) => view.TranslatePoint(point, _window)!.Value;
        void Raise(PointerEventArgs args)
        {
            _scriptedRowPointer = args;
            try { view.RaiseEvent(args); }
            finally { _scriptedRowPointer = null; }
        }

        Raise(new PointerPressedEventArgs(view, pointer, _window, Root(start), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        if (start != end)
            Raise(new PointerEventArgs(InputElement.PointerMovedEvent, view, pointer, _window, Root(end), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
        Raise(new PointerReleasedEventArgs(view, pointer, _window, Root(end), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
    }

}
