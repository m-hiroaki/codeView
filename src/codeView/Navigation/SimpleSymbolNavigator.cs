using CodeView.Contracts;
using CodeView.Files;
using CodeView.Search;

namespace CodeView.Navigation;

internal interface ISymbolNavigator
{
    WorkspaceSearchSession FindDefinitions(WorkspaceSearchRequest request, CancellationToken ct);
    WorkspaceSearchSession FindReferences(WorkspaceSearchRequest request, CancellationToken ct);
}

internal sealed class SimpleSymbolNavigator(TextSearch search, FileReader files, bool cpp = false) : ISymbolNavigator
{
    public WorkspaceSearchSession FindReferences(WorkspaceSearchRequest request, CancellationToken ct) =>
        search.StartWorkspace(request with { Options = new(MatchCase: true, WholeWord: true, UseRegex: false) }, ct);

    public WorkspaceSearchSession FindDefinitions(WorkspaceSearchRequest request, CancellationToken ct)
    {
        var name = request.Query ?? throw new ArgumentException("識別子が必要です。", nameof(request));
        CSharpDeclarationScanner? scanner = null;
        CppDeclarationScanner? cppScanner = null;
        return search.StartWorkspace(request, ct, (source, consume) =>
        {
            if (cpp) return (cppScanner ??= new(name)).Read(files, source, Publish, ct);
            scanner ??= new(files);
            var result = scanner.Read(source, name, Publish, ct);
            return result.CanCommit ? null : result.Lexical.ReadError ?? new(ErrorCode.UnsupportedDeclaration, source.Path.DisplayPath);

            bool Publish(DeclarationMatch match)
            {
                var spelling = match.Range.Length > name.Length ? "@" + name : name;
                var snippet = SearchSnippet.Capture(spelling + "  · " + Label(match.Kind), 0, spelling.Length);
                return consume(match.Range, snippet);
            }
        }, cpp ? CppDeclarationScanner.IncludesPath
            : static path => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase));
    }

    internal static string Label(DeclarationKind kind) => kind switch
    {
        DeclarationKind.Type => "型",
        DeclarationKind.Delegate => "デリゲート",
        DeclarationKind.Method => "メソッド",
        DeclarationKind.Property => "プロパティ",
        DeclarationKind.Function => "関数",
        DeclarationKind.Variable => "変数",
        DeclarationKind.Macro => "マクロ",
        DeclarationKind.Enumerator => "列挙子",
        _ => "フィールド"
    };
}
