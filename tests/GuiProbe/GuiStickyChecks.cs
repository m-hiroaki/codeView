using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using CodeView.Operations;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private async Task CheckStickyScrollAsync(Action<bool, string> require, CancellationToken ct)
    {
        var folder = Directory.CreateDirectory(_output + ".sticky-input").FullName;
        var path = Path.Combine(folder, "Example.cs");
        var other = Path.Combine(folder, "other.txt");
        var trailing = new string('\n', 125);
        await File.WriteAllTextAsync(path, "\nclass Example\n{\n    void First()\n    {\n        Second();\n    }\n"
            + "\n    void Second()\n    {\n        // second\n    }\n}\n" + trailing, ct);
        await File.WriteAllTextAsync(other, "plain text", ct);
        await _viewModel.OpenFilesAsync([path], OpenOrigin.Menu).WaitAsync(ct);
        var header = _window.FindControl<Border>("StickyScrollHeader")!;
        var label = _window.FindControl<TextBlock>("StickyScrollLabel")!;
        var view = _editor.TextArea.TextView;
        var scrollable = (IScrollable)view;

        await Frame(ct);
        await Frame(ct);
        ScrollToLine(6);
        await ((MainWindow)_window).StickyScrollWork.WaitAsync(ct);
        await Frame(ct);
        require(header.IsVisible && label.Text == "Example › First", "sticky-scroll-pins-method");
        await Frame(ct);
        require(!header.IsHitTestVisible && Math.Abs(header.Bounds.Height - view.DefaultLineHeight) <= 1,
            "sticky-scroll-one-hit-transparent-line");
        var editorSize = _editor.Bounds.Size;
        var selection = (_editor.SelectionStart, _editor.SelectionLength, _editor.CaretOffset);
        await ExpectAsync(8, "Example");
        await ExpectAsync(11, "Example › Second");
        await ExpectAsync(14, null);
        await ExpectAsync(11, "Example › Second", "sticky-scroll-reverse");
        await ExpectAsync(2, null);
        require(editorSize == _editor.Bounds.Size && selection == (_editor.SelectionStart, _editor.SelectionLength, _editor.CaretOffset),
            "sticky-scroll-preserves-viewport-and-selection");
        await ExpectAsync(6, "Example › First");
        await _viewModel.OpenFilesAsync([other], OpenOrigin.Menu).WaitAsync(ct);
        await Frame(ct);
        require(!header.IsVisible, "sticky-scroll-clears-unsupported-tab");
        await _viewModel.OpenFilesAsync([path], OpenOrigin.Menu).WaitAsync(ct);
        await ((MainWindow)_window).StickyScrollWork.WaitAsync(ct);
        await Frame(ct);
        require(header.IsVisible && label.Text == "Example › First", "sticky-scroll-follows-restored-tab");
        await File.WriteAllTextAsync(path, _editor.Document.Text.Replace("First", "Renamed", StringComparison.Ordinal), ct);
        await _viewModel.ReloadAsync().WaitAsync(ct);
        await ((MainWindow)_window).StickyScrollWork.WaitAsync(ct);
        await Frame(ct);
        require(header.IsVisible && label.Text == "Example › Renamed", "sticky-scroll-follows-reload");
        var normalSize = (_window.Width, _window.Height, _window.MaxWidth, _window.MaxHeight);
        try
        {
            _window.MaxWidth = _window.Width = _window.MinWidth;
            _window.MaxHeight = _window.Height = _window.MinHeight;
            await Frame(ct);
            await Frame(ct);
            require(HeaderFits(), "sticky-scroll-fits-minimum-width");
            using var screenshot = new RenderTargetBitmap(new PixelSize(
                (int)Math.Ceiling(_window.Bounds.Width), (int)Math.Ceiling(_window.Bounds.Height)));
            screenshot.Render(_window);
            screenshot.Save(_output + ".sticky-scroll.png", PngBitmapEncoderOptions.Default);
        }
        finally
        {
            (_window.MaxWidth, _window.MaxHeight) = (normalSize.MaxWidth, normalSize.MaxHeight);
            (_window.Width, _window.Height) = (normalSize.Width, normalSize.Height);
        }
        await Frame(ct);
        if (OperatingSystem.IsWindows())
        {
            var hwnd = _window.TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("Missing Win32 window.");
            if (!ChromeWin32.GetWindowRect(hwnd, out var original))
                throw new InvalidOperationException("Unable to read the window bounds.");
            var dpi = ChromeWin32.GetDpiForWindow(hwnd);
            var scaled = new ChromeWin32.Rect { Left = 32, Top = 32, Right = 936, Bottom = 672 };
            try
            {
                ChromeWin32.ChangeDpi(hwnd, 120, ref scaled);
                await Frame(ct);
                await Frame(ct);
                if (Math.Abs(_window.RenderScaling - 1.25) > 0.001 || !HeaderFits())
                    throw new InvalidOperationException("Sticky Scroll doesn't fit at Windows 125% DPI.");
            }
            finally { ChromeWin32.ChangeDpi(hwnd, dpi, ref original); }
        }
        await _viewModel.OpenFilesAsync([folder], OpenOrigin.Menu).WaitAsync(ct);
        var invocation = _editor.Document.Text.IndexOf("Second", StringComparison.Ordinal);
        await _viewModel.FindDefinitionsAsync(invocation + 1, invocation, 0).WaitAsync(ct);
        await Frame(ct);
        await Frame(ct);
        var caretY = view.GetVisualPosition(_editor.TextArea.Caret.Position, VisualYPosition.LineTop).Y - view.VerticalOffset;
        require(_editor.SelectedText == "Second" && _editor.TextArea.Caret.Line == 9
            && caretY >= (header.IsVisible ? header.Height - 1 : 0), "sticky-scroll-definition-target-visible");

        async Task ExpectAsync(int line, string? text, string? check = null)
        {
            ScrollToLine(line);
            await Frame(ct);
            await Frame(ct);
            require(text is null ? !header.IsVisible : header.IsVisible && label.Text == text,
                check ?? $"sticky-scroll-boundary-{line}");
        }

        void ScrollToLine(int line) =>
            scrollable.Offset = new Vector(0, view.DefaultLineHeight * (line - 0.75));

        bool HeaderFits() => header.IsVisible && header.Bounds.Width > 0
            && header.TranslatePoint(new Point(header.Bounds.Width, header.Bounds.Height), _editor) is { } edge
            && edge.X <= _editor.Bounds.Width + 1 && edge.Y <= _editor.Bounds.Height;
    }
}
