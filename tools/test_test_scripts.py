"""Verify test-runner orchestration using fake tools, without recursive test runs."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


@unittest.skipIf(os.name == 'nt', 'POSIX shell runner tests')
class TestRunnerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='codeView tests ! ')
        self.addCleanup(self.temporary.cleanup)
        self.repo = Path(self.temporary.name)
        shutil.copy2(ROOT / 'test-mac.sh', self.repo)
        self.bin = self.repo / 'fake-bin'
        self.bin.mkdir()
        self.log = self.repo / 'calls.jsonl'
        uname = self.bin / 'uname'
        uname.write_text('#!/bin/sh\nif [ "$1" = "-m" ]; then echo "${TEST_ARCH:-arm64}"; else echo "${TEST_OS:-Darwin}"; fi\n')
        uname.chmod(0o755)
        stub = '#!' + sys.executable + '\n' + '''import json, os, pathlib, sys
name = pathlib.Path(__file__).name
args = sys.argv[1:]
with open(os.environ['TEST_LOG'], 'a') as stream:
    stream.write(json.dumps(dict(tool=name, args=args, cwd=os.getcwd(),
                                 servers=[os.environ.get(key) for key in ['CODEVIEW_TEST_ROSLYN', 'CODEVIEW_TEST_CLANGD']],
                                 semantic_probe=os.environ.get('CODEVIEW_SEMANTIC_PROBE'))) + '\\n')
if name == 'build-mac.sh':
    key = 'TEST_BUILD_EXIT'
    if os.environ.get(key, '0') == '0' and not os.environ.get('TEST_INCOMPLETE_BUNDLE'):
        app = pathlib.Path('artifacts/publish') / args[0] / 'codeView.app'
        for executable in [app / 'Contents/MacOS/codeView',
                           pathlib.Path(os.environ['CODEVIEW_TEST_ROSLYN']),
                           pathlib.Path(os.environ['CODEVIEW_TEST_CLANGD'])]:
            executable.parent.mkdir(parents=True, exist_ok=True)
            executable.touch()
            executable.chmod(0o755)
elif name == 'dotnet' and args[:2] == ['tool', 'restore']:
    key = 'TEST_TOOL_RESTORE_EXIT'
elif name == 'dotnet' and args[:3] == ['tool', 'run', 'reportgenerator']:
    key = 'TEST_REPORT_EXIT'
    if os.environ.get(key, '0') == '0' and not os.environ.get('TEST_NO_HTML'):
        target = pathlib.Path(next(arg.split(':', 1)[1] for arg in args if arg.startswith('-targetdir:')))
        target.mkdir(parents=True, exist_ok=True)
        (target / 'index.html').write_text('<html>coverage</html>')
else:
    key = 'TEST_PYTHON_EXIT' if name == 'python3' else 'TEST_' + args[0].upper() + '_EXIT'
sys.exit(int(os.environ.get(key, '0')))
'''
        for name in ['dotnet', 'python3']:
            tool = self.bin / name
            tool.write_text(stub)
            tool.chmod(0o755)
        builder = self.repo / 'build-mac.sh'
        builder.write_text(stub)
        builder.chmod(0o755)
        for executable in self.bundle_paths():
            executable.parent.mkdir(parents=True, exist_ok=True)
            executable.touch()
            executable.chmod(0o755)
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ['PATH'], TEST_LOG=str(self.log))

    def bundle_paths(self, rid='osx-arm64'):
        app = self.repo / 'artifacts' / 'publish' / rid / 'codeView.app'
        return (app / 'Contents/MacOS/codeView',
                app / 'Contents/Helpers/Roslyn.app/Contents/MacOS/Microsoft.CodeAnalysis.LanguageServer',
                app / 'Contents/Helpers/clangd')

    def run_script(self, *args, **env):
        return subprocess.run([str(self.repo / 'test-mac.sh'), *args], cwd=self.repo.parent,
                              env=dict(self.env, **env), capture_output=True, text=True)

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def test_normal_mode_restores_runs_units_then_python_from_repo(self):
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls()
        self.assertEqual([call['args'][0] for call in calls], ['restore', 'test', '-m'])
        self.assertTrue(all(Path(call['cwd']).samefile(self.repo) for call in calls))
        self.assertIn('--locked-mode', calls[0]['args'])
        args = calls[1]['args']
        self.assertEqual(calls[1]['servers'], list(map(str, self.bundle_paths()[1:])))
        self.assertEqual(args[args.index('--filter') + 1], 'TestCategory!=Performance')
        self.assertIn('trx;LogFileName=unit.trx', args)
        self.assertIn('-p:PublishAot=false', args)
        self.assertNotIn('--collect', args)
        self.assertEqual(calls[2]['args'], ['-m', 'unittest', 'discover', '-s', 'tools', '-v'])

    def test_missing_native_bundle_is_built_before_coverage(self):
        result = self.run_script('--coverage', TEST_ARCH='x86_64', CODEVIEW_SEMANTIC_PROBE='1')
        self.assertEqual(result.returncode, 0, result.stderr)
        build, restore, test, *_ = self.calls()
        self.assertEqual((build['tool'], build['args']), ('build-mac.sh', ['osx-x64']))
        self.assertEqual(build['semantic_probe'], '')
        self.assertEqual(restore['args'][0], 'restore')
        self.assertEqual(test['args'][0], 'test')
        self.assertEqual(test['servers'], list(map(str, self.bundle_paths('osx-x64')[1:])))

    def test_missing_or_nonexecutable_helper_causes_rebuild(self):
        self.bundle_paths()[1].unlink()
        self.bundle_paths()[2].chmod(0o644)
        result = self.run_script(CODEVIEW_TEST_ROSLYN='/ignored', CODEVIEW_TEST_CLANGD='/ignored')
        self.assertEqual(result.returncode, 0, result.stderr)
        build, _, test, _ = self.calls()
        self.assertEqual((build['tool'], build['args']), ('build-mac.sh', ['osx-arm64']))
        self.assertEqual(test['servers'], list(map(str, self.bundle_paths()[1:])))

    def test_publish_failure_stops_before_tests_and_keeps_previous_coverage(self):
        self.bundle_paths()[0].unlink()
        html = self.repo / 'artifacts/test-results/macos/coverage/html/index.html'
        html.parent.mkdir(parents=True)
        html.write_text('previous report')
        result = self.run_script('--coverage', TEST_BUILD_EXIT='29')
        self.assertEqual(result.returncode, 29)
        self.assertEqual([call['tool'] for call in self.calls()], ['build-mac.sh'])
        self.assertEqual(html.read_text(), 'previous report')

    def test_incomplete_publish_stops_before_tests(self):
        self.bundle_paths()[0].unlink()
        result = self.run_script(TEST_INCOMPLETE_BUNDLE='1')
        self.assertEqual(result.returncode, 1)
        self.assertIn('Published app or language servers are missing', result.stderr)
        self.assertEqual([call['tool'] for call in self.calls()], ['build-mac.sh'])

    def test_performance_mode_runs_only_performance_and_no_python(self):
        result = self.run_script('--performance')
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls()
        self.assertEqual(len(calls), 2)
        args = calls[1]['args']
        self.assertEqual(args[args.index('--filter') + 1], 'TestCategory=Performance')
        self.assertIn('trx;LogFileName=performance.trx', args)
        self.assertNotIn('--collect', args)

    def test_coverage_mode_instruments_units_and_keeps_results_separate(self):
        result = self.run_script('--coverage')
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls()
        self.assertEqual([call['args'][0] for call in calls], ['restore', 'test', 'tool', 'tool', '-m'])
        args = calls[1]['args']
        self.assertEqual(args[args.index('--filter') + 1], 'TestCategory!=Performance')
        self.assertEqual(args[args.index('--collect') + 1], 'XPlat Code Coverage')
        self.assertEqual(Path(args[args.index('--settings') + 1]), self.repo / 'coverage.runsettings')
        self.assertEqual(Path(args[args.index('--results-directory') + 1]),
                         self.repo / 'artifacts/test-results/macos/coverage/current')
        self.assertIn('trx;LogFileName=coverage.trx', args)
        self.assertEqual(calls[2]['args'], ['tool', 'restore', '--tool-manifest',
                                          str(self.repo / '.config/dotnet-tools.json')])
        report_args = calls[3]['args']
        self.assertEqual(report_args[:4], ['tool', 'run', 'reportgenerator', '--'])
        self.assertIn('-reports:' + str(self.repo / 'artifacts/test-results/macos/coverage/current/*/coverage.cobertura.xml'), report_args)
        self.assertIn('-targetdir:' + str(self.repo / 'artifacts/test-results/macos/coverage/html'), report_args)
        self.assertIn('-sourcedirs:' + str(self.repo / 'src/codeView'), report_args)
        self.assertIn('-reporttypes:Html', report_args)

    def test_coverage_clears_current_results_and_html_but_keeps_other_runs(self):
        coverage = self.repo / 'artifacts/test-results/macos/coverage'
        for subdir in ['current/old-run', 'html', 'previous-run']:
            directory = coverage / subdir
            directory.mkdir(parents=True)
            (directory / 'old.txt').write_text('old result')
        result = self.run_script('--coverage')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse((coverage / 'current/old-run').exists())
        self.assertFalse((coverage / 'html/old.txt').exists())
        self.assertTrue((coverage / 'html/index.html').exists())
        self.assertTrue((coverage / 'previous-run/old.txt').exists())

    def test_report_restore_failure_stops_generation_and_python(self):
        result = self.run_script('--coverage', TEST_TOOL_RESTORE_EXIT='24')
        self.assertEqual(result.returncode, 24)
        self.assertEqual(len(self.calls()), 3)

    def test_report_failure_stops_python_and_preserves_exit(self):
        result = self.run_script('--coverage', TEST_REPORT_EXIT='25')
        self.assertEqual(result.returncode, 25)
        self.assertEqual(len(self.calls()), 4)

    def test_missing_html_is_failure_even_when_generator_returns_success(self):
        result = self.run_script('--coverage', TEST_NO_HTML='1')
        self.assertEqual(result.returncode, 1)
        self.assertIn('HTML coverage report was not generated', result.stderr)
        self.assertEqual(len(self.calls()), 4)

    def test_coverage_failure_preserves_exit_and_stops_python(self):
        result = self.run_script('--coverage', TEST_TEST_EXIT='42')
        self.assertEqual(result.returncode, 42)
        self.assertEqual(len(self.calls()), 2)

    def test_invalid_and_extra_arguments_stop_before_tools(self):
        for args in [('unknown',), ('--performance', 'extra'), ('--performance', ''),
                     ('--coverage', '--performance'), ('--coverage', '--coverage'),
                     ('--coverage', '')]:
            with self.subTest(args=args):
                self.assertEqual(self.run_script(*args).returncode, 2)
        self.assertEqual(self.calls(), [])

    def test_non_mac_host_stops_before_tools(self):
        self.assertEqual(self.run_script(TEST_OS='Linux').returncode, 1)
        self.assertEqual(self.calls(), [])

    def test_restore_failure_preserves_exit_and_does_not_run_tests(self):
        self.assertEqual(self.run_script(TEST_RESTORE_EXIT='23').returncode, 23)
        self.assertEqual(len(self.calls()), 1)

    def test_dotnet_failure_preserves_exit_and_does_not_run_python(self):
        self.assertEqual(self.run_script(TEST_TEST_EXIT='41').returncode, 41)
        self.assertEqual(len(self.calls()), 2)

    def test_python_failure_preserves_exit(self):
        self.assertEqual(self.run_script(TEST_PYTHON_EXIT='17').returncode, 17)
        self.assertEqual(len(self.calls()), 3)


if __name__ == '__main__':
    unittest.main()
