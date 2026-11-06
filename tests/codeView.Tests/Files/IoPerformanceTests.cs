using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Tests.Performance;

namespace CodeView.Tests.Files;

public sealed class IoPerformanceTests
{
    [Test, Category("Performance")]
    public void MeasureReadBytesAndProcessIo()
    {
        var input = Environment.GetEnvironmentVariable("CODEVIEW_IO_FILE");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("CODEVIEW_IO_FILE指定時だけI/Oの取得可否と負荷を測ります。");
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) Assert.Ignore("Windows/macOS only.");
        var mode = Environment.GetEnvironmentVariable("CODEVIEW_IO_MODE") ?? "baseline";
        Assert.That(mode, Is.AnyOf("baseline", "counted"));
        var label = Environment.GetEnvironmentVariable("CODEVIEW_IO_LABEL") ?? "io";
        Assert.That(label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), Is.True);
        var encodingName = Environment.GetEnvironmentVariable("CODEVIEW_IO_ENCODING") ?? "utf-8";
        Assert.That(encodingName, Is.AnyOf("utf-8", "cp932"));
        var encoding = ReferenceEncoding(encodingName);
        var path = CodeView.Contracts.SourcePath.Create(input!, Environment.CurrentDirectory);
        var inputBytes = new FileInfo(path.FullPath).Length;
        Assert.That(inputBytes, Is.GreaterThan(0));
        string inputHash;
        using (var file = File.OpenRead(path.FullPath)) inputHash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        long expectedCharacters = 0;
        using (var reference = new StreamReader(path.FullPath, encoding, detectEncodingFromByteOrderMarks: true))
        {
            var buffer = new char[16 * 1024];
            int read;
            while ((read = reference.Read(buffer)) != 0) expectedCharacters += read;
        }

        var io = new ProcessIo();
        var warmSample = io.Read();
        var sampler = Stopwatch.StartNew();
        const int samples = 10_000;
        for (var index = 0; index < samples; index++) _ = io.Read();
        sampler.Stop();
        var rows = new List<object>();
        using var process = Process.GetCurrentProcess();
        for (var index = 0; index < 10; index++)
        {
            var counts = mode == "counted" ? new ReadAccounting() : null;
            CodeView.Files.ISourceAccess source = new CodeView.Files.SourceAccess();
            if (counts is not null) source = new CountingSourceAccess(source, counts);
            var reader = new CodeView.Files.FileReader(source);
            var before = io.Read();
            var cpu = process.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            var prepared = reader.Prepare(path, CodeView.Files.EncodingChoice.Auto, CancellationToken.None);
            Assert.That(prepared.Status, Is.EqualTo(OperationStatus.Success));
            long? prepareBytes = counts?.Bytes;
            long? prepareCalls = counts?.Calls;
            using (var owned = prepared.Value)
            {
                var read = reader.ReadChunks(owned, _ => { }, CancellationToken.None);
                watch.Stop();
                var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                var after = io.Read();
                Assert.That(read.Status, Is.EqualTo(OperationStatus.Success));
                Assert.That(read.Value, Is.EqualTo(expectedCharacters));
                var delta = ProcessIo.Difference(before, after);
                if (counts is not null)
                    Assert.That(counts.Bytes - prepareBytes, Is.EqualTo(inputBytes - owned.BomBytes));
                rows.Add(new { Index = index, DurationMilliseconds = watch.Elapsed.TotalMilliseconds, CpuMilliseconds = cpuMs,
                    Encoding = owned.Encoding.WebName, BomBytes = owned.BomBytes, Characters = read.Value, LogicalTargetBytes = inputBytes,
                    ApiPrepareBytes = prepareBytes, ApiPrepareReadCalls = prepareCalls,
                    ApiConsumeBytes = counts?.Bytes - prepareBytes, ApiTotalBytes = counts?.Bytes, ApiTotalReadCalls = counts?.Calls,
                    ProcessIoBefore = before, ProcessIoAfter = after, ProcessIoDelta = delta });
            }
        }
        string finalHash;
        using (var file = File.OpenRead(path.FullPath)) finalHash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        Assert.That(finalHash, Is.EqualTo(inputHash));
        var output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", label + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Assert.That(File.Exists(output), Is.False, "Use a new label; do not overwrite observations.");
        var report = new { FormatVersion = 1, Scenario = "read-api-vs-process-io", MeasuredAtUtc = DateTimeOffset.UtcNow,
            ProductInformationalVersion = typeof(CodeView.Files.FileReader).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            Mode = mode, InputBytes = inputBytes, InputSha256 = inputHash, FinalInputSha256 = finalHash, ReferenceCharacters = expectedCharacters,
            CacheCondition = "Warm; SHA-256 and independent strict reference decode before measurement",
            ProcessIoMetric = warmSample.Metric, SamplerCalls = samples, SamplerElapsedMilliseconds = sampler.Elapsed.TotalMilliseconds,
            Rows = rows, Limits = "API-returned bytes include validation and consumption; native counters cover the whole process and are not interchangeable across OSes. No Cold or per-file physical attribution." };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(output);
        TestContext.Progress.WriteLine(output);
    }

    internal static Encoding ReferenceEncoding(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return name switch
        {
            "utf-8" => new UTF8Encoding(false, true),
            "cp932" => Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => throw new ArgumentException("Unknown reference encoding", nameof(name))
        };
    }
}
