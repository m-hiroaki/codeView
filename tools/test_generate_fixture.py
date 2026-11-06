import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
import subprocess
import sys

from generate_fixture import MARKER, PROFILES, describe, file_sizes, generate, relative_path, verify


class FixtureTests(unittest.TestCase):
    def test_default_bytes_remain_compatible_with_v1(self):
        expected = {("utf-8", "nested"): "ca7abb82a177de7f96efb9bb5db2f0bdc7207196461954cc8f88394ac30e9401",
                    ("cp932", "nested"): "1998a585be778601f72e112b7f5b060cb8e9e1a598f2a51d2e490f29e41733f2",
                    ("utf-8", "single-line"): "f68b54247120f6cbf6467ce893e25f03576dca25911950864b03733fc68ef292"}
        with tempfile.TemporaryDirectory() as directory:
            for index, ((encoding, layout), digest) in enumerate(expected.items()):
                root = Path(directory) / str(index)
                generate(root, 1, 1027, encoding=encoding, layout=layout)
                self.assertEqual(hashlib.sha256(next(root.rglob("*.cs")).read_bytes()).hexdigest(), digest)

    def test_exact_counts_sizes_and_known_matches(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "fixture"
            info = generate(root, 11, 20_003)
            files = list(root.rglob("*.cs"))
            self.assertEqual(len(files), 11)
            self.assertEqual(sum(file.stat().st_size for file in files), 20_003)
            self.assertEqual(sum(file.read_text().count(MARKER) for file in files), 11)
            self.assertEqual(info["expectedMatches"], 11)
            self.assertEqual(json.loads(root.with_name("fixture.manifest.json").read_text())["totalBytes"], 20_003)

    def test_cp932_and_single_line_are_valid_at_exact_byte_boundaries(self):
        with tempfile.TemporaryDirectory() as directory:
            for index, size in enumerate([64, 65, 8193, 65537]):
                root = Path(directory) / str(index)
                generate(root, 1, size, encoding="cp932", layout="single-line")
                file = next(root.rglob("*.cs"))
                self.assertEqual(file.stat().st_size, size)
                content = file.read_bytes().decode("cp932", errors="strict")
                self.assertNotIn("\n", content)
                self.assertEqual(content.count(MARKER), 1)

    def test_output_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "fixture"
            generate(root, 1, 1024)
            content = next(root.rglob("*.cs")).read_bytes()
            with self.assertRaises(FileExistsError):
                generate(root, 1, 2048)
            self.assertEqual(next(root.rglob("*.cs")).read_bytes(), content)

    def test_invalid_arguments_do_not_create_output(self):
        with tempfile.TemporaryDirectory() as directory:
            for index, (count, size) in enumerate([(0, 100), (-1, 100), (2, 127), (1, -1)]):
                root = Path(directory) / str(index)
                with self.assertRaises(ValueError):
                    generate(root, count, size)
                self.assertFalse(root.exists())

    def test_deterministic_digest_and_mixed_distribution(self):
        with tempfile.TemporaryDirectory() as directory:
            first = generate(Path(directory) / "a", 100, 100_003, layout="mixed", seed=42)
            second = generate(Path(directory) / "b", 100, 100_003, layout="mixed", seed=42)
            self.assertEqual(first["pathSizeDigest"], second["pathSizeDigest"])
            self.assertGreater(first["maxFileBytes"], first["minFileBytes"])
            self.assertEqual(sum(file_sizes(100, 100_003, "mixed")), 100_003)

    def test_variant_bytes_counts_characters_and_lines_match_independent_decode(self):
        with tempfile.TemporaryDirectory() as directory:
            cases = [("utf-8", "unicode", "crlf", "nested"), ("utf-8", "unicode", "mixed", "nested"),
                     ("utf-8", "many-matches", "mixed", "nested"), ("cp932", "many-matches", "crlf", "wide"),
                     ("cp932", "many-matches", "lf", "single-line")]
            for index, (encoding, content, newlines, layout) in enumerate(cases):
                with self.subTest(encoding=encoding, content=content, newlines=newlines, layout=layout):
                    root = Path(directory) / str(index)
                    result = generate(root, 3, 24_593, encoding=encoding, content=content, newlines=newlines, layout=layout)
                    raw = [path.read_bytes() for path in root.rglob("*.cs")]
                    text = [data.decode(encoding, errors="strict") for data in raw]
                    self.assertEqual(sum(map(len, raw)), 24_593)
                    self.assertEqual(sum(value.count(MARKER) for value in text), result["expectedMatches"])
                    self.assertEqual(sum(len(value.encode("utf-16-le")) // 2 for value in text), result["sourceUtf16Units"])
                    # splitlines ignores the final empty row, which is part of the document model.
                    self.assertEqual(sum(len(value.splitlines()) + (value.endswith(("\r", "\n"))) for value in text), result["sourceLines"])
                    if content == "many-matches":
                        self.assertTrue(all(MARKER in line for value in text for line in value.splitlines()))
                        if layout != "single-line":
                            self.assertEqual(result["sourceLines"], result["expectedMatches"])
                    if content == "unicode":
                        self.assertTrue(all("😀" in value and "か\u3099" in value for value in text))
                    if newlines == "crlf":
                        self.assertTrue(all("\n" not in value.replace("\r\n", "") for value in text))
                    self.assertTrue(verify(root)["verified"])

    def test_boundaries_remain_exact_for_many_matches_and_multibyte_content(self):
        with tempfile.TemporaryDirectory() as directory:
            for index, size in enumerate([64, 65, 66, 127, 8193, 65537]):
                root = Path(directory) / str(index)
                result = generate(root, 1, size, encoding="cp932", content="many-matches", newlines="mixed")
                data = next(root.rglob("*.cs")).read_bytes()
                self.assertEqual(len(data), size)
                self.assertEqual(data.decode("cp932").count(MARKER), result["expectedMatches"])

    def test_all_profile_sizes_are_streamed_and_mixed_has_three_scales(self):
        self.assertEqual(PROFILES["text10"], (1, 10 << 20))
        self.assertEqual(PROFILES["text50"], (1, 50 << 20))
        self.assertEqual(PROFILES["text100"], (1, 100 << 20))
        self.assertEqual(PROFILES["tabs50"], (5, 250 << 20))
        for name in ["l1", "l2", "l3"]:
            count, total = PROFILES[name]
            sizes = file_sizes(count, total, "mixed")
            first, second = next(sizes), next(sizes)
            self.assertGreater(first, second)
            self.assertGreater(second, total // count)
            remaining_count = remaining_total = 0
            for size in sizes:
                self.assertGreaterEqual(size, 64)
                remaining_count += 1
                remaining_total += size
            self.assertEqual(remaining_count + 2, count)
            self.assertEqual(first + second + remaining_total, total)

    def test_depth_and_wide_boundary_and_independent_files(self):
        self.assertEqual(len(relative_path(9999, "wide", "cs", 10).parts), 1)
        self.assertEqual(len(relative_path(10000, "wide", "cs", 10).parts), 11)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "deep"
            result = generate(root, 5, 5003, depth=10)
            self.assertEqual(result["maximumDepth"], 10)
            self.assertEqual(result["directories"], 10)
            self.assertTrue(verify(root)["verified"])
            if os.name != "nt":
                self.assertEqual(len({path.stat().st_ino for path in root.rglob("*.cs")}), 5)

    def test_plan_matches_generation_without_writing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "data"
            planned = describe(root, 11, 20_003, content="many-matches", newlines="mixed", depth=3)
            self.assertFalse(root.exists())
            generated = generate(root, 11, 20_003, content="many-matches", newlines="mixed", depth=3)
            self.assertEqual({key: value for key, value in generated.items() if key != "contentDigest"}, planned)
            self.assertTrue(verify(root)["verified"])

    def test_wide_deep_and_mixed_can_coexist_at_the_ten_thousand_boundary(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "wide"
            result = generate(root, 10_001, 2 << 20, layout="wide", depth=10, distribution="mixed")
            self.assertEqual(sum(path.is_file() for path in root.iterdir()), 10_000)
            self.assertTrue((root / relative_path(10_000, "wide", "cs", 10)).is_file())
            self.assertEqual(result["directories"], 10)
            self.assertEqual(result["maximumDepth"], 10)
            self.assertGreaterEqual(len({path.stat().st_size for path in root.rglob("*.cs")}), 3)
            self.assertTrue(verify(root)["verified"])

    def test_invalid_variants_fail_before_creating_output(self):
        with tempfile.TemporaryDirectory() as directory:
            for index, options in enumerate([{"encoding": "cp932", "content": "unicode"}, {"newlines": "bad"},
                    {"content": "bad"}, {"depth": 0}, {"depth": 11}, {"layout": "single-line", "newlines": "crlf"},
                    {"seed": 10 ** 80}]):
                root = Path(directory) / str(index)
                with self.assertRaises(ValueError):
                    generate(root, 1, 64, **options)
                self.assertFalse(root.exists())

    def test_verifier_rejects_changed_bytes_extra_files_and_changed_metadata(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "data"
            generate(root, 1, 1024)
            file = next(root.rglob("*.cs"))
            original = file.read_bytes()
            file.write_bytes(original[:-1] + b"X")
            with self.assertRaisesRegex(ValueError, "内容ハッシュ"):
                verify(root)
            file.write_bytes(original)
            extra = root / "unexpected.txt"
            extra.write_bytes(b"extra")
            with self.assertRaisesRegex(ValueError, "数"):
                verify(root)
            extra.unlink()
            manifest = root.with_name("data.manifest.json")
            data = json.loads(manifest.read_text())
            for key, value in [("literalQuery", "wrong"), ("seed", data["seed"] + 97), ("expectedMatches", 2)]:
                changed = {**data, key: value}
                manifest.write_text(json.dumps(changed), encoding="utf-8")
                with self.assertRaises(ValueError):
                    verify(root)
            manifest.write_text(json.dumps(data), encoding="utf-8")
            self.assertTrue(verify(root)["verified"])

    def test_cli_plan_and_verify_and_ambiguous_arguments(self):
        script = Path(__file__).with_name("generate_fixture.py")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "data"
            command = [sys.executable, str(script), str(root)]
            planned = subprocess.run([*command, "--profile", "tabs50", "--layout", "wide", "--plan"], capture_output=True, text=True)
            self.assertEqual(planned.returncode, 0, planned.stderr)
            self.assertEqual(json.loads(planned.stdout)["totalBytes"], 250 << 20)
            self.assertFalse(root.exists())
            invalid = subprocess.run([*command, "--profile", "l1", "--files", "2"], capture_output=True)
            self.assertNotEqual(invalid.returncode, 0)
            self.assertFalse(root.exists())
            generated = subprocess.run([*command, "--bytes", "1027", "--content", "many-matches"], capture_output=True)
            self.assertEqual(generated.returncode, 0, generated.stderr)
            checked = subprocess.run([*command, "--verify"], capture_output=True, text=True)
            self.assertEqual(checked.returncode, 0, checked.stderr)
            self.assertTrue(json.loads(checked.stdout)["verified"])


if __name__ == "__main__":
    unittest.main()
