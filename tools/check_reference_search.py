"""Run isolated native reference-search checks; no performance or physical-input acceptance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time

CHECKS = [
    'cli-first-folder-and-both-files-open', 'cli-status-reports-missing-and-skipped-folder',
    'cli-original-path-position-and-encoding-visible', 'chrome-menu-hidden-until-requested', 'chrome-menu-shared-groups',
    'settings-dialog-cancel-does-not-write', 'settings-dialog-saves-only-after-confirmation',
    'original-readonly-document', 'identifier-click-highlights-whole-word', 'identifier-highlights-short-document-without-rail',
    'identifier-click-preserves-drag-selection', 'identifier-click-blank-clears', 'identifier-rail-spans-first-middle-and-last-line',
    'identifier-rail-preserves-scrollbar-hit', 'identifier-and-explicit-search-coexist', 'identifier-escape-clears-highlights-and-rail',
    'chrome-menu-alt-repeat-waits-for-release', 'chrome-menu-alt-releases-and-focuses-menu',
    'chrome-menu-arrow-selects-group', 'chrome-menu-enter-opens-group', 'chrome-menu-escape-restores-editor-focus',
    'chrome-menu-alt-combination-keeps-editor-focus',
    'reading-current-line-uses-editor-layer',
    'syntax-colors-visible', 'f1-source-identifier', 'fixed-reference-options',
    'all-text-case-sensitive-word-results', 'visible-name-match-disclaimer', 'search-preserves-source',
    'menu-selection-reference-search', 'result-enter-original-cp932-selection',
    'syntax-detaches-for-unsupported-tab', 'result-enter-editor-focus',
    'invalid-selection-keeps-results', 'unicode-selection-and-word-boundaries',
    'identifier-before-f12-definition', 'f12-single-definition-auto-selection', 'definition-menu-multiple-candidates',
    'definition-enter-original-selection', 'jump-history-shortcut-restores-origin',
    'jump-history-shortcut-restores-destination', 'jump-history-menu-restores-destination',
    'in-file-deferred-focus-preserves-newer-input',
    'in-file-shortcut-focus', 'in-file-ime-client',
    'in-file-preedit-escape-keeps-panel', 'in-file-preedit-does-not-search', 'in-file-committed-input-matches',
    'in-file-enter-selects-wrapped-match', 'in-file-late-move-preserves-newer-input', 'in-file-escape-restores-editor',
    'folder-deferred-focus-preserves-newer-input', 'folder-shortcut-focus', 'folder-ime-client',
    'folder-preedit-enter-does-not-search',
    'folder-edited-input-shows-applied-conditions', 'folder-enter-starts-search', 'folder-escape-cancels-with-panel-visible',
    'folder-enter-retries-committed-query', 'folder-run-updates-applied-conditions',
    'folder-drag-resizes-results', 'folder-hide-releases-row', 'folder-show-restores-height',
    'tree-drag-resizes-width', 'tree-hide-releases-column', 'tree-show-restores-width', 'missing-workspace-guidance',
    'syntax-long-line-stays-plain', 'syntax-closed-tab-result-discarded',
    'reload-external-update-keeps-open-snapshot', 'reload-wrong-encoding-keeps-document',
    'reload-cp932-replaces-document', 'reload-deleted-file-keeps-document-and-caret',
    'reload-shorter-document-clamps-caret',
    'tree-root-direct-filtered-and-tab-preserved', 'tree-right-expands-selected-folder',
    'tree-language-icon-visible-without-disclosure-button',
    'tree-compact-row-spacing',
    'tree-left-selects-parent', 'tree-left-collapses-folder',
    'tree-background-and-disclosure-double-tap-ignored', 'tree-disclosure-edge-hits-button',
    'tree-double-tap-uses-hit-row-and-shows-empty',
    'tree-enter-opens-source', 'tree-enter-focuses-editor', 'tree-open-preserves-newer-search-focus',
    'tree-f5-refreshes-branch-and-keeps-document',
    'tree-virtualizes-offscreen-items', 'tree-scroll-realizes-offscreen-file',
    'tabs-same-name-distinct-labels', 'tabs-compact-height-and-close-button', 'reading-only-selected-tab-has-underline',
    'tabs-selection-attaches-document', 'tabs-repeat-open-reuses-document',
    'tabs-ctrl-tab-wraps-and-focuses-editor', 'tabs-ctrl-shift-tab-wraps-back',
    'tabs-close-button-keeps-active-document', 'tabs-close-shortcut-from-search-closes-only-active-tab',
    'tabs-close-shortcut-clears-last-tab',
    'empty-view-offers-file-and-folder', 'ink-open-buttons-show-primary-and-secondary',
    'ink-button-ring-follows-keyboard-focus', 'document-view-hides-empty-actions',
    'tabs-overflow-scrollbar-below-items', 'tabs-overflow-compact-bar-scrolls', 'tabs-overflow-restores-compact-height',
    'tab-view-distinct-scroll-offsets', 'line-number-gap-without-separator-after-horizontal-scroll',
    'tab-view-restores-first-caret-and-scroll',
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
    'drop-move-only-rejected', 'drop-mixed-invalid-rejected-atomically', 'drop-over-limit-reports-rejection',
    'drop-file-and-first-folder-open-read-only',
    'single-file-loading-status-is-bound', 'single-file-empty-is-readable',
    'single-file-binary-is-rejected-with-status', 'single-file-valid-open-recovers-after-failure',
    'syntax-large-source-visible-without-color', 'syntax-large-source-search-selects-original-tail',
    'workspace-large-regex-result-is-committed-at-original-tail',
    'workspace-large-result-reuses-tab-and-selects-original-tail', 'workspace-large-results-clear-keeps-document',
    'method-definition-partial-search-does-not-auto-jump', 'method-definition-excludes-comment-and-string',
    'method-reference-includes-comment-and-string',
    'basic-definition-ClassTarget', 'basic-definition-StructTarget', 'basic-definition-InterfaceTarget',
    'basic-definition-EnumTarget', 'basic-definition-RecordTarget', 'basic-definition-DelegateTarget',
    'basic-definition-PropertyTarget', 'basic-definition-FieldTarget', 'basic-definition-missing-keeps-source',
    'c-f12-jumps-to-body-without-opening-panel', 'cpp-f12-chooses-among-c-and-cpp-definitions',
    'cpp-candidate-enter-opens-original-header-position',
    'original-copy-keeps-mixed-newlines-and-unicode', 'original-copy-empty-final-line-keeps-clipboard',
    'all-input-sha256-unchanged',
]


SEMANTIC_CHECKS = [
    'semantic-startup-uses-simple-search', 'semantic-folder-open-enables-roslyn', 'semantic-folder-open-starts-roslyn-before-f12',
    'semantic-cs-f12-selects-original-definition', 'semantic-cs-f1-finds-symbol-reference',
    'semantic-dialog-omits-server-paths', 'semantic-invalid-project-keeps-existing-session',
    'semantic-workspace-switch-stops-server', 'semantic-manual-enable-defers-server-launch',
    'semantic-c-f12-selects-original-definition', 'semantic-c-f1-finds-symbol-reference',
    'semantic-cpp-f12-selects-original-definition', 'semantic-cpp-f1-finds-symbol-reference',
    'semantic-restarts-after-server-crash', 'semantic-switches-back-to-name-search',
    'semantic-server-active-before-app-exit',
]


def validate(report, commit, checks=CHECKS):
    if (report.get('Scenario') != 'native-reference-search' or report.get('Failure') is not None
            or commit not in report.get('ProductInformationalVersion', '')
            or report.get('DisplayMode') != 'full-document-candidate'
            or report.get('ReferenceChecks', {}).get('Passed') is not True
            or report['ReferenceChecks'].get('Checks') != checks):
        raise ValueError('Missing/failed native reference-search checks or wrong build')


def create_semantic_fixture(root, measure=False):
    managed = root / "managed"
    native = root / "native"
    managed.mkdir(parents=True)
    native.mkdir()
    sources = {
        managed / "Demo.csproj": (
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0'
            '</TargetFramework></PropertyGroup></Project>'
        ),
        managed / "Caller.cs": "class Caller { int Call() => Library.Run(1); /* Run */ }",
        managed / "Target.cs": "public class Library { public static int Run(int n) => n; }",
        native / "library.h": "int RunC(int);\nint RunCpp(int);\n",
    }
    commands = []
    for extension, compiler, symbol in (("c", "clang", "RunC"), ("cpp", "clang++", "RunCpp")):
        for title, text in (
            ("Caller", f'int use_{extension}() {{ return {symbol}(1); }}'),
            ("Target", f'int {symbol}(int n) {{ return n; }}'),
        ):
            file = native / f"{title}.{extension}"
            sources[file] = '#include <stdarg.h>\n#include "library.h"\n' + text + "\n"
            commands.append(dict(directory=str(native), file=file.name, arguments=[compiler, "-c", file.name]))
    sources[native / "compile_commands.json"] = json.dumps(commands)
    if measure:
        sources[managed / "Caller.cs"] += "\n" + "// Scroll while the language server loads the project.\n" * 600
    for file, text in sources.items():
        file.write_text(text, encoding="utf-8")
    subprocess.run(["dotnet", "restore", str(managed / "Demo.csproj"), "--ignore-failed-sources"], check=True, timeout=45)
    return list(sources)


def process_is_running(pid):
    if os.name != 'nt':
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        return True
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x00100000, False, pid)  # SYNCHRONIZE
    if not handle:
        if ctypes.get_last_error() == 87:  # ERROR_INVALID_PARAMETER, no such process
            return False
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        return kernel.WaitForSingleObject(handle, 0) == 0x102  # WAIT_TIMEOUT
    finally:
        kernel.CloseHandle(handle)


def measure_processes(args, env, log):
    """Sample the app and its descendants on macOS during an ordinary semantic GUI run."""
    samples = []
    with subprocess.Popen(args, env=env, stdout=log, stderr=subprocess.STDOUT) as child:
        deadline = time.monotonic() + 90
        try:
            while child.poll() is None:
                if time.monotonic() > deadline:
                    raise subprocess.TimeoutExpired(args, 90)
                rows = {}
                processes = subprocess.check_output(
                    ['ps', '-axo', 'pid=,ppid=,rss=,time='], text=True, timeout=5,
                )
                for line in processes.splitlines():
                    pid, parent, rss, cpu = line.split()
                    seconds = 0.0
                    for component in cpu.split(':'):
                        seconds = seconds * 60 + float(component)
                    rows[int(pid)] = (int(parent), int(rss), seconds)
                included = {child.pid}
                while True:
                    descendants = {pid for pid, (parent, _, _) in rows.items() if parent in included}
                    if descendants <= included:
                        break
                    included |= descendants
                if child.pid in rows:
                    samples.append({pid: rows[pid][1:] for pid in included if pid in rows})
                time.sleep(0.10)
        finally:
            if child.poll() is None:
                child.kill()
                child.wait()
    active = [sample for sample in samples if len(sample) > 1]
    if len(active) < 2:
        raise ValueError('No active language server was sampled')
    child_cpu = {}
    for sample in active:
        for pid, (_, cpu) in sample.items():
            if pid != child.pid:
                child_cpu[pid] = max(cpu, child_cpu.get(pid, 0))
    resources = {
        'ActiveSamples': len(active),
        'AppPeakResidentKiB': max(sample[child.pid][0] for sample in active),
        'DescendantsPeakResidentKiB': max(sum(rss for pid, (rss, _) in sample.items() if pid != child.pid) for sample in active),
        'CombinedPeakResidentKiB': max(sum(rss for rss, _ in sample.values()) for sample in active),
        'AppCpuSecondsDuringSampledActivity': active[-1][child.pid][1] - active[0][child.pid][1],
        'DescendantsObservedCpuSeconds': sum(child_cpu.values()),
    }
    return child.returncode, resources


def collect(app, output, commit, rounds=3, semantic=False, measure=False, native_aot=False):
    if not 1 <= rounds <= 10:
        raise ValueError('Use 1..10 isolated processes')
    output.mkdir(parents=True, exist_ok=False)
    product = app if native_aot else app.parent / 'codeView.dll'
    record = {'commit': commit, 'appHostSha256': hashlib.sha256(app.read_bytes()).hexdigest(),
              'productSha256': hashlib.sha256(product.read_bytes()).hexdigest(), 'runs': []}
    try:
        for number in range(1, rounds + 1):
            target = output / f'reference-{number}.json'
            env = {k: v for k, v in os.environ.items() if not k.startswith('CODEVIEW_GUI_')}
            env.update(CODEVIEW_GUI_PERF_REPORT=str(target), CODEVIEW_GUI_REFERENCE_CHECKS='1')
            fixture = output / f'reference-{number}.cli'
            first = fixture / 'first root'
            second = fixture / 'second root'
            first.mkdir(parents=True)
            second.mkdir()
            first_file = first / '最初.txt'
            last_file = second / '最後.txt'
            first_file.write_bytes('最初の本文'.encode('utf-8'))
            last_file.write_bytes('日本語の本文'.encode('cp932'))
            inputs = [first_file, last_file]
            checks = SEMANTIC_CHECKS if semantic else CHECKS
            if semantic:
                root = output / f'semantic-{number}'
                inputs.extend(create_semantic_fixture(root, measure))
                env.update(CODEVIEW_GUI_SEMANTIC_ROOT=str(root), CODEVIEW_GUI_SEMANTIC_CACHE=str(output / 'clangd-cache'))
                if measure:
                    env.update(CODEVIEW_GUI_SEMANTIC_MEASURE='1')
                for key in ('CODEVIEW_TEST_ROSLYN', 'CODEVIEW_TEST_CLANGD'):
                    env.pop(key, None)
            original_hashes = [hashlib.sha256(path.read_bytes()).hexdigest() for path in inputs]
            row = {'round': number}; record['runs'].append(row)
            args = [str(app), str(first), str(first_file), str(fixture / 'missing.txt'), str(second), str(last_file)]
            with target.with_suffix('.log').open('wb') as log:
                if measure:
                    row['exitCode'], row['Resources'] = measure_processes(args, env, log)
                else:
                    row['exitCode'] = subprocess.run(
                        args, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=90,
                    ).returncode
            if target.exists():
                row['report'] = json.loads(target.read_text(encoding='utf-8-sig'))
            if row['exitCode'] != 0:
                failure = row.get('report', {}).get('Failure') or f"exit code {row['exitCode']}"
                raise RuntimeError(f'Native reference search failed: {failure}')
            validate(row.get('report', {}), commit, checks)
            if native_aot and row['report'].get('NativeAot') is not True:
                raise ValueError('The bundled acceptance probe was not Native AOT')
            if semantic:
                stopped = row['report']['ReferenceChecks']['Semantic']['ShutdownPids']
                if not stopped or any(process_is_running(pid) for pid in stopped):
                    raise ValueError('A language server survived application shutdown')
            if [hashlib.sha256(path.read_bytes()).hexdigest() for path in inputs] != original_hashes:
                raise ValueError('CLI source changed during native validation')
            row['validated'] = True
            print(f'reference-{number}: {len(checks)} checks passed', flush=True)
        record['passed'] = True
    finally:
        (output / 'runs.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--rounds', type=int, default=3)
    parser.add_argument('--semantic', action='store_true')
    parser.add_argument('--native-aot', action='store_true')
    parser.add_argument('--measure', action='store_true')
    args = parser.parse_args()
    if args.measure and (not args.semantic or sys.platform != 'darwin'):
        parser.error('--measure requires --semantic on macOS')
    if args.native_aot and (not args.semantic or args.measure):
        parser.error('--native-aot requires --semantic without --measure')
    collect(args.app.resolve(), args.output.resolve(), args.commit, args.rounds, args.semantic, args.measure, args.native_aot)
