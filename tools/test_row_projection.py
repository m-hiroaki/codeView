import copy
import hashlib
from pathlib import Path
import tempfile
import unittest

from check_row_projection import fixture, validate_report


class RowProjectionEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.commit = 'a' * 40
        self.expected = {'characters': 4, 'lines': 2, 'bodySha256': 'body'}
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
                                               'SourceLineCount': 2, 'ScrollSteps': 64},
                       'SourceChars': 4, 'SourceLines': 2, 'ConfiguredWindowChars': 16384, 'EditorReadOnly': True,
                       'UndoCanUndo': False, 'ClipboardVerificationChecks': 1, 'ClipboardVerificationFailures': 0,
                       'ScrollSynchronousMilliseconds': [1.] * 64, 'AnimationTickIntervalsMilliseconds': [16.] * 64,
                       'RowProjectionSamples': [{'DisplayChars': 4, 'DisplayRows': 2}] * 64, 'PhysicalPaint': 'not-measured'}

    def validate(self, report=None):
        validate_report(self.report if report is None else report, self.expected, self.commit, 'macos')

    def test_accepts_complete_record_and_both_native_os(self):
        self.validate()
        self.report['Os'] = 'Microsoft Windows test'
        validate_report(self.report, self.expected, self.commit, 'windows')

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
        self.expected.update(characters=0, lines=1)
        self.report.update(SourceChars=0, SourceLines=1, ClipboardVerificationChecks=0)
        result = self.report['RowProjectionChecks']
        result.update(Utf16Characters=0, SourceLineCount=1)
        result['Checks'].remove('native-clipboard-original-count')
        self.validate()

    def test_fixture_uses_raw_utf16_and_crlf_is_one_delimiter(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'mixed.cs'
            body = 'a😀\r\nb\rc\n'
            path.write_bytes(b'\xef\xbb\xbf' + body.encode())
            result = fixture(path)
            self.assertEqual(result['characters'], 9)
            self.assertEqual(result['lines'], 4)
            self.assertEqual(result['bodySha256'], hashlib.sha256(body.encode('utf-16-le')).hexdigest())
            path.write_bytes(b'\xff')
            with self.assertRaises(UnicodeError):
                fixture(path)


if __name__ == '__main__':
    unittest.main()
