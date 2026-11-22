using CodeView.Documents;
using CodeView.Files;
using CodeView.Navigation;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Search;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class DefinitionJumpTests
{
    [Test]
    public void JumpHistoryRestoresTwoHopsAndReopensClosedTabs() => WithView(async (view, folder, body) =>
    {
        var target = Path.Combine(folder, "target.cs");
        var last = Path.Combine(folder, "last.cs");
        const string targetText = "class Target { object Field = Last; }";
        await File.WriteAllTextAsync(target, targetText);
        await File.WriteAllTextAsync(last, "class Last {}");
        var viewport = (X: 0d, Y: 0d);
        view.RestoreViewport += (x, y, _) => viewport = (x, y);

        view.SetPosition(1, 27);
        view.SetViewport(12, 34);
        await Find(view);
        Assert.That(view.FilePath, Is.EqualTo(target));
        var lastOffset = targetText.IndexOf("Last", StringComparison.Ordinal);
        view.SetPosition(1, lastOffset + 1);
        view.SetViewport(56, 78);
        await view.FindDefinitionsAsync(lastOffset, lastOffset, 0);
        Assert.That(view.FilePath, Is.EqualTo(last));
        view.CloseFile();

        await view.JumpBackAsync();
        Assert.That(view.FilePath, Is.EqualTo(target));
        Assert.That(view.Position, Is.EqualTo($"行 1, 列 {lastOffset + 1}"));
        Assert.That(viewport, Is.EqualTo((56d, 78d)));
        await view.JumpBackAsync();
        Assert.That(view.FilePath, Is.EqualTo(body));
        Assert.That(view.Position, Is.EqualTo("行 1, 列 27"));
        Assert.That(viewport, Is.EqualTo((12d, 34d)));
        Assert.That(view.CanJumpBack, Is.False);

        await view.JumpForwardAsync();
        Assert.That(view.FilePath, Is.EqualTo(target));
        await view.JumpForwardAsync();
        Assert.That(view.FilePath, Is.EqualTo(last));
        Assert.That(view.CanJumpForward, Is.False);

        await view.JumpBackAsync();
        await view.FindDefinitionsAsync(lastOffset, lastOffset, 0);
        Assert.That(view.FilePath, Is.EqualTo(last));
        Assert.That(view.CanJumpForward, Is.False);
    });

    [Test]
    public void MissingHistoryFileKeepsCurrentTabAndSwitchingWorkspaceClearsHistory() => WithView(async (view, folder, body) =>
    {
        var target = Path.Combine(folder, "target.cs");
        await File.WriteAllTextAsync(target, "class Target {}");
        await Find(view);
        view.CloseTab(view.Tabs.Single(tab => tab.State.Path.FullPath == body));
        File.Delete(body);

        await view.JumpBackAsync();
        Assert.That(view.FilePath, Is.EqualTo(target));
        Assert.That(view.Tabs.Count, Is.EqualTo(1));
        Assert.That(view.Status, Does.Contain("履歴のファイルを開けません"));
        Assert.That(view.CanJumpBack, Is.True);
        Assert.That(view.CanJumpForward, Is.False);

        var other = Directory.CreateDirectory(Path.Combine(folder, "other"));
        await view.OpenFilesAsync([other.FullName], OpenOrigin.Menu);
        Assert.That(view.CanJumpBack, Is.False);
        Assert.That(view.CanJumpForward, Is.False);
    });

    [Test]
    public void SearchResultHistoryIsBoundedAndSkipsSamePosition() => WithView(async (view, _, body) =>
    {
        view.SearchPanel.Query = "Target";
        await view.SearchPanel.StartAsync();
        var row = view.SearchPanel.Rows.Single();
        Assert.That(row.FullPath, Is.EqualTo(body));
        await view.ActivateSearchResultAsync(row);
        await view.JumpBackAsync();
        Assert.That(view.CanJumpBack, Is.False);
        await view.JumpForwardAsync();
        await view.ActivateSearchResultAsync(row);
        await view.JumpBackAsync();
        Assert.That(view.CanJumpBack, Is.False);

        for (var step = 0; step <= MainWindowViewModel.JumpHistoryLimit; step++)
        {
            view.SetPosition(1, 1);
            await view.ActivateSearchResultAsync(row);
        }
        for (var step = 0; step < MainWindowViewModel.JumpHistoryLimit; step++)
        {
            Assert.That(view.CanJumpBack, Is.True);
            await view.JumpBackAsync();
        }
        Assert.That(view.CanJumpBack, Is.False);
    });

    [TestCase(false)]
    [TestCase(true)]
    public void SingleCompleteCandidateNavigatesWithPanelClosed(bool initiallyVisible) => WithView(async (view, folder, _) =>
    {
        var target = Path.Combine(folder, "target.cs");
        await File.WriteAllTextAsync(target, "namespace A { class @Target {} }");
        await File.WriteAllTextAsync(Path.Combine(folder, "ignored.txt"), "class Target {}");
        var selection = new List<(long, int)>();
        view.SearchPanel.IsVisible = initiallyVisible;
        var visibilityChanges = new List<bool>();
        view.SearchPanel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SearchPanelViewModel.IsVisible))
                visibilityChanges.Add(view.SearchPanel.IsVisible);
        };
        view.SelectSearchRange += (offset, length) => selection.Add((offset, length));

        await Find(view);

        Assert.That(view.FilePath, Is.EqualTo(target));
        Assert.That(selection, Is.EqualTo(new[] { (20L, 7) }));
        Assert.That(view.SearchPanel.IsVisible, Is.False);
        Assert.That(visibilityChanges, Is.EqualTo(initiallyVisible ? new[] { false } : Array.Empty<bool>()));
        Assert.That(view.SearchPanel.Description, Does.Contain("簡易").And.Contain("名前一致"));
        Assert.That(view.SearchPanel.Rows.Single().After, Does.Contain("型"));
    });

    [Test]
    public void ChangedSingleCandidateKeepsTheExistingPanelOpen() => WithView(async (view, folder, _) =>
    {
        var target = Path.Combine(folder, "target.cs");
        await File.WriteAllTextAsync(target, "class Target {}");
        view.SearchPanel.IsVisible = true;
        var updated = false;
        view.SearchPanel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(SearchPanelViewModel.IsRunning) || view.SearchPanel.IsRunning || updated) return;
            updated = true;
            File.WriteAllText(target, "class RenamedTarget {}");
        };

        await Find(view);

        Assert.That(updated, Is.True);
        Assert.That(view.SearchPanel.IsVisible, Is.True);
        Assert.That(view.SearchPanel.Rows, Has.Count.EqualTo(1));
        Assert.That(view.Status, Does.Contain("再検索"));
        Assert.That(view.CanJumpBack, Is.False);
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
        Assert.That(view.SearchPanel.IsVisible, Is.True);
        await view.ActivateSearchResultAsync(view.SearchPanel.Rows.Single(row => row.FullPath == target));
        Assert.That(view.FilePath, Is.EqualTo(target));
        Assert.That(view.SearchPanel.IsVisible, Is.True);
    });

    [TestCase(false)]
    [TestCase(true)]
    public void ZeroCandidatesDistinguishCompleteFromUnevaluated(bool partial) => WithView(async (view, folder, body) =>
    {
        if (partial) await File.WriteAllTextAsync(Path.Combine(folder, "incomplete.cs"), "class Target {");
        await Find(view);

        Assert.That(view.FilePath, Is.EqualTo(body));
        Assert.That(view.SearchPanel.Rows, Is.Empty);
        Assert.That(view.SearchPanel.IsVisible, Is.True);
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
        Assert.That(view.SearchPanel.IsVisible, Is.True);
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
            Assert.That(view.CanJumpBack, Is.False);
            if (cancel) Assert.That(view.SearchPanel.Summary, Does.Contain("取り消し"));
            else
            {
                Assert.That(view.SearchPanel.Rows.Count, Is.EqualTo(1));
                Assert.That(view.SearchPanel.IsVisible, Is.False);
            }
        }, () =>
        {
            var callback = duringDrain;
            duringDrain = null;
            callback?.Invoke();
        });
    }

    private static Task Find(MainWindowViewModel view) => view.FindDefinitionsAsync(27, 26, 6);

    [Test]
    public void CppTagsAreManualRankQualifiedNamesAndKeepReferencesAndFallbackAvailable() => WithView(async (view, folder, _) =>
    {
        const string callerText = "int caller() { return ns::Target(); }";
        var caller = Path.Combine(folder, "caller.cpp");
        var preferred = Path.Combine(folder, "preferred.cpp");
        var other = Path.Combine(folder, "other.cpp");
        var extra = Path.Combine(folder, "reference.txt");
        await File.WriteAllTextAsync(caller, callerText);
        await File.WriteAllTextAsync(preferred, "namespace ns { int Target() { return 1; } }");
        await File.WriteAllTextAsync(other, "namespace other { int Target() { return 2; } }");
        await File.WriteAllTextAsync(extra, "Target");
        await view.OpenFilesAsync([caller], OpenOrigin.Menu);
        var offset = callerText.IndexOf("Target", StringComparison.Ordinal);
        Task FindCpp() => view.FindDefinitionsAsync(offset, offset, 0);

        await FindCpp();
        Assert.That(view.SearchPanel.Description, Does.Contain("簡易"));
        Assert.That(view.SearchPanel.Rows, Has.Count.EqualTo(2));
        Assert.That(Directory.Exists(Path.Combine(Path.GetDirectoryName(folder)!, "cpp-cache")), Is.False);

        view.PropertyChanged += CancelBeforeBuild;
        await view.UpdateCppIndexAsync(rebuild: false);
        view.PropertyChanged -= CancelBeforeBuild;
        Assert.That(view.Status, Does.Contain("取り消し"));
        Assert.That(Directory.Exists(Path.Combine(Path.GetDirectoryName(folder)!, "cpp-cache")), Is.False);

        await view.UpdateCppIndexAsync(rebuild: false);
        Assert.That(view.Status, Does.Contain("タグを保存しました"));
        await FindCpp();
        Assert.That(view.SearchPanel.Description, Does.Contain("定義タグ"));
        Assert.That(view.SearchPanel.Rows.Select(row => row.FullPath), Is.EqualTo(new[] { preferred, other }));
        Assert.That(view.FilePath, Is.EqualTo(caller));
        Assert.That(view.SearchPanel.IsVisible, Is.True);
        await view.FindReferencesAsync(offset, offset, 0);
        Assert.That(view.SearchPanel.Rows.Select(row => row.FullPath), Does.Contain(extra));

        File.Delete(other);
        await view.RefreshWorkspaceAsync();
        await view.PendingWork;
        await FindCpp();
        Assert.That(view.SearchPanel.Description, Does.Contain("簡易"));
        Assert.That(view.FilePath, Is.EqualTo(preferred));

        await view.OpenFilesAsync([caller], OpenOrigin.Menu);
        await view.UpdateCppIndexAsync(rebuild: false);
        await view.RefreshWorkspaceAsync();
        await view.PendingWork;
        await FindCpp();
        Assert.That(view.SearchPanel.Description, Does.Contain("定義タグ"));
        Assert.That(view.FilePath, Is.EqualTo(preferred));
        Assert.That(view.SearchPanel.IsVisible, Is.False);

        await view.OpenFilesAsync([caller], OpenOrigin.Menu);
        // The default database lookup must leave this folder on the tag path.
        Assert.That(view.ConfigureSemanticNavigation(view.WorkspaceEpoch, new SemanticWorkspaceOptions(null, null,
            caller, null)), Is.True);
        await FindCpp();
        Assert.That(view.SearchPanel.Description, Does.Contain("定義タグ"));
        Assert.That(view.SearchPanel.Summary, Does.Contain("コンパイルデータベース"));
        Assert.That(view.FilePath, Is.EqualTo(preferred));

        var unreadableDatabase = Path.Combine(Path.GetDirectoryName(folder)!, "compile_commands.json");
        await File.WriteAllTextAsync(unreadableDatabase, "not JSON");
        await view.OpenFilesAsync([caller], OpenOrigin.Menu);
        Assert.That(view.ConfigureSemanticNavigation(view.WorkspaceEpoch, new SemanticWorkspaceOptions(null, null,
            caller, unreadableDatabase)), Is.True);
        await FindCpp();
        Assert.That(view.SearchPanel.Description, Does.Contain("定義タグ"));
        Assert.That(view.SearchPanel.Summary, Does.Contain("clangd"));
        Assert.That(view.FilePath, Is.EqualTo(preferred));

        void CancelBeforeBuild(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsUpdatingCppIndex) && view.IsUpdatingCppIndex)
                view.CancelCppIndex();
        }
    });

    private static void WithView(Func<MainWindowViewModel, string, string, Task> run, Action? onYield = null)
        => SingleThreadTestContext.Run(async () =>
    {
        var root = Directory.CreateTempSubdirectory("codeview-definition-");
        var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "repo")).FullName;
        var body = Path.Combine(folder, "body.cs");
        await File.WriteAllTextAsync(body, "class Caller { object F = Target; }");
        var source = new SourceAccess();
        using var operations = new AppOperations(new(source), new AvaloniaEditDocumentAdapter(), new(source),
            cppCacheDirectory: Path.Combine(root.FullName, "cpp-cache"));
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
