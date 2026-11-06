import contextlib
import copy
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from check_native_readonly import ACTIONS, POSITIVE, SELECTIONS, collect, validate_report


class NativeReadOnlyTests(unittest.TestCase):
    def report(self, characters=5, body_hash="b" * 64):
        return {"Failure": None, "InputInterrupted": False, "Scenario": "native-gui-readonly-controls",
                "DisplayMode": "full-document-candidate", "Os": "Microsoft Windows test",
                "ProductInformationalVersion": "1+" + "a" * 40, "EditorReadOnly": True,
                "TextDragDropEnabled": False, "UndoCanUndo": False, "UndoCanRedo": False,
                "ReadOnlyChecks": {"Passed": True, "OriginalCharacters": characters,
                    "OriginalSha256": body_hash, "FinalSha256": body_hash, "ProductDocumentPreserved": True,
                    "ProductTabsPreserved": True, "ProductFocused": True,
                    "PositiveControl": {key: True for key in POSITIVE},
                    "NativeCopyRoundTrip": True if characters else None, "CopySkippedForEmpty": characters == 0,
                    "Checks": [{"Selection": selection, "Action": action, "Characters": characters,
                                "BodySha256": body_hash, "UndoCanUndo": False, "UndoCanRedo": False,
                                "Caret": characters if selection == "end" else 0,
                                "SelectionLength": characters if selection == "all" else 0,
                                "Handled": True if action in ("text", "newline", "text-drop") else None}
                               for selection in SELECTIONS for action in ACTIONS]}}

    def test_unicode_and_empty_reports_require_full_attempt_matrix(self):
        for length in (0, 5):
            validate_report(self.report(length), length, "b" * 64, "a" * 40)

    def test_wrong_build_mode_failure_and_readonly_settings_are_rejected(self):
        for key, value in (("Failure", "failed"), ("InputInterrupted", True), ("Scenario", "native-gui-scroll-proxy"),
                           ("DisplayMode", "bounded"), ("Os", "Linux"), ("ProductInformationalVersion", "old"),
                           ("EditorReadOnly", False), ("TextDragDropEnabled", True), ("UndoCanUndo", True)):
            report = self.report(); report[key] = value
            with self.assertRaises(ValueError):
                validate_report(report, 5, "b" * 64, "a" * 40)

    def test_ineffective_positive_controls_cannot_prove_body_invariance(self):
        for key in POSITIVE:
            report = self.report(); report["ReadOnlyChecks"]["PositiveControl"][key] = False
            with self.assertRaises(ValueError):
                validate_report(report, 5, "b" * 64, "a" * 40)

    def test_missing_duplicate_unhandled_and_wrong_boundary_attempts_fail(self):
        changes = [lambda r: r["Checks"].pop(), lambda r: r["Checks"].__setitem__(1, copy.deepcopy(r["Checks"][0])),
                   lambda r: r["Checks"][0].update(Handled=False), lambda r: r["Checks"][0].update(BodySha256="changed"),
                   lambda r: r["Checks"][0].update(SelectionLength=5), lambda r: r["Checks"][0].update(UndoCanRedo=True)]
        for change in changes:
            report = self.report(); change(report["ReadOnlyChecks"])
            with self.assertRaises(ValueError):
                validate_report(report, 5, "b" * 64, "a" * 40)

    def test_body_tabs_focus_and_native_copy_must_match(self):
        for key, value in (("OriginalSha256", "wrong"), ("FinalSha256", "wrong"), ("OriginalCharacters", 4),
                           ("ProductDocumentPreserved", False), ("ProductTabsPreserved", False),
                           ("ProductFocused", False), ("NativeCopyRoundTrip", False)):
            report = self.report(); report["ReadOnlyChecks"][key] = value
            with self.assertRaises(ValueError):
                validate_report(report, 5, "b" * 64, "a" * 40)
        report = self.report(0); report["ReadOnlyChecks"]["NativeCopyRoundTrip"] = True
        with self.assertRaises(ValueError):
            validate_report(report, 0, "b" * 64, "a" * 40)

    def test_collector_preserves_utf16_hash_and_runs_each_fixture_without_interactive_flags(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); app = root / "codeView.exe"; app.write_bytes(b"host")
            (root / "codeView.dll").write_bytes(b"product")
            fixtures = [root / "日本語.cs", root / "empty.cs"]
            fixtures[0].write_bytes("a😀\r\nb".encode()); fixtures[1].write_bytes(b"")
            def execute(command, **kwargs):
                text = Path(command[1]).read_bytes().decode("utf-8-sig")
                body_hash = hashlib.sha256(text.encode("utf-16-le")).hexdigest()
                characters = len(text.encode("utf-16-le")) // 2
                self.assertNotIn("CODEVIEW_GUI_INTERACTIVE", kwargs["env"])
                self.assertEqual(kwargs["env"]["CODEVIEW_GUI_READONLY_CHECKS"], "1")
                Path(kwargs["env"]["CODEVIEW_GUI_PERF_REPORT"]).write_text(json.dumps(self.report(characters, body_hash)))
                return SimpleNamespace(returncode=0)
            with patch.dict("os.environ", {"GITHUB_ACTIONS": "true", "CODEVIEW_GUI_INTERACTIVE": "1"}), \
                    patch("check_native_readonly.subprocess.run", side_effect=execute), contextlib.redirect_stdout(io.StringIO()):
                collect(app, fixtures, root / "out", 2, "a" * 40)
            record = json.loads((root / "out/runs.json").read_text())
            self.assertTrue(record["validated"]); self.assertEqual(len(record["runs"]), 4)
            self.assertEqual(record["fixtures"][0]["characters"], 6)
            with patch.dict("os.environ", {"GITHUB_ACTIONS": "true"}):
                with self.assertRaises(FileExistsError):
                    collect(app, fixtures, root / "out", 2, "a" * 40)

    def test_guard_and_invalid_counts_do_not_launch_or_create_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.dict("os.environ", {"GITHUB_ACTIONS": ""}), patch("check_native_readonly.subprocess.run") as run:
                with self.assertRaises(RuntimeError):
                    collect(root / "app", [], root / "out", 1, "a" * 40)
                run.assert_not_called()
            with patch.dict("os.environ", {"GITHUB_ACTIONS": "true"}):
                for rounds in (0, 11):
                    with self.assertRaises(ValueError):
                        collect(root / "app", [root / "input"], root / "out", rounds, "a" * 40)
            self.assertFalse((root / "out").exists())

    def test_child_failure_and_timeout_stop_after_one_attempt_and_preserve_evidence(self):
        for failure in (False, True):
            with self.subTest(timeout=failure), tempfile.TemporaryDirectory() as directory:
                root = Path(directory); app = root / "codeView.exe"; app.write_bytes(b"host")
                (root / "codeView.dll").write_bytes(b"product"); source = root / "in"; source.write_bytes(b"abc")
                options = {"side_effect": subprocess.TimeoutExpired([str(app)], 60)} if failure else {"return_value": SimpleNamespace(returncode=1)}
                with patch.dict("os.environ", {"GITHUB_ACTIONS": "true"}), patch("check_native_readonly.subprocess.run", **options) as run:
                    with self.assertRaises(subprocess.TimeoutExpired if failure else RuntimeError):
                        collect(app, [source], root / "out", 3, "a" * 40)
                self.assertEqual(run.call_count, 1)
                record = json.loads((root / "out/runs.json").read_text())
                self.assertNotIn("validated", record); self.assertIn("error", record["runs"][0])


if __name__ == "__main__":
    unittest.main()
