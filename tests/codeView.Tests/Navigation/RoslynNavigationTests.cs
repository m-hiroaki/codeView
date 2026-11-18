using System.Diagnostics;
using System.Text.Json;
using CodeView.Navigation;

namespace CodeView.Tests.Navigation;

internal sealed class RoslynNavigationTests
{
    [TestCase(".slnx", false)]
    [TestCase(".sln", false)]
    [TestCase(".csproj", false)]
    [TestCase(".slnx", true)]
    public async Task RealRoslynResolvesConfiguredSourceAndReferences(string extension, bool sharedSource)
    {
        var executable = RequireServer();
        var root = new DirectoryInfo(LspFilePath.Resolve(Directory.CreateTempSubdirectory("codeview-roslyn-").FullName));
        try
        {
            var fixture = await CreateFixtureAsync(root.FullName, extension, sharedSource);
            var roslyn = new RoslynNavigation(root.FullName, fixture.Project);
            await using var session = roslyn.CreateSession(executable);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var definition = await SearchAsync(SemanticOperation.Definition);
            // Cold CI runners can still be loading the project when the first request returns partial results.
            while (definition.IsPartial && definition.Configuration == Path.GetFileName(fixture.Project))
            {
                await Task.Delay(100, deadline.Token);
                definition = await SearchAsync(SemanticOperation.Definition);
            }
            var references = await SearchAsync(SemanticOperation.References);
            Assert.That(definition.PositionEncoding, Is.EqualTo("utf-16"));
            Assert.That(definition.Configuration, Does.Contain("net10.0"));
            Assert.That(definition.IsPartial, Is.EqualTo(sharedSource), definition.Status);
            Assert.That(references.IsPartial, Is.EqualTo(sharedSource), references.Status);
            var target = definition.Locations.EnumerateArray().Single();
            var targetUri = target.GetProperty("uri").GetString();
            Assert.That(LspFilePath.TryFromUri(targetUri, out var targetPath), Is.True, targetUri);
            Assert.That(targetPath, Is.EqualTo(Path.Combine(root.FullName, "Lib", "Api.cs")).IgnoreCase, targetUri);
            Assert.That(StartLine(target), Is.EqualTo(2), "The enabled C# conditional branch is selected.");
            Assert.That(references.Locations.EnumerateArray().Select(StartLine).Distinct().Order(), Is.EqualTo(new[] { 4, 5 }));
            if (extension == ".slnx" && !sharedSource)
            {
                var external = await roslyn.SearchAsync(session, SemanticOperation.Definition, fixture.Uri, 1, fixture.Text,
                    _ => new(8, 38), deadline.Token);
                Assert.That(external.Locations.ValueKind is JsonValueKind.Null or JsonValueKind.Array, Is.True);
                if (external.Locations.ValueKind == JsonValueKind.Array)
                    Assert.That(external.Locations.EnumerateArray().All(location =>
                    {
                        var uri = new Uri(location.GetProperty("uri").GetString()!);
                        return !uri.IsFile || Path.GetRelativePath(root.FullName, uri.LocalPath).StartsWith("..", StringComparison.Ordinal)
                            || Path.IsPathRooted(Path.GetRelativePath(root.FullName, uri.LocalPath));
                    }), Is.True, "Framework definitions are outside the workspace.");
            }

            Task<SemanticReply> SearchAsync(SemanticOperation operation) => roslyn.SearchAsync(session, operation, fixture.Uri,
                1, fixture.Text, _ => new(4, 21), deadline.Token);
        }
        finally { await DeleteFixtureAsync(root); }
    }

