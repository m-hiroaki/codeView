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
            { args.Handled = true; _inputInterrupted = true; Finish("physical-input-during-visual-comparison"); }
            owner.AddHandler(InputElement.PointerPressedEvent, Unexpected, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            owner.AddHandler(InputElement.PointerWheelChangedEvent, Unexpected, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            owner.AddHandler(InputElement.KeyDownEvent, Unexpected, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            owner.Show(_window);
            var cases = new List<object>();
            var accepted = true;
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
                if (contextRange is { } range && indexedSource.ReadRowContext(indexedRows, 0) is { } contextText)
                {
                    contextual.SetRow(contextText, range, new Typeface(_editor.FontFamily), _editor.FontSize, _editor.Foreground);
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
                    ContextSelection = contextual.SelectionBounds.ToArray(), ContextCaret = contextual.CaretPosition });
            }
            _rowVisualResults = new { Acquired = true, Accepted = accepted, Cases = cases, FontFamily = _editor.FontFamily.ToString(),
                FontSize = _editor.FontSize, FlowDirection = _editor.FlowDirection.ToString(), TabSize = _editor.Options.IndentationSize,
                CaptureDpi = 96, NativeRenderScaling = owner.RenderScaling,
                Scope = "Small full-paragraph reference vs bounded row, same native controls/font; control render PNG, not physical paint/input. Comparison rejection is retained, not a workflow failure." };
            owner.Close(); owner = null;
            Finish();
        }
        catch (Exception error) { Console.Error.WriteLine(error); Finish("row-visual-comparison-failed: " + error.Message); }
        finally { owner?.Close(); }
    }

    private static async Task VisualFrame(Window window, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => done.TrySetResult()));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }
}
