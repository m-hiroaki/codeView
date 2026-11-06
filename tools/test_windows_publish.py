import hashlib
from pathlib import Path
import struct
import tempfile
import unittest

from check_windows_publish import inspect_payload, native_x64


def pe_image():
    data = bytearray(64 + 24 + 240)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 60, 64)
    data[64:68] = b'PE\0\0'
    struct.pack_into('<H', data, 68, 0x8664)
    struct.pack_into('<H', data, 84, 240)
    struct.pack_into('<H', data, 88, 0x20b)
    struct.pack_into('<I', data, 196, 16)
    return data


class WindowsPublishTests(unittest.TestCase):
    def test_native_image_and_exact_manifest_hashes(self):
        data = pe_image()
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            (folder / 'codeView.exe').write_bytes(data)
            (folder / 'libSkiaSharp.dll').write_bytes(data)
            (folder / 'codeView.pdb').write_bytes(b'debug')
            report = inspect_payload(folder)
            self.assertEqual(len(report['files']), 3)
            self.assertEqual(report['total_bytes'], len(data) * 2 + 5)
            for item in report['files']:
                self.assertEqual(item['sha256'], hashlib.sha256((folder / item['path']).read_bytes()).hexdigest())
            self.assertFalse(native_x64(data)['clr_header'])

    def test_empty_truncated_and_out_of_bounds_headers(self):
        image = pe_image()
        for length in [0, 1, 63, 64, 87, len(image) - 1]:
            with self.subTest(length=length), self.assertRaises(ValueError):
                native_x64(image[:length])
        struct.pack_into('<I', image, 60, 0xffffffff)
        with self.assertRaises(ValueError):
            native_x64(image)

    def test_wrong_signatures_architecture_and_directory_count(self):
        for offset, value in [(0, b'XX'), (64, b'FAIL'), (68, b'\x4c\x01'),
                              (88, b'\x0b\x01'), (196, struct.pack('<I', 14)),
                              (196, struct.pack('<I', 17))]:
            with self.subTest(offset=offset, value=value), self.assertRaises(ValueError):
                data = pe_image()
                data[offset:offset + len(value)] = value
                native_x64(data)

    def test_managed_clr_header_rejected_even_with_only_size(self):
        for rva, size in [(1, 72), (0, 72), (1, 0)]:
            with self.subTest(rva=rva, size=size), self.assertRaises(ValueError):
                data = pe_image()
                struct.pack_into('<II', data, 88 + 112 + 14 * 8, rva, size)
                native_x64(data)

    def test_missing_executable(self):
        with tempfile.TemporaryDirectory() as folder, self.assertRaises(ValueError):
            inspect_payload(folder)

    def test_managed_fallback_files_rejected(self):
        for name in ['codeView.dll', 'CoreCLR.dll', 'clrjit.dll', 'hostfxr.dll',
                     'hostpolicy.dll', 'codeView.runtimeconfig.json', 'codeView.deps.json']:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                folder = Path(directory)
                (folder / 'codeView.exe').write_bytes(pe_image())
                (folder / name).write_bytes(b'fallback')
                with self.assertRaises(ValueError):
                    inspect_payload(folder)


if __name__ == '__main__':
    unittest.main()
