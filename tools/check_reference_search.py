"""Run isolated native reference-search checks; no performance or physical-input acceptance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

CHECKS = [
    'original-readonly-document', 'syntax-colors-visible', 'shift-f12-source-identifier', 'fixed-reference-options',
    'all-text-case-sensitive-word-results', 'visible-name-match-disclaimer', 'search-preserves-source',
    'menu-selection-reference-search', 'result-enter-original-cp932-selection',
    'syntax-detaches-for-unsupported-tab', 'result-enter-editor-focus',
    'invalid-selection-keeps-results', 'unicode-selection-and-word-boundaries',
    'f12-single-definition-auto-selection', 'definition-menu-multiple-candidates',
    'definition-enter-original-selection', 'in-file-shortcut-focus', 'in-file-ime-client',
    'in-file-preedit-escape-keeps-panel', 'in-file-preedit-does-not-search', 'in-file-committed-input-matches',
    'in-file-enter-selects-wrapped-match', 'in-file-escape-restores-editor',
    'folder-shortcut-focus', 'folder-ime-client', 'folder-preedit-enter-does-not-search',
    'folder-edited-input-shows-applied-conditions', 'folder-enter-starts-search', 'folder-escape-cancels-with-panel-visible',
    'folder-enter-retries-committed-query', 'folder-run-updates-applied-conditions',
    'folder-drag-resizes-results', 'folder-hide-releases-row', 'folder-show-restores-height',
    'tree-drag-resizes-width', 'tree-hide-releases-column', 'tree-show-restores-width', 'missing-workspace-guidance',
    'syntax-long-line-stays-plain', 'syntax-closed-tab-result-discarded',
    'reload-external-update-keeps-open-snapshot', 'reload-wrong-encoding-keeps-document',
    'reload-cp932-replaces-document', 'reload-deleted-file-keeps-document-and-caret',
    'reload-shorter-document-clamps-caret',
    'tree-root-direct-filtered-and-tab-preserved', 'tree-right-expands-selected-folder',
    'tree-left-selects-parent', 'tree-left-collapses-folder',
    'tree-background-and-disclosure-double-tap-ignored', 'tree-double-tap-uses-hit-row-and-shows-empty',
    'tree-enter-opens-source', 'tree-enter-focuses-editor', 'tree-f5-refreshes-branch-and-keeps-document',
    'tree-virtualizes-offscreen-items', 'tree-scroll-realizes-offscreen-file',
    'tabs-same-name-distinct-labels', 'tabs-selection-attaches-document', 'tabs-repeat-open-reuses-document',
    'tabs-ctrl-tab-wraps-and-focuses-editor', 'tabs-ctrl-shift-tab-wraps-back',
    'tabs-close-button-keeps-active-document', 'tabs-close-shortcut-clears-last-tab',
    'tab-view-distinct-scroll-offsets', 'tab-view-restores-first-caret-and-scroll',
    'tab-view-restores-first-search-conditions', 'tab-view-restores-second-caret-and-scroll',
    'tab-view-restores-second-search-conditions', 'tab-view-rapid-query-change-then-switch-keeps-current-results',
    'tab-view-reopened-search-uses-last-query', 'tab-view-close-releases-source-and-restores-search',
    'workspace-first-root-search-results', 'workspace-switch-replaces-tree',
    'workspace-switch-invalidates-previous-results', 'workspace-old-row-cannot-replace-current-view',
    'workspace-rapid-replacement-publishes-last-search-only', 'workspace-search-uses-new-root', 'search-double-tap-background-does-not-open-selection',
    'search-double-tap-opens-hit-and-selects-text', 'search-double-tap-stale-hit-keeps-document-without-selection',
    'workspace-close-preserves-open-tabs',
    'tree-links-visible-in-name-order', 'tree-folder-link-does-not-expand',
    'tree-links-not-followed-by-workspace-search', 'tree-file-link-explicitly-opens',
    'tree-folder-link-explicit-root-and-cycle-blocked', 'tree-search-resolves-explicit-root',
    'drop-move-only-rejected', 'drop-mixed-invalid-rejected-atomically',
    'drop-file-and-first-folder-open-read-only',
    'single-file-loading-status-is-bound', 'single-file-empty-is-readable',
    'single-file-binary-is-rejected-with-status', 'single-file-valid-open-recovers-after-failure',
    'syntax-large-source-visible-without-color', 'syntax-large-source-search-selects-original-tail',
    'workspace-large-regex-result-is-committed-at-original-tail',
    'workspace-large-result-reuses-tab-and-selects-original-tail', 'workspace-large-results-clear-keeps-document',
    'all-input-sha256-unchanged',
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
                failure = row.get('report', {}).get('Failure') or f'exit code {child.returncode}'
                raise RuntimeError(f'Native reference search failed: {failure}')
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
