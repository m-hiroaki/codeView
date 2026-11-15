"""Verify go-to-definition and references over stdio against a real Roslyn server."""
import argparse
import json
from pathlib import Path
import queue
import subprocess
import tempfile
import threading
import time


def fixtures(root):
    for name in ("Lib", "App"):
        folder = root / name
        folder.mkdir()
        references = (
            '<ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup>'
            if name == "App" else ""
        )
        (folder / f"{name}.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0'
            f'</TargetFramework></PropertyGroup>{references}</Project>', encoding="utf-8"
        )
    solution = root / "Probe.slnx"
    solution.write_text(
        '<Solution><Project Path="Lib/Lib.csproj" /><Project Path="App/App.csproj" /></Solution>',
        encoding="utf-8",
    )
    library, caller = root / "Lib/Api.cs", root / "App/Caller.cs"
    library.write_text(
        'namespace Library;\npublic static class Api {\n'
        '    public static int Run(int value) => value;\n'
        '    public static string Run(string value) => value;\n}\n', encoding="utf-8",
    )
    caller.write_text(
        'using Library;\nclass Caller {\n    int One() => /* 😀 */ Api.Run(1);\n'
        '    int Two() => Api.Run(2);\n    string Other() => Api.Run("x");\n    // Run(3);\n}\n',
        encoding="utf-8",
    )
    subprocess.run(["dotnet", "restore", "--ignore-failed-sources", str(solution)], check=True)
    return library, caller


def position(path, line, spelling):
    prefix = path.read_text(encoding="utf-8").splitlines()[line].split(spelling, 1)[0]
    return {"line": line, "character": len(prefix.encode("utf-16-le")) // 2}


def location(path, line):
    start = position(path, line, "Run")
    end = dict(start, character=start["character"] + 3)
    return {"uri": path.as_uri(), "range": {"start": start, "end": end}}


def read_messages(stream, inbox):
    try:
        while True:
            headers = {}
            while (line := stream.readline()) not in (b"\r\n", b""):
                key, value = line.decode("ascii").strip().split(":", 1)
                headers[key.lower()] = value.strip()
            if not line:
                raise EOFError("Roslyn closed stdout")
            payload = stream.read(int(headers["content-length"]))
            inbox.put(json.loads(payload))
    except (OSError, EOFError, ValueError) as error:
        inbox.put(error)


def probe(server, root):
    library, caller = fixtures(root)
    inbox = queue.Queue()
    with (root / "roslyn-stderr.log").open("w+b") as stderr:
        process = subprocess.Popen(
            [str(server), "--stdio", "--telemetryLevel", "off"],
            cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr,
        )
        reader = threading.Thread(target=read_messages, args=(process.stdout, inbox), daemon=True)
        reader.start()
        notifications = []
        progress = []

        def send(**message):
            body = json.dumps(dict(jsonrpc="2.0", **message)).encode("utf-8")
            process.stdin.write(f"Content-Length: {len(body)}\r\n\r\n".encode("ascii") + body)
            process.stdin.flush()

        def until(predicate):
            deadline = time.monotonic() + 45
            while (remaining := deadline - time.monotonic()) > 0:
                message = inbox.get(timeout=remaining)
                if isinstance(message, Exception):
                    raise message
                method = message.get("method")
                if method:
                    notifications.append(method)
                    if method == "$/progress":
                        progress.append(message.get("params", {}).get("value", {}).get("kind"))
                    if "id" in message:
                        result = None
                        if method == "workspace/configuration":
                            result = [None for _ in message.get("params", {}).get("items", [])]
                        send(id=message["id"], result=result)
                if predicate(message):
                    if "error" in message:
                        raise RuntimeError(str(message["error"]))
                    return message.get("result")
            raise TimeoutError("Timed out waiting for Roslyn")

        def request(request_id, method, params=None):
            send(id=request_id, method=method, params=params)
            return until(lambda message: "method" not in message and message.get("id") == request_id)

        try:
            initialized = request(1, "initialize", {
                "processId": None, "rootUri": root.as_uri(),
                "workspaceFolders": [{"uri": root.as_uri(), "name": "Probe"}],
                "capabilities": {
                    "general": {"positionEncodings": ["utf-16", "utf-8"]},
                    "window": {"workDoneProgress": True}, "workspace": {"workspaceFolders": True},
                },
            })
            capabilities = initialized["capabilities"]
            encoding = capabilities.get("positionEncoding", "utf-16")
            if encoding != "utf-16":
                raise ValueError(f"Unexpected position encoding: {encoding}")
            send(method="initialized", params={})
            send(method="solution/open", params={"solution": (root / "Probe.slnx").as_uri()})
            send(method="textDocument/didOpen", params={"textDocument": {
                "uri": caller.as_uri(), "languageId": "csharp", "version": 1,
                "text": caller.read_text(encoding="utf-8"),
            }})
            until(lambda message: message.get("method") == "workspace/projectInitializationComplete")
            params = {"textDocument": {"uri": caller.as_uri()}, "position": position(caller, 2, "Run")}
            definitions = request(2, "textDocument/definition", params)
            references = request(3, "textDocument/references", dict(params, context={"includeDeclaration": False}))
            if definitions != [location(library, 2)]:
                raise AssertionError(f"Wrong definition: {definitions}")
            if sorted(references or [], key=lambda item: item["range"]["start"]["line"]) != [
                location(caller, 2), location(caller, 3),
            ]:
                raise AssertionError(f"Wrong references: {references}")
            request(4, "shutdown")
            send(method="exit")
            if process.wait(timeout=5) != 0:
                raise RuntimeError(f"Roslyn exited with code {process.returncode}")
            return {
                "server": initialized.get("serverInfo"),
                "positionEncoding": encoding,
                "explicitPositionEncoding": capabilities.get("positionEncoding"),
                "textDocumentSync": capabilities.get("textDocumentSync"),
                "projectInitializationComplete": True,
                "workDoneProgress": progress,
                "definition": [item["range"] for item in definitions],
                "references": [item["range"] for item in references],
            }
        except Exception:
            stderr.seek(0)
            print(stderr.read().decode("utf-8", errors="replace"))
            raise
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=5)
            reader.join(timeout=5)
            process.stdin.close()
            process.stdout.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", required=True, type=Path)
    options = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="codeview-roslyn-") as temporary:
        print(json.dumps(probe(options.server.resolve(strict=True), Path(temporary).resolve()), indent=2))
