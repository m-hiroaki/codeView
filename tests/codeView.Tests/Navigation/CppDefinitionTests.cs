using CodeView.Contracts;
using CodeView.Files;
using CodeView.Navigation;
using CodeView.Search;

namespace CodeView.Tests.Navigation;

internal sealed class CppDefinitionTests
{
    [TestCase("static int Target(int value) { return value; }", DeclarationKind.Function)]
    [TestCase("static int Target(int *value) { return *value; }", DeclarationKind.Function)]
    [TestCase("extern \"C\" { int Target(void) { return 0; } }", DeclarationKind.Function)]
    [TestCase("namespace ns { auto Target() -> decltype(2) { return 2; } }", DeclarationKind.Function)]
    [TestCase("inline namespace ns { int Target() noexcept { return 2; } }", DeclarationKind.Function)]
    [TestCase("template <typename T> T Target(T arg) { return arg; }", DeclarationKind.Function)]
    [TestCase("void ns::Class<int>::Target() const & noexcept {}", DeclarationKind.Function)]
    [TestCase("struct Box { Box() : value{1}, other(2) {} void Target() const {} int value, other; };", DeclarationKind.Function)]
    [TestCase("struct Target { Target(int) = delete; };", DeclarationKind.Type, DeclarationKind.Function)]
    [TestCase("struct Box { int value; }; Box::Box() : value{1} {} int Target() { return 0; }", DeclarationKind.Function)]
    [TestCase("template<class T> struct Target final : Base<T> {};", DeclarationKind.Type)]
    [TestCase("struct [[nodiscard]] Target { int x; };", DeclarationKind.Type)]
    [TestCase("union Target { int x; float y; };", DeclarationKind.Type)]
    [TestCase("enum class Target : unsigned { first, second };", DeclarationKind.Type)]
    [TestCase("enum class More { first = f(0, 1), Target = 5 };", DeclarationKind.Enumerator)]
    [TestCase("typedef struct { int value; } Target;", DeclarationKind.Type)]
    [TestCase("typedef unsigned int Target;", DeclarationKind.Type)]
    [TestCase("typedef int Target(int);", DeclarationKind.Type)]
    [TestCase("typedef void (*Target)(int);", DeclarationKind.Type)]
    [TestCase("using Target = std::pair<int, double>;", DeclarationKind.Type)]
    [TestCase("struct Box { public: int other, Target[2]; };", DeclarationKind.Field)]
    [TestCase("struct Box { unsigned Target : 2; };", DeclarationKind.Field)]
    [TestCase("int other = Other<int, char>(), Target{2};", DeclarationKind.Variable)]
    [TestCase("struct Point { int x; } Target = {1};", DeclarationKind.Variable)]
    [TestCase("int Target(42);", DeclarationKind.Variable)]
    [TestCase("int (*Target)(int) = nullptr;", DeclarationKind.Variable)]
    [TestCase("decltype(Other()) Target = Other();", DeclarationKind.Variable)]
    [TestCase("# define /* comment */ Target(value) \\\r\n    value\n", DeclarationKind.Macro)]
    [TestCase("#define Target 3\n", DeclarationKind.Macro)]
    [TestCase("auto a = u8R\"quote(\"); int Target() {} //)quote\"; int Target() { return 1'000; }", DeclarationKind.Function)]
    [TestCase("// comment \\\r\nint Target() {}\r\n/* int Target(); */\r\nint Target() {}", DeclarationKind.Function)]
    public async Task FindsDefinitionsWithOriginalOffsets(string body, params DeclarationKind[] kinds)
    {
        var (hits, complete) = await Search(("sample.HPP", "\r\n/* 日本語 🚀 */\r\n" + body));
        Assert.That(complete.IsPartial, Is.False);
        Assert.That(hits.Select(hit => hit.Kind), Is.EqualTo(kinds));
        foreach (var hit in hits)
        {
            Assert.That(hit.Source.Substring((int)hit.Range.Offset, (int)hit.Range.Length), Is.EqualTo("Target"));
            var position = hit.Source[..(int)hit.Range.Offset];
            Assert.That(hit.Range.Start.Line, Is.EqualTo(position.Count(value => value == '\n') + 1));
            Assert.That(hit.Range.Start.Column, Is.EqualTo(position.Length - position.LastIndexOf('\n')));
        }
    }

