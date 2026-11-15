#if CODEVIEW_SEMANTIC_PROBE
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AvaloniaEdit;

namespace CodeView.Presentation;

internal static class BundledSemanticProbe
{
    internal static async Task RunAsync(MainWindow window, MainWindowViewModel view, TextEditor editor)
    {
        var report = Environment.GetEnvironmentVariable("CODEVIEW_GUI_PERF_REPORT");
        var root = Environment.GetEnvironmentVariable("CODEVIEW_GUI_SEMANTIC_ROOT");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || report is null || root is null) return;

        var checks = new List<string>();
        GuiSemanticChecks.Result? result = null;
        string? failure = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            result = await new GuiSemanticChecks(window, view, editor, (target, key) => target.RaiseEvent(key))
                .RunAsync(root, Require, deadline.Token);
        }
        catch (Exception error)
        {
            failure = error.ToString();
            Console.Error.WriteLine(failure);
            Program.MarkFailed();
        }
        finally
        {
            try
            {
                using var stream = File.Create(report);
                using var json = new Utf8JsonWriter(stream);
                json.WriteStartObject();
                json.WriteString("Scenario", "native-reference-search");
                json.WriteString("ProductInformationalVersion", typeof(BundledSemanticProbe).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
                json.WriteBoolean("NativeAot", !RuntimeFeature.IsDynamicCodeSupported);
                json.WriteString("DisplayMode", "full-document-candidate");
                json.WriteString("Failure", failure);
                json.WriteStartObject("ReferenceChecks");
                json.WriteBoolean("Passed", failure is null);
                json.WriteStartArray("Checks");
                foreach (var check in checks) json.WriteStringValue(check);
                json.WriteEndArray();
                json.WriteStartObject("Semantic");
                json.WriteStartArray("ShutdownPids");
                foreach (var pid in result?.ShutdownPids ?? []) json.WriteNumberValue(pid);
                json.WriteEndArray();
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndObject();
            }
            finally { window.Close(); }
        }

        void Require(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException(name);
            checks.Add(name);
        }
    }
}
#endif
