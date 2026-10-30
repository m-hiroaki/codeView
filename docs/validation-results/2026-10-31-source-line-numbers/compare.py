from pathlib import Path
import json, os, subprocess

root = Path.cwd()
outputs = root / "artifacts/gui-results"
workloads = {
    "long50": root / "artifacts/fixtures/long50/source-0000000.cs",
    "text50": root / "artifacts/fixtures/text50/group-00000/source-0000000.cs",
}
executables = {
    "baseline": root / "artifacts/dev/codeView-pointer-probe.app/Contents/MacOS/codeView",
    "source-lines": root / "artifacts/dev/codeView-line-numbers-probe.app/Contents/MacOS/codeView",
}
summary = []
for workload, input_file in workloads.items():
    for variant, executable in executables.items():
        label = f"line-numbers-{workload}-{variant}-20261031"
        report = outputs / (label + ".json")
        log = outputs / (label + ".log")
        if report.exists() or log.exists():
            raise RuntimeError("output already exists: " + label)
        with input_file.open("rb") as stream:
            while stream.read(1024 * 1024):
                pass
        env = dict(os.environ)
        for key in list(env):
            if key.startswith("CODEVIEW_GUI_"):
                del env[key]
        env.update(CODEVIEW_GUI_WINDOWED="1", CODEVIEW_GUI_WINDOW_CHARS="1024",
                   CODEVIEW_GUI_PERF_REPORT=str(report), CODEVIEW_PERF_CACHE="Warm")
        print("START " + label, flush=True)
        with log.open("wb") as output:
            process = subprocess.run([str(executable), str(input_file)], env=env, stdout=output, stderr=subprocess.STDOUT)
        if process.returncode != 0:
            raise RuntimeError(f"{label} exited {process.returncode}")
        result = json.loads(report.read_text())
        assert result["Failure"] is None and not result["InputInterrupted"]
        assert len(result["ScrollSynchronousMilliseconds"]) == 64
        assert result["LargestDisplayChars"] <= 1024
        assert len(result["VisitedSourceOffsets"]) == 65
        assert "window-closed" in log.read_text()
        row = dict(label=label, exitCode=process.returncode,
                   attachmentMs=result["DocumentAttachmentMilliseconds"],
                   scrollMaxMs=max(result["ScrollSynchronousMilliseconds"]),
                   tickMaxMs=max(result["AnimationTickIntervalsMilliseconds"]),
                   memoryIncreaseMiB=result["MemoryIncrease"]/(1024*1024),
                   cpuMs=result["CpuMilliseconds"], allocatedBytes=result["AllocatedBytes"],
                   gcPauseMs=result["GcPauseTotalMilliseconds"], collections=result["GcCollections"],
                   labelsRendered=result.get("SourceLineNumberLabelsRendered"),
                   finalSourceLineMap=result.get("SourceLineNumberMap"),
                   firstLabel=result.get("LastFirstSourceLineLabel"), lastLabel=result.get("LastLastSourceLineLabel"))
        summary.append(row)
        print(json.dumps(row), flush=True)
(outputs / "line-numbers-comparison-summary.json").write_text(json.dumps(summary, indent=2)+"\n")
