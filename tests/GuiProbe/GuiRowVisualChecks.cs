using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CodeView.Documents;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private readonly bool _visualChecks = Environment.GetEnvironmentVariable("CODEVIEW_GUI_ROW_VISUAL_CHECKS") == "1";
    private object? _rowVisualResults;

    private static void ValidateVisualMode(bool hasInput)
    {
        if (Environment.GetEnvironmentVariable("CODEVIEW_GUI_ROW_VISUAL_CHECKS") != "1") return;
        if (hasInput || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            new[] { "CODEVIEW_GUI_ROWS", "CODEVIEW_GUI_WINDOWED", "CODEVIEW_GUI_INTERACTIVE", "CODEVIEW_GUI_READONLY_CHECKS", "CODEVIEW_GUI_SEARCH_PANEL_CHECKS" }
                .Any(name => Environment.GetEnvironmentVariable(name) == "1"))
            throw new ArgumentException("文字幅の比較は入力なしの専用CIで単独実行してください。");
    }

    private async Task RunRowVisualChecksAsync()
    {
        Window? owner = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_sampling.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var ct = deadline.Token;
            TextEditor Editor() => new()
            {
                IsReadOnly = true, ShowLineNumbers = false, WordWrap = false,
                FontFamily = _editor.FontFamily, FontSize = _editor.FontSize, FlowDirection = _editor.FlowDirection,
                Background = _editor.Background, Foreground = _editor.Foreground,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var reference = Editor(); var projected = Editor();
            using var contextual = new ContextualRowControl { FlowDirection = _editor.FlowDirection };
            DocumentDisplaySession? inputSource = null;
            DocumentRowProjection? inputProjection = null;
            contextual.SourceSelectionRequested += (point, offset, extend) =>
            {
                if (inputSource is not { } session || inputProjection is not { } projection
                    || !session.TryGetGraphemeBounds(offset, out var before, out var after))
                    return false;
                var boundary = contextual.NearestBoundary(point, before, after);
                if (!projection.TryDisplayPosition(boundary, out var displayOffset)
                    || !session.TryMoveFromRows(projection, displayOffset, extend))
                    return false;
                contextual.SetSelection(session.Anchor, session.Caret);
                return true;
            };
            reference.Options.EnableTextDragDrop = projected.Options.EnableTextDragDrop = false;
            reference.Options.IndentationSize = projected.Options.IndentationSize = _editor.Options.IndentationSize;
            var referenceLabel = new TextBlock { Text = "全文の対照", Margin = new Thickness(8), Foreground = Brushes.Black };
            var projectionLabel = new TextBlock { Text = "行別投影", Margin = new Thickness(8), Foreground = Brushes.Black };
            var title = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black };
            var host = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("*,*"), Background = Brushes.WhiteSmoke };
            Grid.SetColumnSpan(title, 2); host.Children.Add(title);
            Grid.SetRow(referenceLabel, 1); host.Children.Add(referenceLabel);
            Grid.SetRow(projectionLabel, 1); Grid.SetColumn(projectionLabel, 1); host.Children.Add(projectionLabel);
            Grid.SetRow(reference, 2); host.Children.Add(reference);
            Grid.SetRow(projected, 2); Grid.SetColumn(projected, 1); host.Children.Add(projected);
            var contextLabel = new TextBlock { Text = "原文の前後を含めて配置", Margin = new Thickness(8), Foreground = Brushes.Black };
            Grid.SetRow(contextLabel, 3); Grid.SetColumnSpan(contextLabel, 2); host.Children.Add(contextLabel);
            var contextHost = new Border { Background = _editor.Background, Child = contextual, ClipToBounds = true };
            Grid.SetRow(contextHost, 4); Grid.SetColumnSpan(contextHost, 2); host.Children.Add(contextHost);
            owner = new Window { Title = "codeView CI Unicode context comparison", Width = 1120, Height = 300, Content = host };
            void Unexpected(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
            {
                if (ReferenceEquals(args, _scriptedRowPointer)) return;
                args.Handled = true; _inputInterrupted = true; Finish("physical-input-during-visual-comparison");
            }
            owner.AddHandler(InputElement.PointerPressedEvent, Unexpected, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            owner.AddHandler(InputElement.PointerReleasedEvent, Unexpected, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            owner.AddHandler(InputElement.PointerWheelChangedEvent, Unexpected, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            owner.AddHandler(InputElement.KeyDownEvent, Unexpected, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            owner.Show(_window);
            var cases = new List<object>();
            var accepted = true;
            var contextualAccepted = true;
            foreach (var c in RowVisualCases.All)
            {
                using var builder = new DocumentBuilder();
                builder.Append(c.Text.AsMemory());
                using var handle = builder.Complete();
                using var source = new DocumentDisplaySession(handle);
                source.SetViewport(1, c.Start);
                var rows = source.CreateRowProjection(1, c.Columns);
                var row = rows.Rows[0];
                using var indexedBuilder = new DocumentBuilder(collectGraphemeIndex: true);
                indexedBuilder.Append(c.Text.AsMemory());
                using var indexedHandle = indexedBuilder.Complete();
                using var indexedSource = new DocumentDisplaySession(indexedHandle);
                indexedSource.SetViewport(1, c.Start);
                var indexedRows = indexedSource.CreateRowProjection(1, c.Columns);
                var contextRange = indexedRows.GetContext(0);
                if (c.Name is "zwj-left" or "regional-left")
                {
                    var expected = c.Name == "zwj-left" ? "👩‍💻" : "🇯🇵";
                    RequireRows(indexedRows.Text == expected && contextRange is { VisibleOffset: 1 } fit
                        && fit.VisibleLength == expected.Length, "Contextual row lost a wide grapheme with spare budget.");
                }
                if (contextRange is { } range && indexedSource.ReadRowContext(indexedRows, 0) is { } contextText)
                {
                    var tabWidth = reference.Options.IndentationSize * reference.TextArea.TextView.WideSpaceWidth;
                    contextual.SetRow(contextText, range, new Typeface(_editor.FontFamily), _editor.FontSize, _editor.Foreground, tabWidth);
                    inputSource = indexedSource;
                    inputProjection = indexedRows;
                    indexedSource.Select(indexedRows.Rows[0].SourceOffset + indexedRows.Rows[0].Length, indexedRows.Rows[0].SourceOffset);
                    contextual.SetSelection(indexedSource.Anchor, indexedSource.Caret);
                }
                else contextual.Dispose();
                var boundaries = StringInfo.ParseCombiningCharacters(c.Text).Append(c.Text.Length).ToHashSet();
                var startBoundary = boundaries.Contains((int)row.SourceOffset);
                var endBoundary = boundaries.Contains((int)row.SourceOffset + row.Length);
                reference.Document = CreateDisplayDocument(c.Text);
                projected.Document = CreateDisplayDocument(rows.Text);
                title.Text = $"{c.Name}  原文 [{row.SourceOffset}, {row.SourceOffset + row.Length})  /  宣言フォント {_editor.FontFamily}";
                referenceLabel.Text = "全文の対照（小入力のみ）";
                projectionLabel.Text = $"行別投影: 結合境界 左={startBoundary} / 右={endBoundary}";
                await VisualFrame(owner, ct);
                var contextualHits = 0;
                if (contextRange is { VisibleLength: > 0 } visible)
                {
                    var anchor = indexedSource.Anchor;
                    var caret = indexedSource.Caret;
                    using var pointer = new Pointer(100004, PointerType.Mouse, true);
                    var width = contextual.DesiredSize.Width;
                    for (var sample = 0; sample < 32; sample++)
                    {
                        var point = new Point(width * (sample + .5) / 32, contextual.Bounds.Height / 2);
                        if (!contextual.TryHitSource(point, out _)) continue;
                        var root = contextual.TranslatePoint(point, owner) ?? throw new InvalidOperationException("No Unicode pointer transform.");
                        var press = new PointerPressedEventArgs(contextual, pointer, owner, root, 0,
                            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
                        _scriptedRowPointer = press;
                        try { contextual.RaiseEvent(press); }
                        finally { _scriptedRowPointer = null; pointer.Capture(null); }
                        var start = visible.SourceOffset + visible.VisibleOffset;
                        RequireRows(press.Handled && indexedSource.Caret >= start && indexedSource.Caret <= start + visible.VisibleLength
                            && boundaries.Contains((int)indexedSource.Caret) && indexedSource.Anchor == indexedSource.Caret,
                            $"Contextual pointer did not select a visible grapheme boundary: {c.Name} sample {sample}.");
                        contextualHits++;
                    }
                    RequireRows(contextualHits > 0, $"Contextual row exposed no pointer hits: {c.Name}.");
                    indexedSource.Select(anchor, caret);
                    contextual.SetSelection(anchor, caret);
                }
                if (c.Name == "ascii-control")
                {
                    RequireRows(contextual.TryHitSource(new Point(1, contextual.Bounds.Height / 2), out var hit)
                        && hit == c.Start, "Contextual row hit did not map to the original offset.");
                    RequireRows(!contextual.TryHitSource(new Point(-1, 0), out _), "Contextual row accepted a hit outside its clip.");
                    var selection = contextual.SelectionBounds.ToArray();
                    RequireRows(selection.Length > 0 && selection.All(rect => rect.Left >= 0 && rect.Right <= contextual.Bounds.Width)
                        && contextual.CaretPosition is { X: 0 }, "Contextual row did not place reverse selection and caret in its clip.");
                    contextual.SetSelection(c.Text.Length, 0);
                    RequireRows(contextual.SelectionBounds.SequenceEqual(selection) && contextual.CaretPosition is null,
                        "Contextual row did not clip selection or hide the offscreen caret.");
                    contextual.SetSelection(indexedSource.Anchor, indexedSource.Caret);
                    VerifyContextualPointer(contextual, owner, indexedSource, c, ct);
                    contextual.SetSelection(indexedSource.Anchor, indexedSource.Caret);
                }
                if (c.Name == "arabic-joining")
                {
                    RequireRows(contextRange is { } arabic && arabic.Length == c.Text.Length
                        && arabic.VisibleLength > 0 && arabic.VisibleLength < arabic.Length,
                        "Arabic comparison did not format the complete source line with a partial visible range.");
                    RequireRows(contextual.SelectionBounds.Length > 0, "Arabic selection had no visible layout bounds.");
                }
                reference.TextArea.TextView.EnsureVisualLines(); projected.TextArea.TextView.EnsureVisualLines();
                var watch = Stopwatch.StartNew();
                Point At(TextEditor editor, int offset) => editor.TextArea.TextView.GetVisualPosition(
                    new TextViewPosition(editor.Document.GetLocation(offset)), VisualYPosition.LineMiddle);
                var referenceOrigin = At(reference, (int)row.SourceOffset);
                var projectedOrigin = At(projected, 0);
                var contextPoints = new List<object>();
                double? contextDifference = null;
                if (contextRange is { VisibleLength: > 0 } measured)
                {
                    var start = checked((int)(measured.SourceOffset + measured.VisibleOffset));
                    var end = start + measured.VisibleLength;
                    RequireRows(boundaries.Contains(start) && boundaries.Contains(end),
                        $"Contextual visible range splits a grapheme: {c.Name}.");
                    var fullStart = At(reference, start).X;
                    var contextStart = contextual.GetSourceX(start)!.Value;
                    contextDifference = 0;
                    foreach (var offset in boundaries.Where(offset => offset >= start && offset <= end).Order())
                    {
                        var full = At(reference, offset).X;
                        var placed = contextual.GetSourceX(offset)!.Value;
                        var difference = (placed - contextStart) - (full - fullStart);
                        RequireRows(double.IsFinite(difference), $"Nonfinite contextual glyph geometry: {c.Name}.");
                        contextDifference = Math.Max(contextDifference.Value, Math.Abs(difference));
                        contextPoints.Add(new { SourceOffset = offset, FullX = full, ContextX = placed, RelativeXDifference = difference });
                    }
                }
                var contextAccepted = contextDifference is <= .1;
                contextualAccepted &= contextAccepted;
                var points = new List<object>();
                var maxDelta = 0d;
                var exactMapping = true;
                for (var i = 0; i <= rows.Text.Length; i++)
                {
                    if (i > 0 && i < rows.Text.Length && char.IsHighSurrogate(rows.Text[i - 1]) && char.IsLowSurrogate(rows.Text[i])) continue;
                    exactMapping &= rows.TrySourcePosition(i, out var offset) && offset == row.SourceOffset + i &&
                        rows.TryDisplayPosition(offset, out var reverse) && reverse == i;
                    var fullPoint = At(reference, checked((int)row.SourceOffset + i));
                    var localPoint = At(projected, i);
                    var dx = (localPoint.X - projectedOrigin.X) - (fullPoint.X - referenceOrigin.X);
                    var dy = (localPoint.Y - projectedOrigin.Y) - (fullPoint.Y - referenceOrigin.Y);
                    RequireRows(double.IsFinite(dx) && double.IsFinite(dy), "Nonfinite native glyph geometry.");
                    maxDelta = Math.Max(maxDelta, Math.Max(Math.Abs(dx), Math.Abs(dy)));
                    var hit = projected.TextArea.TextView.GetPosition(localPoint);
                    int? hitOffset = hit is { } value ? projected.Document.GetOffset(value.Location) : null;
                    points.Add(new { DisplayOffset = i, SourceOffset = row.SourceOffset + i, Full = fullPoint, Projected = localPoint,
                        RelativeXDifference = dx, RelativeYDifference = dy, NativeHitOffset = hitOffset,
                        HitMapsToSource = hitOffset is { } h && rows.TrySourcePosition(h, out _) });
                }
                source.SelectAll();
                using var copy = source.BeginCopy();
                var copyExact = copy.ReadText(ct) == c.Text;
                var geometryEqual = maxDelta <= .1;
                RequireRows(exactMapping && copyExact, "Projection changed source mapping/copy.");
                if (c.Control) RequireRows(startBoundary && endBoundary && geometryEqual, "Native visual comparison control failed.");
                var caseAccepted = startBoundary && endBoundary && geometryEqual;
                accepted &= caseAccepted;
                string? capture = null;
                if (c.Capture)
                {
                    await VisualFrame(owner, ct);
                    capture = Path.GetFileName(_output) + "." + c.Name + ".png";
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(host.Bounds.Width), (int)Math.Ceiling(host.Bounds.Height)), new Vector(96, 96));
                    bitmap.Render(host);
                    bitmap.Save(Path.Combine(Path.GetDirectoryName(_output)!, capture), PngBitmapEncoderOptions.Default);
                }
                cases.Add(new { c.Name, c.Control, OriginalText = c.Text, DisplayText = rows.Text,
                    SourceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(c.Text))).ToLowerInvariant(),
                    row.SourceOffset, row.Length, RequestedColumns = c.Columns, StartGraphemeBoundary = startBoundary,
                    EndGraphemeBoundary = endBoundary, MaximumRelativeDifference = maxDelta, GeometryEqual = geometryEqual,
                    SourceMappingExact = exactMapping, OriginalCopyExact = copyExact, Accepted = caseAccepted,
                    Points = points, Capture = capture, CaseControlMilliseconds = watch.Elapsed.TotalMilliseconds,
                    ContextSourceRange = contextRange, ContextDisplayText = indexedRows.Text,
                    ContextSelection = contextual.SelectionBounds.ToArray(), ContextCaret = contextual.CaretPosition,
                    ContextualPointerHits = contextualHits, ContextPoints = contextPoints,
                    ContextMaximumRelativeXDifference = contextDifference, ContextAccepted = contextAccepted,
                    GraphemeBoundaries = boundaries.Order().ToArray() });
                inputProjection = null;
                inputSource = null;
            }
            await VerifyContextualCrossRowDrag(owner, contextHost, reference, ct);
            _rowVisualResults = new { Acquired = true, Accepted = accepted, ContextAccepted = contextualAccepted, ContextCrossRowDrag = true, Cases = cases, FontFamily = _editor.FontFamily.ToString(),
                FontSize = _editor.FontSize, FlowDirection = _editor.FlowDirection.ToString(), TabSize = _editor.Options.IndentationSize,
                CaptureDpi = 96, NativeRenderScaling = owner.RenderScaling,
                Scope = "Small full-paragraph reference vs bounded row, same native controls/font; control render PNG, not physical paint/input. Comparison rejection is retained, not a workflow failure." };
            owner.Close(); owner = null;
            Finish();
        }
        catch (Exception error) { Console.Error.WriteLine(error); Finish("row-visual-comparison-failed: " + error.Message); }
        finally { owner?.Close(); }
    }

    private void VerifyContextualPointer(ContextualRowControl control, Window owner, DocumentDisplaySession source,
        RowVisualCase test, CancellationToken ct)
    {
        using var pointer = new Pointer(100003, PointerType.Mouse, true);
        Point Root(double x) => control.TranslatePoint(new Point(x, control.Bounds.Height / 2), owner)
            ?? throw new InvalidOperationException("No contextual pointer transform.");
        bool Raise(PointerEventArgs args)
        {
            _scriptedRowPointer = args;
            try { control.RaiseEvent(args); }
            finally { _scriptedRowPointer = null; }
            return args.Handled;
        }
        bool Press(double x, KeyModifiers modifiers, PointerUpdateKind button = PointerUpdateKind.LeftButtonPressed)
        {
            var buttons = button == PointerUpdateKind.LeftButtonPressed ? RawInputModifiers.LeftMouseButton : RawInputModifiers.RightMouseButton;
            return Raise(new PointerPressedEventArgs(control, pointer, owner, Root(x), 0,
                new PointerPointProperties(buttons, button), modifiers));
        }
        bool Move(double x) => Raise(new PointerEventArgs(InputElement.PointerMovedEvent, control, pointer, owner, Root(x), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
        bool Release(double x) => Raise(new PointerReleasedEventArgs(control, pointer, owner, Root(x), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));

        var end = test.Start + test.Columns;
        var nearRight = control.DesiredSize.Width - 1;
        RequireRows(Press(nearRight, KeyModifiers.None) && source.Anchor == end && source.Caret == end,
            "Contextual click did not collapse selection at its original offset.");
        RequireRows(Release(nearRight), "Contextual click did not release its pointer.");
        RequireRows(Press(1, KeyModifiers.Shift) && source.Anchor == end && source.Caret == test.Start,
            "Contextual shift-click did not extend the original selection.");
        RequireRows(Release(1), "Contextual shift-click did not release its pointer.");
        using var copy = source.BeginCopy();
        RequireRows(copy.ReadText(ct) == test.Text.Substring(test.Start, test.Columns),
            "Contextual pointer selection changed the original copy range.");
        RequireRows(!Press(-1, KeyModifiers.None) && !Press(1, KeyModifiers.None, PointerUpdateKind.RightButtonPressed)
            && source.Anchor == end && source.Caret == test.Start, "Contextual pointer accepted an outside or right click.");

        RequireRows(Press(1, KeyModifiers.None) && pointer.Captured == control && Move(nearRight)
            && source.Anchor == test.Start && source.Caret == end, "Contextual drag did not extend forward.");
        RequireRows(Move(-1) && source.Caret == end && Release(nearRight) && pointer.Captured is null
            && !Move(1) && source.Caret == end, "Contextual drag did not preserve selection after release.");
        RequireRows(Press(nearRight, KeyModifiers.None) && Move(1) && source.Anchor == end && source.Caret == test.Start,
            "Contextual drag did not extend backward.");
        pointer.Capture(null);
        RequireRows(!Move(nearRight) && source.Caret == test.Start, "Contextual drag survived capture loss.");

        RequireRows(Press(nearRight, KeyModifiers.None) && Move(1), "Contextual drag did not restart.");
        source.SetViewport(1, test.Start + 1);
        RequireRows(Move(nearRight) && pointer.Captured is null && !Press(1, KeyModifiers.None)
            && source.Anchor == end && source.Caret == test.Start,
            "Contextual pointer accepted an old projection.");
    }

    private async Task VerifyContextualCrossRowDrag(Window owner, Border host, TextEditor reference, CancellationToken ct)
    {
        const string text = "abc\r\ndef\r\nghi\r\njkl";
        using var builder = new DocumentBuilder(collectGraphemeIndex: true);
        builder.Append(text.AsMemory());
        using var handle = builder.Complete();
        using var source = new DocumentDisplaySession(handle);
        var projection = source.CreateRowProjection(4, 8);
        var panel = new StackPanel();
        var rows = new ContextualRowControl[4];
        var previous = host.Child;
        try
        {
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i] = new ContextualRowControl { FlowDirection = reference.FlowDirection };
                row.SetRow(source.ReadRowContext(projection, i)!, projection.GetContext(i)!.Value,
                    new Typeface(reference.FontFamily), reference.FontSize, reference.Foreground,
                    reference.Options.IndentationSize * reference.TextArea.TextView.WideSpaceWidth);
                row.SourceSelectionRequested += (point, offset, extend) =>
                {
                    if (!source.TryGetGraphemeBounds(offset, out var before, out var after)) return false;
                    var boundary = row.NearestBoundary(point, before, after);
                    if (!projection.TryDisplayPosition(boundary, out var display)
                        || !source.TryMoveFromRows(projection, display, extend)) return false;
                    foreach (var current in rows) current.SetSelection(source.Anchor, source.Caret);
                    return true;
                };
                row.FindDragRow = point => rows.FirstOrDefault(candidate =>
                    row.TranslatePoint(point, candidate) is { } local && new Rect(candidate.Bounds.Size).Contains(local));
                panel.Children.Add(row);
            }
            host.Child = panel;
            await VisualFrame(owner, ct);
            using var pointer = new Pointer(100005, PointerType.Mouse, true);
            Point Root(ContextualRowControl row) => row.TranslatePoint(new Point(1, row.Bounds.Height / 2), owner)
                ?? throw new InvalidOperationException("No cross-row pointer transform.");
            bool Raise(ContextualRowControl row, PointerEventArgs args)
            {
                _scriptedRowPointer = args;
                try { row.RaiseEvent(args); }
                finally { _scriptedRowPointer = null; }
                return args.Handled;
            }
            for (var start = 0; start < 2; start++)
            {
                var from = rows[start];
                var to = rows[1 - start];
                RequireRows(Raise(from, new PointerPressedEventArgs(from, pointer, owner, Root(from), 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None))
                    && pointer.Captured == from, "Cross-row press did not capture its pointer.");
                RequireRows(Raise(from, new PointerEventArgs(InputElement.PointerMovedEvent, from, pointer, owner, Root(to), 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None))
                    && source.Anchor == projection.Rows[start].SourceOffset && source.Caret == projection.Rows[1 - start].SourceOffset,
                    "Cross-row drag did not use the target row's original position.");
                using var copy = source.BeginCopy();
                RequireRows(copy.ReadText(ct) == "abc\r\n", "Cross-row drag did not preserve CRLF in the original copy.");
                RequireRows(Raise(from, new PointerReleasedEventArgs(from, pointer, owner, Root(to), 0,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left))
                    && pointer.Captured is null, "Cross-row release did not relinquish capture.");
            }

            var rowHeight = rows[0].Bounds.Height;
            var viewport = new ScrollViewer { Content = panel, Height = 2 * rowHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
            host.Child = null;
            host.Child = viewport;
            await VisualFrame(owner, ct);
            var firstVisible = 0;
            foreach (var row in rows)
            {
                row.FindDragRow = point => row.TranslatePoint(point, viewport) is { } inside
                    && new Rect(viewport.Bounds.Size).Contains(inside)
                    ? rows.FirstOrDefault(candidate => row.TranslatePoint(point, candidate) is { } local
                        && new Rect(candidate.Bounds.Size).Contains(local)) : null;
                row.ScrollDragViewport = point =>
                {
                    if (row.TranslatePoint(point, viewport) is not { } local) return false;
                    var direction = local.Y < 0 ? -1 : local.Y >= viewport.Bounds.Height ? 1 : 0;
                    var next = Math.Clamp(firstVisible + direction, 0, rows.Length - 2);
                    if (next == firstVisible) return false;
                    var target = direction < 0 ? next : next + 1;
                    var targetRow = rows[target];
                    var targetPoint = new Point(local.X, rowHeight / 2);
                    if (!targetRow.TryExtendAt(targetPoint)) return false;
                    firstVisible = next;
                    viewport.Offset = new Vector(0, next * rowHeight);
                    return true;
                };
            }
            var fromRow = rows[0];
            RequireRows(Raise(fromRow, new PointerPressedEventArgs(fromRow, pointer, owner, Root(fromRow), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None))
                && pointer.Captured == fromRow, "Autoscroll press did not capture its pointer.");
            var outside = viewport.TranslatePoint(new Point(1, viewport.Bounds.Height + 4), owner)
                ?? throw new InvalidOperationException("No viewport transform.");
            RequireRows(Raise(fromRow, new PointerEventArgs(InputElement.PointerMovedEvent, fromRow, pointer, owner, outside, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None)),
                "Autoscroll move was not handled.");
            var timerDeadline = Stopwatch.StartNew();
            while (firstVisible < 2 && pointer.Captured == fromRow && timerDeadline.Elapsed < TimeSpan.FromSeconds(2))
                await VisualFrame(owner, ct);
            RequireRows(firstVisible == 2 && viewport.Offset.Y > rowHeight && source.Anchor == 0
                && source.Caret == projection.Rows[3].SourceOffset, "Autoscroll did not extend through the newly visible rows.");
            using (var copy = source.BeginCopy())
                RequireRows(copy.ReadText(ct) == "abc\r\ndef\r\nghi\r\n", "Autoscroll selection changed the original copy.");
            var offset = viewport.Offset;
            Raise(fromRow, new PointerReleasedEventArgs(fromRow, pointer, owner, outside, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            await VisualFrame(owner, ct);
            RequireRows(pointer.Captured is null && viewport.Offset == offset, "Autoscroll continued after release.");
        }
        finally
        {
            foreach (var row in rows) row?.Dispose();
            host.Child = previous;
        }
    }

    private static async Task VisualFrame(Window window, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => done.TrySetResult()));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }
}
