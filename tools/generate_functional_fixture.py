#!/usr/bin/env python3
"""Generate small deterministic fixtures for detailed-design/08-validation.md."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess


GENERATOR = "codeView-functional-v1"
ENCODING_TEXT = "日本語\r\nABC\r\n"


def fixture_files():
    files = []

    def text(path, value, **expectations):
        files.append((path, value.encode("utf-8"), {"encoding": "utf-8", "text": value, **expectations}))

    text("text/search.txt", "foo food Foo\r\nfoo", searches=[
        {"query": "foo", "matchCase": False, "wholeWord": True, "offsets": [0, 9, 14], "positions": [[1, 1], [1, 10], [2, 1]]},
        {"query": "foo", "matchCase": True, "wholeWord": True, "offsets": [0, 14], "positions": [[1, 1], [2, 1]]}])
    text("text/overlap.txt", "aaa", searches=[{"query": "aa", "offsets": [0]}])
    text("text/unicode.txt", "a😀b\r\nが\nか\u3099", searches=[
        {"query": "b", "offsets": [3], "positions": [[1, 4]]},
        {"query": "が", "offsets": [6], "positions": [[2, 1]]},
        {"query": "か\u3099", "offsets": [8], "positions": [[3, 1]]}])
    text("text/empty.txt", "", lineCount=1, eof={"offset": 0, "line": 1, "column": 1}, searches=[{"query": "x", "offsets": []}])
    text("text/trailing-crlf.txt", "x\r\n", lineCount=2, eof={"offset": 3, "line": 2, "column": 1})
    text("csharp/basic.cs", "class C { void M() {} }", declarations=[
        {"name": "C", "kind": "class", "offset": 6, "line": 1, "column": 7},
        {"name": "M", "kind": "method", "offset": 15, "line": 1, "column": 16}])
    text("csharp/non-code.cs", '// class C { void M() {} }\n/* class C { void M() {} } */\nvar s = "class C { void M() {} }";\n', declarations=[])
    text("csharp/members.cs", "class Outer {\n    class Inner {}\n    int Field;\n    int Property { get; set; }\n    void Method() {}\n}\nstruct Value {}\ninterface Contract {}\nenum Choice { One }\nrecord Item(int Id);\ndelegate void Handler();\n",
         expectedNames=["Outer", "Inner", "Field", "Property", "Method", "Value", "Contract", "Choice", "Item", "Handler"])
    for encoding, bom in [("utf-8", b"\xef\xbb\xbf"), ("cp932", b""), ("utf-16-le", b"\xff\xfe"),
                          ("utf-16-be", b"\xfe\xff"), ("utf-32-le", b"\xff\xfe\x00\x00"), ("utf-32-be", b"\x00\x00\xfe\xff")]:
        payload = ENCODING_TEXT.encode(encoding)
        files.append((f"encoding/{encoding}.txt", payload, {"encoding": encoding, "bomBytes": 0, "text": ENCODING_TEXT}))
        if bom:
            files.append((f"encoding/{encoding}-bom.txt", bom + payload, {"encoding": encoding, "bomBytes": len(bom), "text": ENCODING_TEXT}))
    for name, encoding, payload in [
        ("utf8-truncated", "utf-8", b"\xe3\x81"), ("utf8-overlong", "utf-8", b"\xc0\xaf"),
        ("cp932-truncated", "cp932", b"\x81"), ("cp932-bad-trail", "cp932", b"\x82\x20"),
        ("utf16-surrogate", "utf-16-le", b"\x00\xd8"), ("utf32-out-of-range", "utf-32-le", b"\x00\x00\x11\x00")]:
        files.append((f"invalid/{name}.txt", payload, {"encoding": encoding, "expectedError": "DecodeFailed", "encodingMustBeExplicit": True}))
    files.append(("invalid/binary.txt", b"\x89PNG\r\n\x1a\n", {"expectedError": "Binary"}))
    text("io/update-before.txt", "before\r\n")
    text("io/update-after.txt", "after update\r\n")
    text("io/update.txt", "before\r\n", scenario="Prepare then replace with update-after.txt before ReadChunks; expect ChangedDuringRead")
    text("io/denied.txt", "denied\n", scenario="Native generation denies current-user read; expect AccessDenied")
    return files


def current_windows_sid():
    result = subprocess.run(["whoami", "/user", "/fo", "csv", "/nh"], check=True, capture_output=True)
    # SIDs are ASCII, independent of the current user's name and console code page.
    match = re.search(rb"S-1-\d+(?:-\d+)+", result.stdout)
    if match is None:
        raise RuntimeError("whoami did not return a user SID")
    return "*" + match.group().decode("ascii")


def set_read_access(path, *, denied):
    if path.is_symlink() or not path.is_file():
        raise ValueError("Permission fixture must be a regular file, not a link")
    if os.name == "nt":
        sid = current_windows_sid()
        args = ["/deny", sid + ":(RD)"] if denied else ["/remove:d", sid]
        subprocess.run(["icacls", str(path), *args], check=True, capture_output=True)
    else:
        path.chmod(0o000 if denied else 0o600)


def generate(output, *, native=True):
    output = Path(output)
    if output.exists() or output.is_symlink():
        raise FileExistsError("Output exists; choose a new fixture directory")
    output.mkdir(parents=True, exist_ok=False)
    entries = []
    for relative, data, expected in fixture_files():
        target = output / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as stream:
            stream.write(data)
        entries.append({"path": relative, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(), **expected})
    links = [("file.txt", "../text/search.txt", False), ("folder", "../text", True),
             ("broken.txt", "absent.txt", False), ("cycle-a", "cycle-b", True),
             ("cycle-b", "cycle-a", True), ("loop", "..", True)]
    native_status = {"requested": native, "links": "not-run", "readDenied": "not-run"}
    errors = []
    if native:
        (output / "links").mkdir()
        try:
            for name, target, directory in links:
                (output / "links" / name).symlink_to(target, target_is_directory=directory)
            native_status["links"] = "created"
        except OSError as error:
            native_status["links"] = "unavailable"
            errors.append(f"links: {type(error).__name__}")
        denied_file = output / "io/denied.txt"
        try:
            set_read_access(denied_file, denied=True)
            try:
                denied_file.read_bytes()
            except PermissionError:
                native_status["readDenied"] = "verified"
            else:
                raise RuntimeError("Read denial is not enforced for the current user")
        except (OSError, RuntimeError, subprocess.SubprocessError) as error:
            native_status["readDenied"] = "unavailable"
            errors.append(f"read denial: {type(error).__name__}")
            set_read_access(denied_file, denied=False)
    manifest = {"formatVersion": 1, "generator": GENERATOR, "files": entries,
                "positionUnits": "UTF-16", "links": [{"path": "links/" + n, "target": t, "directory": d} for n, t, d in links],
                "native": native_status, "errors": errors,
                "update": {"target": "io/update.txt", "before": "io/update-before.txt", "after": "io/update-after.txt"}}
    (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--portable", action="store_true", help="Skip links/read-denial setup; this is not all native cases")
    args = parser.parse_args()
    try:
        manifest = generate(args.output, native=not args.portable)
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        parser.exit(1, f"ERROR: {error}\n")
    print(json.dumps({"manifest": str(args.output / "manifest.json"), "native": manifest["native"], "errors": manifest["errors"]}))
    if manifest["errors"]:
        parser.exit(1, "ERROR: Native cases unavailable; inspect manifest.json\n")


if __name__ == "__main__":
    main()
