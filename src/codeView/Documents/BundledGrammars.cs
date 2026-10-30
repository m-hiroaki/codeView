using TextMateSharp.Grammars;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace CodeView.Documents;

/// <summary>Offline grammar candidate. Construction and unsupported paths do not initialize assets.</summary>
internal sealed class BundledGrammars : IRegistryOptions
{
    private readonly Lazy<RegistryOptions> _options = new(() => new RegistryOptions(ThemeName.DarkPlus));

    internal bool IsInitialized => _options.IsValueCreated;

    internal static string? ScopeForPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "source.cs",
            ".c" => "source.c",
            ".cpp" or ".h" => "source.cpp",
            ".json" => "source.json",
            ".xml" or ".config" or ".csproj" or ".props" or ".targets" or ".xaml" => "text.xml",
            ".md" or ".markdown" => "text.html.markdown",
            _ => null
        };
    }

    // Only embedded package assets are used; no local source paths or network locator are configured.
    public IRawGrammar? GetGrammar(string scopeName) => _options.Value.GetGrammar(scopeName);
    public IRawTheme? GetTheme(string scopeName) => _options.Value.GetTheme(scopeName);
    public ICollection<string>? GetInjections(string scopeName) => _options.Value.GetInjections(scopeName);
    public IRawTheme GetDefaultTheme() => _options.Value.GetDefaultTheme();
}
