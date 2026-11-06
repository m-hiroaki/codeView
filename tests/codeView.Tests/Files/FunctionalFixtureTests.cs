using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Search;

namespace CodeView.Tests.Files;

internal sealed class FunctionalFixtureTests
{
    [Test]
    public async Task GeneratedFixedBytesMatchReaderDocumentAndChunkedSearchExpectations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codeview-functional-" + Guid.NewGuid().ToString("N"));
        try
        {
            await GenerateAsync(directory);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")));
            Assert.That(manifest.RootElement.GetProperty("files").GetArrayLength(), Is.EqualTo(30));
            var reader = new FileReader(new SourceAccess());
            foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var path = SourcePath.Create(entry.GetProperty("path").GetString()!, directory);
                var encoding = entry.TryGetProperty("encoding", out var name) ? name.GetString() switch
                {
                    "utf-8" => EncodingChoice.Utf8, "cp932" => EncodingChoice.Cp932,
                    "utf-16-le" => EncodingChoice.Utf16Le, "utf-16-be" => EncodingChoice.Utf16Be,
                    "utf-32-le" => EncodingChoice.Utf32Le, "utf-32-be" => EncodingChoice.Utf32Be,
                    _ => throw new InvalidDataException("Unknown fixture encoding")
                } : EncodingChoice.Auto;
                var prepared = reader.Prepare(path, encoding, CancellationToken.None);
                using var builder = new AvaloniaEditDocumentAdapter().CreateBuilder();
                OperationError? error = prepared.Error;
                if (prepared.Status == OperationStatus.Success)
                {
                    using var input = prepared.Value;
                    error = reader.ReadChunks(input, builder.Append, CancellationToken.None).Error;
                }
                if (entry.TryGetProperty("expectedError", out var expectedError))
                    Assert.That(error?.Code.ToString(), Is.EqualTo(expectedError.GetString()), path.DisplayPath);
                else
                {
                    Assert.That(error, Is.Null, path.DisplayPath);
                    using var handle = builder.Complete();
                    var document = handle.ClaimForDisplay();
                    var text = entry.GetProperty("text").GetString()!;
                    Assert.That(document.Text, Is.EqualTo(text), path.DisplayPath);
                    if (entry.TryGetProperty("lineCount", out var lineCount))
                        Assert.That(document.LineCount, Is.EqualTo(lineCount.GetInt32()));
                    if (entry.TryGetProperty("eof", out var eof))
                    {
                        var location = document.GetLocation(eof.GetProperty("offset").GetInt32());
                        Assert.That((location.Line, location.Column), Is.EqualTo((eof.GetProperty("line").GetInt32(), eof.GetProperty("column").GetInt32())));
                    }
                    if (entry.TryGetProperty("searches", out var searches))
                        foreach (var search in searches.EnumerateArray()) VerifySearch(text, search);
                    if (entry.TryGetProperty("declarations", out var declarations))
                        foreach (var declaration in declarations.EnumerateArray())
                        {
                            var location = document.GetLocation(declaration.GetProperty("offset").GetInt32());
                            Assert.That((location.Line, location.Column), Is.EqualTo((declaration.GetProperty("line").GetInt32(), declaration.GetProperty("column").GetInt32())));
                        }
                }
                Assert.That(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path.FullPath))).ToLowerInvariant(),
                    Is.EqualTo(entry.GetProperty("sha256").GetString()), "Reading must not modify fixture bytes.");
            }
            var changed = SourcePath.Create("io/update.txt", directory);
            using var before = reader.Prepare(changed, EncodingChoice.Utf8, CancellationToken.None).Value;
            File.Copy(Path.Combine(directory, "io/update-after.txt"), changed.FullPath, overwrite: true);
            Assert.That(reader.ReadChunks(before, _ => { }, CancellationToken.None).Error!.Code, Is.EqualTo(ErrorCode.ChangedDuringRead));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void VerifySearch(string text, JsonElement expected)
    {
        var options = new SearchOptions(MatchCase: expected.TryGetProperty("matchCase", out var matchCase) && matchCase.GetBoolean(),
            WholeWord: expected.TryGetProperty("wholeWord", out var wholeWord) && wholeWord.GetBoolean());
        foreach (var size in new[] { 1, 2, 3, 7, 16_384 })
        {
            var matcher = LiteralMatcher.Create(expected.GetProperty("query").GetString()!, options).Value;
            var hits = new List<TextRange>();
            bool Consume(TextRange hit) { hits.Add(hit); return true; }
            for (var offset = 0; offset < text.Length; offset += size)
                Assert.That(matcher.Push(text.AsSpan(offset, Math.Min(size, text.Length - offset)), Consume, CancellationToken.None), Is.True);
            Assert.That(matcher.Complete(Consume, CancellationToken.None), Is.True);
            Assert.That(hits.Select(hit => hit.Offset), Is.EqualTo(expected.GetProperty("offsets").EnumerateArray().Select(value => value.GetInt64())));
            if (expected.TryGetProperty("positions", out var positions))
                Assert.That(hits.Select(hit => hit.Start), Is.EqualTo(positions.EnumerateArray().Select(value => new TextPosition(value[0].GetInt64(), value[1].GetInt64()))));
        }
    }

    private static async Task GenerateAsync(string output)
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tools/generate_functional_fixture.py"))) root = root.Parent;
        Assert.That(root, Is.Not.Null, "Run tests from the repository checkout.");
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "py" : "python3")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (OperatingSystem.IsWindows()) start.ArgumentList.Add("-3");
        start.ArgumentList.Add(Path.Combine(root!.FullName, "tools/generate_functional_fixture.py"));
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("--portable");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        await stdout;
        Assert.That(process.ExitCode, Is.Zero, await stderr);
    }
}
