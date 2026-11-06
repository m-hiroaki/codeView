"""Same-host, alternating startup comparison and CI-only first-search UI checks."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import statistics
import subprocess

from measure_gui_startup import collect, nonnegative, sha256

FLAGS = ("InitiallyAbsent", "InitialBindingAndFocus", "LateFocusCancelled", "TwoWayBinding", "InvalidRegexReported",
         "ResultTabReused", "ResultSelectionAndFocus", "ControlsReused", "ClearWorked", "EmptyQueryDisabled", "BodyUnchanged")


def validate_search(report, commit, expected_os, body_hash):
    prefix = {"windows": "Microsoft Windows", "macos": "macOS"}[expected_os]
    if (report["Failure"] is not None or report["InputInterrupted"]
            or report["Scenario"] != "native-gui-search-panel-controls"
            or report["DisplayMode"] != "full-document-candidate"
            or not report["ProductInformationalVersion"].endswith("+" + commit)
            or not report["Os"].startswith(prefix) or report["EditorReadOnly"] is not True):
        raise ValueError("Unexpected/failed search-panel GUI report")
    result = report["SearchPanelChecks"]
    if (result["Passed"] is not True or not all(result[key] is True for key in FLAGS)
            or result["CommittedMatches"] != 1 or result["RepeatedShows"] != 3 or result["BodySha256"] != body_hash):
        raise ValueError("Search panel bindings/state/navigation/body invariants failed")
    synchronous = result["FirstShowSynchronousMilliseconds"]
    ticks = result["FirstShowTwoTicksMilliseconds"]
    if not nonnegative(synchronous) or not nonnegative(ticks) or ticks < synchronous:
        raise ValueError("Invalid first-show timing")


def search_checks(app, source, output, commit, expected_os, rounds):
    if os.environ.get("GITHUB_ACTIONS") != "true":
        raise RuntimeError("Run scripted UI only in a disposable CI session")
    if not 3 <= rounds <= 10:
        raise ValueError("Functional rounds must be 3..10")
    text = source.read_bytes().decode("utf-8-sig", errors="strict")
    if text.count("CODEVIEW_FIND") != 1:
        raise ValueError("Use a generated single-match functional fixture")
    original_hash = sha256(source)
    body_hash = hashlib.sha256(text.encode("utf-16-le")).hexdigest()
    output.mkdir(parents=True, exist_ok=False)
    record = {"runs": [], "inputSha256": original_hash, "bodySha256": body_hash, "commit": commit}
    for index in range(rounds):
        label = f"search-{index + 1:02d}"
        target = output / (label + ".json")
        env = {k: v for k, v in os.environ.items() if not k.startswith("CODEVIEW_GUI_")}
        env.update(CODEVIEW_GUI_SEARCH_PANEL_CHECKS="1", CODEVIEW_GUI_PERF_REPORT=str(target))
        row = {"number": index + 1}
        record["runs"].append(row)
        try:
            if sha256(source) != original_hash:
                raise ValueError("Fixture changed before search checks")
            with (output / (label + ".log")).open("wb") as log:
                child = subprocess.run([str(app), str(source)], env=env, stdout=log, stderr=subprocess.STDOUT, timeout=60)
            row["exitCode"] = child.returncode
            if target.exists():
                row["report"] = json.loads(target.read_text(encoding="utf-8-sig"))
            if child.returncode:
                raise RuntimeError("Search-panel native process failed")
            validate_search(row["report"], commit, expected_os, body_hash)
            if sha256(source) != original_hash:
                raise ValueError("Fixture changed during search checks")
            row["validated"] = True
        except Exception as error:
            row["error"] = str(error)
            raise
        finally:
            (output / "runs.json").write_text(json.dumps(record, indent=2) + "\n")
    record["validated"] = True
    record["synchronousBudgetPassed"] = all(r["report"]["SearchPanelChecks"]["FirstShowSynchronousMilliseconds"] <= 100
                                            for r in record["runs"])
    (output / "runs.json").write_text(json.dumps(record, indent=2) + "\n")
    return record


def summarize(rows):
    result = {}
    for scenario in ("empty", "file"):
        result[scenario] = {}
        for metric in ("OpenedMilliseconds", "CpuMilliseconds", "SampledPeakMemory"):
            values = [r["report"][metric] for r in rows if r["scenario"] == scenario]
            if not values or not all(nonnegative(v) and v > 0 for v in values):
                raise ValueError("Missing/invalid comparison metric")
            result[scenario][metric] = {"median": statistics.median(values), "max": max(values)}
    return result


def assess(baseline, candidate, search):
    # Fixed before measurement: >=5% startup reduction in both scenarios,
    # <=5% median CPU/memory increase, all first-show synchronous samples <=100ms.
    checks = {"firstSearchWithin100ms": search["synchronousBudgetPassed"]}
    for scenario in ("empty", "file"):
        checks[scenario + "StartupImproved5Percent"] = candidate[scenario]["OpenedMilliseconds"]["median"] <= baseline[scenario]["OpenedMilliseconds"]["median"] * .95
        for metric in ("CpuMilliseconds", "SampledPeakMemory"):
            checks[scenario + metric + "Within5Percent"] = candidate[scenario][metric]["median"] <= baseline[scenario][metric]["median"] * 1.05
    return checks


def compare(baseline_app, candidate_app, source, output, baseline_commit, commit, expected_os, rounds=10, functional_rounds=3):
    if os.environ.get("GITHUB_ACTIONS") != "true":
        raise RuntimeError("Run comparison only in a disposable CI session")
    if not 10 <= rounds <= 30 or not 3 <= functional_rounds <= 10 or expected_os not in ("windows", "macos"):
        raise ValueError("Use 10..30 comparison rounds, 3..10 functional rounds, windows/macos")
    if not all(path.is_file() for path in (baseline_app, candidate_app, source)):
        raise FileNotFoundError("Missing comparison application/input")
    output.mkdir(parents=True, exist_ok=False)
    apps = {"baseline": (baseline_app, baseline_commit), "candidate": (candidate_app, commit)}
    record = {"commit": commit, "baselineCommit": baseline_commit, "os": expected_os,
              "rounds": rounds, "order": [], "baseline": [], "candidate": [], "acquired": False}
    try:
        for number in range(1, rounds + 1):
            for variant in (("baseline", "candidate") if number % 2 else ("candidate", "baseline")):
                app, sha = apps[variant]
                label = f"{variant}-{number:02d}"
                record["order"].append(label)
                collect(app, source, output / label, 1, sha, expected_os)
                evidence = json.loads((output / label / "runs.json").read_text())
                record[variant].extend(evidence["runs"])
        search = search_checks(candidate_app, source, output / "search", commit, expected_os, functional_rounds)
        summary = {key: summarize(record[key]) for key in apps}
        record["summary"] = summary
        record["checks"] = assess(summary["baseline"], summary["candidate"], search)
        record["acquired"] = True
        record["candidateAccepted"] = all(record["checks"].values())
        return record
    except Exception as error:
        record["error"] = str(error)
        raise
    finally:
        (output / "comparison.json").write_text(json.dumps(record, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("baseline-app", "candidate-app", "input", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    for name in ("baseline-commit", "commit"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--os", choices=("windows", "macos"), required=True)
    parser.add_argument("--rounds", type=int, default=10)
    parser.add_argument("--functional-rounds", type=int, default=3)
    parser.add_argument("--functional-only", action="store_true")
    args = parser.parse_args()
    if not all(re.fullmatch(r"[0-9a-f]{40}", sha) for sha in (args.baseline_commit, args.commit)):
        parser.error("Use complete lowercase commits")
    if args.functional_only:
        search_checks(args.candidate_app.resolve(), args.input.resolve(), args.output.resolve(), args.commit, args.os, args.functional_rounds)
        raise SystemExit(0)
    result = compare(args.baseline_app.resolve(), args.candidate_app.resolve(), args.input.resolve(), args.output.resolve(),
                     args.baseline_commit, args.commit, args.os, args.rounds, args.functional_rounds)
    raise SystemExit(0 if result["candidateAccepted"] else 1)


if __name__ == '__main__':
    main()
