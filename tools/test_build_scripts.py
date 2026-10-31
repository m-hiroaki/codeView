"""Exercise macOS packaging without downloading or invoking the real SDK."""
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


@unittest.skipIf(os.name == "nt", "macOS shell packaging tests require POSIX")
class MacBuildTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="codeView build ! ")
        self.addCleanup(self.temporary.cleanup)
        self.repo = Path(self.temporary.name)
        shutil.copy2(ROOT / "build-mac.sh", self.repo)
        (self.repo / "build").mkdir()
        shutil.copy2(ROOT / "build/Info.plist", self.repo / "build/Info.plist")
        project = self.repo / "src/codeView"
        project.mkdir(parents=True)
        (project / "codeView.csproj").write_text("<Project />")
        self.lock = project / "packages.lock.json"
        self.lock.write_text('{"version":1,"dependencies":{}}\n')
        self.bin = self.repo / "fake-bin"
        self.bin.mkdir()
        self.executable("uname", '''#!/bin/sh
if [ "$1" = "-s" ]; then echo "${TEST_OS:-Darwin}"; else echo "${TEST_ARCH:-arm64}"; fi
''')
        self.executable("dotnet", '''#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
with open(os.environ['TEST_LOG'], 'a') as f: f.write(json.dumps(args)+'\\n')
if args[0] == 'restore': sys.exit(int(os.environ.get('TEST_RESTORE_EXIT', '0')))
if args[0] != 'publish': sys.exit(99)
status = int(os.environ.get('TEST_PUBLISH_EXIT', '0'))
if status: sys.exit(status)
output = pathlib.Path(args[args.index('-o') + 1]); output.mkdir(parents=True)
if os.environ.get('TEST_MISSING_EXECUTABLE') != '1':
    app = output / 'codeView'; app.write_text('#!/bin/sh\\nexit 0\\n'); app.chmod(0o755)
(output / 'libAvaloniaNative.dylib').write_bytes(b'native dependency')
''')
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ['PATH'],
                        TEST_LOG=str(self.repo / "calls.jsonl"))

    def executable(self, name, content):
        path = self.bin / name
        path.write_text(content)
        path.chmod(0o755)

    def run_build(self, *args, **env):
        return subprocess.run([str(self.repo / "build-mac.sh"), *args], cwd=self.repo.parent,
                              env=dict(self.env, **env), capture_output=True, text=True)

    def calls(self):
        path = self.repo / "calls.jsonl"
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    def test_default_arm64_packages_self_contained_app_and_keeps_source_lock(self):
        original = self.lock.read_bytes()
        result = self.run_build()
        self.assertEqual(result.returncode, 0, result.stderr)
        app = self.repo / "artifacts/publish/osx-arm64/codeView.app"
        self.assertTrue((app / "Contents/MacOS/codeView").stat().st_mode & 0o111)
        self.assertTrue((app / "Contents/MacOS/libAvaloniaNative.dylib").exists())
        with (app / "Contents/Info.plist").open('rb') as stream:
            self.assertEqual(plistlib.load(stream)['CFBundleExecutable'], 'codeView')
        self.assertEqual(self.lock.read_bytes(), original)
        self.assertEqual([call[0] for call in self.calls()], ['restore', 'publish'])
        publish = self.calls()[1]
        self.assertEqual(publish[publish.index('-r') + 1], 'osx-arm64')
        self.assertEqual(publish[publish.index('--self-contained') + 1], 'true')
        self.assertIn('-p:CodeViewPerformanceProbe=false', publish)
        self.assertIn('-p:PublishAot=true', publish)
        self.assertIn('-p:PublishTrimmed=true', publish)
        self.assertIn('-p:IlcTreatWarningsAsErrors=true', publish)
        self.assertIn('-p:ILLinkTreatWarningsAsErrors=true', publish)
        self.assertTrue((app.parent / 'publish.packages.lock.json').exists())
        self.assertEqual(list((self.repo / 'artifacts/build/osx-arm64').glob('stage.*')), [])

    def test_default_intel_and_explicit_cross_architecture(self):
        for args, host, expected in [((), 'x86_64', 'osx-x64'), (('osx-arm64',), 'x86_64', 'osx-arm64')]:
            with self.subTest(args=args):
                result = self.run_build(*args, TEST_ARCH=host)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertTrue((self.repo / f'artifacts/publish/{expected}/codeView.app').exists())

    def test_rejects_invalid_rid_extra_arguments_and_unknown_host_before_sdk(self):
        for args, env in [(('win-x64',), {}), (('osx-arm64', 'extra'), {}), ((), {'TEST_ARCH': 'unknown'})]:
            with self.subTest(args=args, env=env):
                self.assertEqual(self.run_build(*args, **env).returncode, 2)
        self.assertEqual(self.calls(), [])

    def test_rejects_non_mac_host(self):
        self.assertEqual(self.run_build(TEST_OS='Linux').returncode, 1)
        self.assertEqual(self.calls(), [])

    def test_restore_failure_stops_before_publish_and_preserves_exit_code(self):
        self.assertEqual(self.run_build(TEST_RESTORE_EXIT='23').returncode, 23)
        self.assertEqual([call[0] for call in self.calls()], ['restore'])

    def test_failed_publish_preserves_previous_app_and_cleans_staging(self):
        old = self.repo / 'artifacts/publish/osx-arm64/codeView.app/old-marker'
        old.parent.mkdir(parents=True)
        old.write_text('previous successful build')
        self.assertEqual(self.run_build(TEST_PUBLISH_EXIT='37').returncode, 37)
        self.assertEqual(old.read_text(), 'previous successful build')
        self.assertEqual(list((self.repo / 'artifacts/build/osx-arm64').glob('stage.*')), [])

    def test_success_without_executable_is_failure(self):
        self.assertEqual(self.run_build(TEST_MISSING_EXECUTABLE='1').returncode, 1)
        self.assertFalse((self.repo / 'artifacts/publish/osx-arm64/codeView.app').exists())


if __name__ == '__main__':
    unittest.main()
