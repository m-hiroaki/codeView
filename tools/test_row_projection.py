import copy
import hashlib
from pathlib import Path
import tempfile
import unittest
import json
import subprocess
from types import SimpleNamespace
from unittest import mock

from check_row_projection import SCROLL_CHECKS, LIFECYCLE_CHECKS, collect, fixture, validate_report, validate_shutdown


class RowProjectionEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.commit = 'a' * 40
        self.expected = {'characters': 4, 'lines': 2, 'bodySha256': 'body', 'maximumLineLength': 3,
                         'firstLineHasText': True, 'checksTrailingBlank': False, 'checksInitialGrapheme': False}
        checks = ['routed-document-end', 'end-caret-projected', 'routed-document-start', 'routed-shift-selection',
                  'routed-select-all-original', 'native-selection-excludes-synthetic-lf', 'selection-spans-exclude-separators',
                  'offscreen-caret-stays-independent', 'selection-background-rendered', 'next-input-reveals-source-caret',
                  'stale-display-rejected', 'display-source-first-row-label', 'source-unchanged', 'read-only-native-display',
                  'native-clipboard-original-count', 'clipping-marks-rendered', 'source-labels-rendered']
        checks += ['viewport-preserves-source-' + str(i) for i in range(64)]
        checks += ['bounded-rows-' + str(i) for i in range(64)]
        self.report = {'Failure': None, 'InputInterrupted': False, 'RowProjectionEnabled': True,
                       'Scenario': 'native-interactive-bounded-candidate', 'DisplayMode': 'bounded-window-prototype',
                       'ProductInformationalVersion': '1.0+' + self.commit, 'Os': 'macOS test',
                       'RowProjectionChecks': {'Checks': checks, 'SourceSha256': 'body', 'Utf16Characters': 4,
                                               'SourceLineCount': 2, 'ScrollSteps': 64,
                                               'ScrollChecks': sorted(SCROLL_CHECKS), 'MaximumLineLength': 3},
                       'SourceChars': 4, 'SourceLines': 2, 'ConfiguredWindowChars': 16384, 'EditorReadOnly': True,
                       'UndoCanUndo': False, 'ClipboardVerificationChecks': 1, 'ClipboardVerificationFailures': 0,
                       'ScrollSynchronousMilliseconds': [1.] * 64, 'AnimationTickIntervalsMilliseconds': [16.] * 64,
                       'RowProjectionSamples': [{'DisplayChars': 4, 'DisplayRows': 2}] * 64, 'PhysicalPaint': 'not-measured'}
        self.report['RowProjectionChecks']['Lifecycle'] = {'Checks': sorted(LIFECYCLE_CHECKS | {
            'real-search-committed-primary-hit', 'real-search-enter-selects-original', 'search-caret-before-deferred-selection'}), 'SearchOutcome': 'hit', 'WorkloadStarts': 1}
        self.shutdown = {'Passed': True, 'HistoryCleared': True, 'ViewHasDocument': False, 'Tabs': 0,
                         'PendingViewCompleted': True, 'PendingCopyCompleted': True, 'SurvivingWeakHandles': 3,
                         'UnreleasedHandles': 0, 'ActiveLeases': 0}

    def validate(self, report=None):
        validate_report(self.report if report is None else report, self.expected, self.commit, 'macos')

    def test_accepts_complete_record_and_both_native_os(self):
        self.validate()
        self.report['Os'] = 'Microsoft Windows test'
        validate_report(self.report, self.expected, self.commit, 'windows')

    def test_contextual_viewport_requires_draws_and_pointer_hits_only_when_enabled(self):
        checks = self.report['RowProjectionChecks']
        for field in ('ContextualDrawnRows', 'ContextualResolvedHits'):
            with self.assertRaisesRegex(ValueError, field):
                validate_report(self.report, self.expected, self.commit, 'macos', contextual=True)
            checks[field] = 1
        with self.assertRaisesRegex(ValueError, 'wheel/pointer'):
            validate_report(self.report, self.expected, self.commit, 'macos', contextual=True)
        checks['ScrollChecks'].append('contextual-caret-rectangle-visible')
        self.expected['checksInitialGrapheme'] = True
        for check in ('routed-shift-selects-whole-grapheme', 'routed-shift-returns-to-grapheme-start'):
            with self.assertRaisesRegex(ValueError, 'Source, checks'):
                validate_report(self.report, self.expected, self.commit, 'macos', contextual=True)
            checks['Checks'].append(check)
        validate_report(self.report, self.expected, self.commit, 'macos', contextual=True)
        with self.assertRaisesRegex(ValueError, 'ContextualDrawnRows'):
            self.validate()

    def test_short_ascii_fixture_requires_trailing_blank_click_and_position(self):
        self.expected['checksTrailingBlank'] = True
        for check in ('trailing-blank-click-selects-line-end', 'trailing-blank-click-updates-source-position',
                      'horizontal-edge-drag-stops-at-line-end'):
            with self.assertRaisesRegex(ValueError, 'wheel/pointer'):
                self.validate()
            self.report['RowProjectionChecks']['ScrollChecks'].append(check)
        self.validate()

    def test_rejects_wrong_build_mode_os_and_interruption(self):
        for field, value in [('Failure', 'failed'), ('InputInterrupted', True), ('RowProjectionEnabled', False),
                             ('Os', 'Linux'), ('ProductInformationalVersion', 'other'), ('EditorReadOnly', False),
                             ('ConfiguredWindowChars', 32768)]:
            with self.subTest(field=field):
                report = copy.deepcopy(self.report)
                report[field] = value
                with self.assertRaises(ValueError):
                    self.validate(report)

    def test_rejects_missing_duplicate_or_corrupt_source_evidence(self):
        for key, value in [('Checks', self.report['RowProjectionChecks']['Checks'][:-1]),
                           ('Checks', self.report['RowProjectionChecks']['Checks'] + ['source-unchanged']),
                           ('SourceSha256', 'changed'), ('Utf16Characters', 5), ('ScrollSteps', 63)]:
            with self.subTest(key=key):
                report = copy.deepcopy(self.report)
                report['RowProjectionChecks'][key] = value
                with self.assertRaises(ValueError):
                    self.validate(report)

    def test_rejects_nonfinite_incomplete_timings_and_unbounded_rows(self):
        for field, value in [('ScrollSynchronousMilliseconds', [1.] * 63),
                             ('AnimationTickIntervalsMilliseconds', [float('nan')] * 64),
                             ('RowProjectionSamples', [{'DisplayChars': 16385, 'DisplayRows': 2}] * 64),
                             ('RowProjectionSamples', [{'DisplayChars': 1, 'DisplayRows': 513}] * 64),
                             ('ClipboardVerificationFailures', 1), ('ClipboardVerificationChecks', 0)]:
            report = copy.deepcopy(self.report)
            report[field] = value
            with self.assertRaises(ValueError):
                self.validate(report)

    def test_empty_fixture_requires_no_fake_clipboard_copy(self):
        self.expected.update(characters=0, lines=1, maximumLineLength=0)
        self.report.update(SourceChars=0, SourceLines=1, ClipboardVerificationChecks=0)
        result = self.report['RowProjectionChecks']
        result.update(Utf16Characters=0, SourceLineCount=1, MaximumLineLength=0)
        result['Checks'].remove('native-clipboard-original-count')
        result['Lifecycle'] = {'Checks': sorted(LIFECYCLE_CHECKS | {'empty-search-has-no-fabricated-hit'}), 'SearchOutcome': 'empty', 'WorkloadStarts': 1}
        self.validate()

    def test_fixture_uses_raw_utf16_and_crlf_is_one_delimiter(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'mixed.cs'
            body = 'a😀\r\nb\rc\n'
            path.write_bytes(b'\xef\xbb\xbf' + body.encode())
            result = fixture(path)
            self.assertEqual(result['characters'], 9)
            self.assertEqual(result['lines'], 4)
            self.assertEqual(result['maximumLineLength'], 3)
            self.assertEqual(result['bodySha256'], hashlib.sha256(body.encode('utf-16-le')).hexdigest())
            path.write_bytes(b'\xff')
            with self.assertRaises(UnicodeError):
                fixture(path)

    def test_rejects_incomplete_scroll_capture_or_wrong_extent_evidence(self):
        for field, value in [('ScrollChecks', sorted(SCROLL_CHECKS)[:-1]),
                             ('ScrollChecks', sorted(SCROLL_CHECKS) + ['routed-pointer-captured']),
                             ('MaximumLineLength', 4)]:
            report = copy.deepcopy(self.report)
            report['RowProjectionChecks'][field] = value
            with self.assertRaises(ValueError):
                self.validate(report)

    def test_requires_real_tab_reload_search_close_checks(self):
        for key, value in [('Checks', sorted(LIFECYCLE_CHECKS)), ('SearchOutcome', 'empty'), ('WorkloadStarts', 2)]:
            report = copy.deepcopy(self.report)
            report['RowProjectionChecks']['Lifecycle'][key] = value
            with self.assertRaises(ValueError):
                self.validate(report)

    def test_shutdown_requires_finished_work_and_released_sources(self):
        validate_shutdown(self.shutdown)
        for key, value in [('Passed', False), ('HistoryCleared', False), ('ViewHasDocument', True), ('Tabs', 1),
                           ('PendingViewCompleted', False), ('PendingCopyCompleted', False), ('UnreleasedHandles', 1),
                           ('ActiveLeases', 1), ('SurvivingWeakHandles', -1)]:
            report = dict(self.shutdown, **{key: value})
            with self.assertRaises(ValueError):
                validate_shutdown(report)
        with self.assertRaises(ValueError):
            validate_shutdown({})

    def test_collector_checks_shutdown_and_both_fixtures_without_overwriting_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            app = root / 'codeView'
            app.write_bytes(b'app'); (root / 'codeView.dll').write_bytes(b'product')
            inputs = [root / 'a.cs', root / 'b.cs']
            for path in inputs: path.write_bytes(b'abc\n')
            environments = []

            def run(command, **kwargs):
                env = kwargs['env']; environments.append(env)
                report = copy.deepcopy(self.report)
                report['RowProjectionChecks']['SourceSha256'] = fixture(Path(command[1]))['bodySha256']
                target = Path(env['CODEVIEW_GUI_PERF_REPORT'])
                target.write_text(json.dumps(report))
                Path(str(target) + '.shutdown.json').write_text(json.dumps(self.shutdown))
                return SimpleNamespace(returncode=0)

            with mock.patch('check_row_projection.subprocess.run', side_effect=run):
                collect(app, inputs, root / 'out', 1, self.commit, 'macos')
            record = json.loads((root / 'out/runs.json').read_text())
            self.assertTrue(record['validated'])
            self.assertEqual(len(record['runs']), 2)
            self.assertTrue(all(r['shutdown']['Passed'] for r in record['runs']))
            self.assertEqual([x['CODEVIEW_GUI_ROW_SECONDARY'] for x in environments], [str(inputs[1]), str(inputs[0])])
            self.assertTrue(all(p.read_bytes() == b'abc\n' for p in inputs))

    def test_collector_failure_timeout_and_missing_shutdown_stop_without_success(self):
        for failure in ['exit', 'timeout', 'shutdown']:
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                root = Path(directory); app = root / 'codeView'; app.write_bytes(b'app')
                (root / 'codeView.dll').write_bytes(b'product')
                inputs = [root / 'a.cs', root / 'b.cs']
                for path in inputs: path.write_bytes(b'abc\n')

                def run(command, **kwargs):
                    if failure == 'timeout': raise subprocess.TimeoutExpired(command, 90)
                    target = Path(kwargs['env']['CODEVIEW_GUI_PERF_REPORT'])
                    report = copy.deepcopy(self.report)
                    report['RowProjectionChecks']['SourceSha256'] = fixture(inputs[0])['bodySha256']
                    target.write_text(json.dumps(report))
                    return SimpleNamespace(returncode=1 if failure == 'exit' else 0)

                with mock.patch('check_row_projection.subprocess.run', side_effect=run) as launch:
                    with self.assertRaises((RuntimeError, subprocess.TimeoutExpired, FileNotFoundError)):
                        collect(app, inputs, root / 'out', 1, self.commit, 'macos')
                record = json.loads((root / 'out/runs.json').read_text())
                self.assertNotIn('validated', record)
                self.assertEqual(launch.call_count, 1)
                self.assertIn('error', record['runs'][0])

    def test_collector_requires_two_distinct_fixtures_before_creating_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); path = root / 'same.cs'
            for paths in [[path], [path, path]]:
                with self.assertRaises(ValueError):
                    collect(root / 'app', paths, root / 'out', 1, self.commit, 'macos')
                self.assertFalse((root / 'out').exists())


if __name__ == '__main__':
    unittest.main()
