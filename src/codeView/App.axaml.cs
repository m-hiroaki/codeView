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
#if CODEVIEW_PERFORMANCE_PROBE
            var rows = Environment.GetEnvironmentVariable("CODEVIEW_GUI_ROWS") == "1";
            documents = new AvaloniaEditDocumentAdapter(rows,
                collectGraphemeIndex: rows && Environment.GetEnvironmentVariable("CODEVIEW_GUI_GRAPHEME_INDEX") == "1");
#endif
            var operations = new AppOperations(new FileReader(source), documents, new DirectoryReader(source));
            var settings = new SettingsStore(SettingsLocations.ForApplication(AppContext.BaseDirectory));
            desktop.MainWindow = new MainWindow(new MainWindowViewModel(operations, settings), desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
#if CODEVIEW_PERFORMANCE_PROBE
        GuiPerformanceProbe.Checkpoint("framework-end");
#endif
    }
}
