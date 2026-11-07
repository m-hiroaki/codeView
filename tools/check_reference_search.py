"""Run isolated native reference-search checks; no performance or physical-input acceptance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

CHECKS = [
    'original-readonly-document', 'shift-f12-source-identifier', 'fixed-reference-options',
    'all-text-case-sensitive-word-results', 'visible-name-match-disclaimer', 'search-preserves-source',
    'menu-selection-reference-search', 'result-enter-original-cp932-selection', 'result-enter-editor-focus',
    'invalid-selection-keeps-results', 'unicode-selection-and-word-boundaries',
    'missing-workspace-guidance', 'all-input-sha256-unchanged',
]


def validate(report, commit):
    if (report.get('Scenario') != 'native-reference-search' or report.get('Failure') is not None
            or commit not in report.get('ProductInformationalVersion', '')
            or report.get('DisplayMode') != 'full-document-candidate'
            or report.get('ReferenceChecks', {}).get('Passed') is not True
            or report['ReferenceChecks'].get('Checks') != CHECKS):
        raise ValueError('Missing/failed native reference-search checks or wrong build')


def collect(app, output, commit, rounds=3):
    if not 1 <= rounds <= 10:
        raise ValueError('Use 1..10 isolated processes')
    output.mkdir(parents=True, exist_ok=False)
    record = {'commit': commit, 'appHostSha256': hashlib.sha256(app.read_bytes()).hexdigest(),
              'productSha256': hashlib.sha256((app.parent / 'codeView.dll').read_bytes()).hexdigest(), 'runs': []}
    try:
        for number in range(1, rounds + 1):
            target = output / f'reference-{number}.json'
            env = {k: v for k, v in os.environ.items() if not k.startswith('CODEVIEW_GUI_')}
            env.update(CODEVIEW_GUI_PERF_REPORT=str(target), CODEVIEW_GUI_REFERENCE_CHECKS='1')
            row = {'round': number}; record['runs'].append(row)
            with target.with_suffix('.log').open('wb') as log:
                child = subprocess.run([str(app)], env=env, stdout=log, stderr=subprocess.STDOUT, timeout=90)
            row['exitCode'] = child.returncode
            if target.exists():
                row['report'] = json.loads(target.read_text(encoding='utf-8-sig'))
            if child.returncode != 0:
                raise RuntimeError('Native reference search failed')
            validate(row.get('report', {}), commit)
            row['validated'] = True
            print(f'reference-{number}: {len(CHECKS)} checks passed', flush=True)
        record['passed'] = True
    finally:
        (output / 'runs.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--commit', required=True)
    args = parser.parse_args()
    collect(args.app.resolve(), args.output.resolve(), args.commit)
