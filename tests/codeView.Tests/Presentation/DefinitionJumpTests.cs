using CodeView.Documents;
using CodeView.Files;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Search;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class DefinitionJumpTests
{
    [Test]
    public void SingleCompleteCandidateNavigatesToOriginalSpelling() => WithView(async (view, folder, _) =>
    {
        var target = Path.Combine(folder, "target.cs");
        await File.WriteAllTextAsync(target, "namespace A { class @Target {} }");
        await File.WriteAllTextAsync(Path.Combine(folder, "ignored.txt"), "class Target {}");
        var selection = new List<(long, int)>();
        view.SelectSearchRange += (offset, length) => selection.Add((offset, length));

        await Find(view);

        Assert.That(view.FilePath, Is.EqualTo(target));
        Assert.That(selection, Is.EqualTo(new[] { (20L, 7) }));
        Assert.That(view.SearchPanel.Description, Does.Contain("簡易").And.Contain("名前一致"));
        Assert.That(view.SearchPanel.Rows.Single().After, Does.Contain("型"));
    });

    [Test]
    public void MultipleCandidatesWaitForExplicitSelection() => WithView(async (view, folder, body) =>
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "first.cs"), "class Target {}");
        var target = Path.Combine(folder, "second.cs");
        await File.WriteAllTextAsync(target, "class C { int Target() => 1; }");
        await Find(view);

        Assert.That(view.FilePath, Is.EqualTo(body));
        Assert.That(view.SearchPanel.Rows.Count, Is.EqualTo(2));
        await view.ActivateSearchResultAsync(view.SearchPanel.Rows.Single(row => row.FullPath == target));
        Assert.That(view.FilePath, Is.EqualTo(target));
    });

    [TestCase(false)]
    [TestCase(true)]
    public void ZeroCandidatesDistinguishCompleteFromUnevaluated(bool partial) => WithView(async (view, folder, body) =>
    {
        if (partial) await File.WriteAllTextAsync(Path.Combine(folder, "incomplete.cs"), "class Target {");
        await Find(view);

        Assert.That(view.FilePath, Is.EqualTo(body));
        Assert.That(view.SearchPanel.Rows, Is.Empty);
        Assert.That(view.SearchPanel.Summary, Does.Contain(partial ? "未評価あり" : "定義が見つかりません"));
    });

    [Test]
    public void PartialSearchPreservesConfirmedCandidateForExplicitNavigation() => WithView(async (view, folder, body) =>
    {
        var target = Path.Combine(folder, "valid.cs");
        await File.WriteAllTextAsync(target, "class Target {}");
        await File.WriteAllTextAsync(Path.Combine(folder, "broken.cs"), "class Target {");
        await Find(view);

        Assert.That(view.FilePath, Is.EqualTo(body));
        Assert.That(view.SearchPanel.Summary, Does.Contain("部分結果"));
        var row = view.SearchPanel.Rows.Single();
        Assert.That(view.SearchPanel.CanActivate(row), Is.True);
        await view.ActivateSearchResultAsync(row);
        Assert.That(view.FilePath, Is.EqualTo(target));
    });

    [Test]
    public void LimitLeavesUnfinishedFileCandidatesUnselectable() => WithView(async (view, folder, body) =>
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "many.cs"), string.Concat(Enumerable.Repeat("class Target {}\n", SearchLimits.ResultLimit)));
        await Find(view);

        Assert.That(view.FilePath, Is.EqualTo(body));
        Assert.That(view.SearchPanel.Rows.Count, Is.EqualTo(SearchLimits.ResultLimit));
        Assert.That(view.SearchPanel.CanActivate(view.SearchPanel.Rows[0]), Is.False);
        Assert.That(view.SearchPanel.Summary, Does.Contain("打ち切り"));
    });

    [TestCase(true)]
    [TestCase(false)]
    public void CancelOrNewUserInputPreventsAutomaticNavigation(bool cancel)
    {
        Action? duringDrain = null;
        WithView(async (view, folder, body) =>
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "target.cs"), "class Target {}");
            duringDrain = () =>
            {
                if (cancel) view.SearchPanel.Cancel();
                else view.RecordUserInteraction();
            };
            await Find(view);
            Assert.That(duringDrain, Is.Null);
            Assert.That(view.FilePath, Is.EqualTo(body));
            if (cancel) Assert.That(view.SearchPanel.Summary, Does.Contain("取り消し"));
            else Assert.That(view.SearchPanel.Rows.Count, Is.EqualTo(1));
        }, () =>
        {
            var callback = duringDrain;
            duringDrain = null;
            callback?.Invoke();
        });
    }

    private static Task Find(MainWindowViewModel view) => view.FindDefinitionsAsync(27, 26, 6);

    private static void WithView(Func<MainWindowViewModel, string, string, Task> run, Action? onYield = null)
        => SingleThreadTestContext.Run(async () =>
    {
        var root = Directory.CreateTempSubdirectory("codeview-definition-");
        var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "repo")).FullName;
        var body = Path.Combine(folder, "body.cs");
        await File.WriteAllTextAsync(body, "class Caller { object F = Target; }");
        var source = new SourceAccess();
        using var operations = new AppOperations(new(source), new AvaloniaEditDocumentAdapter(), new(source));
        var view = new MainWindowViewModel(operations, new SettingsStore(new(
            Path.Combine(root.FullName, "portable.json"), Path.Combine(root.FullName, "user.json"))), YieldAsync);
        try
        {
            await view.InitializeAsync([folder, body]);
            await run(view, folder, body);
        }
        finally
        {
            view.Stop();
            await view.PendingWork;
            root.Delete(true);
        }

        async Task YieldAsync()
        {
            onYield?.Invoke();
            await Task.Yield();
        }
    });
}
