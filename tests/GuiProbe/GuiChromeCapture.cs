using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaloniaEdit;
using CodeView.Operations;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private async Task CaptureChromeAsync(CancellationToken ct)
    {
        _viewModel.SearchPanel.IsVisible = false;
        _viewModel.InFileSearch.IsVisible = false;
        _viewModel.CloseWorkspace();
        foreach (var tab in _viewModel.Tabs.ToArray()) _viewModel.CloseTab(tab);
        await Frame(ct);
        Save("empty");

        var folder = Directory.CreateDirectory(Path.Combine(_output + ".chrome-input", "shop")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(folder, "長い名前のフォルダ")).FullName;
        var body = Path.Combine(nested, "Source.cs");
        await File.WriteAllTextAsync(body, "namespace Demo;\n\n" + string.Concat(Enumerable.Range(1, 1200)
            .Select(index => $"// {index} Window chrome scroll sample\n")), ct);
        var other = Path.Combine(nested, "Other.cs");
        await File.WriteAllTextAsync(other, "class Other {}\n", ct);
        await _viewModel.OpenFilesAsync([folder, other, body], OpenOrigin.Menu);
        var view = (IScrollable)_editor.TextArea.TextView;
        view.Offset = default;
        await Frame(ct);
        await Frame(ct);
        CheckHeader(body);
        Save("document");
        bool? appKitOption = OperatingSystem.IsMacOS() ? CheckAppKitOption() : null;

        var frameTimes = new double[48];
        var movement = 0;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < frameTimes.Length; i++)
        {
            var before = view.Offset;
            view.Offset = new Vector(0, i * 32);
            await Frame(ct);
            if (view.Offset != before) movement++;
            frameTimes[i] = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
        }
        if (movement < frameTimes.Length - 2) throw new InvalidOperationException("Chrome scroll sample did not move.");

        _viewModel.SearchPanel.IsVisible = true;
        _viewModel.SearchPanel.Query = "chrome scroll sample";
        await _viewModel.SearchPanel.StartAsync().WaitAsync(ct);
        await Frame(ct);
        Save("search");
        Array.Sort(frameTimes);
        var viewport = _window.Bounds.Size;
        var renderScaling = _window.RenderScaling;
        _viewModel.SearchPanel.IsVisible = false;
        await Frame(ct);
        await MeasureIdentifierAsync("normal", _editor.Document.Text.IndexOf("chrome", StringComparison.Ordinal), horizontal: false, ct);
        var longLine = Path.Combine(nested, "long-line.txt");
        var denseLine = string.Concat(Enumerable.Repeat("needle ", 4681)) + "\n";
        await File.WriteAllTextAsync(longLine, string.Concat(Enumerable.Repeat(denseLine, 3)) + new string('\n', 120), ct);
        await _viewModel.OpenFilesAsync([longLine], OpenOrigin.Menu);
        await Frame(ct);
        await MeasureIdentifierAsync("32767-column", offset: 1, horizontal: true, ct);
        _viewModel.CloseFile();
        var longFolder = Directory.CreateDirectory(Path.Combine(nested, new string('長', 48))).FullName;
        var longPath = Path.Combine(longFolder, "Source.cs");
        File.Copy(body, longPath);
        await _viewModel.OpenFilesAsync([longPath], OpenOrigin.Menu);
        var inFile = _viewModel.InFileSearch;
        inFile.HighlightIdentifier(_editor.Document, _editor.Document.Text.IndexOf("chrome", StringComparison.Ordinal), true);
        await inFile.PendingWork.WaitAsync(ct);
        _viewModel.SearchPanel.IsVisible = true;
        await Frame(ct);
        var originalRail = CheckIdentifierRail();
        var normalWidth = _window.Width;
        var normalHeight = _window.Height;
        var maximumWidth = _window.MaxWidth;
        var maximumHeight = _window.MaxHeight;
        _window.MaxWidth = _window.Width = _window.MinWidth;
        _window.MaxHeight = _window.Height = _window.MinHeight;
        await Frame(ct);
        await Frame(ct);
        CheckHeader(longPath);
        CheckSearchDock();
        var minimumRail = CheckIdentifierRail();
        if (minimumRail.Height >= originalRail.Height)
            throw new InvalidOperationException("Identifier markers did not follow the minimum window size.");
        var minimum = _window.Bounds.Size;
        if (minimum.Width > _window.MinWidth + 16 || minimum.Height > _window.MinHeight + 48)
            throw new InvalidOperationException($"Did not reach the minimum window size: {minimum}.");
        Save("minimum");
        _window.MaxWidth = maximumWidth;
        _window.MaxHeight = maximumHeight;
        _window.WindowState = WindowState.Maximized;
        await Frame(ct);
        await Frame(ct);
        CheckHeader(longPath);
        CheckSearchDock();
        if (CheckIdentifierRail().Height <= minimumRail.Height)
            throw new InvalidOperationException("Identifier markers did not follow the maximized window size.");
        var maximized = _window.Bounds.Size;
        if (maximized.Width <= minimum.Width || maximized.Height <= minimum.Height)
            throw new InvalidOperationException($"Window did not maximize: {maximized}.");
        Save("maximized");
        _window.WindowState = WindowState.Normal;
        await Frame(ct);
        double? windowsDpiScale = null;
        if (OperatingSystem.IsWindows())
        {
            var hwnd = _window.TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("Missing Win32 window.");
            if (!ChromeWin32.GetWindowRect(hwnd, out var originalRect))
                throw new InvalidOperationException("Could not read window bounds for DPI validation.");
            var originalDpi = ChromeWin32.GetDpiForWindow(hwnd);
            if (originalDpi == 0) throw new InvalidOperationException("Could not read the window DPI.");
            const uint ProbeDpi = 120;
            const double ProbeScale = 1.25;
            // Keep the outer frame inside the standard CI desktop while leaving 680 DIPs for content.
            var scaledRect = new ChromeWin32.Rect { Left = 32, Top = 32, Right = 936, Bottom = 672 };
            try
            {
                ChromeWin32.ChangeDpi(hwnd, ProbeDpi, ref scaledRect);
                await Frame(ct);
                await Frame(ct);
                if (Math.Abs(_window.RenderScaling - ProbeScale) > 0.001)
                    throw new InvalidOperationException("The native DPI event did not set the expected render scale.");
                CheckHeader(longPath);
                CheckSearchDock();
                CheckIdentifierRail();
                Save("windows-125dpi", ProbeScale, _window.GetVisualParent());
                windowsDpiScale = _window.RenderScaling;
            }
            finally { ChromeWin32.ChangeDpi(hwnd, originalDpi, ref originalRect); }
        }
        _window.Width = normalWidth;
        _window.Height = normalHeight;
        inFile.ClearIdentifier();
        await File.WriteAllTextAsync(_output + ".chrome.json", JsonSerializer.Serialize(new
        {
            WindowWidth = viewport.Width, WindowHeight = viewport.Height, RenderScaling = renderScaling,
            MinimumWidth = minimum.Width, MinimumHeight = minimum.Height,
            MaximizedWidth = maximized.Width, MaximizedHeight = maximized.Height,
            AppKitOption = appKitOption,
            WindowsDpiEventScale = windowsDpiScale,
            StartupMilliseconds = _openedMilliseconds, FrameMedianMilliseconds = frameTimes[frameTimes.Length / 2],
            FrameMaxMilliseconds = frameTimes[^1], Movement = movement, IdentifierSamples = _identifierSamples
        }), ct);

        void CheckSearchDock()
        {
            var layout = _window.FindControl<Grid>("DocumentLayout")!;
            var pane = _window.FindControl<Border>("SearchResultsPane")!;
            var list = _window.FindControl<ListBox>("SearchResultsList")!;
            var main = layout.ColumnDefinitions[1];
            var right = pane.TranslatePoint(new Point(pane.Bounds.Width, 0), layout);
            if (main.ActualWidth < 223.5 || pane.Bounds.Width < 245.5 || list.Bounds.Height < 80
                || right is not { } edge || Math.Abs(edge.X - layout.Bounds.Width) > 1)
                throw new InvalidOperationException("The search sidebar clipped the editor or results at this window size.");
        }

        Rect CheckIdentifierRail()
        {
            var rail = _window.FindControl<IdentifierScrollMarkers>("IdentifierMarkers")!;
            if (rail.TrackBounds is not { Height: > 2, Width: >= 2 } bounds
                || bounds.Bottom > rail.Bounds.Height + 0.5 || bounds.Right > rail.Bounds.Width + 0.5
                || !rail.HasMarkerAt(1202, _editor.Document.LineCount))
                throw new InvalidOperationException("Identifier markers are outside the editor's scrollbar.");
            return bounds;
        }

        void CheckHeader(string path)
        {
            var header = _window.FindControl<Grid>("ChromeHeader")!;
            var breadcrumb = _window.FindControl<TextBlock>("BreadcrumbText")!;
            var menu = _window.FindControl<Button>("ChromeMenuButton")!;
            var search = _window.FindControl<Button>("ChromeSearch")!;
            if (ToolTip.GetTip(breadcrumb) is not string tip || tip != path
                || menu.Bounds.Right > breadcrumb.Bounds.Left || breadcrumb.Bounds.Right > search.Bounds.Left
                || search.Bounds.Right > header.Bounds.Width + 0.5 || !search.IsEffectivelyVisible)
                throw new InvalidOperationException("Window header controls overlap or lose the source path.");
            if (!OperatingSystem.IsWindows()) return;
            // Avalonia hosts the client window and its drawn decorations as siblings.
            var host = _window.GetVisualParent() ?? throw new InvalidOperationException("Missing native window host.");
            var titleText = host.GetVisualDescendants().OfType<Panel>()
                .Single(panel => panel.Name == "PART_TitleTextPanel");
            if (titleText.IsEffectivelyVisible || _window.Title != $"{path} — codeView")
                throw new InvalidOperationException("Windows draws a duplicate title or loses the window name.");
            var caption = host.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Name is "PART_MinimizeButton" or "PART_MaximizeButton" or "PART_CloseButton"
                    && button.IsEffectivelyVisible)
                .Select(button => button.TranslatePoint(default, host))
                .OfType<Point>().ToArray();
            var searchEdge = search.TranslatePoint(new Point(search.Bounds.Width, 0), host);
            if (caption.Length != 3 || searchEdge is null || caption.Any(point => point.X < searchEdge.Value.X))
                throw new InvalidOperationException("Windows caption buttons overlap the search button or are unavailable.");
        }

        void Save(string state, double rasterScale = 1, Visual? decorationsHost = null)
        {
            var host = decorationsHost ?? _window.FindControl<Grid>("MainLayout")!;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(host.Bounds.Width * rasterScale),
                (int)Math.Ceiling(host.Bounds.Height * rasterScale)), new Vector(96 * rasterScale, 96 * rasterScale));
            bitmap.Render(host);
            bitmap.Save($"{_output}.chrome-{state}.png", PngBitmapEncoderOptions.Default);
        }
    }
}
