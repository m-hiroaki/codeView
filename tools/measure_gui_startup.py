"""Native-window startup acquisition; animation callbacks are not physical paint/input."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import time

STARTUP_STAGES = (
    "main-entered", "builder-configured", "app-initialize-start", "app-initialize-end",
    "framework-start", "window-constructor-start", "window-xaml-loaded",
    "probe-attach-start", "probe-attach-end", "window-constructor-end", "framework-end", "window-opened")


def sha256(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def expected_text(path):
    data = path.read_bytes()
    if len(data) > 1024 * 1024:
        raise ValueError("Use a small startup fixture, at most 1 MiB")
    text = data.decode("utf-8-sig", errors="strict")
    return len(text.encode("utf-16-le")) // 2, 1 + len(re.findall(r"\r\n|\r|\n", text))


def nonnegative(value):
    return type(value) in (int, float) and math.isfinite(value) and value >= 0


def validate_report(report, scenario, characters, lines, commit, expected_os):
    if scenario not in ("empty", "file"):
        raise ValueError("Unknown startup scenario")
    if (report["Failure"] is not None or report["InputInterrupted"]
            or report["Scenario"] != "native-gui-scroll-proxy"
            or report["DisplayMode"] != "full-document-candidate"
            or report["EditorReadOnly"] is not True or report["TextDragDropEnabled"] is not False
            or report["UndoCanUndo"] not in (None, False) or report["UndoCanRedo"] not in (None, False)
            or not report["ProductInformationalVersion"].endswith("+" + commit)):
        raise ValueError("GUI probe failed, was interrupted, or used a different build/mode")
    os_prefix = {"windows": "Microsoft Windows", "macos": "macOS"}[expected_os]
    if not report["Os"].startswith(os_prefix):
        raise ValueError("Unexpected native OS")
    for name in ("OpenedMilliseconds", "WindowTwoTicksMilliseconds", "ClientWidth", "ClientHeight", "RenderScaling", "CpuMilliseconds"):
        if not nonnegative(report[name]):
            raise ValueError("Missing or invalid GUI metric: " + name)
    if min(report[name] for name in ("ClientWidth", "ClientHeight", "RenderScaling")) <= 0:
        raise ValueError("Native window has no client area/scaling")
    if report["WindowTwoTicksMilliseconds"] < report["OpenedMilliseconds"]:
        raise ValueError("Animation callbacks precede Opened")
    stages = report.get("StartupStages")
    if not isinstance(stages, list) or len(stages) != len(STARTUP_STAGES):
        raise ValueError("Incomplete startup timeline")
    previous = 0
    for expected, sample in zip(STARTUP_STAGES, stages):
        if (not isinstance(sample, dict) or sample.get("Stage") != expected
                or not nonnegative(sample.get("Milliseconds")) or sample["Milliseconds"] < previous):
            raise ValueError("Invalid startup checkpoint/order")
        previous = sample["Milliseconds"]
    if stages[0]["Milliseconds"] != 0 or previous != report["OpenedMilliseconds"]:
        raise ValueError("Startup timeline does not span Main to Opened")
    scrolls = report["ScrollSynchronousMilliseconds"]
    ticks = report["AnimationTickIntervalsMilliseconds"]
    if scenario == "file":
        if (report["DocumentChars"] != characters or report["DocumentLines"] != lines
                or not nonnegative(report["DocumentTwoTicksMilliseconds"])
                or report["DocumentTwoTicksMilliseconds"] < report["OpenedMilliseconds"]
                or not nonnegative(report["DocumentAttachmentMilliseconds"])
                or report["UndoCanUndo"] is not False or report["UndoCanRedo"] is not False
                or len(scrolls) != 64 or len(ticks) != 64):
            raise ValueError("File was not attached/animated/scrolled completely")
    elif (report["DocumentChars"] not in (None, 0) or report["DocumentTwoTicksMilliseconds"] is not None
          or scrolls or ticks):
        raise ValueError("Empty launch unexpectedly has a document/workload")
    if not all(nonnegative(x) for x in [*scrolls, *ticks]):
        raise ValueError("Invalid scroll/frame interval")


def collect(app, input_file, output, rounds, commit, expected_os):
    if not 1 <= rounds <= 100:
        raise ValueError("Rounds must be 1..100 per scenario")
    if not app.is_file():
        raise FileNotFoundError(app)
    characters, lines = expected_text(input_file)
    initial_hash = sha256(input_file)
    output.mkdir(parents=True, exist_ok=False)
    record = {"formatVersion": 1, "commit": commit, "roundsPerScenario": rounds,
              "application": str(app), "appHostSha256": sha256(app),
              "productSha256": sha256(app.parent / "codeView.dll"),
              "inputSha256": initial_hash, "expectedUtf16Characters": characters,
              "expectedLines": lines, "runs": [],
              "limits": "Probe build, native window only; Main-entry-relative callbacks are not OS launch-to-physical-paint or physical-input acceptance. Whole process includes scroll and shutdown."}
    for number in range(1, rounds + 1):
        for scenario in ("empty", "file"):
            label = f"{scenario}-{number:02d}"
            report_file = output / (label + ".json")
            command = [str(app)] + ([str(input_file)] if scenario == "file" else [])
            # Re-read/hash the small input before each child; do not label this as Cold.
            if sha256(input_file) != initial_hash:
                raise ValueError("Input changed before launch")
            env = {key: value for key, value in os.environ.items() if not key.startswith("CODEVIEW_GUI_")}
            env.update(CODEVIEW_GUI_PERF_REPORT=str(report_file),
                       CODEVIEW_PERF_CACHE="Warm; fixture hashed before each process start" if scenario == "file" else "not-applicable; empty launch")
            row = {"scenario": scenario, "number": number, "command": command}
            record["runs"].append(row)
            started = time.perf_counter()
            try:
                with (output / (label + ".log")).open("wb") as log:
                    result = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=60)
                row["exitCode"] = result.returncode
                row["wholeProcessSeconds"] = time.perf_counter() - started
                if result.returncode:
                    raise RuntimeError("Native process failed: " + label)
                report = json.loads(report_file.read_text(encoding="utf-8-sig"))
                row["report"] = report
                validate_report(report, scenario, characters, lines, commit, expected_os)
                if sha256(input_file) != initial_hash:
                    raise ValueError("Input changed during native GUI run")
                row["validated"] = True
                print(label + ": native Opened/animation/normal shutdown verified", flush=True)
            except Exception as error:
                row["error"] = str(error)
                raise
            finally:
                (output / "runs.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    record["validated"] = True
    (output / "runs.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    files = sorted(path for path in output.iterdir() if path.is_file())
    (output / "sha256.json").write_text(json.dumps({path.name: sha256(path) for path in files}, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("app", "input", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--rounds", type=int, default=10)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--os", choices=("windows", "macos"), required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9a-f]{40}", args.commit):
        parser.error("--commit must be a complete lowercase SHA-1")
    collect(args.app.resolve(), args.input.resolve(), args.output.resolve(), args.rounds, args.commit, args.os)


if __name__ == "__main__":
    main()
