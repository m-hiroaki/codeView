using CodeView.Contracts;
using CodeView.Files;
using CodeView.Navigation;

namespace CodeView.Tests.Navigation;

internal sealed class CSharpDeclarationTests
{
    [TestCase("class N {}")]
    [TestCase("struct N {}")]
    [TestCase("interface N {}")]
    [TestCase("enum N { A, B }")]
    [TestCase("record N(int Value);")]
    [TestCase("record class N {}")]
    [TestCase("readonly record struct N<T>(T Value) where T : class;")]
    [TestCase("namespace Outer { [A] public partial class Host { public class N<T> where T : class {} } }")]
    [TestCase("namespace Outer; [A] public class @N {}")]
    [TestCase("class N;")]
    [TestCase("class N(int value) : Base(value);")]
    [TestCase("namespace Outer { struct N<T> where T : class; }")]
    [TestCase("class Outer { interface N<T> : IDisposable; }")]
    [TestCase("[A] enum N : byte;")]
    public void FindsTypes(string source) => AssertMatches(source, DeclarationKind.Type);

    [TestCase("public delegate void N();")]
    [TestCase("[A] public delegate System.Collections.Generic.List<T[]> N<T>(T value) where T : class;")]
    [TestCase("class C { public delegate ref int N(in int value); }")]
    [TestCase("interface where {} delegate T N<T>(T value) where T : where;")]
    public void FindsDelegates(string source) => AssertMatches(source, DeclarationKind.Delegate);

    [TestCase("class C { void N() {} }")]
    [TestCase("interface C { void N(); }")]
    [TestCase("class C { public int N() => 42; }")]
    [TestCase("class C { [A(typeof(int[]))]\r\n public static\n (T Value, int Count) N<T>([B] T value) where T : class, new() => (value, 1); }")]
    [TestCase("class C { public T N<T,U>(T value) where T : class where U : new() { return value; } }")]
    [TestCase("struct C { public ref readonly global::System.Int32 N() => ref Value; }")]
    [TestCase("class async {} class C { public async N() => new async(); }")]
    [TestCase("class C { public async Task<int> N() => await Task.FromResult(1); }")]
    [TestCase("interface where {} class C { T N<T>(T value) where T : where => value; }")]
    [TestCase("interface where {} interface I<T> {} class C { T N<T,U>(T value) where T : I<where> where U : new() { return value; } }")]
    [TestCase("interface where {} interface C { T N<T>(T value) where T : where; }")]
    [TestCase("using where = System; class C { T N<T>(T value) where T : where::IDisposable => value; }")]
    public void FindsMethods(string source) => AssertMatches(source, DeclarationKind.Method);

    [TestCase("class C { int N { get; set; } }")]
    [TestCase("class C { [A] int N => new C { Value = 1 }.Value; }")]
    [TestCase("class C { public int N { [A] get => 1; init {} } = 2; }")]
    [TestCase("interface C { string? @N { get; } }")]
    [TestCase("class required {} class C { public required N { get; } }")]
    [TestCase("class required {} class C { public required required N { get; init; } }")]
    public void FindsProperties(string source) => AssertMatches(source, DeclarationKind.Property);

    [TestCase("class C { int N; }")]
    [TestCase("class C { [A] private readonly int[] N = [1, 2]; }")]
    [TestCase("class C { int A = Call(1, 2), N = 3, B; }")]
    [TestCase("class C { object A = new Pair<int, int> { Value = 1 }, N = new { A = 1, B = 2 }; }")]
    [TestCase("class C { object A = Call<int, string>(), N; }")]
    [TestCase("class C { bool A = a < b, N = c > (d); }")]
    [TestCase("class C { System.Action A = () => { int Local = 0; }, N = null; }")]
    [TestCase("class C { private file.Value<int>[] N; }")]
    [TestCase("class partial {} class C { private partial N = new partial(); }")]
    public void FindsFields(string source) => AssertMatches(source, DeclarationKind.Field);

    [TestCase("N(); class Other {}")]
    [TestCase("class Other; struct NN; interface NN; enum NN;")]
    [TestCase("class C { C() : base() {} void M() { N(); obj.N(); int N = 1; void N() {} } }")]
    [TestCase("class C { void M() { /* class N {} */ string x = \"class N {}\"; } }")]
    [TestCase("class C { int I.N() => 1; event Action N; }")]
    [TestCase("class async {} class C { void M() { async N = null; N.ToString(); } }")]
    [TestCase("class C { object A = Call<int, N, string>(); }")]
    [TestCase("class C { bool A = value is Pair<int, N, string> pair; }")]
    [TestCase("class C { bool A = Cache<int, N, string> == null; }")]
    [TestCase("class NN { int n; int Name; }")]
    public void ExcludesNonDeclarationsAndUnsupportedMemberForms(string source)
    {
        var (declarations, matches) = Scan(source);
        Assert.That(declarations.Error, Is.EqualTo(DeclarationScanError.None));
        Assert.That(matches, Is.Empty);
    }

