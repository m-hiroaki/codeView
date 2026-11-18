using System.Text.Json;
using CodeView.Navigation;

namespace CodeView.Tests.Navigation;

internal sealed class ClangdNavigationTests
{
    [TestCase("null")]
    [TestCase("""{"file":"entry.c","command":"clang -c entry.c"}""")]
    [TestCase("""{"directory":42,"file":"entry.c","command":"clang -c entry.c"}""")]
    [TestCase("""{"directory":" ","file":"entry.c","command":"clang -c entry.c"}""")]
    [TestCase("""{"directory":".","command":"clang -c entry.c"}""")]
    [TestCase("""{"directory":".","file":42,"command":"clang -c entry.c"}""")]
    [TestCase("""{"directory":".","file":" ","command":"clang -c entry.c"}""")]
    [TestCase("""{"directory":".","file":"bad\u0000name.c","command":"clang -c entry.c"}""")]
    [TestCase("""{"directory":".","file":"entry.c","arguments":["clang",42],"command":" "}""")]
    [TestCase("""{"directory":".","file":"entry.c","arguments":[]}""")]
    [TestCase("""{"directory":".","file":"missing.c","command":"clang -c missing.c"}""")]
    [TestCase("""{"directory":".","file":"../outside.c","command":"clang -c ../outside.c"}""")]
    public async Task InvalidCompilationEntryKeepsValidCommandsButMarksConfigurationPartial(string invalidEntry)
    {
        var temporary = Directory.CreateTempSubdirectory("codeview-clangd-database-");
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(temporary.FullName, "source")).FullName;
            var source = Path.Combine(workspace, "entry.c");
            await File.WriteAllTextAsync(source, "int value;");
            await File.WriteAllTextAsync(Path.Combine(temporary.FullName, "outside.c"), "int outside;");
            await File.WriteAllTextAsync(Path.Combine(workspace, "compile_commands.json"),
                $$"""[{"directory":".","file":"entry.c","command":"clang -c entry.c","output":"entry.o"},{{invalidEntry}}]""");

            var database = new ClangdCompilationDatabase(workspace, null, Path.Combine(temporary.FullName, "cache"));
            Assert.That(database.Description, Is.EqualTo("コンパイル条件未設定"));
            await database.RefreshAsync(default);
            Assert.That(database.Description, Is.EqualTo("compile_commands.json"));
            Assert.That(database.IsComplete, Is.False);
            Assert.That(database.HasCommand(LspFilePath.Resolve(source)), Is.True);
            using var cached = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(database.CacheDirectory, "compile_commands.json")));
            var command = cached.RootElement.EnumerateArray().Single();
            Assert.That(command.GetProperty("file").GetString(), Is.EqualTo(LspFilePath.Resolve(source)));
            Assert.That(command.GetProperty("output").GetString(), Is.EqualTo("entry.o"));
        }
        finally { temporary.Delete(recursive: true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ChangedCompilationDatabaseUpdatesCacheAndDetectsDuplicateCommands(bool selectExplicitly)
    {
        var temporary = Directory.CreateTempSubdirectory("codeview-clangd-cache-");
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(temporary.FullName, "source")).FullName;
            var build = Directory.CreateDirectory(Path.Combine(workspace, "build")).FullName;
            var source = Path.Combine(workspace, "entry.c");
            var configuration = Path.Combine(build, "compile_commands.json");
            await File.WriteAllTextAsync(source, "int value;");
            if (selectExplicitly)
                await File.WriteAllTextAsync(Path.Combine(workspace, "compile_commands.json"), "[]");
            var database = new ClangdCompilationDatabase(workspace, selectExplicitly ? configuration : null,
                Path.Combine(temporary.FullName, "cache"));
            const string command = """{"directory":"..","file":"entry.c","arguments":["clang","-c","entry.c"]}""";
            await File.WriteAllTextAsync(configuration, $"[{command}]");
            await database.RefreshAsync(default);
            Assert.That(database.Description, Is.EqualTo(Path.Combine("build", "compile_commands.json")));
            Assert.That(database.IsComplete, Is.True);
            Assert.That(database.HasCommand(LspFilePath.Resolve(source)), Is.True);
            var previous = database.CacheDirectory;
            Assert.That(await database.RefreshAsync(default), Is.False);
            Assert.That(database.CacheDirectory, Is.EqualTo(previous));

            await File.WriteAllTextAsync(configuration, $"[{command},{command}]");
            Assert.That(await database.RefreshAsync(default), Is.True);
            Assert.That(database.CacheDirectory, Is.Not.EqualTo(previous));
            Assert.That(database.IsComplete, Is.False);
            Assert.That(database.HasCommand(LspFilePath.Resolve(source)), Is.True);

            File.Delete(configuration);
            Assert.That(await database.RefreshAsync(default), Is.True);
            Assert.That(database.Description, Is.EqualTo("コンパイル条件未設定"));
            Assert.That(database.HasCommand(LspFilePath.Resolve(source)), Is.False);
        }
        finally { temporary.Delete(recursive: true); }
    }

    [Test]
    public async Task InvalidCompilationDatabaseShapeDoesNotPublishAnIncompleteCacheFile()
    {
        var temporary = Directory.CreateTempSubdirectory("codeview-clangd-invalid-db-");
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(temporary.FullName, "source")).FullName;
            var configuration = Path.Combine(workspace, "compile_commands.json");
            var cache = Path.Combine(temporary.FullName, "cache");
            await File.WriteAllTextAsync(configuration, """{"commands":[]}""");
            var database = new ClangdCompilationDatabase(workspace, configuration, cache);
            Assert.ThrowsAsync<InvalidDataException>(async () => await database.RefreshAsync(default));
            Assert.That(database.CacheDirectory, Is.Empty);
            Assert.That(Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories), Is.Empty);
        }
        finally { temporary.Delete(recursive: true); }
    }

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
