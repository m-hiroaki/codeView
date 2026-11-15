import hashlib
from pathlib import Path
import tempfile
import unittest
from zipfile import ZipFile

from package_language_servers import archive, copy_tree, stage_windows
from test_windows_publish import pe_image


class PackageLanguageServersTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='codeView 日本語 ')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def test_download_cache_is_validated_before_use(self):
        source = self.root / 'input.zip'
        source.write_bytes(b'data')
        digest = hashlib.sha256(b'data').hexdigest()
        cache = self.root / 'cache'
        self.assertEqual(archive(cache, source.as_uri(), digest).read_bytes(), b'data')
        (cache / source.name).write_bytes(b'edited')
        with self.assertRaisesRegex(ValueError, 'SHA-256 mismatch'):
            archive(cache, source.as_uri(), digest)

    def test_copy_rejects_archive_entry_outside_payload(self):
        path = self.root / 'input.zip'
        with ZipFile(path, 'w') as package:
            package.writestr('tools/../escaped', b'bad')
        with ZipFile(path) as package, self.assertRaisesRegex(ValueError, 'Unexpected archive path'):
            copy_tree(package, 'tools/', self.root / 'payload')
        self.assertFalse((self.root / 'escaped').exists())

    def test_stages_windows_helpers_and_rejects_missing_build_host(self):
        managed = self.root / 'roslyn.nupkg'
        native = self.root / 'clangd.zip'
        prefix = 'tools/net10.0/win-x64/'
        host = 'BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll'
        with ZipFile(managed, 'w') as package:
            package.writestr('ThirdPartyNotices.rtf', b'notice')
            package.writestr(prefix + 'Microsoft.CodeAnalysis.LanguageServer.exe', pe_image())
            package.writestr(prefix + 'Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json', b'{}')
        with ZipFile(native, 'w') as package:
            package.writestr('clangd_23.1.0/bin/clangd.exe', pe_image())
            package.writestr('clangd_23.1.0/lib/clang/23/include/stddef.h', b'header')
            package.writestr('clangd_23.1.0/LICENSE.TXT', b'license')
        settings = {'version': '23.1.0', 'resourceVersion': '23'}
        with self.assertRaisesRegex(ValueError, 'Missing language server dependency'):
            stage_windows(self.root / 'broken', managed, native, settings)
        with ZipFile(managed, 'a') as package:
            package.writestr(prefix + host, b'build host')
        output = self.root / 'application'
        stage_windows(output, managed, native, settings)
        self.assertEqual((output / 'language-servers/roslyn' / host).read_bytes(), b'build host')
        self.assertEqual((output / 'language-servers/clangd/lib/clang/23/include/stddef.h').read_bytes(), b'header')


if __name__ == '__main__':
    unittest.main()
