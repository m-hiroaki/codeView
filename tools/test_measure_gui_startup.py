import contextlib
import copy
import io
import json
from pathlib import Path
import tempfile
import subprocess
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from measure_gui_startup import STARTUP_STAGES, collect, expected_text, validate_report


class GuiStartupTests(unittest.TestCase):
    def report(self, scenario):
        return {"Failure": None, "InputInterrupted": False, "Scenario": "native-gui-scroll-proxy",
                "DisplayMode": "full-document-candidate", "ProductInformationalVersion": "1+" + "a" * 40,
                "EditorReadOnly": True, "TextDragDropEnabled": False, "UndoCanUndo": False, "UndoCanRedo": False,
                "Os": "Microsoft Windows test", "OpenedMilliseconds": 20, "WindowTwoTicksMilliseconds": 40,
                "ClientWidth": 1180, "ClientHeight": 800, "RenderScaling": 1, "CpuMilliseconds": 100,
                "StartupStages": [{"Stage": stage, "Milliseconds": i * 20 / (len(STARTUP_STAGES) - 1)}
                                  for i, stage in enumerate(STARTUP_STAGES)],
                "DocumentChars": 5 if scenario == "file" else None,
                "DocumentLines": 2 if scenario == "file" else None,
                "DocumentTwoTicksMilliseconds": 60 if scenario == "file" else None,
                "DocumentAttachmentMilliseconds": 3 if scenario == "file" else None,
                "ScrollSynchronousMilliseconds": [1] * 64 if scenario == "file" else [],
                "AnimationTickIntervalsMilliseconds": [16] * 64 if scenario == "file" else []}

    def test_empty_and_file_native_reports_are_distinct(self):
        for platform, description in (("windows", "Microsoft Windows test"), ("macos", "macOS test")):
            for scenario in ("empty", "file"):
                with self.subTest(platform=platform, scenario=scenario):
                    report = self.report(scenario)
                    report["Os"] = description
                    validate_report(report, scenario, 5, 2, "a" * 40, platform)
                    with self.assertRaises(ValueError):
                        validate_report(report, scenario, 5, 2, "a" * 40,
                                        "macos" if platform == "windows" else "windows")

    def test_failure_interruption_wrong_build_mode_or_os_is_rejected(self):
        for key, value in (("Failure", "open-failed"), ("InputInterrupted", True), ("ProductInformationalVersion", "old"),
                           ("DisplayMode", "bounded-window-prototype"), ("Scenario", "other"), ("Os", "Linux"),
                           ("EditorReadOnly", False), ("TextDragDropEnabled", True), ("UndoCanUndo", True), ("UndoCanRedo", True)):
            report = self.report("file"); report[key] = value
            with self.assertRaises(ValueError):
                validate_report(report, "file", 5, 2, "a" * 40, "windows")

    def test_metrics_must_be_finite_with_real_client_area_and_order(self):
        for key, value in (("OpenedMilliseconds", None), ("OpenedMilliseconds", float("nan")), ("ClientWidth", 0),
                           ("ClientHeight", -1), ("RenderScaling", float("inf")), ("CpuMilliseconds", True),
                           ("WindowTwoTicksMilliseconds", 19), ("DocumentTwoTicksMilliseconds", 19)):
            report = self.report("file"); report[key] = value
            with self.assertRaises(ValueError):
                validate_report(report, "file", 5, 2, "a" * 40, "windows")

    def test_startup_timeline_rejects_missing_duplicate_out_of_order_and_invalid_times(self):
        cases = [None, [], self.report('empty')['StartupStages'][:-1]]
        for index, key, value in [(1, 'Stage', 'main-entered'), (2, 'Milliseconds', -1),
                                  (2, 'Milliseconds', float('nan')), (2, 'Milliseconds', True),
                                  (2, 'Milliseconds', 0), (0, 'Milliseconds', 1),
                                  (11, 'Milliseconds', 21)]:
            stages = copy.deepcopy(self.report('empty')['StartupStages'])
            stages[index][key] = value
            cases.append(stages)
        for stages in cases:
            with self.subTest(stages=stages), self.assertRaises(ValueError):
                report = self.report('empty'); report['StartupStages'] = stages
                validate_report(report, 'empty', 5, 2, 'a' * 40, 'windows')

    def test_incomplete_file_or_unexpected_empty_workload_is_rejected(self):
        for key, value in (("DocumentChars", 4), ("DocumentLines", 1), ("ScrollSynchronousMilliseconds", [1] * 63),
                           ("AnimationTickIntervalsMilliseconds", [16] * 65), ("DocumentAttachmentMilliseconds", None)):
            report = self.report("file"); report[key] = value
            with self.assertRaises(ValueError):
                validate_report(report, "file", 5, 2, "a" * 40, "windows")
        with self.assertRaises(ValueError):
            validate_report(self.report("file"), "empty", 5, 2, "a" * 40, "windows")

    def test_reference_counts_utf16_and_crlf_without_normalizing(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "input"
            for text, counts in [("", (0, 1)), ("a😀\r\nb\r\n", (8, 3)), ("a\rb\nc", (5, 3))]:
                path.write_bytes(text.encode("utf-8")); self.assertEqual(expected_text(path), counts)
            path.write_bytes(b"\xef\xbb\xbfhello"); self.assertEqual(expected_text(path), (5, 1))
            path.write_bytes(b"\xff")
            with self.assertRaises(UnicodeDecodeError):
                expected_text(path)
            path.write_bytes(b"a" * (1024 * 1024 + 1))
            with self.assertRaises(ValueError):
                expected_text(path)

    def test_collection_validates_both_scenarios_and_refuses_existing_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); app = root / "space dir/codeView.exe"; app.parent.mkdir()
            app.write_bytes(b"host"); (app.parent / "codeView.dll").write_bytes(b"product")
            source = root / "input.cs"; source.write_bytes("a😀\nb".encode())
            output = root / "out"
            def execute(command, **kwargs):
                scenario = "file" if len(command) == 2 else "empty"
                self.assertNotIn("CODEVIEW_GUI_INTERACTIVE", kwargs["env"])
                target = Path(kwargs["env"]["CODEVIEW_GUI_PERF_REPORT"])
                target.write_text(json.dumps(self.report(scenario)))
                return SimpleNamespace(returncode=0)
            with patch.dict("os.environ", {"CODEVIEW_GUI_INTERACTIVE": "1"}), \
                    patch("measure_gui_startup.subprocess.run", side_effect=execute), \
                    contextlib.redirect_stdout(io.StringIO()):
                collect(app, source, output, 2, "a" * 40, "windows")
            result = json.loads((output / "runs.json").read_text())
            self.assertTrue(result["validated"]); self.assertEqual(len(result["runs"]), 4)
            self.assertTrue((output / "sha256.json").is_file())
            with self.assertRaises(FileExistsError):
                collect(app, source, output, 1, "a" * 40, "windows")
            for count in (0, 101):
                with self.assertRaises(ValueError):
                    collect(app, source, root / "unused", count, "a" * 40, "windows")

    def test_failed_native_child_preserves_failure_record_and_stops(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); app = root / "codeView.exe"; app.write_bytes(b"host")
            (root / "codeView.dll").write_bytes(b"product"); source = root / "in"; source.write_bytes(b"abc")
            with patch("measure_gui_startup.subprocess.run", return_value=SimpleNamespace(returncode=1)) as child:
                with self.assertRaises(RuntimeError):
                    collect(app, source, root / "out", 10, "a" * 40, "windows")
            self.assertEqual(child.call_count, 1)
            row = json.loads((root / "out/runs.json").read_text())["runs"][0]
            self.assertEqual(row["exitCode"], 1); self.assertIn("error", row)

    def test_timeout_is_not_retried_or_recorded_as_success(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); app = root / "codeView.exe"; app.write_bytes(b"host")
            (root / "codeView.dll").write_bytes(b"product"); source = root / "in"; source.write_bytes(b"abc")
            with patch("measure_gui_startup.subprocess.run", side_effect=subprocess.TimeoutExpired([str(app)], 60)) as child:
                with self.assertRaises(subprocess.TimeoutExpired):
                    collect(app, source, root / "out", 10, "a" * 40, "windows")
            self.assertEqual(child.call_count, 1)
            record = json.loads((root / "out/runs.json").read_text())
            self.assertNotIn("validated", record)
            self.assertIn("error", record["runs"][0])


if __name__ == "__main__":
    unittest.main()
