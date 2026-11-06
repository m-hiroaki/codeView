"""Opt-in EventPipe acquisition check; hosted CI is not a product performance baseline."""
import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import time


ORDER = [("baseline", 1), ("gc-collect", 1), ("gc-verbose", 1), ("counters", 1),
         ("baseline", 2), ("gc-verbose", 2), ("baseline", 3), ("gc-verbose", 3)]
ROUNDS = 10


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def command_for(mode, host, tools, output, label, windows):
    command = ["dotnet", str(host), label, str(ROUNDS)]
    suffix = ".exe" if windows else ""
    if mode in ("gc-collect", "gc-verbose"):
        return [str(tools / ("dotnet-trace" + suffix)), "collect", "--profile", mode,
                "--buffersize", "32", "--providers", "codeView-Diagnostics:0xFFFFFFFFFFFFFFFF:4",
                "--show-child-io", "--output", str(output / (label + ".nettrace")), "--", *command]
    if mode == "counters":
        return [str(tools / ("dotnet-counters" + suffix)), "collect", "--counters", "System.Runtime",
                "--refresh-interval", "1", "--format", "csv", "--output",
                str(output / (label + ".csv")), "--", *command]
    if mode != "baseline":
        raise ValueError("Unknown diagnostic mode")
    return command


def validate_trace(trace, label):
    for key in ("EventsLost", "OverlappingSuspendStarts", "OrphanRestarts"):
        if trace[key] != 0:
            raise ValueError("Incomplete trace: " + key)
    if trace["UnmatchedFinalSuspendStart"] is not None:
        raise ValueError("Unmatched final GC suspension")
    if not trace["GcStartsByDepth"] or trace["HeapStatsEvents"] <= 0:
        raise ValueError("Missing GC/heap events")
    failures = [marker for marker in trace["Markers"] if marker["EventName"] == "BudgetFailed"]
    markers = [marker for marker in trace["Markers"] if marker["EventName"] != "BudgetFailed"]
    if len(markers) != 2 * ROUNDS:
        raise ValueError("Missing or extra operation markers")
    previous = -1
    for index, marker in enumerate(markers):
        payload = marker["Payload"]
        if (marker["EventName"] != ("Round/Start" if index % 2 == 0 else "Round/Stop")
                or payload["label"] != label or payload["round"] != str(index // 2)
                or marker["TimeStampRelativeMSec"] < previous):
            raise ValueError("Invalid operation marker pairing")
        previous = marker["TimeStampRelativeMSec"]
    failed_rounds = set()
    for marker in failures:
        payload = marker["Payload"]
        number = int(payload["round"])
        if (payload["label"] != label or number not in range(ROUNDS) or number in failed_rounds
                or not markers[number * 2]["TimeStampRelativeMSec"] <= marker["TimeStampRelativeMSec"] <= markers[number * 2 + 1]["TimeStampRelativeMSec"]):
            raise ValueError("Invalid assertion-failure marker")
        failed_rounds.add(number)
    for pause in trace["SuspensionIntervals"]:
        if pause["DurationMilliseconds"] < 0 or pause["EndMilliseconds"] < pause["StartMilliseconds"]:
            raise ValueError("Negative GC suspension")
    return len(failures)


def validate_reports(reports, input_bytes, commit):
    if len(reports) != ROUNDS:
        raise ValueError("Incomplete core operation reports")
    for report in reports:
        if (report["InputBytes"] != input_bytes or report["Copies"] != 1
                or not report["ProductInformationalVersion"].endswith("+" + commit)
                or not report["CacheCondition"].startswith("Warm;")
                or len(report["BuildMilliseconds"]) != 1):
            raise ValueError("Core report differs from requested build/input/conditions")


def validate_child(exit_code, reports, input_bytes, commit, summary, label):
    # dotnet-counters may return 0 even when its child returns 1. Require the host's
    # completion record as well as the tool exit; do not infer core success from the tool.
    if exit_code not in (0, 1):
        raise RuntimeError("Diagnostic child did not reach its normal/assertion exit")
    validate_reports(reports, input_bytes, commit)
    failures = summary["AssertionFailures"]
    if (summary["Label"] != label or summary["Rounds"] != ROUNDS or type(failures) is not int
            or not 0 <= failures <= ROUNDS):
        raise ValueError("Invalid host completion record")
    if exit_code == 1 and failures == 0:
        raise RuntimeError("Tool failed despite successful host completion")
    return failures == 0


def counter_rows(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if reader.fieldnames != ["Timestamp", "Provider", "Counter Name", "Counter Type", "Mean/Increment"]:
            raise ValueError("Unexpected counter CSV schema")
        rows = list(reader)
    if not rows or not all(row["Provider"] == "System.Runtime" for row in rows):
        raise ValueError("Missing runtime counter rows")
    names = {row["Counter Name"] for row in rows}
    if not any(name.startswith("dotnet.gc.") for name in names):
        raise ValueError("Missing GC counters")
    return len(rows)


def collect(input_file, host, reader, tools, output, commit):
    if not output.name or not all(c.isascii() and (c.isalnum() or c == "-") for c in output.name):
        raise ValueError("Output name must be ASCII letters/digits/hyphens")
    if input_file.stat().st_size != 50 * 1024 * 1024:
        raise ValueError("This acquisition check requires the 50 MiB UTF-8 fixture")
    for name in (host, reader, tools / ("dotnet-trace.exe" if os.name == "nt" else "dotnet-trace"),
                 tools / ("dotnet-counters.exe" if os.name == "nt" else "dotnet-counters")):
        if not name.is_file():
            raise FileNotFoundError(name)
    labels = [f"{output.name}-{mode}-{number}" for mode, number in ORDER]
    for label in labels:
        if (host.parent / "TestResults" / (label + "-host.json")).exists():
            raise FileExistsError("Use new output name; preserve previous host result")
        for index in range(ROUNDS):
            if (host.parent / "TestResults" / f"{label}-{index}-1.json").exists():
                raise FileExistsError("Use new output name; preserve previous reports")
    output.mkdir(parents=True, exist_ok=False)
    initial_hash = digest(input_file)
    record = {"formatVersion": 1, "commit": commit, "inputSha256": initial_hash,
              "inputBytes": input_file.stat().st_size, "platform": platform.platform(),
              "hostSha256": digest(host), "readerSha256": digest(reader),
              "productSha256": digest(host.parent / "codeView.dll"), "runs": [],
              "limits": "Whole child process; warm core operations only. Hosted runner is not a reference PC. No UI/physical I/O/allocation stacks."}
    env = {**os.environ, "CODEVIEW_PERF_FILE": str(input_file), "CODEVIEW_PERF_COPIES": "1"}
    for (mode, number), label in zip(ORDER, labels):
        command = command_for(mode, host, tools, output, label, os.name == "nt")
        run = {"mode": mode, "number": number, "label": label, "command": command}
        record["runs"].append(run)
        started = time.perf_counter()
        try:
            with (output / (label + ".log")).open("w", encoding="utf-8") as log:
                child = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=180)
            run["exitCode"] = child.returncode
            run["wholeCommandSeconds"] = time.perf_counter() - started
            reports = []
            for index in range(ROUNDS):
                path = host.parent / "TestResults" / f"{label}-{index}-1.json"
                if path.exists():
                    reports.append(json.loads(path.read_text(encoding="utf-8-sig")))
            run["reports"] = reports
            run["hostSummary"] = json.loads((host.parent / "TestResults" / (label + "-host.json")).read_text(encoding="utf-8-sig"))
            run["coreAssertionsPassed"] = validate_child(child.returncode, reports, record["inputBytes"], commit, run["hostSummary"], label)
            run["reportedHostExitCode"] = 0 if run["coreAssertionsPassed"] else 1
            if mode in ("gc-collect", "gc-verbose"):
                analysis = output / (label + "-trace.json")
                parsed = subprocess.run(["dotnet", str(reader), str(output / (label + ".nettrace")), str(analysis)],
                                        capture_output=True, text=True, timeout=60)
                run["analysisExitCode"] = parsed.returncode
                run["analysisError"] = parsed.stderr
                if analysis.exists():
                    run["trace"] = json.loads(analysis.read_text(encoding="utf-8-sig"))
                if parsed.returncode:
                    raise RuntimeError("Trace parser rejected recording")
                failures = validate_trace(run["trace"], label)
                run["assertionFailureMarkers"] = failures
                if failures != run["hostSummary"]["AssertionFailures"]:
                    raise ValueError("Child exit differs from recorded assertion markers")
                if mode == "gc-verbose" and not run["trace"]["AllocationSamples"]:
                    raise ValueError("Missing verbose allocation samples")
            elif mode == "counters":
                run["counterRows"] = counter_rows(output / (label + ".csv"))
            if digest(input_file) != initial_hash:
                raise ValueError("Input changed during acquisition")
            run["acquisitionValidated"] = True
        except Exception as error:
            run["error"] = str(error)
            raise
        finally:
            (output / "runs.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
        print(f"{label}: 10 reports acquired, child exit {run['exitCode']}, core assertions passed={run['coreAssertionsPassed']}", flush=True)
    record["acquisitionValidated"] = True
    record["coreAssertionsPassed"] = all(run["coreAssertionsPassed"] for run in record["runs"])
    (output / "runs.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    files = sorted(path for path in output.iterdir() if path.is_file())
    (output / "sha256.json").write_text(json.dumps({path.name: digest(path) for path in files}, indent=2) + "\n", encoding="utf-8")
    if not record["coreAssertionsPassed"]:
        raise RuntimeError("Acquisition completed; core assertions failed. Inspect reports/logs; budgets unchanged.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("input", "host", "reader", "tools", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--commit", required=True)
    args = parser.parse_args()
    if len(args.commit) != 40 or any(c not in "0123456789abcdef" for c in args.commit):
        parser.error("--commit requires a full lowercase SHA-1")
    collect(args.input.resolve(), args.host.resolve(), args.reader.resolve(), args.tools.resolve(),
            args.output.resolve(), args.commit)


if __name__ == "__main__":
    main()
