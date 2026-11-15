using System.Diagnostics;
using System.Text.Json;
using CodeView.Contracts;
using CodeView.Documents;
using CodeView.Files;
using CodeView.Navigation;
using CodeView.Operations;
using CodeView.Settings;

namespace CodeView.Tests.Navigation;

internal sealed class LanguageServerSessionTests
{
    [Test]
    public async Task StartsLazilyReusesTheWorkspaceProcessAndShutsDown()
    {
        using var fixture = await ServerFixture.CreateAsync();
        var server = fixture.Session();
        int pid;
        await using (server)
        {
            Assert.That(File.Exists(fixture.Log), Is.False);
            pid = (await Request(server, "pid")).GetInt32();
            Assert.That((await Request(server, "pid")).GetInt32(), Is.EqualTo(pid));
            await server.UseAsync(async (transport, ct) =>
            {
                var uri = new Uri(Path.Combine(fixture.Root.FullName, "Code.cs")).AbsoluteUri;
                await server.SynchronizeAsync(transport, uri, "csharp", 10, "// 😀", ct);
                await server.SynchronizeAsync(transport, uri, "csharp", 10, "// 😀", ct);
                await server.SynchronizeAsync(transport, uri, "csharp", 11, "// changed", ct);
                return await transport.RequestAsync("pid", null, ct);
            }, CancellationToken.None);
        }
        Assert.That(File.ReadAllLines(fixture.Log), Is.EqualTo(new[]
        {
            "initialize", "initialized", "pid", "pid", "textDocument/didOpen:// 😀",
            "textDocument/didClose", "textDocument/didOpen:// changed", "pid", "shutdown", "exit"
        }));
        Assert.That(IsRunning(pid), Is.False);
    }

