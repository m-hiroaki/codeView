"""Acquire native Unicode comparison evidence; rejection is retained separately from acquisition success."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import struct
import subprocess

from measure_gui_startup import sha256

CASES = {
    "ascii-control": ("0123456789abcdef", 4, 8, True, False),
    "unicode-full-control": ("a\t日本語 e\u0301 👩‍💻 🇯🇵 אבג سلام", 0, 512, True, True),
    "tab-phase": ("a\tb\tc end", 1, 8, False, True),
    "combining-left": ("Ae\u0301B", 2, 2, False, True),
    "combining-right": ("Ae\u0301B", 0, 2, False, False),
    "zwj-left": ("A👩‍💻B", 3, 4, False, False),
    "zwj-right": ("A👩‍💻B", 0, 3, False, False),
    "regional-left": ("A🇯🇵B", 3, 3, False, False),
    "hangul-left": ("A\u1100\u1161\u11a8B", 2, 3, False, False),
    "indic-left": ("Aक्‍षB", 2, 4, False, False),
    "bidi-embedding": ("A\u202babc אבג\u202c Z", 3, 7, False, True),
    "arabic-joining": ("xx سلام yy", 4, 4, False, False),
}


def finite(value):
    return type(value) in (int, float) and math.isfinite(value)


def scalar_boundaries(raw):
    units = struct.unpack('<' + 'H' * (len(raw) // 2), raw)
    return [i for i in range(len(units) + 1) if not (0 < i < len(units) and 0xd800 <= units[i - 1] <= 0xdbff and 0xdc00 <= units[i] <= 0xdfff)]


def validate_report(report, commit, expected_os):
    if (report.get('Failure') is not None or report.get('InputInterrupted') is not False
            or report.get('Scenario') != 'native-row-visual-comparison'
            or not report.get('ProductInformationalVersion', '').endswith('+' + commit)
            or not report.get('Os', '').startswith({'windows': 'Microsoft Windows', 'macos': 'macOS'}[expected_os])
            or report.get('PhysicalPaint') != 'not-measured'):
        raise ValueError('Wrong native build/mode/OS or failed/interrupted comparison')
    comparison = report.get('RowVisualComparison') or {}
    cases = comparison.get('Cases', [])
    if (comparison.get('Acquired') is not True or len(cases) != len(CASES)
            or [c.get('Name') for c in cases] != list(CASES)
            or comparison.get('TabSize') != 4 or not comparison.get('FontFamily')
            or comparison.get('FontSize') != 14 or comparison.get('FlowDirection') != 'LeftToRight'
            or comparison.get('CaptureDpi') != 96 or not finite(comparison.get('NativeRenderScaling'))
            or comparison['NativeRenderScaling'] <= 0):
        raise ValueError('Incomplete or different reference conditions')
    for c in cases:
        name = c['Name']; text, start, columns, control, capture = CASES[name]
        raw = text.encode('utf-16-le'); end = min(start + columns, len(raw) // 2)
        if end not in scalar_boundaries(raw): end -= 1
        displayed = raw[2 * start:2 * end].decode('utf-16-le')
        if (c.get('OriginalText') != text or c.get('DisplayText') != displayed or c.get('SourceOffset') != start
                or c.get('Length') != end - start or c.get('RequestedColumns') != columns or c.get('Control') is not control
                or c.get('SourceSha256') != hashlib.sha256(raw).hexdigest()
                or c.get('SourceMappingExact') is not True or c.get('OriginalCopyExact') is not True
                or bool(c.get('Capture')) != capture):
            raise ValueError('Source/copy/reference changed: ' + name)
        points = c.get('Points', []); offsets = scalar_boundaries(displayed.encode('utf-16-le'))
        if [p.get('DisplayOffset') for p in points] != offsets:
            raise ValueError('Missing scalar geometry samples: ' + name)
        maximum = 0
        for p in points:
            if p.get('SourceOffset') != start + p['DisplayOffset']:
                raise ValueError('Wrong original offset')
            if not all(finite(p[k][axis]) for k in ('Full', 'Projected') for axis in ('X', 'Y')):
                raise ValueError('Nonfinite native geometry')
            for axis, field in [('X', 'RelativeXDifference'), ('Y', 'RelativeYDifference')]:
                delta = (p['Projected'][axis] - points[0]['Projected'][axis]) - (p['Full'][axis] - points[0]['Full'][axis])
                if not finite(p.get(field)) or abs(p[field] - delta) > 1e-6:
                    raise ValueError('Incorrect relative geometry')
                maximum = max(maximum, abs(delta))
            hit = p.get('NativeHitOffset')
            if p.get('HitMapsToSource') is not (type(hit) is int and hit in offsets):
                raise ValueError('Incorrect hit/source mapping classification')
        if not finite(c.get('MaximumRelativeDifference')) or abs(c['MaximumRelativeDifference'] - maximum) > 1e-6:
            raise ValueError('Incorrect maximum geometry difference')
        if any(type(c.get(k)) is not bool for k in ['StartGraphemeBoundary', 'EndGraphemeBoundary', 'GeometryEqual', 'Accepted']):
            raise ValueError('Missing comparison decision')
        accepted = c['StartGraphemeBoundary'] and c['EndGraphemeBoundary'] and maximum <= .1
        if c['GeometryEqual'] is not (maximum <= .1) or c['Accepted'] is not accepted or (control and not accepted):
            raise ValueError('Comparison/control decision mismatch')
        if name in {'combining-left', 'zwj-left', 'regional-left', 'hangul-left', 'indic-left'} and c['StartGraphemeBoundary']:
            raise ValueError('Known split cluster was hidden')
        if name in {'combining-right', 'zwj-right'} and c['EndGraphemeBoundary']:
            raise ValueError('Known split right cluster was hidden')
    if comparison.get('Accepted') is not all(c['Accepted'] for c in cases):
        raise ValueError('Overall rejection was lost')


def collect(app, output, rounds, commit, expected_os):
    if not 1 <= rounds <= 10: raise ValueError('Use 1..10 independent processes')
    if not app.is_file() or not (app.parent / 'codeView.dll').is_file(): raise FileNotFoundError(app)
    output.mkdir(parents=True, exist_ok=False)
    record = {'commit': commit, 'appHostSha256': sha256(app), 'productSha256': sha256(app.parent / 'codeView.dll'),
              'rounds': rounds, 'runs': [], 'scope': 'Native small-reference comparison; rejection retained; not physical paint/input or large-file acceptance.'}
    try:
        for number in range(1, rounds + 1):
            target = output / f'visual-{number}.json'
            env = {k: v for k, v in os.environ.items() if not k.startswith('CODEVIEW_GUI_')}
            env.update(CODEVIEW_GUI_PERF_REPORT=str(target), CODEVIEW_GUI_ROW_VISUAL_CHECKS='1')
            row = {'round': number}; record['runs'].append(row)
            with target.with_suffix('.log').open('wb') as log:
                child = subprocess.run([str(app)], env=env, stdout=log, stderr=subprocess.STDOUT, timeout=90)
            row['exitCode'] = child.returncode
            if target.exists(): row['report'] = json.loads(target.read_text(encoding='utf-8-sig'))
            if child.returncode != 0: raise RuntimeError('Native visual acquisition failed')
            validate_report(row['report'], commit, expected_os)
            captures = {}
            for c in row['report']['RowVisualComparison']['Cases']:
                if c['Capture'] is None: continue
                name = target.name + '.' + c['Name'] + '.png'
                if c['Capture'] != name: raise ValueError('Unexpected capture path')
                p = output / name; data = p.read_bytes()
                if data[:8] != b'\x89PNG\r\n\x1a\n' or len(data) < 24: raise ValueError('Missing/corrupt PNG')
                width, height = struct.unpack('>II', data[16:24])
                if not 1 <= width <= 4096 or not 1 <= height <= 2048: raise ValueError('Invalid capture dimensions')
                captures[name] = {'sha256': sha256(p), 'width': width, 'height': height}
            row.update(validated=True, captures=captures)
            print(f"visual-{number}: acquired; accepted={row['report']['RowVisualComparison']['Accepted']}", flush=True)
        record['acquired'] = True
        record['accepted'] = all(r['report']['RowVisualComparison']['Accepted'] for r in record['runs'])
    except Exception as error:
        if record['runs']: record['runs'][-1]['error'] = str(error)
        raise
    finally:
        (output / 'runs.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--rounds', type=int, default=3)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--os', choices=('windows', 'macos'), required=True)
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9a-f]{40}', args.commit): parser.error('Use a full lowercase commit hash')
    collect(args.app.resolve(), args.output.resolve(), args.rounds, args.commit, args.os)
