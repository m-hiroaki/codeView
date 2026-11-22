using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CodeView.Presentation;
using CodeView.Files;
using CodeView.Documents;
using CodeView.Operations;
using CodeView.Settings;

namespace CodeView;

internal sealed partial class App : Application
{
    public override void Initialize()
    {
#if CODEVIEW_PERFORMANCE_PROBE
        GuiPerformanceProbe.Checkpoint("app-initialize-start");
#endif
        Name = "codeView";
        AvaloniaXamlLoader.Load(this);
#if CODEVIEW_PERFORMANCE_PROBE
        GuiPerformanceProbe.Checkpoint("app-initialize-end");
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
#if CODEVIEW_PERFORMANCE_PROBE
        GuiPerformanceProbe.Checkpoint("framework-start");
#endif
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var source = new SourceAccess();
            var documents = new AvaloniaEditDocumentAdapter();
            string? semanticCacheDirectory = null;
            string? cppCacheDirectory = null;
#if CODEVIEW_PERFORMANCE_PROBE
            semanticCacheDirectory = Environment.GetEnvironmentVariable("CODEVIEW_GUI_SEMANTIC_CACHE");
            if (Environment.GetEnvironmentVariable("CODEVIEW_GUI_PERF_REPORT") is { } tagReport)
                cppCacheDirectory = Environment.GetEnvironmentVariable("CODEVIEW_GUI_CPP_TAG_CACHE") ?? tagReport + ".cpp-tags";
            var rows = Environment.GetEnvironmentVariable("CODEVIEW_GUI_ROWS") == "1";
            documents = new AvaloniaEditDocumentAdapter(rows,
                collectGraphemeIndex: rows && Environment.GetEnvironmentVariable("CODEVIEW_GUI_GRAPHEME_INDEX") == "1");
#endif
            var locations = SettingsLocations.ForApplication(AppContext.BaseDirectory);
#if CODEVIEW_PERFORMANCE_PROBE
            locations = GuiPerformanceProbe.SelectSettingsLocations(locations);
#endif
#if CODEVIEW_SEMANTIC_PROBE
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
                && Environment.GetEnvironmentVariable("CODEVIEW_GUI_PERF_REPORT") is { } report)
            {
                locations = new(report + ".portable-settings.json", report + ".user-settings.json");
                semanticCacheDirectory = Environment.GetEnvironmentVariable("CODEVIEW_GUI_SEMANTIC_CACHE");
            }
#endif
            var operations = new AppOperations(new FileReader(source), documents, new DirectoryReader(source), semanticCacheDirectory,
                cppCacheDirectory: cppCacheDirectory);
            var settings = new SettingsStore(locations);
#if CODEVIEW_PERFORMANCE_PROBE || CODEVIEW_SEMANTIC_PROBE
            var recentHistory = (Environment.GetEnvironmentVariable("CODEVIEW_CHROME_CAPTURE") == "1"
                || Environment.GetEnvironmentVariable("CODEVIEW_WELCOME_STARTUP") == "1")
                && Environment.GetEnvironmentVariable("CODEVIEW_GUI_PERF_REPORT") is { } historyReport
                ? new RecentHistoryStore(historyReport + ".recent.json") : null;
#else
            var recentHistory = new RecentHistoryStore(Path.Combine(Path.GetDirectoryName(locations.UserFile)!, "codeview.recent.json"));
#endif
            desktop.MainWindow = new MainWindow(new MainWindowViewModel(operations, settings, recentHistory: recentHistory), desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
#if CODEVIEW_PERFORMANCE_PROBE
        GuiPerformanceProbe.Checkpoint("framework-end");
#endif
    }
}
