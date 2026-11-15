namespace CodeView.Navigation;

internal sealed record SemanticWorkspaceOptions(string? RoslynExecutable, string? CSharpProject,
    string? ClangdExecutable, string? CompilationDatabase)
{
    internal string? Executable(SemanticLanguage language) =>
        language == SemanticLanguage.CSharp ? RoslynExecutable : ClangdExecutable;
}
