"""CI-only native-control editing attempts; not OS keyboard/mouse/IME injection."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

from measure_gui_startup import expected_text, sha256


ACTIONS = ("text", "newline", "paste", "delete", "cut", "undo", "redo", "text-drop")
SELECTIONS = ("start", "end", "all")
POSITIVE = ("RoutedInputChanged", "NativeClipboardPasteChanged", "DeleteChanged", "UndoChanged", "RedoChanged", "TextDropChanged")
OS_PREFIXES = {"windows": "Microsoft Windows", "macos": "macOS"}


def validate_report(report, characters, body_hash, commit, expected_os="windows"):
    if expected_os not in OS_PREFIXES:
        raise ValueError("Use windows or macos")
    if (report["Failure"] is not None or report["InputInterrupted"]
            or report["Scenario"] != "native-gui-readonly-controls"
            or report["DisplayMode"] != "full-document-candidate"
            or not report["Os"].startswith(OS_PREFIXES[expected_os])
            or not report["ProductInformationalVersion"].endswith("+" + commit)
            or report["EditorReadOnly"] is not True or report["TextDragDropEnabled"] is not False
            or report["UndoCanUndo"] is not False or report["UndoCanRedo"] is not False):
        raise ValueError("Unexpected or failed native read-only probe")
    result = report["ReadOnlyChecks"]
    if (result["Passed"] is not True or result["OriginalCharacters"] != characters
            or result["OriginalSha256"] != body_hash or result["FinalSha256"] != body_hash
            or result["ProductDocumentPreserved"] is not True or result["ProductTabsPreserved"] is not True
            or result["ProductFocused"] is not True):
        raise ValueError("Body/tab/focus invariants were not verified")
    if not all(result["PositiveControl"][key] is True for key in POSITIVE):
        raise ValueError("Editing attempts were not proven effective on the editable control")
    if characters:
        if result["NativeCopyRoundTrip"] is not True or result["CopySkippedForEmpty"] is not False:
            raise ValueError("Read-only native copy failed")
    elif result["NativeCopyRoundTrip"] is not None or result["CopySkippedForEmpty"] is not True:
        raise ValueError("Empty copy was misreported")
    checks = result["Checks"]
    expected = {(selection, action) for selection in SELECTIONS for action in ACTIONS}
    actual = {(row["Selection"], row["Action"]) for row in checks}
    if len(checks) != len(expected) or actual != expected:
        raise ValueError("Missing/duplicate/unexpected editing attempts")
    for row in checks:
        if (row["Characters"] != characters or row["BodySha256"] != body_hash
                or row["UndoCanUndo"] is not False or row["UndoCanRedo"] is not False
                or row["Caret"] != (characters if row["Selection"] == "end" else 0)
                or row["SelectionLength"] != (characters if row["Selection"] == "all" else 0)):
            raise ValueError("Editing attempt changed body/history or skipped a selection boundary")
        if row["Action"] in ("text", "newline", "text-drop") and row["Handled"] is not True:
            raise ValueError("Routed input/drop did not reach its handler")
        if row["Action"] in ("delete", "paste", "cut") and type(row["CommandAvailable"]) is not bool:
            raise ValueError("Command availability was not recorded")


def collect(app, inputs, output, rounds, commit, expected_os="windows"):
    if os.environ.get("GITHUB_ACTIONS") != "true":
        raise RuntimeError("Run clipboard-changing automation only in a disposable CI session")
    if expected_os not in OS_PREFIXES:
        raise ValueError("Use windows or macos")
    if not 1 <= rounds <= 10 or not 1 <= len(inputs) <= 10:
        raise ValueError("Use 1..10 rounds and 1..10 small UTF-8 fixtures")
    if not app.is_file() or not (app.parent / "codeView.dll").is_file():
        raise FileNotFoundError(app)
    fixtures = []
    for path in inputs:
        characters, lines = expected_text(path)
        text = path.read_bytes().decode("utf-8-sig", errors="strict")
        fixtures.append({"path": str(path), "characters": characters, "lines": lines,
                         "inputSha256": sha256(path), "bodySha256": hashlib.sha256(text.encode("utf-16-le")).hexdigest()})
    output.mkdir(parents=True, exist_ok=False)
    record = {"formatVersion": 1, "commit": commit, "expectedOs": expected_os, "fixtures": fixtures, "rounds": rounds,
              "productSha256": sha256(app.parent / "codeView.dll"), "appHostSha256": sha256(app), "runs": []}
    for number in range(1, rounds + 1):
        for index, fixture in enumerate(fixtures):
            label = f"readonly-{index}-{number}"
            target = output / (label + ".json")
            env = {key: value for key, value in os.environ.items() if not key.startswith("CODEVIEW_GUI_")}
            env.update(CODEVIEW_GUI_PERF_REPORT=str(target), CODEVIEW_GUI_READONLY_CHECKS="1")
            command = [str(app), fixture["path"]]
            row = {"fixture": index, "round": number, "command": command}
            record["runs"].append(row)
            try:
                if sha256(Path(fixture["path"])) != fixture["inputSha256"]:
                    raise ValueError("Fixture changed before native test")
                with (output / (label + ".log")).open("wb") as log:
                    child = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=60)
                row["exitCode"] = child.returncode
                if target.exists():
                    row["report"] = json.loads(target.read_text(encoding="utf-8-sig"))
                if child.returncode:
                    raise RuntimeError("Native read-only process failed: " + label)
                validate_report(row["report"], fixture["characters"], fixture["bodySha256"], commit, expected_os)
                if sha256(Path(fixture["path"])) != fixture["inputSha256"]:
                    raise ValueError("Fixture changed during native editing attempts")
                row["validated"] = True
            except Exception as error:
                row["error"] = str(error)
                raise
            finally:
                (output / "runs.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
            print(label + ": 24 editing attempts and positive control verified", flush=True)
    record["validated"] = True
    (output / "runs.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    files = sorted(path for path in output.iterdir() if path.is_file())
    (output / "sha256.json").write_text(json.dumps({path.name: sha256(path) for path in files}, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--inputs", type=Path, nargs="+", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--rounds", type=int, default=3)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--os", choices=tuple(OS_PREFIXES), default="windows")
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9a-f]{40}", args.commit):
        parser.error("--commit requires a complete lowercase SHA-1")
    collect(args.app.resolve(), [path.resolve() for path in args.inputs], args.output.resolve(), args.rounds, args.commit, args.os)


if __name__ == "__main__":
    main()
