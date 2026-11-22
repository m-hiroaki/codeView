using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using CodeView.Operations;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private async Task CheckCppStickyScrollAsync(Action<bool, string> require, CancellationToken ct)
    {
        _viewModel.CloseWorkspace();
        _viewModel.SearchPanel.IsVisible = false;
        var folder = Directory.CreateDirectory(_output + ".sticky-cpp-input").FullName;
        var c = Path.Combine(folder, "reader.c");
        var cpp = Path.Combine(folder, "reader.cxx");
        var headerPath = Path.Combine(folder, "reader.hpp");
        var padding = new string('\n', 125);
        await File.WriteAllTextAsync(c, "struct State\n{\n    int value;\n};\n\n"
            + "int read(int value)\n{\n    return value;\n}\n" + padding, ct);
        await File.WriteAllTextAsync(cpp, "int Widget::run()\n{\n    return 1;\n}\n" + padding, ct);
        await File.WriteAllTextAsync(headerPath, "#ifndef READER_HPP\n#define READER_HPP\n"
            + "class Reader\n{\npublic:\n    int readMember()\n    {\n        return 1;\n    }\n};\n"
            + "int readValue(Reader& reader) { return reader.readMember(); }\n#endif\n" + padding, ct);
        var header = _window.FindControl<Border>("StickyScrollHeader")!;
        var code = _window.FindControl<TextBlock>("StickyScrollTypeCode")!;
        var method = _window.FindControl<TextBlock>("StickyScrollMethodCode")!;
        var number = _window.FindControl<TextBlock>("StickyScrollTypeNumber")!;
        var methodNumber = _window.FindControl<TextBlock>("StickyScrollMethodNumber")!;
        var view = _editor.TextArea.TextView;
        var scrollable = (IScrollable)view;

        await OpenAtAsync(c, 3);
        require(header.IsVisible && !method.IsEffectivelyVisible && Text(code) == "struct State"
            && number.Text == "1", "sticky-c-struct-original-declaration");
        await ScrollAsync(8);
        require(header.IsVisible && !method.IsEffectivelyVisible && Text(code) == "int read(int value)"
            && number.Text == "6" && !_viewModel.HasWorkspace, "sticky-c-standalone-function-line");
        await ExpectColorsAsync("int", code);
        require(Colored(code, "int"), "sticky-c-uses-source-colors");

        await OpenAtAsync(cpp, 3);
        require(header.IsVisible && !method.IsEffectivelyVisible && Text(code) == "int Widget::run()",
            "sticky-cpp-qualified-function-line");
        await OpenAtAsync(headerPath, 8);
        require(header.IsVisible && method.IsEffectivelyVisible && Text(code) == "class Reader"
            && Text(method).TrimStart() == "int readMember()" && number.Text == "3" && methodNumber.Text == "6",
            "sticky-cpp-guarded-header-two-original-lines");
        await ExpectColorsAsync("class", code);
        await ExpectColorsAsync("int", method);
        require(Colored(code, "class") && Colored(method, "int"), "sticky-cpp-header-uses-source-colors");
        var toggle = _window.FindControl<MenuItem>("StickyScrollMenu")!;
        var position = view.VerticalOffset;
        toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await _viewModel.PendingStickySettings.WaitAsync(ct);
        require(!header.IsVisible && view.VerticalOffset == position, "sticky-cpp-off-keeps-viewport");
        toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await _viewModel.PendingStickySettings.WaitAsync(ct);
        await ((MainWindow)_window).StickyScrollWork.WaitAsync(ct);
        await Frame(ct);
        require(header.IsVisible && method.IsEffectivelyVisible && view.VerticalOffset == position,
            "sticky-cpp-on-restores-viewport-header");
        using (var screenshot = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(_window.Bounds.Width),
                   (int)Math.Ceiling(_window.Bounds.Height))))
        {
            screenshot.Render(_window);
            screenshot.Save(_output + ".sticky-cpp.png", PngBitmapEncoderOptions.Default);
        }
        await ScrollAsync(11);
        require(!header.IsVisible, "sticky-cpp-leaving-class-clears-header");
        await _viewModel.OpenFilesAsync([folder], OpenOrigin.Menu).WaitAsync(ct);
        var invocation = _editor.Document.Text.LastIndexOf("readMember", StringComparison.Ordinal);
        await _viewModel.FindDefinitionsAsync(invocation + 1, invocation, 0).WaitAsync(ct);
        await Frame(ct);
        await Frame(ct);
        var caretY = view.GetVisualPosition(_editor.TextArea.Caret.Position, VisualYPosition.LineTop).Y - view.VerticalOffset;
        require(_editor.SelectedText == "readMember" && _editor.TextArea.Caret.Line == 6
            && caretY >= (header.IsVisible ? header.Height - 1 : 0), "sticky-cpp-f12-target-visible");

        async Task OpenAtAsync(string path, int line)
        {
            await _viewModel.OpenFilesAsync([path], OpenOrigin.Menu).WaitAsync(ct);
            await ((MainWindow)_window).StickyScrollWork.WaitAsync(ct);
            await Frame(ct);
            await ScrollAsync(line);
        }

        async Task ScrollAsync(int line)
        {
            scrollable.Offset = new Vector(0, view.DefaultLineHeight * (line - 0.75));
            await Frame(ct);
            await Frame(ct);
        }

        async Task ExpectColorsAsync(string keyword, TextBlock text)
        {
            for (var retry = 0; retry < 75 && !Colored(text, keyword); retry++) await Task.Delay(20, ct);
            await Frame(ct);
        }

        static string Text(TextBlock text) => string.Concat(text.Inlines!.OfType<Run>().Select(run => run.Text));
        static bool Colored(TextBlock text, string keyword) =>
            text.Foreground is ISolidColorBrush normal && text.Inlines!.OfType<Run>().Any(run =>
                run.Text == keyword && run.Foreground is ISolidColorBrush brush && brush.Color != normal.Color);
    }
}
