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
        Name = "codeView";
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var operations = new AppOperations(new FileReader(new SourceAccess()), new AvaloniaEditDocumentAdapter());
            var settings = new SettingsStore(SettingsLocations.ForApplication(AppContext.BaseDirectory));
            desktop.MainWindow = new MainWindow(new MainWindowViewModel(operations, settings), desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
