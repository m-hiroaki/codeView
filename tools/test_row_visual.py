import copy
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

from compare_row_visual import CASES, collect, scalar_boundaries, validate_report


def report(commit, prefix='visual-1.json'):
    cases = []
    for name, (text, start, columns, control, capture) in CASES.items():
        raw = text.encode('utf-16-le'); end = min(start + columns, len(raw) // 2)
        if end not in scalar_boundaries(raw): end -= 1
        displayed = raw[2 * start:2 * end].decode('utf-16-le')
        points = []
        for i in scalar_boundaries(displayed.encode('utf-16-le')):
            dx = 8 if name == 'tab-phase' and i > 0 else 0
            points.append({'DisplayOffset': i, 'SourceOffset': start + i, 'Full': {'X': i * 10, 'Y': 10},
                           'Projected': {'X': i * 10 + dx, 'Y': 10}, 'RelativeXDifference': dx,
                           'RelativeYDifference': 0, 'NativeHitOffset': i, 'HitMapsToSource': True})
        left = name not in {'combining-left', 'zwj-left', 'regional-left', 'hangul-left', 'indic-left'}
        right = name not in {'combining-right', 'zwj-right'}
        equal = name != 'tab-phase'
        bounds = scalar_boundaries(raw)
        context_points = [{'SourceOffset': i, 'FullX': i * 10, 'ContextX': i * 10, 'RelativeXDifference': 0} for i in bounds]
        cases.append({'Name': name, 'OriginalText': text, 'DisplayText': displayed, 'SourceOffset': start, 'Length': end - start,
                      'RequestedColumns': columns, 'Control': control, 'SourceSha256': hashlib.sha256(raw).hexdigest(),
                      'SourceMappingExact': True, 'OriginalCopyExact': True, 'StartGraphemeBoundary': left,
                      'EndGraphemeBoundary': right, 'GeometryEqual': equal, 'MaximumRelativeDifference': 0 if equal else 8,
                      'Accepted': left and right and equal, 'Points': points,
                      'Capture': prefix + '.' + name + '.png' if capture else None,
                      'GraphemeBoundaries': bounds, 'ContextDisplayText': text,
                      'ContextSourceRange': {'SourceOffset': 0, 'Length': bounds[-1], 'VisibleOffset': 0, 'VisibleLength': bounds[-1]},
                      'ContextPoints': context_points, 'ContextMaximumRelativeXDifference': 0, 'ContextAccepted': True})
    return {'Failure': None, 'InputInterrupted': False, 'Scenario': 'native-row-visual-comparison',
            'ProductInformationalVersion': '1+' + commit, 'Os': 'macOS test', 'PhysicalPaint': 'not-measured',
            'RowVisualComparison': {'Acquired': True, 'Accepted': False, 'ContextAccepted': True, 'Cases': cases, 'TabSize': 4, 'FontSize': 14,
                                    'FontFamily': 'Menlo,Consolas,monospace', 'FlowDirection': 'LeftToRight', 'CaptureDpi': 96,
                                    'NativeRenderScaling': 2}}


class RowVisualTests(unittest.TestCase):
    commit = 'a' * 40

    def test_context_comparison_requires_raw_geometry_and_preserves_differences(self):
        r = report(self.commit)
        comparison = r['RowVisualComparison']
        first = comparison['Cases'][0]
        first['ContextPoints'][1].update(ContextX=11, RelativeXDifference=1)
        first.update(ContextMaximumRelativeXDifference=1, ContextAccepted=False)
        comparison['ContextAccepted'] = False
        validate_report(r, self.commit, 'macos')
        for field, value in [('ContextPoints', []), ('ContextMaximumRelativeXDifference', 0), ('ContextAccepted', True)]:
            invalid = copy.deepcopy(r)
            invalid['RowVisualComparison']['Cases'][0][field] = value
            with self.assertRaises(ValueError): validate_report(invalid, self.commit, 'macos')

    def test_rejection_is_preserved_with_exact_source_and_both_os(self):
        r = report(self.commit)
        validate_report(r, self.commit, 'macos')
        r['Os'] = 'Microsoft Windows test'
        validate_report(r, self.commit, 'windows')
        self.assertFalse(r['RowVisualComparison']['Accepted'])

    def test_wrong_mode_build_os_interruption_and_physical_claim_are_rejected(self):
        for key, value in [('Failure', 'error'), ('InputInterrupted', True), ('Scenario', 'other'),
                           ('ProductInformationalVersion', 'wrong'), ('Os', 'Linux'), ('PhysicalPaint', 'verified')]:
            r = report(self.commit); r[key] = value
            with self.assertRaises(ValueError): validate_report(r, self.commit, 'macos')

    def test_missing_cases_altered_conditions_and_lost_rejection_fail(self):
        for key, value in [('Cases', report(self.commit)['RowVisualComparison']['Cases'][:-1]), ('TabSize', 8),
                           ('FontSize', 15), ('NativeRenderScaling', float('nan')), ('Accepted', True)]:
            r = report(self.commit); r['RowVisualComparison'][key] = value
            with self.assertRaises(ValueError): validate_report(r, self.commit, 'macos')

    def test_mapping_copy_geometry_and_grapheme_decisions_must_match_raw_samples(self):
        for key, value in [('OriginalCopyExact', False), ('SourceOffset', 99), ('SourceSha256', 'bad'),
                           ('MaximumRelativeDifference', 99), ('GeometryEqual', False), ('Points', [])]:
            r = report(self.commit); r['RowVisualComparison']['Cases'][0][key] = value
            with self.assertRaises(ValueError): validate_report(r, self.commit, 'macos')
        for name, field in [('combining-left', 'StartGraphemeBoundary'), ('zwj-right', 'EndGraphemeBoundary')]:
            r = report(self.commit); c = next(c for c in r['RowVisualComparison']['Cases'] if c['Name'] == name)
            c[field] = True; c['Accepted'] = True
            with self.assertRaises(ValueError): validate_report(r, self.commit, 'macos')

    def test_nonfinite_or_fabricated_native_positions_and_hit_classification_fail(self):
        for mutate in [lambda p: p['Full'].update(X=float('inf')), lambda p: p.update(RelativeXDifference=1),
                       lambda p: p.update(SourceOffset=123), lambda p: p.update(HitMapsToSource=False)]:
            r = report(self.commit); mutate(r['RowVisualComparison']['Cases'][0]['Points'][0])
            with self.assertRaises(ValueError): validate_report(r, self.commit, 'macos')

    def test_collector_records_acquisition_success_separately_from_rejection_and_captures(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); app = root / 'codeView'; app.write_bytes(b'app'); (root / 'codeView.dll').write_bytes(b'product')

            def run(command, **kwargs):
                env = kwargs['env']; self.assertNotIn('CODEVIEW_GUI_ROWS', env)
                target = Path(env['CODEVIEW_GUI_PERF_REPORT']); r = report(self.commit, target.name)
                target.write_text(json.dumps(r))
                for c in r['RowVisualComparison']['Cases']:
                    if c['Capture']:
                        (target.parent / c['Capture']).write_bytes(b'\x89PNG\r\n\x1a\n' + b'\0\0\0\rIHDR' + struct.pack('>II', 1120, 260))
                return SimpleNamespace(returncode=0)

            with mock.patch('compare_row_visual.subprocess.run', side_effect=run): collect(app, root / 'out', 3, self.commit, 'macos')
            r = json.loads((root / 'out/runs.json').read_text())
            self.assertTrue(r['acquired']); self.assertFalse(r['accepted']); self.assertEqual(len(r['runs']), 3)
            self.assertTrue(all(len(x['captures']) == 4 for x in r['runs']))

    def test_failed_or_timed_out_child_is_never_retried_or_accepted(self):
        for timeout in [False, True]:
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory); app = root / 'codeView'; app.write_bytes(b'app'); (root / 'codeView.dll').write_bytes(b'product')
                result = subprocess.TimeoutExpired([str(app)], 90) if timeout else None
                with mock.patch('compare_row_visual.subprocess.run', side_effect=result, return_value=SimpleNamespace(returncode=1)) as child:
                    with self.assertRaises((RuntimeError, subprocess.TimeoutExpired)): collect(app, root / 'out', 3, self.commit, 'macos')
                r = json.loads((root / 'out/runs.json').read_text())
                self.assertNotIn('acquired', r); self.assertEqual(child.call_count, 1); self.assertIn('error', r['runs'][0])


if __name__ == '__main__': unittest.main()
