using CodeView.Contracts;
using CodeView.Files;
using CodeView.Search;

namespace CodeView.Navigation;

internal interface ISymbolNavigator
{
    WorkspaceSearchSession FindDefinitions(WorkspaceSearchRequest request, CancellationToken ct);
    WorkspaceSearchSession FindReferences(WorkspaceSearchRequest request, CancellationToken ct);
}

internal sealed class SimpleSymbolNavigator(TextSearch search, FileReader files) : ISymbolNavigator
{
    public WorkspaceSearchSession FindReferences(WorkspaceSearchRequest request, CancellationToken ct) =>
        search.StartWorkspace(request with { Options = new(MatchCase: true, WholeWord: true, UseRegex: false) }, ct);

    public WorkspaceSearchSession FindDefinitions(WorkspaceSearchRequest request, CancellationToken ct)
    {
        var name = request.Query ?? throw new ArgumentException("識別子が必要です。", nameof(request));
        CSharpDeclarationScanner? scanner = null;
        return search.StartWorkspace(request, ct, (source, consume) =>
        {
            scanner ??= new(files);
            var result = scanner.Read(source, name, match =>
            {
                var spelling = match.Range.Length > name.Length ? "@" + name : name;
                var label = match.Kind switch
                {
                    DeclarationKind.Type => "型",
                    DeclarationKind.Delegate => "デリゲート",
                    DeclarationKind.Method => "メソッド",
                    DeclarationKind.Property => "プロパティ",
                    _ => "フィールド"
                };
                var snippet = SearchSnippet.Capture(spelling + "  · " + label, 0, spelling.Length);
                return consume(match.Range, snippet);
            }, ct);
            return result.CanCommit ? null : result.Lexical.ReadError ?? new(ErrorCode.UnsupportedDeclaration, source.Path.DisplayPath);
        });
    }
}