    [TestCase("int Target(int);")]
    [TestCase("class Target;")]
    [TestCase("extern int Target;")]
    [TestCase("using ns::Target;")]
    [TestCase("int target() { return Target(); }")]
    [TestCase("int Other() { int Target = 0; return Target; }")]
    [TestCase("auto other = Target{1};")]
    [TestCase("auto other = ns::Target();")]
    [TestCase("const char *text = \"int Target() {}\"; char q = '\\'';")]
    [TestCase("#include <Target>\n#define OTHER int Target() {}\n#undef Target\n")]
    [TestCase("#pragma message(\"/* int Target() {}\")\n#warning Don't use Target\n")]
    [TestCase("/* int Target() {} */ auto x = R\"(\"); int Target() {} )\";")]
    public async Task IgnoresNonDefinitions(string body)
    {
        var (hits, complete) = await Search(("sample.cc", body));
        Assert.That(complete.IsPartial, Is.False);
        Assert.That(hits, Is.Empty);
    }

    [TestCase("int Target() {} /*")]
    [TestCase("int Target() {} void broken() {")]
    [TestCase("auto broken = R\"wrong(unclosed; int Target() {}")]
    [TestCase("int Target() {}\nint ")]
    public async Task InvalidatesIncompleteFilesAndKeepsOtherCandidates(string broken)
    {
        var (hits, complete) = await Search(("broken.cpp", broken), ("ok.c", "void Target(void) {}"));
        Assert.That(complete.IsPartial, Is.True);
        Assert.That(hits.Select(hit => hit.File), Is.EqualTo(new[] { "ok.c" }));
    }

    [Test]
    public async Task BoundsWorkAndContinuesWithNextFile()
    {
        var (hits, complete) = await Search(("long.cpp", "int " + new string('x', 4097) + ";"), ("ok.cpp", "void Target() {}"));
        Assert.That(complete.IsPartial, Is.True);
        Assert.That(hits.Select(hit => hit.File), Is.EqualTo(new[] { "ok.cpp" }));
    }

    [Test]
    public async Task HandlesRawsAndTokensAcrossReadBoundaries()
    {
        var source = "auto raw = u8R\"marker(" + new string('x', 128 * 1024)
            + "\"); int Target() {} )marker\";\r\nvoid Target() {}";
        var (hits, complete) = await Search(("sample.cpp", source));
        Assert.That(complete.IsPartial, Is.False);
        Assert.That(hits.Single().Range.Offset, Is.EqualTo(source.LastIndexOf("Target", StringComparison.Ordinal)));
    }