    [Test]
    public async Task CancelledRequestDoesNotPoisonTheNextRequestAndCrashCanBeRetried()
    {
        using var fixture = await ServerFixture.CreateAsync();
        await using var server = fixture.Session();
        var pid = (await Request(server, "pid")).GetInt32();
        using var cancelled = new CancellationTokenSource();
        var held = Request(server, "hold", cancelled.Token);
        await WaitForLog(fixture.Log, "hold");
        cancelled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await held);
        await WaitForLog(fixture.Log, "$/cancelRequest");
        Assert.That((await Request(server, "pid")).GetInt32(), Is.EqualTo(pid));
        Assert.CatchAsync<IOException>(async () => await Request(server, "crash"));
        var restarted = (await Request(server, "pid")).GetInt32();
        Assert.That(restarted, Is.Not.EqualTo(pid));
        Assert.That(IsRunning(pid), Is.False);
    }

    [Test]
    public async Task DisposalCancelsOutstandingRequestAndTerminatesUnresponsiveServer()
    {
        using var fixture = await ServerFixture.CreateAsync();
        var server = fixture.Session("--ignore-shutdown");
        var pid = (await Request(server, "pid")).GetInt32();
        var held = Request(server, "hold");
        await WaitForLog(fixture.Log, "hold");
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.CatchAsync<OperationCanceledException>(async () => await held);
        Assert.That(IsRunning(pid), Is.False);
        Assert.CatchAsync<OperationCanceledException>(async () => await Request(server, "pid"));
    }

    [Test]
    public async Task RequestTimeoutStopsTheProcessAndNextRequestRestarts()
    {
        using var fixture = await ServerFixture.CreateAsync();
        await using var server = fixture.SessionWithTimeout(TimeSpan.FromSeconds(1));
        var pid = (await Request(server, "pid")).GetInt32();
        Assert.ThrowsAsync<TimeoutException>(async () => await Request(server, "hold"));
        Assert.That(IsRunning(pid), Is.False);
        Assert.That((await Request(server, "pid")).GetInt32(), Is.Not.EqualTo(pid));
    }

    [Test]
    public async Task OversizedResponseEndsTheTransportAndNextRequestRestartsServer()
    {
        using var fixture = await ServerFixture.CreateAsync();
        await using var server = fixture.Session();
        var pid = (await Request(server, "pid")).GetInt32();
        Assert.CatchAsync<IOException>(async () => await Request(server, "oversized"));
        Assert.That((await Request(server, "pid")).GetInt32(), Is.Not.EqualTo(pid));
        Assert.That(IsRunning(pid), Is.False);
    }

    [Test]
    public async Task MissingExecutableIsReportedBeforeStartingAProcess()
    {
        using var fixture = await ServerFixture.CreateAsync();
        await using var server = new LanguageServerSession(
            Path.Combine(fixture.Root.FullName, "absent"), [], fixture.Root.FullName, (_, _) => { });
        Assert.CatchAsync<IOException>(async () => await Request(server, "pid"));
        Assert.That(File.Exists(fixture.Log), Is.False);
    }

    [Test]
    public async Task SwitchingWorkspaceAndAppShutdownReleaseOwnedServers()
    {
        using var first = await ServerFixture.CreateAsync();
        using var second = await ServerFixture.CreateAsync();
        var access = new SourceAccess();
        using var operations = new AppOperations(new(access), new AvaloniaEditDocumentAdapter(), new(access));
        var owner = await OpenAsync(first);
        var server = operations.GetLanguageServer(owner, SemanticLanguage.CSharp, () => first.Session());
        var pid = (await Request(server, "pid")).GetInt32();
        var replacement = await OpenAsync(second);
        Assert.That(IsRunning(pid), Is.False);
        Assert.Throws<OperationCanceledException>(() =>
            operations.GetLanguageServer(owner, SemanticLanguage.CSharp, () => first.Session()));

        var next = operations.GetLanguageServer(replacement, SemanticLanguage.Cpp, () => second.Session());
        var nextPid = (await Request(next, "pid")).GetInt32();
        operations.Dispose();
        await operations.PendingSearchWork.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(IsRunning(nextPid), Is.False);

        async Task<WorkspaceState> OpenAsync(ServerFixture fixture) =>
            (await operations.OpenFolderAsync(SourcePath.Create(fixture.Root.FullName, fixture.Root.FullName),
                SettingsSnapshot.Default)).Value;
    }

    private static Task<JsonElement> Request(LanguageServerSession server, string method, CancellationToken ct = default) =>
        server.UseAsync((transport, token) => transport.RequestAsync(method, null, token), ct);

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private static async Task WaitForLog(string path, string line)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.ReadAllLines(path).Contains(line)) await Task.Delay(10, timeout.Token);
    }

    private sealed class ServerFixture(DirectoryInfo root, string python) : IDisposable
    {
        private const string Server = """
            import json, os, sys

            def send(**message):
                data = json.dumps(dict(jsonrpc="2.0", **message)).encode()
                sys.stdout.buffer.write(f"Content-Length: {len(data)}\r\n\r\n".encode() + data)
                sys.stdout.buffer.flush()

            pending = None
            while (header := sys.stdin.buffer.readline()):
                assert header.startswith(b"Content-Length: ")
                assert sys.stdin.buffer.readline() == b"\r\n"
                message = json.loads(sys.stdin.buffer.read(int(header.split(b":")[1])))
                method = message.get("method")
                request_id = message.get("id")
                if method == "textDocument/didOpen":
                    method += ":" + message["params"]["textDocument"]["text"]
                with open("server.log", "a", encoding="utf-8") as log:
                    log.write(method + "\n")
                if method == "initialize":
                    send(id=request_id, result={"capabilities": {"positionEncoding": "utf-16"}})
                elif method == "pid":
                    send(id=request_id, result=os.getpid())
                elif method == "hold":
                    pending = request_id
                elif method == "$/cancelRequest" and pending is not None:
                    send(id=pending, result="late reply")
                elif method == "crash":
                    sys.exit(7)
                elif method == "oversized":
                    sys.stdout.buffer.write(b"Content-Length: 999999999\r\n\r\n")
                    sys.stdout.buffer.flush()
                elif method == "shutdown" and "--ignore-shutdown" not in sys.argv:
                    send(id=request_id, result=None)
                elif method == "exit":
                    sys.exit(0)
            """;

        public DirectoryInfo Root { get; } = root;
        public string Log => Path.Combine(Root.FullName, "server.log");

        public static async Task<ServerFixture> CreateAsync()
        {
            var options = new ProcessStartInfo(OperatingSystem.IsWindows() ? "py" : "python3")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            if (OperatingSystem.IsWindows()) options.ArgumentList.Add("-3");
            options.ArgumentList.Add("-c");
            options.ArgumentList.Add("import sys; print(sys.executable)");
            using var launcher = Process.Start(options)!;
            var python = (await launcher.StandardOutput.ReadToEndAsync()).Trim();
            await launcher.WaitForExitAsync();
            Assert.That(launcher.ExitCode, Is.Zero);
            var root = Directory.CreateTempSubdirectory("codeview-lsp-test-");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "server.py"), Server);
            return new(root, python);
        }

        public LanguageServerSession Session(params string[] arguments) =>
            new(python, [Path.Combine(Root.FullName, "server.py"), .. arguments], Root.FullName, (_, _) => { });

        public LanguageServerSession SessionWithTimeout(TimeSpan timeout) =>
            new(python, [Path.Combine(Root.FullName, "server.py")], Root.FullName, (_, _) => { }, requestTimeout: timeout);

        public void Dispose() => Root.Delete(recursive: true);
    }
}
