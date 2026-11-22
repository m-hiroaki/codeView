"""Measure manual C/C++ tag creation, reuse and update in the native GUI."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess
import tempfile


CASES = {"moderate": (200, 8 * 1024**2), "l1": (50_000, 1024**3)}


def make_fixture(root, count, size):
    caller = root / "caller.cpp"
    caller.write_bytes(b"int caller() { return IndexProbeTarget(); }\n" + b"// scroll\n" * 300)
    target = root / "target.cpp"
    target.write_bytes(b"int IndexProbeTarget() { return 42; }\n")
    width, extra = divmod(size - caller.stat().st_size - target.stat().st_size, count - 2)
    for index in range(count - 2):
        directory = root / f"part-{index // 200:03d}"
        directory.mkdir(exist_ok=True)
        spelling = f"int generated_{index:05}() {{ return 1; }}\n".encode()
        length = width + (index < extra) - len(spelling)
        line = b"// source data " + b"x" * 496 + b"\n"
        full, tail = divmod(length, len(line))
        path = directory / f"source-{index:05}.cpp"
        path.write_bytes(spelling + line * full + b" " * tail)
    return caller, target


def read_cache(cache):
    files = list(cache.glob("*/definitions.jsonl"))
    if len(files) > 1:
        raise RuntimeError("Unexpected additional tag cache")
    if not files:
        return 0, None
    data = files[0].read_bytes()
    footer = json.loads(data.splitlines()[-1])
    return len(data), (hashlib.sha256(data).hexdigest(), footer)


def collect(app, output, commit, rounds, names):
    output.mkdir(parents=True)
    result = {"commit": commit, "cases": {}, "runs": []}
    for case in names:
        count, size = CASES[case]
        result["cases"][case] = {"files": count, "bytes": size}
        for round_number in range(1, rounds + 1):
            with tempfile.TemporaryDirectory(dir=output) as workspace:
                work = Path(workspace)
                root = work / "source"
                root.mkdir()
                caller, target = make_fixture(root, count, size)
                cache = work / "tag-cache"
                initial_target = target.read_bytes()
                previous = None
                for mode in ("simple", "create", "reopen", "update"):
                    if mode == "update":
                        changed = root / "part-000" / "source-00000.cpp"
                        changed.write_bytes(changed.read_bytes().replace(b"return 1;", b"return 2;", 1))
                    label = f"{case}-{round_number}-{mode}"
                    report = output / (label + ".json")
                    env = {key: value for key, value in os.environ.items() if not key.startswith("CODEVIEW_GUI_")}
                    env.update(GITHUB_ACTIONS="true", CODEVIEW_GUI_PERF_REPORT=str(report),
                               CODEVIEW_GUI_REFERENCE_CHECKS="1", CODEVIEW_GUI_CPP_TAG_MEASURE=mode,
                               CODEVIEW_GUI_CPP_TAG_CACHE=str(cache), CODEVIEW_PERF_CACHE="Repeated local fixture")
                    with (output / (label + ".log")).open("xb") as log:
                        subprocess.run([str(app), str(root), str(caller)], env=env, stdout=log,
                                       stderr=subprocess.STDOUT, timeout=180, check=True)
                    data = json.loads(report.read_text(encoding="utf-8-sig"))
                    measurements = data["ReferenceChecks"]
                    if (data["Failure"] or data["InputInterrupted"]
                            or not data["ProductInformationalVersion"].endswith("+" + commit)
                            or not measurements["Passed"] or measurements["Mode"] != mode
                            or len(data["AnimationTickIntervalsMilliseconds"]) != 64
                            or Path(measurements["Target"]) != target or target.read_bytes() != initial_target):
                        raise RuntimeError("Invalid GUI report: " + label)
                    cache_bytes, snapshot = read_cache(cache)
                    if mode == "simple":
                        if snapshot is not None:
                            raise RuntimeError("A search wrote a cache: " + label)
                    elif (snapshot is None or not snapshot[1]["complete"] or snapshot[1]["files"] != count
                          or snapshot[1]["definitions"] != count
                          or (mode == "reopen" and snapshot != previous)
                          or (mode == "update" and snapshot == previous)):
                        raise RuntimeError("Unexpected cache: " + label)
                    previous = snapshot
                    result["runs"].append({"case": case, "round": round_number, "mode": mode,
                                           "cacheBytes": cache_bytes, "report": data})
                    print(label + " verified", flush=True)
    (output / "comparison.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    for run in result["runs"]:
        report = run["report"]
        measurements = report["ReferenceChecks"]
        print(run["case"], run["mode"], "F12 ms", measurements["F12Milliseconds"],
              "scroll median ms", round(statistics.median(report["AnimationTickIntervalsMilliseconds"]), 2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--rounds", type=int, choices=range(1, 4), default=1)
    parser.add_argument("--case", action="append", choices=CASES)
    args = parser.parse_args()
    collect(args.app.resolve(), args.output.resolve(), args.commit, args.rounds, args.case or CASES)