    private static async Task<(Found[] Hits, SearchCompletion Complete)> Search(params (string File, string Text)[] inputs)
    {
        var root = Directory.CreateTempSubdirectory("codeview-cpp-symbols-");
        try
        {
            foreach (var (file, source) in inputs) await File.WriteAllTextAsync(Path.Combine(root.FullName, file), source);
            var access = new SourceAccess();
            var files = new FileReader(access);
            var dirs = new DirectoryReader(access);
            var scanner = new CppDeclarationScanner("Target");
            var definitions = new Dictionary<(string, long), Found>();
            var search = new TextSearch(dirs, files);
            var request = new WorkspaceSearchRequest(SourcePath.Create(root.FullName, root.FullName),
                new ExclusionPolicy([]), EncodingChoice.Auto, "Target", default, 1, 1, 1, Definitions: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var session = search.StartWorkspace(request, deadline.Token, (source, emit) => scanner.Read(files, source, match =>
            {
                var file = Path.GetFileName(source.Path.FullPath);
                definitions.Add((file, match.Range.Offset), new(file, inputs.Single(input => input.File == file).Text, match.Kind, match.Range));
                return emit(match.Range, SearchSnippet.Capture("Target", 0, 6));
            }, deadline.Token), CppDeclarationScanner.IncludesPath);
            var committed = new HashSet<long>();
            var emitted = new List<SearchHit>();
            await foreach (var item in session.Events.ReadAllAsync(deadline.Token))
            {
                if (item is SearchEvent<SearchHit>.Hits batch) emitted.AddRange(batch.Items);
                if (item is SearchEvent<SearchHit>.FileCommitted commit) committed.Add(commit.FileScanId);
            }
            var hits = emitted.Where(hit => committed.Contains(hit.FileScanId))
                .Select(hit => definitions[(Path.GetFileName(hit.Path.FullPath), hit.Range.Offset)]).ToArray();
            return (hits, await session.Completion);
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed record Found(string File, string Source, DeclarationKind Kind, TextRange Range);

    [Test]
    public void CollectsQualifiedDefinitionsAtOriginalPositions()
    {
        const string source = """
            /* 日本語 🚀 */
            #ifndef SAMPLE_HPP
            #define SAMPLE_HPP 1
            namespace app::net {
              struct Client {
                int open() { return 1; }
                int close();
              };
              struct Other { int open() { return 2; } };
            }
            int app::net::Client::close() { return 0; }
            template<class T> void app::net::Box<T>::open() {}
            #endif
            """;
        var (definitions, error) = Collect(source);
        Assert.That(error, Is.Null);
        Assert.That(definitions.Select(entry => (entry.Name, entry.Qualifier, entry.HasBody)),
            Is.EqualTo(new (string, string?, bool)[]
            {
                ("SAMPLE_HPP", null, false),
                ("Client", "app::net", false),
                ("open", "app::net::Client", true),
                ("Other", "app::net", false),
                ("open", "app::net::Other", true),
                ("close", "app::net::Client", true),
                ("open", "app::net::Box", true)
            }));
        foreach (var entry in definitions)
            Assert.That(source.Substring((int)entry.Range.Offset, (int)entry.Range.Length), Is.EqualTo(entry.Name));
    }

    [TestCase("#if WIN\nint open() {}\n#else\nint open() {}\n#endif\n", false, 2)]
    [TestCase("#if WIN\nnamespace a {\n#else\nnamespace b {\n#endif\nint open() {}\n}\n", true, 0)]
    [TestCase("int open() {}\nvoid broken() {", true, 2)]
    public void ReportsTheCompletenessOfConditionalAndMalformedSources(string source, bool partial, int observed)
    {
        var (definitions, error) = Collect(source);
        Assert.That(error is not null, Is.EqualTo(partial));
        Assert.That(definitions, Has.Count.EqualTo(observed));
    }

    [Test]
    public void StopsCollectionWhenBudgetIsReachedAndHonorsCancellation()
    {
        var (_, error) = Collect("int first() {} int second() {}", budget: 1);
        Assert.That(error?.Code, Is.EqualTo(ErrorCode.UnsupportedDeclaration));
        using var cancelled = new CancellationTokenSource();
        Assert.That(() => Collect("int first() {}", cancellation: cancelled),
            Throws.InstanceOf<OperationCanceledException>());
    }

    private static (List<CppDefinition> Definitions, OperationError? Error) Collect(string body, int budget = int.MaxValue,
        CancellationTokenSource? cancellation = null)
    {
        var root = Directory.CreateTempSubdirectory("codeview-cpp-definition-collection-");
        try
        {
            var path = SourcePath.Create("sample.hpp", root.FullName);
            File.WriteAllText(path.FullPath, body);
            var files = new FileReader(new SourceAccess());
            using var source = files.Prepare(path, EncodingChoice.Auto, default).Value;
            cancellation?.Cancel();
            var definitions = new List<CppDefinition>();
            var error = CppDeclarationScanner.CreateDefinitionCollector().CollectDefinitions(files, source, entry =>
            {
                if (definitions.Count >= budget) return false;
                definitions.Add(entry);
                return true;
            }, cancellation?.Token ?? default);
            return (definitions, error);
        }
        finally { root.Delete(recursive: true); }
    }
}
