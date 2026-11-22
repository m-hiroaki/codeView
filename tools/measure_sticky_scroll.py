"""Compare native GUI proxies for Sticky Scroll on one host with warm source files."""
import argparse
import json
import os
from pathlib import Path
import statistics
import subprocess

from measure_gui_startup import sha256


CSHARP_CASES = {
    "empty": (None, None, False),
    "normal": ("normal.cs", 64 * 1024, True),
    "eligible": ("eligible.cs", 1024 * 1024, True),
    "large": ("large.cs", 50 * 1024 * 1024, False),
}
CPP_CASES = {
    "empty": (None, None, False),
    "c": ("functions.c", 64 * 1024, True),
    "cpp": ("members.cpp", 64 * 1024, True),
    "header": ("reader.hpp", 64 * 1024, True),
    "eligible": ("eligible.cpp", 1024 * 1024, True),
    "large": ("large.cpp", 50 * 1024 * 1024, False),
    "large-c": ("large.c", 50 * 1024 * 1024, False),
    "large-header": ("large.hpp", 50 * 1024 * 1024, False),
}
MODES = ("baseline", "off", "on")


def make_source(path, length):
    if path.suffix == ".c":
        opening, closing, declaration = "", "", "unsigned scan{number:06d}(unsigned value)"
    elif path.suffix in (".cpp", ".hpp"):
        opening, closing, declaration = "struct Reader\n{\n", "};\n", "    unsigned scan{number:06d}(unsigned value)"
        if path.suffix == ".hpp":
            opening = "#ifndef READER_FIXTURE_HPP\n#define READER_FIXTURE_HPP\n" + opening
            closing += "#endif\n"
    else:
        opening, closing, declaration = "sealed class Reader\n{\n", "}\n", "    public void Scan{number:06d}()"
    indentation = "    " if path.suffix != ".c" else ""
    body = indentation + "    // A page of source text passes the declaration.\n"
    if path.suffix != ".cs":
        body = body * 32 + indentation + "    return value + 1;\n"
    else:
        body *= 32
    closing_bytes = closing.encode()
    with path.open("xb") as output:
        output.write(opening.encode())
        counter = 0
        while True:
            method = (declaration.format(number=counter) + "\n" + indentation + "{\n"
                      + body + indentation + "}\n\n").encode()
            if output.tell() + len(method) + len(closing_bytes) > length:
                break
            output.write(method)
            counter += 1
        output.write(closing_bytes)
        output.write(b" " * (length - output.tell()))
    return sha256(path)


def collect(baseline, candidate, output, baseline_commit, commit, rounds, cases=CSHARP_CASES):
    output.mkdir(parents=True)
    sources = {}
    for case, (filename, size, _) in cases.items():
        if filename is not None:
            path = output / filename
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
        for case, (_, size, eligible) in cases.items():
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
                        or result["DocumentChars"] not in ((None, 0) if size is None else (size,))
                        or len(result["AnimationTickIntervalsMilliseconds"]) != (0 if case == "empty" else 64)):
                    raise RuntimeError("Invalid native report: " + label)
                if mode != "baseline":
                    sticky = result["StickyScroll"]
                    should_scan = mode == "on" and eligible
                    if (sticky["Enabled"] != (mode == "on") or sticky["RangesFound"] != should_scan
                            or (sticky["CompletedMilliseconds"] is not None) != should_scan
                            or (sticky["StartedMilliseconds"] is not None) != should_scan
                            or len(sticky["FramePhases"]) != (0 if case == "empty" else 64)):
                        raise RuntimeError("Unexpected Sticky Scroll execution: " + label)
                record["runs"].append({"mode": mode, "case": case, "file": destination.name, "report": result})
                print(label + " verified", flush=True)
    record["summary"] = summarize(record["runs"], cases)
    (output / "comparison.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
    print(json.dumps(record["summary"], indent=2))


def summarize(runs, cases=CSHARP_CASES):
    summary = {}
    for case in cases:
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
    parser.add_argument("--cpp", action="store_true", help="Measure C/C++ source and header cases")
    args = parser.parse_args()
    if not 1 <= args.rounds <= 10:
        parser.error("rounds must be 1..10")
    collect(args.baseline_app.resolve(), args.candidate_app.resolve(), args.output.resolve(),
            args.baseline_commit, args.commit, args.rounds, CPP_CASES if args.cpp else CSHARP_CASES)
