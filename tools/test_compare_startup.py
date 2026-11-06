import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from compare_gui_startup import FLAGS, assess, compare, search_checks, validate_search


def report(body_hash='b' * 64):
    return dict(Failure=None, InputInterrupted=False, Scenario='native-gui-search-panel-controls',
                DisplayMode='full-document-candidate', ProductInformationalVersion='1+' + 'a' * 40,
                Os='Microsoft Windows test', EditorReadOnly=True,
                SearchPanelChecks=dict({key: True for key in FLAGS}, Passed=True, CommittedMatches=1,
                    RepeatedShows=3, BodySha256=body_hash, FirstShowSynchronousMilliseconds=100,
                    FirstShowTwoTicksMilliseconds=120))


def summary(time=100, cpu=100, memory=100):
    return {scenario: {key: {'median': value, 'max': value} for key, value in
            [('OpenedMilliseconds', time), ('CpuMilliseconds', cpu), ('SampledPeakMemory', memory)]}
            for scenario in ('empty', 'file')}


class CompareStartupTests(unittest.TestCase):
    def test_search_body_focus_and_state_are_all_required(self):
        validate_search(report(), 'a' * 40, 'windows', 'b' * 64)
        for key in FLAGS:
            broken = report(); broken['SearchPanelChecks'][key] = False
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate_search(broken, 'a' * 40, 'windows', 'b' * 64)
        for key, value in [('CommittedMatches', 0), ('RepeatedShows', 2), ('BodySha256', 'wrong'),
                           ('FirstShowSynchronousMilliseconds', float('nan')), ('FirstShowTwoTicksMilliseconds', 99)]:
            broken = report(); broken['SearchPanelChecks'][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate_search(broken, 'a' * 40, 'windows', 'b' * 64)
        with self.assertRaises(ValueError):
            validate_search(report(), 'a' * 40, 'macos', 'b' * 64)

    def test_budgets_accept_exact_boundaries_and_reject_deferred_cost_or_resource_increase(self):
        self.assertTrue(all(assess(summary(), summary(95, 105, 105), {'synchronousBudgetPassed': True}).values()))
        self.assertFalse(all(assess(summary(), summary(95, 105, 105), {'synchronousBudgetPassed': False}).values()))
        for candidate in [summary(95.001), summary(95, 105.001), summary(95, 100, 105.001)]:
            self.assertFalse(all(assess(summary(), candidate, {'synchronousBudgetPassed': True}).values()))

    def test_comparison_alternates_all_ten_rounds_and_retains_budget_rejection(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); base = root / 'base'; cand = root / 'candidate'; source = root / 'input'
            for p in (base, cand, source): p.write_bytes(b'fixture')
            order = []
            def collect(app, input_file, output, rounds, commit, expected_os):
                order.append(output.name); output.mkdir()
                ms = 100 if app == base else 96 # 4% reduction is insufficient.
                data = {'runs': [dict(scenario=s, report=dict(OpenedMilliseconds=ms, CpuMilliseconds=100, SampledPeakMemory=100)) for s in ('empty', 'file')]}
                (output / 'runs.json').write_text(json.dumps(data))
            with patch.dict('os.environ', {'GITHUB_ACTIONS': 'true'}), patch('compare_gui_startup.collect', side_effect=collect), \
                    patch('compare_gui_startup.search_checks', return_value={'synchronousBudgetPassed': True}):
                result = compare(base, cand, source, root / 'out', 'b' * 40, 'a' * 40, 'windows')
            self.assertEqual(order[:4], ['baseline-01', 'candidate-01', 'candidate-02', 'baseline-02'])
            self.assertEqual(len(order), 20); self.assertEqual(len(result['baseline']), 20)
            self.assertTrue(result['acquired']); self.assertFalse(result['candidateAccepted'])
            self.assertFalse(json.loads((root / 'out/comparison.json').read_text())['candidateAccepted'])

    def test_failed_acquisition_is_not_accepted_or_retried(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); app = root / 'app'; app.write_bytes(b'fixture')
            with patch.dict('os.environ', {'GITHUB_ACTIONS': 'true'}), \
                    patch('compare_gui_startup.collect', side_effect=RuntimeError('native failure')) as launch:
                with self.assertRaises(RuntimeError):
                    compare(app, app, app, root / 'out', 'b' * 40, 'a' * 40, 'windows')
            self.assertEqual(launch.call_count, 1)
            result = json.loads((root / 'out/comparison.json').read_text())
            self.assertFalse(result['acquired']); self.assertNotIn('candidateAccepted', result)

    def test_invalid_environment_or_counts_do_not_create_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); app = root / 'app'
            with patch.dict('os.environ', {'GITHUB_ACTIONS': ''}), self.assertRaises(RuntimeError):
                compare(app, app, app, root / 'out', 'b' * 40, 'a' * 40, 'windows')
            with patch.dict('os.environ', {'GITHUB_ACTIONS': 'true'}):
                for rounds, functional in [(9, 3), (31, 3), (10, 2), (10, 11)]:
                    with self.assertRaises(ValueError):
                        compare(app, app, app, root / 'out', 'b' * 40, 'a' * 40, 'windows', rounds, functional)
            self.assertFalse((root / 'out').exists())

    def test_native_functional_failure_or_timeout_stops_once_and_preserves_record(self):
        for timed_out in (False, True):
            with self.subTest(timed_out=timed_out), tempfile.TemporaryDirectory() as directory:
                root = Path(directory); source = root / 'input'; source.write_bytes(b'CODEVIEW_FIND')
                options = {'side_effect': subprocess.TimeoutExpired(['app'], 60)} if timed_out else {'return_value': SimpleNamespace(returncode=1)}
                with patch.dict('os.environ', {'GITHUB_ACTIONS': 'true'}), patch('compare_gui_startup.subprocess.run', **options) as launch:
                    with self.assertRaises(subprocess.TimeoutExpired if timed_out else RuntimeError):
                        search_checks(root / 'app', source, root / 'out', 'a' * 40, 'windows', 3)
                self.assertEqual(launch.call_count, 1)
                self.assertIn('error', json.loads((root / 'out/runs.json').read_text())['runs'][0])

    def test_successful_native_checks_keep_functional_success_separate_from_performance(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root / 'input'; source.write_bytes('CODEVIEW_FIND 日本語😀'.encode())
            body_hash = hashlib.sha256(source.read_bytes().decode().encode('utf-16-le')).hexdigest()
            def execute(command, **kwargs):
                data = report(body_hash); data['SearchPanelChecks']['FirstShowSynchronousMilliseconds'] = 100.001
                Path(kwargs['env']['CODEVIEW_GUI_PERF_REPORT']).write_text(json.dumps(data))
                return SimpleNamespace(returncode=0)
            with patch.dict('os.environ', {'GITHUB_ACTIONS': 'true'}), patch('compare_gui_startup.subprocess.run', side_effect=execute) as launch:
                result = search_checks(root / 'app', source, root / 'out', 'a' * 40, 'windows', 3)
            self.assertEqual(launch.call_count, 3); self.assertTrue(result['validated'])
            self.assertFalse(result['synchronousBudgetPassed'])


if __name__ == '__main__':
    unittest.main()
