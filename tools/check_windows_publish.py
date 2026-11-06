"""Check the real win-x64 publish payload, without executing it."""
import argparse
import hashlib
import json
from pathlib import Path
import struct


def native_x64(data):
    """Require PE32+ AMD64 with no CLR header (reject a managed/JIT fallback)."""
    if len(data) < 64 or data[:2] != b'MZ':
        raise ValueError('Missing DOS header')
    pe = struct.unpack_from('<I', data, 60)[0]
    if pe < 64 or pe + 24 > len(data) or data[pe:pe + 4] != b'PE\0\0':
        raise ValueError('Missing PE header')
    machine = struct.unpack_from('<H', data, pe + 4)[0]
    size = struct.unpack_from('<H', data, pe + 20)[0]
    optional = pe + 24
    if machine != 0x8664 or size < 240 or optional + size > len(data):
        raise ValueError('Expected complete AMD64 optional header')
    if struct.unpack_from('<H', data, optional)[0] != 0x20b:
        raise ValueError('Expected PE32+')
    directories = struct.unpack_from('<I', data, optional + 108)[0]
    if directories < 15 or size < 112 + directories * 8:
        raise ValueError('Incomplete data directories')
    if struct.unpack_from('<II', data, optional + 112 + 14 * 8) != (0, 0):
        raise ValueError('Managed CLR header present')
    return {'machine': 'AMD64', 'format': 'PE32+', 'clr_header': False}


def inspect_payload(folder):
    folder = Path(folder)
    executable = folder / 'codeView.exe'
    if not executable.is_file():
        raise ValueError('Missing codeView.exe')
    files = []
    forbidden = {'codeview.dll', 'coreclr.dll', 'clrjit.dll', 'hostfxr.dll', 'hostpolicy.dll'}
    for path in sorted(folder.rglob('*')):
        if path.is_symlink():
            raise ValueError('Unexpected symlink in payload')
        if not path.is_file():
            continue
        if path.name.lower() in forbidden or path.name.endswith(('.deps.json', '.runtimeconfig.json')):
            raise ValueError(f'Unexpected managed runtime payload: {path.name}')
        data = path.read_bytes()
        item = {'path': path.relative_to(folder).as_posix(), 'bytes': len(data),
                'sha256': hashlib.sha256(data).hexdigest()}
        if path.suffix.lower() in ('.exe', '.dll'):
            item['pe'] = native_x64(data)
        files.append(item)
    return {'schema': 1, 'files': files, 'total_bytes': sum(x['bytes'] for x in files)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--folder', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    report = inspect_payload(args.folder)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
