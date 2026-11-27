using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Svg.Skia;
using MarkView.Avalonia.Rendering;

namespace CodeView.Presentation;

internal sealed class PlantUmlPreview(string jar, string java)
{
    internal const int MaximumSourceChars = 32 * 1024;
    internal const int MaximumOutputBytes = 2 * 1024 * 1024;
    private static readonly SemaphoreSlim Slot = new(1, 1);

    internal static PlantUmlPreview? FromEnvironment()
    {
        var jar = Environment.GetEnvironmentVariable("CODEVIEW_PLANTUML_JAR");
        if (jar is null || !Path.IsPathFullyQualified(jar) || !File.Exists(jar)) return null;
        var java = Environment.GetEnvironmentVariable("CODEVIEW_PLANTUML_JAVA");
        if (java is not null && (!Path.IsPathFullyQualified(java) || !File.Exists(java))) return null;
        return new(jar, java ?? "java");
    }

    internal Control? CreateBlock(string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaximumSourceChars || ct.IsCancellationRequested) return null;
        var block = new Border { Child = new SelectableTextBlock { Text = code, FontFamily = Avalonia.Media.FontFamily.Parse("monospace") } };
        ViewportTracking.RunWhenNearViewport(block, Start);
        return block;

        async void Start()
        {
            try
            {
                var svg = await Task.Run(() => RenderAsync(code, ct), ct);
                if (svg is null || ct.IsCancellationRequested) return;
                using var stream = new MemoryStream(svg, writable: false);
                if (SvgSource.LoadFromStream(stream) is { } source)
                {
                    // Transparent SVGs from the default PlantUML theme use dark ink.
                    block.Child = new Border
                    {
                        Background = Avalonia.Media.Brushes.White,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        Child = new Image
                        {
                            Source = new SvgImage { Source = source },
                            StretchDirection = Avalonia.Media.StretchDirection.DownOnly
                        }
                    };
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException
                or ArgumentException or IOException or System.Xml.XmlException or NotSupportedException)
            {
                // Keep the original block readable when the local renderer is unavailable or fails.
            }
        }
    }

    internal ProcessStartInfo CreateStartInfo()
    {
        var start = new ProcessStartInfo(java)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[] { "-Xmx128m", "-Djava.awt.headless=true", "-DPLANTUML_SECURITY_PROFILE=SANDBOX",
            "-jar", jar, "-charset", "UTF-8", "-tsvg", "-pipe", "-failfast2" })
            start.ArgumentList.Add(argument);
        start.Environment["PLANTUML_SECURITY_PROFILE"] = "SANDBOX";
        return start;
    }

    internal async Task<byte[]?> RenderAsync(string code, CancellationToken lifetime)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaximumSourceChars) return null;
        await Slot.WaitAsync(lifetime).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var ct = timeout.Token;
            using var process = Process.Start(CreateStartInfo());
            if (process is null) return null;
            using var stop = ct.Register(() => Stop(process));
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
            var write = SendAsync(process, code, ct);
            try
            {
                using var result = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (result.Length + read > MaximumOutputBytes) return null;
                    result.Write(buffer, 0, read);
                }
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                await Task.WhenAll(write, errors).ConfigureAwait(false);
                return process.ExitCode == 0 && result.Length > 0 ? result.ToArray() : null;
            }
            finally
            {
                timeout.Cancel();
                try { await Task.WhenAll(write, errors).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally { Slot.Release(); }
    }

    private static async Task SendAsync(Process process, string source, CancellationToken ct)
    {
        using var input = process.StandardInput;
        await input.WriteAsync(source.AsMemory(), ct).ConfigureAwait(false);
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
