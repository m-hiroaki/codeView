using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace CodeView.Presentation;

internal sealed partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}
