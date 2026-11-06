import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from generate_functional_fixture import GENERATOR, current_windows_sid, generate, set_read_access


class FunctionalFixtureTests(unittest.TestCase):
    def test_portable_bytes_and_expectations_are_deterministic_across_roots(self):
        with tempfile.TemporaryDirectory() as directory:
            first = generate(Path(directory) / "a", native=False)
            second = generate(Path(directory) / "b", native=False)
            self.assertEqual(first, second)
            self.assertEqual(first["generator"], GENERATOR)
            self.assertEqual(len(first["files"]), 30)
            for entry in first["files"]:
                raw = (Path(directory) / "a" / entry["path"]).read_bytes()
                self.assertEqual(len(raw), entry["bytes"])
                self.assertEqual(hashlib.sha256(raw).hexdigest(), entry["sha256"])

    def test_strict_encodings_and_invalid_sequences_have_independent_expectations(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest = generate(Path(directory) / "fixture", native=False)
            for entry in manifest["files"]:
                raw = (Path(directory) / "fixture" / entry["path"]).read_bytes()
                with self.subTest(path=entry["path"]):
                    if "text" in entry:
                        self.assertEqual(raw[entry.get("bomBytes", 0):].decode(entry["encoding"], errors="strict"), entry["text"])
                    elif entry.get("expectedError") == "DecodeFailed":
                        with self.assertRaises(UnicodeDecodeError):
                            raw.decode(entry["encoding"], errors="strict")

    def test_utf16_offsets_eof_and_declared_names_match_fixed_text(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest = generate(Path(directory) / "fixture", native=False)
            for entry in manifest["files"]:
                text = entry.get("text", "")
                for search in entry.get("searches", []):
                    for offset in search["offsets"]:
                        suffix = text.encode("utf-16-le")[offset * 2:].decode("utf-16-le")
                        self.assertTrue(suffix.casefold().startswith(search["query"].casefold()))
                if "eof" in entry:
                    self.assertEqual(len(text.encode("utf-16-le")) // 2, entry["eof"]["offset"])
                for declaration in entry.get("declarations", []):
                    self.assertEqual(text[declaration["offset"]:][:len(declaration["name"])], declaration["name"])

    def test_existing_directory_or_file_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            file = root / "file"
            file.write_text("keep", encoding="utf-8")
            for existing in [root, file]:
                with self.assertRaises(FileExistsError):
                    generate(existing)
            self.assertEqual(file.read_text(), "keep")

    @unittest.skipIf(hasattr(os, "geteuid") and os.geteuid() == 0, "Root can bypass POSIX read denial")
    def test_native_links_cycles_and_read_denial_are_real_and_recoverable(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "fixture"
            try:
                manifest = generate(root)
                self.assertEqual(manifest["errors"], [])
                self.assertEqual(manifest["native"], {"requested": True, "links": "created", "readDenied": "verified"})
                for link in manifest["links"]:
                    path = root / link["path"]
                    self.assertTrue(path.is_symlink())
                    self.assertEqual(os.readlink(path).replace("\\", "/"), link["target"])
                self.assertEqual((root / "links/file.txt").read_bytes(), (root / "text/search.txt").read_bytes())
                self.assertFalse((root / "links/broken.txt").exists())
                self.assertEqual((root / "links/folder/search.txt").read_bytes(), (root / "text/search.txt").read_bytes())
                with self.assertRaises((OSError, RuntimeError)):
                    (root / "links/cycle-a").resolve(strict=True)
                with self.assertRaises(PermissionError):
                    (root / "io/denied.txt").read_bytes()
            finally:
                if (root / "io/denied.txt").is_file():
                    set_read_access(root / "io/denied.txt", denied=False)
            self.assertEqual((root / "io/denied.txt").read_bytes(), b"denied\n")

    def test_unsupported_native_setup_is_reported_in_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(Path, "symlink_to", side_effect=PermissionError()), patch("generate_functional_fixture.set_read_access"):
                manifest = generate(Path(directory) / "fixture")
            self.assertEqual(manifest["native"]["links"], "unavailable")
            self.assertEqual(manifest["native"]["readDenied"], "unavailable")
            self.assertEqual(len(manifest["errors"]), 2)

    def test_cli_portable_success_and_existing_output_failure(self):
        script = Path(__file__).with_name("generate_functional_fixture.py")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "fixture"
            command = [sys.executable, str(script), str(root), "--portable"]
            first = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(first.returncode, 0, first.stderr)
            self.assertFalse(json.loads(first.stdout)["native"]["requested"])
            repeated = subprocess.run(command, capture_output=True, text=True)
            self.assertNotEqual(repeated.returncode, 0)
            self.assertIn("Output exists", repeated.stderr)

    def test_native_setup_errors_make_cli_fail(self):
        from generate_functional_fixture import main
        with patch.object(sys, "argv", ["generate", "unused"]), patch("generate_functional_fixture.generate", return_value={"native": {}, "errors": ["failed"]}):
            with self.assertRaises(SystemExit) as error, patch("builtins.print"), patch("sys.stderr"):
                main()
            self.assertEqual(error.exception.code, 1)

    def test_windows_acl_uses_current_sid_and_denies_only_file_data(self):
        with tempfile.TemporaryDirectory() as directory:
            file = Path(directory) / "denied.txt"
            file.write_bytes(b"test")
            with patch("generate_functional_fixture.os.name", "nt"), patch("generate_functional_fixture.current_windows_sid", return_value="*S-1-5-21-123"), patch("generate_functional_fixture.subprocess.run") as run:
                set_read_access(file, denied=True)
                self.assertEqual(run.call_args.args[0], ["icacls", str(file), "/deny", "*S-1-5-21-123:(RD)"])
                set_read_access(file, denied=False)
                self.assertEqual(run.call_args.args[0], ["icacls", str(file), "/remove:d", "*S-1-5-21-123"])

    def test_sid_extraction_does_not_depend_on_account_name_encoding(self):
        result = subprocess.CompletedProcess([], 0, stdout=b'"example\\\x82\xa0","S-1-5-21-123-456"\r\n')
        with patch("generate_functional_fixture.subprocess.run", return_value=result):
            self.assertEqual(current_windows_sid(), "*S-1-5-21-123-456")


if __name__ == "__main__":
    unittest.main()
