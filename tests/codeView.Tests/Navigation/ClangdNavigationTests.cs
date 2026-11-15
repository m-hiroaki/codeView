using System.Text.Json;
using CodeView.Navigation;

namespace CodeView.Tests.Navigation;

internal sealed class ClangdNavigationTests
{
    [TestCase("c")]
    [TestCase("cpp")]
    public async Task RealClangdUsesCompilationConditionsAndAnOutOfTreeIndex(string language)
    {
        var executable = RequireClangd();
        var temporary = Directory.CreateTempSubdirectory("codeview-clangd-");
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(temporary.FullName, "src")).FullName;
            var cache = Path.Combine(temporary.FullName, "cache");
            var header = Path.Combine(workspace, "api.h");
            var api = Path.Combine(workspace, $"api.{language}");
            var caller = Path.Combine(workspace, $"caller.{language}");
            var extra = Path.Combine(workspace, $"other.{language}");
            var configuration = Path.Combine(workspace, "compile_commands.json");
            var declaration = language == "cpp" ? "int run(int);\nint run(char);" : "int run(int);";
            var otherUse = language == "cpp" ? "int differently() { return run('x'); }\n" : "";
            var prefix = "#include \"api.h\"\n";
            var goodCall = "int calling() { return run(1); }\n";
            var callerText = prefix + goodCall;
            await File.WriteAllTextAsync(header, declaration);
            await File.WriteAllTextAsync(api, prefix + """
                #ifdef PICK
                int run(int n) { return n; }
                #else
                int run(int n) { return -1; }
                #endif
                """ + "\n" + (language == "cpp" ? "int run(char n) { return n; }\n" : ""));
            await File.WriteAllTextAsync(caller, callerText);
            await File.WriteAllTextAsync(extra, prefix + "int secondary() { return run(2); }\n" + otherUse);
            await WriteDatabaseAsync(pick: true);
            var clang = new ClangdNavigation(workspace, configuration, cache);
            await using var session = clang.CreateSession(executable);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var first = await clang.SearchAsync(session, SemanticOperation.Definition, new Uri(caller).AbsoluteUri,
                language, 1, callerText, _ => new(1, goodCall.IndexOf("run", StringComparison.Ordinal)), deadline.Token);
            Assert.That(first.IsPartial, Is.True, "A request started before the first index and diagnostics is partial.");
            var result = await WaitUntilReadyAsync(SemanticOperation.Definition);
            Assert.That(result.Configuration, Is.EqualTo("compile_commands.json"));
            Assert.That(Targets(result), Is.EqualTo(new[] { ("api." + language, 2) }));
            var refs = await WaitUntilReadyAsync(SemanticOperation.References);
            Assert.That(Targets(refs).Distinct().OrderBy(item => item.File),
                Is.EqualTo(new[] { ("caller." + language, 1), ("other." + language, 1) }));
            Assert.That(Directory.EnumerateFiles(cache, "*.idx", SearchOption.AllDirectories), Is.Not.Empty);
            Assert.That(Directory.Exists(Path.Combine(workspace, ".cache")), Is.False);

            var changed = await WriteDatabaseAsync(pick: false);
            File.SetLastWriteTimeUtc(configuration, changed.AddSeconds(2));
            var updated = await WaitUntilReadyAsync(SemanticOperation.Definition);
            Assert.That(Targets(updated), Is.EqualTo(new[] { ("api." + language, 4) }));
            Assert.That(Directory.EnumerateFiles(cache, "compile_commands.json", SearchOption.AllDirectories).Count(), Is.EqualTo(2));

            var headerReply = await clang.SearchAsync(session, SemanticOperation.Definition,
                new Uri(header).AbsoluteUri, language, 2, declaration, _ => new(0, 4), deadline.Token);
            Assert.That(headerReply.IsPartial, Is.True, "Headers without their own compile command have an inferred configuration.");

            async Task<DateTime> WriteDatabaseAsync(bool pick)
            {
                var records = new[] { api, caller, extra }.Select(file => new
                {
                    directory = workspace,
                    file = Path.GetFileName(file),
                    arguments = new[] { language == "c" ? "clang" : "clang++", pick ? "-DPICK" : "-UNOPICK", "-c", Path.GetFileName(file) }
                });
                await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(records));
                return File.GetLastWriteTimeUtc(configuration);
            }

            async Task<SemanticReply> WaitUntilReadyAsync(SemanticOperation operation)
            {
                SemanticReply reply;
                do
                {
                    reply = await clang.SearchAsync(session, operation, new Uri(caller).AbsoluteUri, language, 1,
                        callerText, _ => new(1, goodCall.IndexOf("run", StringComparison.Ordinal)), deadline.Token);
                    if (reply.IsPartial) await Task.Delay(50, deadline.Token);
                }
                while (reply.IsPartial);
                return reply;
            }
        }
        finally { temporary.Delete(recursive: true); }
    }

    [Test]
    public async Task MissingCompilationDatabaseIsReportedAsPartial()
    {
        var executable = RequireClangd();
        var temporary = Directory.CreateTempSubdirectory("codeview-clangd-missing-");
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(temporary.FullName, "src")).FullName;
            var source = Path.Combine(workspace, "entry.c");
            const string text = "int calc(void) { return 0; }\nint use(void) { return calc(); }\n";
            await File.WriteAllTextAsync(source, text);
            var clang = new ClangdNavigation(workspace, null, Path.Combine(temporary.FullName, "cache"));
            await using var session = clang.CreateSession(executable);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await clang.SearchAsync(session, SemanticOperation.Definition, new Uri(source).AbsoluteUri,
                "c", 1, text, _ => new(1, 23), deadline.Token);
            Assert.That(result.IsPartial, Is.True);
            Assert.That(result.Status, Does.Contain("コンパイル条件"));
        }
        finally { temporary.Delete(recursive: true); }
    }

    private static IEnumerable<(string File, int Line)> Targets(SemanticReply reply) =>
        reply.Locations.EnumerateArray().Select(item =>
        (
            Path.GetFileName(new Uri(item.GetProperty("uri").GetString()!).LocalPath),
            item.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()
        ));

    private static string RequireClangd()
    {
        var executable = Environment.GetEnvironmentVariable("CODEVIEW_TEST_CLANGD");
        if (string.IsNullOrWhiteSpace(executable)) Assert.Ignore("Set CODEVIEW_TEST_CLANGD to the clangd executable.");
        return Path.GetFullPath(executable!);
    }
}
