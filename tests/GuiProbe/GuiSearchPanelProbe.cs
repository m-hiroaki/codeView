using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using AvaloniaEdit;
using CodeView.Operations;

namespace CodeView.Presentation;

internal static class GuiSearchPanelProbe
{
    private const string Query = "CODEVIEW_FIND";

    internal static async Task<object> RunAsync(Window owner, TextEditor editor, MainWindowViewModel view, CancellationToken ct)
    {
        Require(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "Use a disposable CI session.");
        var original = editor.Document;
        var text = original.Text;
        Require(Find<TextBox>(owner, "SearchQuery") is null, "Search panel was eagerly constructed.");
        await view.OpenFilesAsync([Path.GetDirectoryName(view.FilePath)!], OpenOrigin.Menu);
        Require(view.CanSearchWorkspace, "Functional fixture workspace unavailable.");
        view.SearchPanel.Query = Query;
        view.SearchPanel.MatchCase = true;
        var started = Stopwatch.GetTimestamp();
        owner.FindControl<MenuItem>("WorkspaceSearchMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var synchronous = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Console.WriteLine($"SEARCH first-show synchronous-ms={synchronous:F3}");
        await Frame(owner, ct);
        await Frame(owner, ct);
        var twoTicks = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var query = Find<TextBox>(owner, "SearchQuery") ?? throw new InvalidOperationException("No query control after show.");
        var results = Find<ListBox>(owner, "SearchResultsList") ?? throw new InvalidOperationException("No results control.");
        Console.WriteLine($"SEARCH after-layout visible={view.SearchPanel.IsVisible} query-matches={query.Text == Query} focused={query.IsFocused}");
        Require(view.SearchPanel.IsVisible && query.Text == Query && query.IsFocused, "First show lost binding/focus.");
        // Force a failed immediate focus, then simulate a later user interaction.
        // The queued focus must not steal focus back once the input epoch changes.
        view.SearchPanel.IsVisible = false;
        query.IsEnabled = false;
        owner.FindControl<MenuItem>("WorkspaceSearchMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        view.RecordUserInteraction();
        editor.TextArea.Focus();
        query.IsEnabled = true;
        await Frame(owner, ct);
        Require(editor.TextArea.IsFocused, "Delayed search focus stole focus after a newer interaction.");
        query.Focus();
        var regex = owner.GetLogicalDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "正規表現"));
        query.Text = "[";
        regex.IsChecked = true;
        await Frame(owner, ct);
        Require(view.SearchPanel.Query == "[" && view.SearchPanel.UseRegex, "Two-way binding failed.");
        Click(owner, "検索");
        await view.SearchPanel.PendingWork.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await Frame(owner, ct);
        Require(view.SearchPanel.Rows.Count == 0 && view.SearchPanel.Summary.Contains("正規表現の構文が不正", StringComparison.Ordinal),
            "Invalid regex was not reported as a normal search failure.");
        regex.IsChecked = false;
        query.Text = Query;
        await Frame(owner, ct);
        Click(owner, "検索");
        await view.SearchPanel.PendingWork.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await Frame(owner, ct);
        Require(view.SearchPanel.Rows.Count == 1 && view.SearchPanel.Rows[0].File.IsCommitted, "Expected one committed match.");
        results.SelectedItem = view.SearchPanel.Rows[0];
        await Frame(owner, ct);
        GuiPerformanceProbe.RaiseSearchKey(results, new KeyEventArgs
        { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.None });
        await view.PendingWork.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await Frame(owner, ct);
        await Frame(owner, ct);
        Require(view.Tabs.Count == 1 && ReferenceEquals(editor.Document, original), "Result navigation did not reuse the tab/document.");
        Require(editor.SelectedText == Query && editor.TextArea.IsFocused, "Result Enter lost selection/focus.");
        for (var i = 0; i < 3; i++)
        {
            view.SearchPanel.IsVisible = false;
            await Frame(owner, ct);
            view.SearchPanel.IsVisible = true;
            await Frame(owner, ct);
            Require(ReferenceEquals(query, Find<TextBox>(owner, "SearchQuery")) && query.Text == Query
                && view.SearchPanel.MatchCase && !view.SearchPanel.UseRegex && view.SearchPanel.Rows.Count == 1,
                "Repeated show rebuilt controls or lost state.");
        }
        Click(owner, "クリア");
        await Frame(owner, ct);
        Require(view.SearchPanel.Rows.Count == 0, "Clear button lost its event handler.");
        query.Text = "";
        await Frame(owner, ct);
        Require(!view.SearchPanel.CanStart && !Button(owner, "検索").IsEnabled, "Empty query offered search.");
        Require(ReferenceEquals(editor.Document, original) && original.Text == text, "Search controls changed the body.");
        return new
        {
            Passed = true, FirstShowSynchronousMilliseconds = synchronous, FirstShowTwoTicksMilliseconds = twoTicks,
            InitiallyAbsent = true, InitialBindingAndFocus = true, LateFocusCancelled = true, TwoWayBinding = true, InvalidRegexReported = true,
            CommittedMatches = 1, ResultTabReused = true, ResultSelectionAndFocus = true, RepeatedShows = 3,
            ControlsReused = true, ClearWorked = true, EmptyQueryDisabled = true, BodyUnchanged = true,
            BodySha256 = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(text))).ToLowerInvariant(),
            Scope = "Native CI controls, routed menu click/result Enter, no OS input injection; two ticks are not physical paint."
        };
    }

    private static T? Find<T>(Window owner, string name) where T : Control =>
        owner.GetLogicalDescendants().OfType<T>().SingleOrDefault(c => c.Name == name);
    private static Button Button(Window owner, string text) =>
        owner.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, text));
    private static void Click(Window owner, string text) => Button(owner, text).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static async Task Frame(Window owner, CancellationToken ct)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.RequestAnimationFrame(_ => frame.TrySetResult());
        await frame.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
