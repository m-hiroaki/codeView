"""Compare native GUI proxies for Sticky Scroll on one host with warm source files."""
import argparse
import json
import os
from pathlib import Path
import statistics
import subprocess

from measure_gui_startup import sha256


CASES = {"empty": None, "normal": 64 * 1024, "eligible": 1024 * 1024, "large": 50 * 1024 * 1024}
MODES = ("baseline", "off", "on")


def make_source(path, length):
    with path.open("xb") as output:
        output.write(b"sealed class Reader\n{\n")
        counter = 0
        while True:
            method = (f"    public void Scan{counter:06d}()\n    {{\n"
                      + "        // A page of source text passes the declaration.\n" * 32
                      + "    }\n\n").encode()
            if output.tell() + len(method) + 2 > length:
                break
            output.write(method)
            counter += 1
        output.write(b"}\n")
        output.write(b" " * (length - output.tell()))
    return sha256(path)


def collect(baseline, candidate, output, baseline_commit, commit, rounds):
    output.mkdir(parents=True)
    sources = {}
    for case, size in CASES.items():
        if size is not None:
            path = output / f"{case}.cs"
            sources[case] = (path, make_source(path, size))
    baseline_settings = baseline.parent / "codeview.settings.json"
    try:
        with baseline_settings.open("x", encoding="utf-8") as settings:
            settings.write("{}")
    except FileExistsError:
        if baseline_settings.read_text(encoding="utf-8") != "{}":
            raise ValueError("Use a baseline build with no user or portable settings") from None
    record = {"baseline": baseline_commit, "candidate": commit,
              "sources": {name: digest for name, (_, digest) in sources.items()}, "runs": []}
    for index in range(rounds):
        order = MODES[index % len(MODES):] + MODES[:index % len(MODES)]
        for case in CASES:
            for mode in order:
                label = f"{case}-{mode}-{index + 1}"
                destination = output / (label + ".json")
                env = {key: value for key, value in os.environ.items()
                       if not key.startswith("CODEVIEW_GUI_") and key != "CODEVIEW_CHROME_CAPTURE"}
                env.update(CODEVIEW_GUI_PERF_REPORT=str(destination), CODEVIEW_GUI_STICKY_MEASURE="1",
                           CODEVIEW_PERF_CACHE="Warm; source hash checked immediately before launch")
                if mode != "baseline":
                    Path(str(destination) + ".portable-settings.json").write_text(
                        json.dumps({"stickyScrollEnabled": mode == "on"}), encoding="utf-8")
                args = [str(baseline if mode == "baseline" else candidate)]
                if case in sources:
                    source, digest = sources[case]
                    if sha256(source) != digest:
                        raise RuntimeError("Fixture changed: " + case)
                    args.append(str(source))
                with (output / (label + ".log")).open("xb") as log:
                    subprocess.run(args, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=60, check=True)
                result = json.loads(destination.read_text(encoding="utf-8-sig"))
                expected_commit = baseline_commit if mode == "baseline" else commit
                if (result["Failure"] or result["InputInterrupted"]
                        or not result["ProductInformationalVersion"].endswith("+" + expected_commit)
                        or result["DocumentChars"] not in ((None, 0) if case == "empty" else (CASES[case],))
                        or len(result["AnimationTickIntervalsMilliseconds"]) != (0 if case == "empty" else 64)):
                    raise RuntimeError("Invalid native report: " + label)
                if mode != "baseline":
                    sticky = result["StickyScroll"]
                    should_scan = mode == "on" and case in ("normal", "eligible")
                    if (sticky["Enabled"] != (mode == "on") or sticky["RangesFound"] != should_scan
                            or (sticky["CompletedMilliseconds"] is not None) != should_scan
                            or (sticky["StartedMilliseconds"] is not None) != should_scan
                            or len(sticky["FramePhases"]) != (0 if case == "empty" else 64)):
                        raise RuntimeError("Unexpected Sticky Scroll execution: " + label)
                record["runs"].append({"mode": mode, "case": case, "file": destination.name, "report": result})
                print(label + " verified", flush=True)
    record["summary"] = summarize(record["runs"])
    (output / "comparison.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
    print(json.dumps(record["summary"], indent=2))


def summarize(runs):
    summary = {}
    for case in CASES:
        summary[case] = {}
        for mode in MODES:
            runs_by_case = [run["report"] for run in runs if run["case"] == case and run["mode"] == mode]
            row = summary[case][mode] = {}
            for key in ("OpenedMilliseconds", "WindowTwoTicksMilliseconds", "DocumentTwoTicksMilliseconds",
                        "CpuMilliseconds", "SampledPeakMemory"):
                values = [run[key] for run in runs_by_case if run[key] is not None]
                if values:
                    row[key] = [statistics.median(values), max(values)]
            intervals = [tick for run in runs_by_case for tick in run["AnimationTickIntervalsMilliseconds"]]
            if intervals:
                row["ScrollMilliseconds"] = [statistics.median(intervals), max(intervals)]
            for phase in ("before-analysis", "analysis", "ready"):
                values = [tick for run in runs_by_case if run.get("StickyScroll")
                          for tick, tag in zip(run["AnimationTickIntervalsMilliseconds"],
                                               run["StickyScroll"]["FramePhases"]) if tag == phase]
                if values:
                    row[phase] = [len(values), statistics.median(values), max(values)]
            latencies = [run["StickyScroll"]["CompletedMilliseconds"] - run["StickyScroll"]["StartedMilliseconds"]
                         for run in runs_by_case if run.get("StickyScroll")
                         and run["StickyScroll"]["CompletedMilliseconds"] is not None]
            if latencies:
                row["AnalysisMilliseconds"] = [statistics.median(latencies), max(latencies)]
    return summary


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-app", type=Path, required=True)
    parser.add_argument("--candidate-app", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--baseline-commit", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--rounds", type=int, default=3)
    args = parser.parse_args()
    if not 1 <= args.rounds <= 10:
        parser.error("rounds must be 1..10")
    collect(args.baseline_app.resolve(), args.candidate_app.resolve(), args.output.resolve(),
            args.baseline_commit, args.commit, args.rounds)
