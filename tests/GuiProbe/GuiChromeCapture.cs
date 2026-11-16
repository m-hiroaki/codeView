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
        await Frame(ct);
        Save("document");

        var view = (IScrollable)_editor.TextArea.TextView;
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
        await File.WriteAllTextAsync(_output + ".chrome.json", JsonSerializer.Serialize(new
        {
            WindowWidth = _window.Bounds.Width, WindowHeight = _window.Bounds.Height, _window.RenderScaling,
            StartupMilliseconds = _openedMilliseconds, FrameMedianMilliseconds = frameTimes[frameTimes.Length / 2],
            FrameMaxMilliseconds = frameTimes[^1], Movement = movement
        }), ct);

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