    [Test]
    public async Task RealRoslynMarksAnUnresolvedProjectAsPartial()
    {
        var executable = RequireServer();
        var root = Directory.CreateTempSubdirectory("codeview-roslyn-broken-");
        try
        {
            var project = Path.Combine(root.FullName, "Broken.csproj");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "NuGet.Config"),
                "<configuration><packageSources><clear /></packageSources></configuration>");
            await File.WriteAllTextAsync(project, "<Project Sdk=\"Nonexistent.CodeView.Demo.Sdk\"></Project>");
            var path = Path.Combine(root.FullName, "Code.cs");
            const string text = "class Code { void Method() {} }";
            await File.WriteAllTextAsync(path, text);
            var roslyn = new RoslynNavigation(root.FullName, project);
            await using var session = roslyn.CreateSession(executable);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = await roslyn.SearchAsync(session, SemanticOperation.Definition, new Uri(path).AbsoluteUri,
                1, text, _ => new(0, 19), deadline.Token);
            Assert.That(result.IsPartial, Is.True);
        }
        finally { await DeleteFixtureAsync(root); }
    }

    private static async Task DeleteFixtureAsync(DirectoryInfo root)
    {
        // On Windows a Roslyn build host can briefly keep the project directory open as it exits.
        for (var attempt = 0; ; attempt++)
        {
            try { root.Delete(recursive: true); return; }
            catch (IOException) when (OperatingSystem.IsWindows() && attempt < 50) { await Task.Delay(200); }
        }
    }

    private static int StartLine(JsonElement location) =>
        location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32();

    private static string RequireServer()
    {
        var executable = Environment.GetEnvironmentVariable("CODEVIEW_TEST_ROSLYN");
        if (string.IsNullOrWhiteSpace(executable)) Assert.Ignore("Set CODEVIEW_TEST_ROSLYN to the Roslyn executable.");
        return Path.GetFullPath(executable!);
    }

    private static async Task<(string Project, string Uri, string Text)> CreateFixtureAsync(string root, string extension, bool shared)
    {
        const string projectStart = """
            <Project Sdk="Microsoft.NET.Sdk">
            <PropertyGroup><TargetFramework>net10.0</TargetFramework><DefineConstants>SELECT_INT</DefineConstants></PropertyGroup>
            """;
        const string caller = """
            using Library;
            class Caller
            {
                int Unrelated() => 0;
                int One() => Api.Run(1);
                int Two() => Api.Run(2);
                string Other() => Api.Run("x");
                // Run(3);
                int External() => System.DateTime.Now.Year;
            }
            """;
        var app = Directory.CreateDirectory(Path.Combine(root, "App")).FullName;
        var lib = Directory.CreateDirectory(Path.Combine(root, "Lib")).FullName;
        var appProject = Path.Combine(app, "App.csproj");
        var libProject = Path.Combine(lib, "Lib.csproj");
        const string reference = """<ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup>""";
        await File.WriteAllTextAsync(appProject, projectStart + reference + "</Project>");
        await File.WriteAllTextAsync(libProject, projectStart + "</Project>");
        var path = Path.Combine(app, "Caller.cs");
        await File.WriteAllTextAsync(path, caller);
        await File.WriteAllTextAsync(Path.Combine(lib, "Api.cs"), """
            namespace Library;
            #if SELECT_INT
            public static class Api { public static int Run(int value) => value; public static string Run(string value) => value; }
            #else
            public static class Api { public static int Run(int value) => -1; public static string Run(string value) => value; }
            #endif
            """);

        string? sharedProject = null;
        if (shared)
        {
            var other = Directory.CreateDirectory(Path.Combine(root, "Shared")).FullName;
            sharedProject = Path.Combine(other, "Shared.csproj");
            await File.WriteAllTextAsync(sharedProject, projectStart + reference +
                """<ItemGroup><Compile Include="../App/Caller.cs" /></ItemGroup></Project>""");
        }
        var selected = extension == ".csproj" ? appProject : Path.Combine(root, "Probe" + extension);
        if (extension == ".slnx")
        {
            var extra = shared ? """<Project Path="Shared/Shared.csproj" />""" : "";
            await File.WriteAllTextAsync(selected,
                """<Solution><Project Path="Lib/Lib.csproj" /><Project Path="App/App.csproj" />""" + extra + "</Solution>");
        }
        else if (extension == ".sln")
        {
            await DotnetAsync(root, "new", "sln", "--format", "sln", "--name", "Probe");
            await DotnetAsync(root, "sln", selected, "add", appProject, libProject);
        }
        // Restore from project entry points to avoid concurrent writes for Lib through the solution and project reference.
        await DotnetAsync(root, "restore", appProject, "--ignore-failed-sources", "--disable-build-servers");
        if (sharedProject is not null)
            await DotnetAsync(root, "restore", sharedProject, "--ignore-failed-sources", "--disable-build-servers");
        return (selected, new Uri(path).AbsoluteUri, caller);
    }

    private static async Task DotnetAsync(string root, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        var messages = await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(process.ExitCode, Is.Zero, string.Concat(messages));
    }
}
