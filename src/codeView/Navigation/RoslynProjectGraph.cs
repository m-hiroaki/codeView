using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CodeView.Navigation;

// Roslyn's project/open loads the supplied projects. Evaluate ProjectReference for a standalone .csproj
// so referenced source is also available when there is no selected solution.
internal sealed record RoslynProjectGraph(IReadOnlyList<string> Projects, bool Complete)
{
    public static async Task<RoslynProjectGraph> ResolveAsync(string root, string entry, CancellationToken ct)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var projects = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(entry);
        var complete = true;
        while (pending.TryDequeue(out var project))
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(project)) continue;
            var relative = Path.GetRelativePath(root, project);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(project) || !Path.GetExtension(project).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                complete = false;
                continue;
            }
            projects.Add(project);
            using var evaluation = await EvaluateAsync(project, null, ct).ConfigureAwait(false);
            if (evaluation is null)
            {
                complete = false;
                continue;
            }
            var properties = evaluation.RootElement.GetProperty("Properties");
            var frameworks = properties.GetProperty("TargetFrameworks").GetString();
            if (string.IsNullOrWhiteSpace(frameworks))
            {
                AddReferences(evaluation.RootElement);
                continue;
            }
            foreach (var framework in frameworks.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                using var target = await EvaluateAsync(project, framework, ct).ConfigureAwait(false);
                if (target is null) complete = false;
                else AddReferences(target.RootElement);
            }
        }
        if (projects.Count == 0) throw new IOException("選択したC#プロジェクトを読み取れませんでした。");
        return new(projects, complete);

        void AddReferences(JsonElement evaluation)
        {
            foreach (var item in evaluation.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray())
            {
                if (item.TryGetProperty("FullPath", out var value) && value.GetString() is { } path && Path.IsPathFullyQualified(path))
                    pending.Enqueue(path);
                else complete = false;
            }
        }
    }

    private static async Task<JsonDocument?> EvaluateAsync(string project, string? framework, CancellationToken ct)
    {
        var options = new ProcessStartInfo(FindDotnet())
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        options.ArgumentList.Add("msbuild");
        options.ArgumentList.Add(project);
        options.ArgumentList.Add("-nologo");
        options.ArgumentList.Add("-getProperty:TargetFrameworks");
        options.ArgumentList.Add("-getItem:ProjectReference");
        if (framework is not null) options.ArgumentList.Add("-property:TargetFramework=" + framework);
        try
        {
            using var process = Process.Start(options);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var json = await output.ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0) return null;
                return JsonDocument.Parse(json);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                try { await Task.WhenAll(output, stderr).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
                throw;
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or JsonException) { return null; }
    }

    private static string FindDotnet()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { } host && File.Exists(host)) return host;
        const string macInstall = "/usr/local/share/dotnet/dotnet";
        return OperatingSystem.IsMacOS() && File.Exists(macInstall) ? macInstall : "dotnet";
    }
}
