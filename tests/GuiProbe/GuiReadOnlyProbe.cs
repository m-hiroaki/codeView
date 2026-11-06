using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace CodeView.Presentation;

/// <summary>CI-only native integration checks; calls control input/commands, not OS input injection.</summary>
internal static class GuiReadOnlyProbe
{
    internal static readonly string[] Actions = ["text", "newline", "paste", "delete", "cut", "undo", "redo", "text-drop"];
    private const string PasteText = "PASTE_日本語😀";
    private const string DropText = "DROP_日本語😀";

    internal static async Task<object> RunAsync(Window owner, TextEditor editor, MainWindowViewModel view,
        CancellationToken ct)
    {
        // This test deliberately changes the OS clipboard. Do not run on a user's desktop.
        Require(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "Read-only automation requires a disposable CI session.");
        Dispatcher.UIThread.VerifyAccess();
        Require(editor.IsReadOnly && !editor.Options.EnableTextDragDrop, "Product editor must deny editing/text drag/drop.");
        var source = editor.Document ?? throw new InvalidOperationException("No product document.");
        Require(source.TextLength <= 1024 * 1024, "Use small functional fixtures.");
        var original = source.Text;
        var initialPath = view.FilePath;
        var initialTabs = view.Tabs.Count;
        var clipboard = owner.Clipboard ?? throw new InvalidOperationException("Native clipboard unavailable.");
        var checks = new List<object>();
        var control = new TextEditor { Document = new TextDocument("CONTROL"), IsReadOnly = false };
        control.Options.EnableTextDragDrop = true;
        var controlWindow = new Window { Title = "codeView CI editable control", Width = 400, Height = 220, Content = control };
        try
        {
            controlWindow.Show(owner);
            await NextFrame(controlWindow, ct);
            await NextFrame(controlWindow, ct);
            control.TextArea.Focus();
            Console.WriteLine("READONLY positive-input-undo-redo");
            control.Select(0, 0);
            control.CaretOffset = 0;
            var input = new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "TYPED_" };
            control.TextArea.RaiseEvent(input);
            Require(input.Handled && control.Document.Text == "TYPED_CONTROL", "Editable routed text input did not change its control.");
            Require(control.Undo() && control.Document.Text == "CONTROL", "Editable undo did not restore content.");
            Require(control.Redo() && control.Document.Text == "TYPED_CONTROL", "Editable redo did not restore input.");

            control.Document = new TextDocument("CONTROL");
            control.Select(0, 0);
            control.CaretOffset = 0;
            await clipboard.SetValueAsync(DataFormat.Text, PasteText);
            Console.WriteLine("READONLY positive-native-paste");
            await clipboard.FlushAsync();
            await WaitClipboard(clipboard, PasteText, ct);
            Require(control.CanPaste, "Editable paste is unavailable.");
            var pasted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<TextEventArgs> onPasted = (_, _) => pasted.TrySetResult(true);
            control.TextArea.TextPasted += onPasted;
            try
            {
                control.Paste();
                await pasted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            finally { control.TextArea.TextPasted -= onPasted; }
            Require(control.Document.Text == PasteText + "CONTROL", "Native clipboard paste did not change the editable control.");
            control.Select(0, PasteText.Length);
            Require(control.CanDelete, "Editable delete is unavailable.");
            control.Delete();
            Require(control.Document.Text == "CONTROL", "Editable delete did not remove selection.");
            control.TextArea.TextView.EnsureVisualLines();
            Console.WriteLine("READONLY positive-text-drop");
            var positiveDrop = Drop(control, DropText);
            await NextFrame(controlWindow, ct);
            Require(positiveDrop.Handled && control.Document.Text.Contains(DropText, StringComparison.Ordinal),
                "Editable text drop did not reach/change its control.");
            controlWindow.Close();
            owner.Activate();
            editor.TextArea.Focus();
            await NextFrame(owner, ct);

            foreach (var selection in new[] { "start", "end", "all" })
            foreach (var action in Actions)
            {
                ct.ThrowIfCancellationRequested();
                Dispatcher.UIThread.VerifyAccess();
                Require(ReferenceEquals(source, editor.Document), "Product document was unexpectedly replaced.");
                var position = selection == "end" ? source.TextLength : 0;
                editor.Select(position, selection == "all" ? source.TextLength : 0);
                editor.CaretOffset = position;
                var selectedLength = editor.SelectionLength;
                Console.WriteLine($"READONLY {selection}/{action}");
                bool? handled = null;
                bool? commandAvailable = null;
                switch (action)
                {
                    case "text":
                    case "newline":
                        var args = new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent,
                            Text = action == "text" ? "ATTEMPT_日本語😀" : "\r\n" };
                        editor.TextArea.RaiseEvent(args);
                        Require(args.Handled, "Product text input did not reach its handler.");
                        handled = args.Handled;
                        break;
                    case "paste":
                        commandAvailable = editor.CanPaste;
                        Require(!editor.CanPaste, "Read-only editor offered paste.");
                        editor.Paste();
                        break;
                    case "delete":
                        // AvalonEdit's CanDelete allows a document; the deletion handler
                        // enforces read-only segments. Verify the body after executing it.
                        commandAvailable = editor.CanDelete;
                        editor.Delete();
                        break;
                    case "cut":
                        commandAvailable = editor.CanCut;
                        Require(!editor.CanCut, "Read-only editor offered cut.");
                        editor.Cut();
                        break;
                    case "undo": Require(!editor.Undo(), "Product undo was executed."); break;
                    case "redo": Require(!editor.Redo(), "Product redo was executed."); break;
                    case "text-drop":
                        var drop = Drop(editor, DropText);
                        Require(drop.Handled && drop.DragEffects == DragDropEffects.None, "Product text drop was not rejected by its routed handler.");
                        handled = drop.Handled;
                        break;
                }
                await NextFrame(owner, ct);
                Require(ReferenceEquals(source, editor.Document) && source.Text == original, "Read-only input changed the product body.");
                Require(view.FilePath == initialPath && view.Tabs.Count == initialTabs, "Text input/drop opened another document.");
                Require(!source.UndoStack.CanUndo && !source.UndoStack.CanRedo, "Editing history was created.");
                checks.Add(new { Selection = selection, Action = action, Caret = position,
                    SelectionLength = selectedLength, Handled = handled, CommandAvailable = commandAvailable, Characters = source.TextLength,
                    BodySha256 = Hash(source.Text), UndoCanUndo = source.UndoStack.CanUndo, UndoCanRedo = source.UndoStack.CanRedo });
            }
            bool? copyVerified = null;
            bool? osCopyVerified = null;
            int? clipboardCharacters = null;
            string? clipboardHash = null;
            if (original.Length > 0)
            {
                editor.Select(0, source.TextLength);
                Require(editor.CanCopy, "Read-only source selection cannot be copied.");
                editor.Copy();
                // Standard AvaloniaEdit Copy normalizes CR/LF to Environment.NewLine.
                // Keep the raw-source comparison separate from the OS-format round trip.
                var expectedCopy = original.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                    .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
                var copied = await WaitClipboard(clipboard, expectedCopy, ct);
                copyVerified = copied == original;
                osCopyVerified = true;
                clipboardCharacters = copied.Length;
                clipboardHash = Hash(copied);
            }
            Require(source.Text == original, "Copy changed the product body.");
            return new
            {
                Passed = true, OriginalCharacters = original.Length, OriginalSha256 = Hash(original), FinalSha256 = Hash(source.Text),
                PositiveControl = new { RoutedInputChanged = true, NativeClipboardPasteChanged = true, DeleteChanged = true,
                    UndoChanged = true, RedoChanged = true, TextDropChanged = true },
                Checks = checks, NativeCopyRoundTrip = copyVerified, NativeCopyOsRoundTrip = osCopyVerified,
                ClipboardCharacters = clipboardCharacters, ClipboardSha256 = clipboardHash,
                CopySkippedForEmpty = original.Length == 0, ProductDocumentPreserved = true,
                ProductTabsPreserved = true, ProductFocused = editor.TextArea.IsFocused,
                Scope = "Native CI UI and clipboard; routed TextInput/Drop and control commands. No OS key/mouse/IME injection."
            };
        }
        finally
        {
            if (controlWindow.IsVisible) controlWindow.Close();
            // CI session owns this clipboard; clear the generated payload before exit.
            await clipboard.ClearAsync();
        }
    }

    private static DragEventArgs Drop(TextEditor editor, string text)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(text));
        var args = new DragEventArgs(DragDrop.DropEvent, data, editor.TextArea, new Point(12, 12), KeyModifiers.None)
        { DragEffects = DragDropEffects.Copy };
        editor.TextArea.RaiseEvent(args);
        return args;
    }

    private static async Task NextFrame(Window window, CancellationToken ct)
    {
        var next = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ => next.TrySetResult(true));
        await next.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    private static async Task<string> WaitClipboard(IClipboard clipboard, string expected, CancellationToken ct)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            ct.ThrowIfCancellationRequested();
            var actual = await clipboard.TryGetValueAsync(DataFormat.Text);
            if (actual == expected) return actual;
            await Task.Delay(10, ct);
        }
        throw new TimeoutException("Native clipboard value did not match the generated fixture.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
