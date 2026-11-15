"""Stage the pinned Roslyn and clangd packages inside a Native AOT distribution."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import tempfile
from urllib.request import urlopen
from zipfile import ZipFile

from check_windows_publish import native_x64


ROOT = Path(__file__).resolve().parents[1]
ROSLYN_EXECUTABLE = 'Microsoft.CodeAnalysis.LanguageServer'


def verify_archive(path, expected):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    if digest.hexdigest() != expected:
        raise ValueError(f'SHA-256 mismatch: {path}')


def archive(cache, url, expected, name=None):
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / (name or url.rsplit('/', 1)[-1])
    if path.is_file():
        verify_archive(path, expected)
        return path
    with tempfile.TemporaryDirectory(prefix='download-', dir=cache) as temporary:
        pending = Path(temporary) / path.name
        with urlopen(url, timeout=60) as source, pending.open('wb') as target:
            shutil.copyfileobj(source, target)
        verify_archive(pending, expected)
        pending.replace(path)
    return path


def copy_entry(package, entry, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    with package.open(entry) as source, target.open('xb') as output:
        shutil.copyfileobj(source, output)


def copy_tree(package, prefix, target):
    for entry in package.infolist():
        if entry.is_dir() or not entry.filename.startswith(prefix) or entry.filename.endswith('.pdb'):
            continue
        relative = PurePosixPath(entry.filename[len(prefix):])
        if relative.is_absolute() or '..' in relative.parts or any('\\' in part or ':' in part for part in relative.parts):
            raise ValueError(f'Unexpected archive path: {entry.filename}')
        copy_entry(package, entry, target.joinpath(*relative.parts))


def stage_windows(root, roslyn, clangd, configuration):
    servers = root / 'language-servers'
    managed = servers / 'roslyn'
    native = servers / 'clangd'
    notices = root / 'licenses/language-servers'
    with ZipFile(roslyn) as package:
        copy_tree(package, 'tools/net10.0/win-x64/', managed)
        copy_entry(package, 'ThirdPartyNotices.rtf', notices / 'roslyn/ThirdPartyNotices.rtf')
    shutil.copyfile(ROOT / 'build/licenses/Roslyn.txt', notices / 'roslyn/LICENSE.txt')
    prefix = f"clangd_{configuration['version']}/"
    with ZipFile(clangd) as package:
        copy_entry(package, prefix + 'bin/clangd.exe', native / 'bin/clangd.exe')
        copy_tree(package, prefix + 'lib/clang/', native / 'lib/clang')
        copy_entry(package, prefix + 'LICENSE.TXT', notices / 'clangd/LICENSE.TXT')
    native_x64((managed / (ROSLYN_EXECUTABLE + '.exe')).read_bytes())
    native_x64((native / 'bin/clangd.exe').read_bytes())
    for path in (managed / (ROSLYN_EXECUTABLE + '.runtimeconfig.json'),
                 managed / 'BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll',
                 native / 'lib/clang' / configuration['resourceVersion'] / 'include/stddef.h'):
        if not path.is_file():
            raise ValueError(f'Missing language server dependency: {path}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('rid', choices=('win-x64',))
    parser.add_argument('payload', type=Path)
    parser.add_argument('--cache', type=Path, default=ROOT / 'artifacts/language-servers/downloads')
    args = parser.parse_args()
    configuration = json.loads((ROOT / 'build/language-servers.json').read_text(encoding='utf-8'))
    managed = configuration['roslyn']
    version = managed['version']
    identifier = 'roslyn-language-server.' + args.rid
    roslyn = archive(args.cache, f'https://api.nuget.org/v3-flatcontainer/{identifier}/{version}/{identifier}.{version}.nupkg',
                     managed['sha256'][args.rid], f'roslyn-{args.rid}-{version}.nupkg')
    native = configuration['clangd']
    version = native['version']
    clangd = archive(args.cache, f'https://github.com/clangd/clangd/releases/download/{version}/clangd-windows-{version}.zip',
                     native['sha256']['windows'])
    stage_windows(args.payload, roslyn, clangd, native)


if __name__ == '__main__':
    main()
