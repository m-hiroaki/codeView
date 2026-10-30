import json
from pathlib import Path
import tempfile
import unittest

from generate_fixture import MARKER, file_sizes, generate


class FixtureTests(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()
