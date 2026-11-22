import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from measure_gui_startup import sha256
from measure_sticky_scroll import collect, make_source


class StickyScrollMeasurementTests(unittest.TestCase):
    def test_fixtures_have_expected_structure_and_size(self):
        with tempfile.TemporaryDirectory() as directory:
            for suffix, declaration, ending in (
                    (".cs", "public void Scan000000()", "}"),
                    (".c", "unsigned scan000000(unsigned value)", "}"),
                    (".cpp", "struct Reader", "};"),
                    (".hpp", "#ifndef READER_FIXTURE_HPP", "#endif")):
                with self.subTest(suffix=suffix):
                    path = Path(directory) / ("fixture" + suffix)
                    self.assertEqual(make_source(path, 8192), sha256(path))
                    self.assertEqual(path.stat().st_size, 8192)
                    text = path.read_text(encoding="utf-8")
                    self.assertIn(declaration, text)
                    self.assertTrue(text.rstrip().endswith(ending))

    def test_native_report_requires_analysis_only_for_eligible_enabled_input(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cases = {"header": ("fixture.hpp", 4096, True), "large": ("large.cpp", 4096, False)}
            base = root / "baseline" / "codeView"
            base.parent.mkdir()
            candidate = root / "candidate" / "codeView"

            def report(command, *, env, **kwargs):
                destination = Path(env["CODEVIEW_GUI_PERF_REPORT"])
                is_candidate = command[0] == str(candidate)
                enabled = "-on-" in destination.name
                has_scopes = enabled and destination.name.startswith("header-")
                frame = [16.0] * 64
                value = dict(Failure=None, InputInterrupted=False, DocumentChars=4096,
                             ProductInformationalVersion="1.0+" + ("candidate" if is_candidate else "baseline"),
                             AnimationTickIntervalsMilliseconds=frame, CpuMilliseconds=50,
                             OpenedMilliseconds=10, WindowTwoTicksMilliseconds=20,
                             DocumentTwoTicksMilliseconds=30, SampledPeakMemory=100)
                if is_candidate:
                    value["StickyScroll"] = dict(Enabled=enabled, RangesFound=has_scopes,
                        StartedMilliseconds=40 if has_scopes else None,
                        CompletedMilliseconds=45 if has_scopes else None,
                        FramePhases=["ready" if has_scopes else "before-analysis"] * 64)
                destination.write_text(json.dumps(value), encoding="utf-8")

            output = root / "results"
            with patch("measure_sticky_scroll.subprocess.run", side_effect=report):
                collect(base, candidate, output, "baseline", "candidate", 1, cases)
            data = json.loads((output / "comparison.json").read_text(encoding="utf-8"))
            self.assertEqual(data["summary"]["header"]["on"]["AnalysisMilliseconds"], [5, 5])
            self.assertNotIn("AnalysisMilliseconds", data["summary"]["large"]["on"])


if __name__ == "__main__":
    unittest.main()
