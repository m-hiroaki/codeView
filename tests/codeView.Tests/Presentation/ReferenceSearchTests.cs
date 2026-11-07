using System.Text;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Navigation;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class ReferenceSearchTests
{
    private static async Task YieldAsync() => await Task.Yield();

    [Test]
    public void ReferencesUseCaseSensitiveWordsAcrossDeclarationsCommentsStringsAndCp932() => WithView(
        "class Target {}\r\n// Target target TargetSuffix\r\nvar x = \"Target\";\r\n@Target", async (view, root, file) =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp932 = Path.Combine(root, "repo", "日本語.txt");
        await File.WriteAllBytesAsync(cp932, Encoding.GetEncoding(932).GetBytes("// 日本語 Target"));
        var original = await File.ReadAllBytesAsync(file);
        view.SearchPanel.MatchCase = false;
        view.SearchPanel.WholeWord = false;
        view.SearchPanel.UseRegex = true;
        await view.FindReferencesAsync(8, 8, 0);
        var panel = view.SearchPanel;
        Assert.That(panel.Query, Is.EqualTo("Target"));
        Assert.That((panel.MatchCase, panel.WholeWord, panel.UseRegex), Is.EqualTo((true, true, false)));
        Assert.That(panel.IsVisible, Is.True);
        Assert.That(panel.Description, Does.Contain("名前一致").And.Contain("意味解析は行いません"));
        Assert.That(panel.Rows, Has.Count.EqualTo(5));
        Assert.That(panel.Rows.All(panel.CanActivate), Is.True);
        Assert.That(panel.Rows.Select(x => x.Match), Is.All.EqualTo("Target"));
        var cp932Row = panel.Rows.Single(x => x.FullPath == cp932);
        Assert.That(cp932Row.Hit.Range.Start, Is.EqualTo(new TextPosition(1, 8)));
        Assert.That(await File.ReadAllBytesAsync(file), Is.EqualTo(original));
        (long Offset, int Length)? selected = null;
        view.SelectSearchRange += (offset, length) => selected = (offset, length);
        await view.ActivateSearchResultAsync(cp932Row);
        Assert.That(view.FilePath, Is.EqualTo(cp932));
        Assert.That(selected, Is.EqualTo((7L, 6)));
    });

    [Test]
    public void EscapedReservedIdentifierSearchesItsNameAndOrdinarySearchResetsDescription() => WithView(
        "class C { int @class; } // class", async (view, _, _) =>
    {
        await view.FindReferencesAsync(0, 14, 6);
        Assert.That(view.SearchPanel.Query, Is.EqualTo("class"));
        Assert.That(view.SearchPanel.Rows, Has.Count.EqualTo(3));
        view.SearchPanel.Query = "C";
        await view.SearchPanel.StartAsync();
        Assert.That(view.SearchPanel.Description, Is.EqualTo("フォルダ内のテキスト検索"));
        Assert.That(view.SearchPanel.Rows, Has.Count.EqualTo(1));
    });

    [TestCase("日本語", "日本語 日本語列 日本語", 2)]
    [TestCase("e\u0301", "e\u0301 é e\u0301", 2)]
    [TestCase("\U00010400name", "\U00010400name \U00010428name \U00010400nameSuffix", 1)]
    public void UnicodeNamesKeepOrdinalSpellingAndWordBoundaries(string name, string text, int count) => WithView(text, async (view, _, _) =>
    {
        await view.FindReferencesAsync(0, 0, name.Length);
        Assert.That(view.SearchPanel.Query, Is.EqualTo(name));
        Assert.That(view.SearchPanel.Rows, Has.Count.EqualTo(count));
    });

    [Test]
    public void InvalidRequestsKeepExistingResultsAndDoNotStartAnotherSearch() => WithView("class C { int value; }", async (view, _, _) =>
    {
        view.SearchPanel.Query = "value";
        await view.SearchPanel.StartAsync();
        var previous = view.SearchPanel.CurrentOperation;
        foreach (var (caret, start, length) in new[] { (0, 0, 5), (0, 0, 7), (-1, 0, 0), (100, 0, 0), (0, 0, -1), (0, 10, 100) })
        {
            await view.FindReferencesAsync(caret, start, length);
            Assert.That(view.SearchPanel.CurrentOperation, Is.SameAs(previous));
            Assert.That(view.SearchPanel.Rows, Has.Count.EqualTo(1));
        }
    });

    [TestCase(0)]
    [TestCase(12000)]
    [TestCase(24000)]
    public void HugeTokenCannotBecomeATruncatedQuery(int caret) => WithView(new string('x', 24000), async (view, _, _) =>
    {
        await view.FindReferencesAsync(caret, caret, 0);
        Assert.That(view.SearchPanel.CurrentOperation, Is.Null);
        Assert.That(view.Status, Does.Contain("識別子"));
        await view.FindReferencesAsync(caret, 0, IdentifierExtractor.MaximumChars + 2);
        Assert.That(view.SearchPanel.CurrentOperation, Is.Null);
    });

    [Test]
    public void MaximumNameWorksInTheMiddleOfALongDocument() => WithView(
        new string(' ', 20000) + "@" + new string('x', IdentifierExtractor.MaximumChars) + ";", async (view, _, _) =>
    {
        await view.FindReferencesAsync(22000, 22000, 0);
        Assert.That(view.SearchPanel.Query, Has.Length.EqualTo(IdentifierExtractor.MaximumChars));
        Assert.That(view.SearchPanel.Rows, Has.Count.EqualTo(1));
        Assert.That(view.SearchPanel.Rows[0].Hit.Range.Offset, Is.EqualTo(20001));
    });

    [Test]
    public void MissingWorkspaceIsActionableAndStopRejectsLateRequests() => WithView("name", async (view, _, _) =>
    {
        view.CloseWorkspace();
        await view.FindReferencesAsync(0, 0, 0);
        Assert.That(view.Status, Does.Contain("フォルダを開いて"));
        Assert.That(view.SearchPanel.CurrentOperation, Is.Null);
        view.Stop();
        await view.FindReferencesAsync(0, 0, 0);
        Assert.That(view.SearchPanel.IsRunning, Is.False);
    });

    private static void WithView(string text, Func<MainWindowViewModel, string, string, Task> run) => SingleThreadTestContext.Run(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "codeview-references-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "repo");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "body.cs");
        await File.WriteAllTextAsync(file, text);
        var source = new SourceAccess();
        using var operations = new AppOperations(new(source), new AvaloniaEditDocumentAdapter(), new(source));
        var view = new MainWindowViewModel(operations, new SettingsStore(new(Path.Combine(root, "portable.json"), Path.Combine(root, "user.json"))), YieldAsync);
        try
        {
            await view.InitializeAsync([folder, file]);
            await run(view, root, file);
        }
        finally
        {
            view.Stop();
            await view.PendingWork;
            Directory.Delete(root, true);
        }
    });
}
