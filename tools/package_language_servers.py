"""Stage the pinned Roslyn and clangd packages inside a Native AOT distribution."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import plistlib
import shutil
import subprocess
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


def stage_roslyn(roslyn, rid, managed, notices):
    with ZipFile(roslyn) as package:
        copy_tree(package, f'tools/net10.0/{rid}/', managed)
        copy_entry(package, 'ThirdPartyNotices.rtf', notices / 'ThirdPartyNotices.rtf')
    shutil.copyfile(ROOT / 'build/licenses/Roslyn.txt', notices / 'LICENSE.txt')
    require_files(managed / (ROSLYN_EXECUTABLE + '.runtimeconfig.json'),
                  managed / 'BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll')


def stage_clangd(clangd, executable, resources, notices, configuration, system):
    prefix = f"clangd_{configuration['version']}/"
    with ZipFile(clangd) as package:
        binary = 'clangd.exe' if system == 'windows' else 'clangd'
        copy_entry(package, prefix + 'bin/' + binary, executable)
        resource_prefix = prefix + 'lib/clang/' + configuration['resourceVersion'] + '/'
        copy_tree(package, resource_prefix + 'include/', resources / 'include')
        copy_tree(package, resource_prefix + 'share/', resources / 'share')
        copy_entry(package, prefix + 'LICENSE.TXT', notices / 'LICENSE.TXT')
    require_files(resources / 'include/stddef.h')


def require_files(*paths):
    for path in paths:
        if not path.is_file():
            raise ValueError(f'Missing language server dependency: {path}')


def stage_windows(root, roslyn, clangd, configuration):
    servers = root / 'language-servers'
    managed = servers / 'roslyn'
    native = servers / 'clangd'
    notices = root / 'licenses/language-servers'
    stage_roslyn(roslyn, 'win-x64', managed, notices / 'roslyn')
    stage_clangd(clangd, native / 'bin/clangd.exe', native / 'lib/clang' / configuration['resourceVersion'],
                 notices / 'clangd', configuration, 'windows')
    native_x64((managed / (ROSLYN_EXECUTABLE + '.exe')).read_bytes())
    native_x64((native / 'bin/clangd.exe').read_bytes())


def sign(path):
    subprocess.run(['codesign', '--force', '--sign', '-', '--timestamp=none', path], check=True)


def verify_macos_architecture(path, arch):
    architectures = subprocess.check_output(['lipo', '-archs', path], text=True).split()
    if arch not in architectures:
        raise ValueError(f'{path} requires {arch}, found {" ".join(architectures)}')


def stage_macos(root, rid, roslyn, clangd, configuration):
    arch = 'arm64' if rid == 'osx-arm64' else 'x86_64'
    contents = root / 'Contents'
    helpers = contents / 'Helpers'
    resources = contents / 'Resources'
    for entry in (contents / 'MacOS').iterdir():
        if entry.suffix == '.dylib':
            sign(entry)
        elif entry.name != 'codeView':
            entry.rename(resources / entry.name)
    notices = resources / 'licenses/language-servers'
    helper = helpers / 'Roslyn.app'
    managed = helper / 'Contents/MacOS'
    managed_resources = helper / 'Contents/Resources'
    stage_roslyn(roslyn, rid, managed, notices / 'roslyn')
    with (helper / 'Contents/Info.plist').open('wb') as stream:
        plistlib.dump({'CFBundleName': 'Roslyn', 'CFBundleExecutable': ROSLYN_EXECUTABLE,
                       'CFBundleIdentifier': 'io.github.m-hiroaki.codeview.roslyn',
                       'CFBundleVersion': '1', 'CFBundlePackageType': 'APPL'}, stream)
    managed_resources.mkdir()
    for entry in managed.iterdir():
        if entry.name in (ROSLYN_EXECUTABLE, 'roslyn-language-server') or entry.suffix == '.dylib':
            entry.chmod(0o755)
            verify_macos_architecture(entry, arch)
            if entry.name != ROSLYN_EXECUTABLE:
                sign(entry)
        else:
            entry.rename(managed_resources / entry.name)
            entry.symlink_to(Path('../Resources') / entry.name)
    require_files(managed / ROSLYN_EXECUTABLE)
    sign(helper)

    with tempfile.TemporaryDirectory(prefix='clangd-', dir=root.parent) as temporary:
        universal = Path(temporary) / 'clangd'
        stage_clangd(clangd, universal, resources / 'clangd', notices / 'clangd', configuration, 'mac')
        binary = helpers / 'clangd'
        subprocess.run(['lipo', universal, '-thin', arch, '-output', binary], check=True)
        binary.chmod(0o755)
        verify_macos_architecture(binary, arch)
        sign(binary)

    sign(root)
    subprocess.run(['codesign', '--verify', '--deep', '--strict', root], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('rid', choices=('win-x64', 'osx-arm64', 'osx-x64'))
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
    system = 'windows' if args.rid == 'win-x64' else 'mac'
    clangd = archive(args.cache, f'https://github.com/clangd/clangd/releases/download/{version}/clangd-{system}-{version}.zip',
                     native['sha256'][system])
    def payload_bytes():
        return sum(path.stat().st_size for path in args.payload.rglob('*') if not path.is_symlink() and path.is_file())

    before = payload_bytes()
    if args.rid == 'win-x64':
        stage_windows(args.payload, roslyn, clangd, native)
    else:
        stage_macos(args.payload, args.rid, roslyn, clangd, native)
    after = payload_bytes()
    print(f'Payload bytes ({args.rid}): before={before}, after={after}, added={after - before}')


if __name__ == '__main__':
    main()
