using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
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
        Save("document");

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
        var longFolder = Directory.CreateDirectory(Path.Combine(nested, new string('長', 48))).FullName;
        var longPath = Path.Combine(longFolder, "Source.cs");
        File.Copy(body, longPath);
        await _viewModel.OpenFilesAsync([longPath], OpenOrigin.Menu);
        var normalWidth = _window.Width;
        var normalHeight = _window.Height;
        _window.PlatformImpl!.Resize(new Size(_window.MinWidth, _window.MinHeight), WindowResizeReason.Application);
        await Frame(ct);
        await Frame(ct);
        CheckHeader(longPath);
        var minimum = _window.Bounds.Size;
        if (minimum.Width > _window.MinWidth + 16 || minimum.Height > _window.MinHeight + 48)
            throw new InvalidOperationException($"Did not reach the minimum window size: {minimum}.");
        Save("minimum");
        _window.WindowState = WindowState.Maximized;
        await Frame(ct);
        await Frame(ct);
        CheckHeader(longPath);
        var maximized = _window.Bounds.Size;
        if (maximized.Width <= minimum.Width || maximized.Height <= minimum.Height)
            throw new InvalidOperationException($"Window did not maximize: {maximized}.");
        Save("maximized");
        _window.WindowState = WindowState.Normal;
        _window.Width = normalWidth;
        _window.Height = normalHeight;
        await File.WriteAllTextAsync(_output + ".chrome.json", JsonSerializer.Serialize(new
        {
            WindowWidth = viewport.Width, WindowHeight = viewport.Height, RenderScaling = renderScaling,
            MinimumWidth = minimum.Width, MinimumHeight = minimum.Height,
            MaximizedWidth = maximized.Width, MaximizedHeight = maximized.Height,
            StartupMilliseconds = _openedMilliseconds, FrameMedianMilliseconds = frameTimes[frameTimes.Length / 2],
            FrameMaxMilliseconds = frameTimes[^1], Movement = movement
        }), ct);

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
        }

        void Save(string state)
        {
            var host = _window.FindControl<Grid>("MainLayout")!;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(host.Bounds.Width),
                (int)Math.Ceiling(host.Bounds.Height)), new Vector(96, 96));
            bitmap.Render(host);
            bitmap.Save($"{_output}.chrome-{state}.png", PngBitmapEncoderOptions.Default);
        }
    }
}
