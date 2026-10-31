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
        uname.write_text('#!/bin/sh\necho "${TEST_OS:-Darwin}"\n')
        uname.chmod(0o755)
        stub = '#!' + sys.executable + '\n' + '''import json, os, pathlib, sys
name = pathlib.Path(__file__).name
args = sys.argv[1:]
with open(os.environ['TEST_LOG'], 'a') as stream:
    stream.write(json.dumps(dict(tool=name, args=args, cwd=os.getcwd())) + '\\n')
key = 'TEST_PYTHON_EXIT' if name == 'python3' else 'TEST_' + args[0].upper() + '_EXIT'
sys.exit(int(os.environ.get(key, '0')))
'''
        for name in ['dotnet', 'python3']:
            tool = self.bin / name
            tool.write_text(stub)
            tool.chmod(0o755)
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ['PATH'], TEST_LOG=str(self.log))

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
        self.assertEqual(args[args.index('--filter') + 1], 'TestCategory!=Performance')
        self.assertIn('trx;LogFileName=unit.trx', args)
        self.assertIn('-p:PublishAot=false', args)
        self.assertEqual(calls[2]['args'], ['-m', 'unittest', 'discover', '-s', 'tools', '-v'])

    def test_performance_mode_runs_only_performance_and_no_python(self):
        result = self.run_script('--performance')
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls()
        self.assertEqual(len(calls), 2)
        args = calls[1]['args']
        self.assertEqual(args[args.index('--filter') + 1], 'TestCategory=Performance')
        self.assertIn('trx;LogFileName=performance.trx', args)

    def test_invalid_and_extra_arguments_stop_before_tools(self):
        for args in [('unknown',), ('--performance', 'extra'), ('--performance', '')]:
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
