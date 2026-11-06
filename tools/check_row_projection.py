"""Native row-projection integration acquisition; does not prove physical paint/input."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess

from measure_gui_startup import sha256

SCROLL_CHECKS = {"source-scrollbar-extents", "source-scrollbar-last-position", "scrollbar-preserves-source-selection",
                 "scrollbar-preserves-original-copy", "sub-unit-wheel-retained", "routed-trackpad-accumulation",
                 "routed-shift-wheel-horizontal", "routed-diagonal-wheel", "wheel-preserves-source-selection",
                 "routed-pointer-captured", "routed-pointer-selects-original", "routed-edge-timer", "routed-pointer-release",
                 "routed-capture-loss", "replacement-detaches-capture", "reattach-single-owner", "source-caret-rectangle-visible",
                 "stale-scrollbar-gesture-rejected", "stale-scrollbar-release-rejected", "current-scrollbar-release-retires"}


def fixture(path):
    data = path.read_bytes()
    if len(data) > 2 * 1024 * 1024:
        raise ValueError("Use functional fixtures up to 2 MiB")
    text = data.decode("utf-8-sig", errors="strict")
    raw = text.encode("utf-16-le")
    return {"path": str(path), "inputSha256": sha256(path), "bodySha256": hashlib.sha256(raw).hexdigest(),
            "characters": len(raw) // 2, "lines": 1 + len(re.findall(r"\r\n|\r|\n", text)),
            "maximumLineLength": max(len(line.encode("utf-16-le")) // 2 for line in re.split(r"\r\n|\r|\n", text))}


def validate_report(report, expected, commit, expected_os):
    if (report.get("Failure") is not None or report.get("InputInterrupted") is not False
            or report.get("RowProjectionEnabled") is not True
            or report.get("Scenario") != "native-interactive-bounded-candidate"
            or report.get("DisplayMode") != "bounded-window-prototype"
            or not report.get("ProductInformationalVersion", "").endswith("+" + commit)
            or not report.get("Os", "").startswith({"windows": "Microsoft Windows", "macos": "macOS"}[expected_os])):
        raise ValueError("Wrong build/OS/mode or interrupted/failed native run")
    checks = report.get("RowProjectionChecks") or {}
    scroll_checks = checks.get("ScrollChecks", [])
    if (set(scroll_checks) != SCROLL_CHECKS or len(scroll_checks) != len(SCROLL_CHECKS)
            or checks.get("MaximumLineLength") != expected["maximumLineLength"]):
        raise ValueError("Source scrollbar, wheel/pointer or line extent evidence incomplete")
    names = checks.get("Checks", [])
    required = {"routed-document-end", "end-caret-projected", "routed-document-start", "routed-shift-selection",
                "routed-select-all-original", "native-selection-excludes-synthetic-lf", "selection-spans-exclude-separators",
                "offscreen-caret-stays-independent", "selection-background-rendered", "next-input-reveals-source-caret",
                "stale-display-rejected", "display-source-first-row-label", "source-unchanged", "read-only-native-display",
                "clipping-marks-rendered", "source-labels-rendered"}
    required.update("viewport-preserves-source-" + str(i) for i in range(64))
    required.update("bounded-rows-" + str(i) for i in range(64))
    if expected["characters"]:
        required.add("native-clipboard-original-count")
    if (set(names) != required or len(names) != len(required)
            or checks.get("SourceSha256") != expected["bodySha256"]
            or checks.get("Utf16Characters") != expected["characters"] or checks.get("SourceLineCount") != expected["lines"]
            or report.get("SourceChars") != expected["characters"] or report.get("SourceLines") != expected["lines"]
            or checks.get("ScrollSteps") != 64 or report.get("ConfiguredWindowChars") != 16384
            or report.get("EditorReadOnly") is not True or report.get("UndoCanUndo") is not False
            or report.get("ClipboardVerificationFailures") != 0
            or report.get("ClipboardVerificationChecks") != int(bool(expected["characters"]))):
        raise ValueError("Source, checks, bounded display or original clipboard verification incomplete")
    for field in ("ScrollSynchronousMilliseconds", "AnimationTickIntervalsMilliseconds"):
        values = report.get(field, [])
        if len(values) != 64 or not all(type(x) in (int, float) and math.isfinite(x) and x >= 0 for x in values):
            raise ValueError("Incomplete/nonfinite render timing")
    samples = report.get("RowProjectionSamples", [])
    if len(samples) < 64 or not all(0 <= x["DisplayChars"] <= 16384 and 1 <= x["DisplayRows"] <= 512 for x in samples):
        raise ValueError("Unbounded/missing row samples")
    if report.get("PhysicalPaint") != "not-measured":
        raise ValueError("Unexpected physical paint claim")


def collect(app, inputs, output, rounds, commit, expected_os):
    if not 1 <= rounds <= 10 or not 1 <= len(inputs) <= 10:
        raise ValueError("Use 1..10 rounds and fixtures")
    fixtures = [fixture(path) for path in inputs]
    output.mkdir(parents=True, exist_ok=False)
    record = {"commit": commit, "fixtures": fixtures, "productSha256": sha256(app.parent / "codeView.dll"),
              "appHostSha256": sha256(app), "rounds": rounds, "runs": [],
              "limit": "Native functional integration, logical UTF-16 columns; full scrollbars, physical paint/input and performance acceptance pending."}
    try:
        for number in range(rounds):
            for index, expected in enumerate(fixtures):
                label = f"rows-{index}-{number + 1}"
                report_path = output / (label + ".json")
                env = {k: v for k, v in os.environ.items() if not k.startswith("CODEVIEW_GUI_")}
                env.update(CODEVIEW_GUI_PERF_REPORT=str(report_path), CODEVIEW_GUI_WINDOWED="1", CODEVIEW_GUI_INTERACTIVE="1",
                           CODEVIEW_GUI_ROWS="1", CODEVIEW_GUI_ROW_CHECKS="1", CODEVIEW_GUI_VERIFY_CLIPBOARD="1")
                row = {"fixture": index, "round": number + 1}
                record["runs"].append(row)
                if sha256(inputs[index]) != expected["inputSha256"]:
                    raise ValueError("Fixture changed before run")
                with (output / (label + ".log")).open("wb") as log:
                    child = subprocess.run([str(app), str(inputs[index])], env=env, stdout=log, stderr=subprocess.STDOUT, timeout=90)
                row["exitCode"] = child.returncode
                if report_path.exists():
                    row["report"] = json.loads(report_path.read_text(encoding="utf-8-sig"))
                if child.returncode != 0:
                    raise RuntimeError("Native row test failed: " + label)
                validate_report(row["report"], expected, commit, expected_os)
                if sha256(inputs[index]) != expected["inputSha256"]:
                    raise ValueError("Fixture changed during run")
                row["validated"] = True
                print(label + ": verified", flush=True)
        record["validated"] = True
    finally:
        (output / "runs.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", required=True, type=Path)
    parser.add_argument("--inputs", required=True, type=Path, nargs="+")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--rounds", type=int, default=3)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--os", required=True, choices=("windows", "macos"))
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9a-f]{40}", args.commit):
        parser.error("Use the full lowercase commit hash")
    collect(args.app.resolve(), [p.resolve() for p in args.inputs], args.output.resolve(), args.rounds, args.commit, args.os)
