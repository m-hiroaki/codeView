import copy
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from measure_diagnostics import collect, command_for, counter_rows, digest, validate_child, validate_reports, validate_trace


class DiagnosticTests(unittest.TestCase):
    def test_core_failure_collects_all_modes_and_finishes_nonzero_with_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            input_file = root / "input.cs"
            with input_file.open("wb") as stream:
                stream.truncate(50 * 1024 * 1024)
            host = root / "host/CoreHost.dll"; host.parent.mkdir()
            reader = root / "TraceSummary.dll"
            tools = root / "tools"; tools.mkdir()
            suffix = ".exe" if os.name == "nt" else ""
            for path in (host, reader, host.parent / "codeView.dll", tools / ("dotnet-trace" + suffix),
                         tools / ("dotnet-counters" + suffix)):
                path.write_bytes(b"test-placeholder")
            results = host.parent / "TestResults"; results.mkdir()
            output = root / "trial"

            def execute(command, **kwargs):
                if command[1] == str(reader):
                    analysis = Path(command[-1]); label = analysis.name[:-len("-trace.json")]
                    trace = self.trace()
                    for marker in trace["Markers"]:
                        marker["Payload"]["label"] = label
                    trace["AllocationSamples"] = [{"KindAndType": "sample"}]
                    analysis.write_text(json.dumps(trace))
                    return SimpleNamespace(returncode=0, stderr="")
                label = command[-2]
                report = {"InputBytes": 50 * 1024 * 1024, "Copies": 1, "ProductInformationalVersion": "1+" + "a" * 40,
                          "CacheCondition": "Warm; test", "BuildMilliseconds": [1]}
                for index in range(10):
                    (results / f"{label}-{index}-1.json").write_text(json.dumps(report))
                if "-counters-" in label:
                    (output / (label + ".csv")).write_text("Timestamp,Provider,Counter Name,Counter Type,Mean/Increment\ntime,System.Runtime,dotnet.gc.collections,Rate,1\n")
                return SimpleNamespace(returncode=1 if label == "trial-baseline-1" else 0)

            with patch("measure_diagnostics.platform.platform", return_value="test-platform"), \
                    patch("measure_diagnostics.subprocess.run", side_effect=execute):
                with self.assertRaisesRegex(RuntimeError, "Acquisition completed; core assertions failed"):
                    collect(input_file, host, reader, tools, output, "a" * 40)
            record = json.loads((output / "runs.json").read_text())
            self.assertEqual(len(record["runs"]), 8)
            self.assertTrue(record["acquisitionValidated"])
            self.assertFalse(record["coreAssertionsPassed"])
            self.assertEqual(record["runs"][0]["exitCode"], 1)
            self.assertTrue(all(run["acquisitionValidated"] for run in record["runs"]))
            self.assertEqual(sum(run["coreAssertionsPassed"] for run in record["runs"]), 7)
            self.assertTrue((output / "sha256.json").is_file())

    def test_hash_streams_empty_and_multiple_blocks_on_supported_python(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "input"
            for value in (b"", b"abc" * (1024 * 1024)):
                path.write_bytes(value)
                self.assertEqual(digest(path), hashlib.sha256(value).hexdigest())

    def trace(self):
        return {"EventsLost": 0, "OverlappingSuspendStarts": 0, "OrphanRestarts": 0,
                "UnmatchedFinalSuspendStart": None, "GcStartsByDepth": {"0": 1}, "HeapStatsEvents": 1,
                "SuspensionIntervals": [{"StartMilliseconds": 1, "EndMilliseconds": 2, "DurationMilliseconds": 1}],
                "Markers": [{"EventName": "Round/Start" if i % 2 == 0 else "Round/Stop",
                             "TimeStampRelativeMSec": i, "Payload": {"label": "trial", "round": str(i // 2)}}
                            for i in range(20)]}

    def test_trace_accepts_ten_paired_rounds(self):
        validate_trace(self.trace(), "trial")

    def test_assertion_failure_is_retained_inside_its_round(self):
        trace = self.trace()
        marker = {"EventName": "BudgetFailed", "TimeStampRelativeMSec": 0.5,
                  "Payload": {"label": "trial", "round": "0"}}
        trace["Markers"].insert(1, marker)
        self.assertEqual(validate_trace(trace, "trial"), 1)
        trace["Markers"].insert(2, copy.deepcopy(marker))
        with self.assertRaises(ValueError):
            validate_trace(trace, "trial")
        trace["Markers"].pop(2)
        marker["TimeStampRelativeMSec"] = 2
        with self.assertRaises(ValueError):
            validate_trace(trace, "trial")

    def test_trace_rejects_loss_or_unmatched_gc(self):
        for field in ("EventsLost", "OverlappingSuspendStarts", "OrphanRestarts", "UnmatchedFinalSuspendStart"):
            with self.subTest(field=field):
                trace = self.trace(); trace[field] = 1
                with self.assertRaises(ValueError):
                    validate_trace(trace, "trial")

    def test_trace_rejects_incomplete_or_duplicate_or_wrong_markers(self):
        for mutation in (lambda x: x["Markers"].pop(), lambda x: x["Markers"].append(x["Markers"][-1]),
                         lambda x: x["Markers"][1]["Payload"].update(round="1"),
                         lambda x: x["Markers"][0].update(EventName="BudgetFailed"),
                         lambda x: x["Markers"][2].update(TimeStampRelativeMSec=-1)):
            trace = self.trace(); mutation(trace)
            with self.assertRaises(ValueError):
                validate_trace(trace, "trial")

    def test_trace_rejects_missing_gc_or_negative_interval(self):
        for field, value in (("GcStartsByDepth", {}), ("HeapStatsEvents", 0),
                             ("SuspensionIntervals", [{"StartMilliseconds": 2, "EndMilliseconds": 1, "DurationMilliseconds": -1}])):
            trace = self.trace(); trace[field] = value
            with self.assertRaises(ValueError):
                validate_trace(trace, "trial")

    def test_commands_quote_with_argument_lists_and_select_os_executable(self):
        for windows in (False, True):
            for mode in ("baseline", "gc-collect", "gc-verbose", "counters"):
                args = command_for(mode, Path("space dir/host.dll"), Path("space dir/tools"), Path("out"), "trial", windows)
                self.assertIn(str(Path("space dir/host.dll")), args)
                self.assertEqual(args[-2:], ["trial", "10"])
                if mode != "baseline":
                    self.assertEqual(args[0].endswith(".exe"), windows)
        with self.assertRaises(ValueError):
            command_for("unknown", Path("host"), Path("tools"), Path("out"), "trial", False)

    def test_report_identity_and_counts_are_required(self):
        original = [{"InputBytes": 50, "Copies": 1, "ProductInformationalVersion": "1.0+" + "a" * 40,
                     "CacheCondition": "Warm; fixture read", "BuildMilliseconds": [1]} for _ in range(10)]
        validate_reports(original, 50, "a" * 40)
        self.assertTrue(validate_child(0, original, 50, "a" * 40))
        self.assertFalse(validate_child(1, original, 50, "a" * 40))
        for code in (-1, 2, 134):
            with self.assertRaises(RuntimeError):
                validate_child(code, original, 50, "a" * 40)
        with self.assertRaises(ValueError):
            validate_reports(original[:-1], 50, "a" * 40)
        for key, value in (("InputBytes", 51), ("Copies", 5), ("ProductInformationalVersion", "old"),
                           ("CacheCondition", "unknown"), ("BuildMilliseconds", [])):
            reports = copy.deepcopy(original); reports[0][key] = value
            with self.assertRaises(ValueError):
                validate_reports(reports, 50, "a" * 40)

    def test_counter_csv_accepts_bom_but_rejects_empty_or_wrong_schema(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "counter.csv"
            header = "Timestamp,Provider,Counter Name,Counter Type,Mean/Increment\n"
            path.write_text(header + "time,System.Runtime,dotnet.gc.collections,Rate,1\n", encoding="utf-8-sig")
            self.assertEqual(counter_rows(path), 1)
            for text in (header, "Unexpected\n", header + "time,other,foo,Rate,1\n",
                         header + "time,System.Runtime,dotnet.process.cpu.count,Metric,1\n"):
                path.write_text(text)
                with self.assertRaises(ValueError):
                    counter_rows(path)


if __name__ == "__main__":
    unittest.main()
