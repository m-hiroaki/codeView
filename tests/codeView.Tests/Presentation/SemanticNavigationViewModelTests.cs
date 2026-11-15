using System.Diagnostics;
using System.Text.Json;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Navigation;
using CodeView.Operations;
using CodeView.Presentation;
using CodeView.Settings;

namespace CodeView.Tests.Presentation;

internal sealed class SemanticNavigationViewModelTests
{
    [TestCase("cs")]
    [TestCase("c")]
    [TestCase("cpp")]
    public void RunsDefinitionsAndReferencesThroughTheExistingViewModel(string language) => SingleThreadTestContext.Run(async () =>
    {
        var csharp = language == "cs";
        var tool = Environment.GetEnvironmentVariable(csharp ? "CODEVIEW_TEST_ROSLYN" : "CODEVIEW_TEST_CLANGD");
        if (string.IsNullOrEmpty(tool)) Assert.Ignore("A real language server is required.");
        var executable = Path.GetFullPath(tool!);
        var temporary = Directory.CreateTempSubdirectory("codeview-semantic-ui-");
        try
        {
            var root = temporary.CreateSubdirectory("project").FullName;
            var caller = Path.Combine(root, "Caller." + language);
            var target = Path.Combine(root, "Target." + language);
            var setup = csharp ? Path.Combine(root, "Demo.csproj") : Path.Combine(root, "compile_commands.json");
            string text;
            if (csharp)
            {
                text = "class Caller { int Execute() => Library.Run(1); }";
                await File.WriteAllTextAsync(target, "public class Library { public static int Run(int n) => n; }");
                await File.WriteAllTextAsync(setup, """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                    </Project>
                    """);
                await RestoreAsync(root, setup);
            }
            else
            {
                text = "#include \"library.h\"\nint execute() { return Run(1); }";
                await File.WriteAllTextAsync(Path.Combine(root, "library.h"), "int Run(int);");
                await File.WriteAllTextAsync(target, "#include \"library.h\"\nint Run(int n) { return n; }");
                var commands = new[] { caller, target }.Select(file => new
                {
                    directory = root, file,
                    arguments = new[] { language == "c" ? "clang" : "clang++", "-c", file }
                });
                await File.WriteAllTextAsync(setup, JsonSerializer.Serialize(commands));
            }
            await File.WriteAllTextAsync(caller, text);

            var access = new SourceAccess();
            using var operations = new AppOperations(new(access), new AvaloniaEditDocumentAdapter(), new(access),
                Path.Combine(temporary.FullName, "cache"));
            var view = new MainWindowViewModel(operations,
                new SettingsStore(new(Path.Combine(temporary.FullName, "portable.json"), Path.Combine(temporary.FullName, "user.json"))),
                async () => await Task.Yield());
            try
            {
                await view.InitializeAsync([root, caller]);
                Assert.That(view.DefinitionMenuLabel, Does.Contain("簡易"));
                var config = csharp ? new SemanticWorkspaceOptions(executable, setup, null, null)
                    : new SemanticWorkspaceOptions(null, null, executable, setup);
                Assert.That(view.ConfigureSemanticNavigation(view.WorkspaceEpoch, config), Is.True);
                Assert.That(view.DefinitionMenuLabel, Does.Contain("意味解析"));
                var selections = new List<(long Offset, int Length)>();
                view.SelectSearchRange += (offset, length) => selections.Add((offset, length));
                var caret = text.IndexOf("Run", StringComparison.Ordinal);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                string? lastSummary = null;
                do
                {
                    view.SearchPanel.IsVisible = false;
                    try { await view.FindDefinitionsAsync(caret, caret, 0).WaitAsync(deadline.Token); }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        Assert.Fail($"Definition search timed out. Last result: {lastSummary}; status: {view.Status}");
                    }
                    if (view.FilePath == caller)
                    {
                        lastSummary = $"{view.SearchPanel.Summary} ({view.SearchPanel.Rows.Count} rows, {view.SearchPanel.Description})";
                        Assert.That(lastSummary, Does.Contain("部分結果"));
                        await Task.Delay(50, deadline.Token);
                    }
                }
                while (view.FilePath == caller);
                Assert.That(view.FilePath, Is.EqualTo(target));
                Assert.That(view.SearchPanel.IsVisible, Is.False);
                Assert.That(selections.Single(), Is.EqualTo(((long)(await File.ReadAllTextAsync(target)).LastIndexOf("Run", StringComparison.Ordinal), 3)));

                await view.OpenFilesAsync([caller], OpenOrigin.Menu);
                await view.FindReferencesAsync(caret, caret, 0).WaitAsync(deadline.Token);
                Assert.That(view.SearchPanel.IsVisible, Is.True);
                Assert.That(view.SearchPanel.Description, Does.Contain("意味解析"));
                Assert.That(view.SearchPanel.Rows.Select(row => row.FullPath).Distinct(), Is.EqualTo(new[] { caller }));
                if (csharp)
                {
                    var whitespace = text.LastIndexOf(' ');
                    await view.FindDefinitionsAsync(whitespace, whitespace, 0).WaitAsync(deadline.Token);
                    Assert.That(view.SearchPanel.Summary, Does.Contain("定義が見つかりません"));
                    Assert.That(view.SearchPanel.Rows, Is.Empty);
                    Assert.That(view.FilePath, Is.EqualTo(caller));
                }
                Assert.That(view.ConfigureSemanticNavigation(view.WorkspaceEpoch, null), Is.True);
                Assert.That(view.ReferencesMenuLabel, Does.Contain("名前一致"));
                await view.FindReferencesAsync(caret, caret, 0).WaitAsync(deadline.Token);
                Assert.That(view.SearchPanel.Rows.Count, Is.GreaterThan(1));
            }
            finally
            {
                view.Stop();
                await view.PendingWork;
            }
        }
        finally { temporary.Delete(recursive: true); }
    });

    private static async Task RestoreAsync(string root, string project)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "restore", project, "--ignore-failed-sources", "--disable-build-servers" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        var messages = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(process.ExitCode, Is.Zero, string.Concat(messages));
    }
}
