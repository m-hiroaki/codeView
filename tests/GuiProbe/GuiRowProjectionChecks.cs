using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CodeView.Documents;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private async Task RunRowChecksAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ct = timeout.Token;
            var source = _displaySession!;
            var original = _sourceDocument!;
            RequireRows(original.TextLength <= 2 * 1024 * 1024, "Use bounded functional fixtures, not the performance corpus.");
            var body = original.Text;
            var checks = new List<string>();
            void Check(string name, bool passed)
            { RequireRows(passed, name); checks.Add(name); }
            void SendKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
            {
                var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
                _scriptedRowKey = args;
                try { _editor.TextArea.RaiseEvent(args); }
                finally { _scriptedRowKey = null; }
                RequireRows(args.Handled && !_finished, "Key was not handled by source input.");
            }
            var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            var documentModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            SendKey(OperatingSystem.IsMacOS() ? Key.Down : Key.End, documentModifier);
            Check("routed-document-end", source.Caret == source.Length);
            await RowFrame(ct);
            Check("end-caret-projected", _interactiveRows!.TryDisplayPosition(source.Caret, out _));
            SendKey(OperatingSystem.IsMacOS() ? Key.Up : Key.Home, documentModifier);
            Check("routed-document-start", source.Caret == 0);
            SendKey(Key.Right, KeyModifiers.Shift);
            Check("routed-shift-selection", source.SelectionLength == Math.Min(1, source.Length));
            SendKey(Key.A, primary);
            Check("routed-select-all-original", source.SelectionLength == source.Length);
            await RowFrame(ct);
            Check("native-selection-excludes-synthetic-lf", _editor.SelectionLength == 0);
            Check("selection-spans-exclude-separators", _interactiveRows!.ProjectSelection(0, source.Length).Spans.ToArray()
                .All(s => !_interactiveRows.Text.AsSpan(s.Offset, s.Length).Contains('\n')));

            // Clipboard goes through the same coordinator/independent original lease as physical keys.
            if (source.Length > 0)
            {
                var count = _copiesCompleted;
                SendKey(Key.C, primary);
                while (_copiesCompleted == count) await Task.Delay(10, ct);
                await _copyObserver.WaitAsync(ct);
                Check("native-clipboard-original-count", _lastCopiedCharacters == source.Length && _clipboardVerificationFailures == 0);
            }
            var anchor = source.Anchor;
            var caret = source.Caret;
            var beforeSpans = _rowRenderer!.DrawnSpans;
            var previous = Stopwatch.GetTimestamp();
            for (var i = 0; i < 64; i++)
            {
                var start = Stopwatch.GetTimestamp();
                source.SetViewport(1 + i % original.LineCount, i % 2 == 0 ? 0 : Math.Min(32, source.Length));
                RenderInteractive();
                _editor.TextArea.TextView.EnsureVisualLines();
                _scrollTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                Check("viewport-preserves-source-" + i, source.Anchor == anchor && source.Caret == caret);
                Check("bounded-rows-" + i, _editor.Document.TextLength <= _windowChars &&
                    _editor.Document.LineCount == Math.Min(_rowCount, original.LineCount - source.FirstVisibleLine + 1));
                await RowFrame(ct);
                var now = Stopwatch.GetTimestamp();
                _tickIntervals.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                previous = now;
            }
            Check("offscreen-caret-stays-independent", source.Length == 0 || !_interactiveRows!.TryDisplayPosition(caret, out _));
            Check("selection-background-rendered", source.Length == 0 || _rowRenderer.DrawnSpans > beforeSpans);
            Check("clipping-marks-rendered", !_sawClippedRow || _rowMargin!.DrawnMarks > 0);
            SendKey(Key.Left);
            Check("next-input-reveals-source-caret", _interactiveRows!.TryDisplayPosition(source.Caret, out _));

            var stale = _interactiveRows!;
            source.SetViewport(original.LineCount, 0);
            RenderInteractive();
            var priorCaret = source.Caret;
            Check("stale-display-rejected", !source.TryMoveFromRows(stale, 0) && source.Caret == priorCaret);
            var rows = _interactiveRows!;
            Check("display-source-first-row-label", _sourceLineNumbers!.Numbers.FirstSourceLine == source.FirstVisibleLine);
            Check("source-labels-rendered", _sourceLineNumbers.RenderedLabels > 0);
            Check("source-unchanged", original.Text == body && !original.UndoStack.CanUndo && !original.UndoStack.CanRedo);
            Check("read-only-native-display", _editor.IsReadOnly && !_editor.Document.UndoStack.CanUndo);
            _rowResults = new
            {
                Checks = checks, SourceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(body))).ToLowerInvariant(),
                Utf16Characters = source.Length, SourceLineCount = original.LineCount,
                ScrollSteps = _scrollTimes.Count, DrawnSelectionSpans = _rowRenderer.DrawnSpans,
                DrawnClippingMarks = _rowMargin!.DrawnMarks, SawClippedRow = _sawClippedRow,
                OriginalClipboardChecks = _clipboardVerificationChecks,
                Limit = "Native control integration; no physical input injection/paint. Logical UTF-16 horizontal axis; full scrollbars/visual-column alignment pending."
            };
            Finish();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Finish("row-projection-check-failed: " + error.Message);
        }
    }

    private async Task RowFrame(CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _window.RequestAnimationFrame(_ => _window.RequestAnimationFrame(_ => completion.TrySetResult()));
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    private static void RequireRows(bool passed, string message)
    { if (!passed) throw new InvalidOperationException(message); }
}