    [Test]
    public void KeepsOverloadsAndNamesAcrossScopesInSourceOrder()
    {
        const string source = "namespace A { class N {} }\r\nclass C { int N(int x) => x; int N(string x) => 1; }";
        var (declarations, matches) = Scan(source);
        Assert.That(declarations.Error, Is.EqualTo(DeclarationScanError.None));
        Assert.That(matches.Select(m => m.Kind), Is.EqualTo(new[] { DeclarationKind.Type, DeclarationKind.Method, DeclarationKind.Method }));
        Assert.That(matches.Select(m => m.Range.Offset), Is.Ordered);
        Assert.That(matches.Select(m => m.Range.Offset), Is.Unique);
        Assert.That(matches[1].Range.Start, Is.EqualTo(new TextPosition(2, 15)));
    }

    [TestCase("class C { int N;")]
    [TestCase("class N")]
    [TestCase("class C { void N(] {} }")]
    [TestCase("class C { int N = ; }")]
    [TestCase("class C { int N, ; }")]
    [TestCase("class C { int N => ; }")]
    [TestCase("interface where {} class C { T N<T>(T value) where T : where => ; }")]
    public void ReportsIncompleteContext(string source) =>
        Assert.That(Scan(source).Declarations.Error, Is.EqualTo(DeclarationScanError.Incomplete));

    [Test]
    public void BoundsDeclarationTokensCharactersAndDepth()
    {
        var tokens = "class C { int N = " + string.Join("+", Enumerable.Repeat("1", 130)) + "; }";
        Assert.That(Scan(tokens).Declarations.Error, Is.EqualTo(DeclarationScanError.DeclarationLimit));
        Assert.That(Scan("class C { int" + new string(' ', CSharpDeclarations.MaximumChars) + "N; }").Declarations.Error,
            Is.EqualTo(DeclarationScanError.DeclarationLimit));
        Assert.That(Scan("class C { void M() {" + new string('{', CSharpLexer.MaximumDepth) + "} }").Declarations.Error,
            Is.EqualTo(DeclarationScanError.DepthLimit));
        AssertMatches("class C { int" + new string(' ', CSharpDeclarations.MaximumChars - 6) + "N; }", DeclarationKind.Field);
    }

    [TestCase("class C { int N; }", true)]
    [TestCase("class C { int N;", false)]
    [TestCase("class C { int N; } /*", false)]
    public void FileResultConfirmsOrInvalidatesProvisionalMatches(string source, bool canCommit)
    {
        var root = Directory.CreateTempSubdirectory("codeview-declarations-");
        try
        {
            var path = Path.Combine(root.FullName, "source.cs");
            File.WriteAllText(path, source);
            var files = new FileReader(new SourceAccess());
            using var prepared = files.Prepare(SourcePath.Create(path, path), EncodingChoice.Utf8, default).Value;
            var matches = new List<DeclarationMatch>();
            var result = new CSharpDeclarationScanner(files).Read(prepared, "N", match => { matches.Add(match); return true; }, default);
            Assert.That(result.CanCommit, Is.EqualTo(canCommit));
            Assert.That(matches.Select(m => m.Kind), Is.EqualTo(new[] { DeclarationKind.Field }));
        }
        finally { root.Delete(recursive: true); }
    }

    private static void AssertMatches(string source, DeclarationKind kind)
    {
        var (declarations, matches) = Scan(source);
        Assert.That(declarations.Error, Is.EqualTo(DeclarationScanError.None));
        Assert.That(matches.Select(m => m.Kind), Is.EqualTo(new[] { kind }));
        var range = matches.Single().Range;
        Assert.That(source.Substring((int)range.Offset, (int)range.Length).TrimStart('@'), Is.EqualTo("N"));
    }

    private static (CSharpDeclarations Declarations, List<DeclarationMatch> Matches) Scan(string source)
    {
        var matches = new List<DeclarationMatch>();
        var declarations = new CSharpDeclarations("N", match => { matches.Add(match); return true; });
        var lexer = new CSharpLexer();
        foreach (var c in source)
        {
            if (!lexer.Push(new ReadOnlySpan<char>(in c), declarations.Push)) break;
        }
        if (!lexer.IsFinished && lexer.Complete(declarations.Push)) declarations.Complete();
        if (declarations.Error == DeclarationScanError.None) Assert.That(lexer.IsComplete, Is.True, lexer.Error.ToString());
        return (declarations, matches);
    }
}
