#!/usr/bin/env python3
"""Generate isolated, reproducible codeView test data without modifying existing files."""

import argparse
import hashlib
import json
from pathlib import Path

PROFILES = {"l1": (50_000, 1 << 30), "l2": (200_000, 5 << 30), "l3": (1_000_000, 20 << 30)}
MARKER = "CODEVIEW_MATCH"
MIN_FILE_BYTES = 64


def file_sizes(count, total_bytes, layout):
    if count <= 0 or total_bytes < count * MIN_FILE_BYTES:
        raise ValueError("各ファイルに64バイト以上を割り当ててください。")
    if layout not in ("nested", "wide", "mixed", "single-line"):
        raise ValueError("未知の配置です。")
    if layout == "mixed" and count > 1:
        first = min(50 << 20, total_bytes // 4)
        first = max(MIN_FILE_BYTES, min(first, total_bytes - (count - 1) * MIN_FILE_BYTES))
        yield first
        count -= 1
        total_bytes -= first
    size, remainder = divmod(total_bytes, count)
    for index in range(count):
        yield size + (index < remainder)


def relative_path(index, layout, extension):
    name = f"source-{index:07d}.{extension}"
    if layout == "single-line" or (layout == "wide" and index < 10_000):
        return Path(name)
    return Path(f"group-{index // 500:05d}") / name


def write_source(path, size, encoding, single_line, seed, index, extension):
    header = f"// {MARKER} {seed}:{index}\n".encode(encoding)
    if single_line:
        header = header.rstrip(b"\n") + b" "
        line = "sample_value = 123; /* 注文データ */ ".encode(encoding)
        ending = b""
    else:
        # Real tokens as well as comments exercise syntax highlighting; no marker is repeated.
        line = f"value += {(seed + index) % 97}; // 注文明細を集計する\n".encode(encoding)
        ending = b"\n"
    if extension in ("txt", "md"):
        line = "注文データ sample_value 123\n".encode(encoding) if not single_line else "注文データ sample_value 123 ".encode(encoding)
    remaining = size - len(header) - len(ending)
    if remaining < 0:
        raise ValueError("ヘッダーを格納するサイズがありません。")
    repeats, padding = divmod(remaining, len(line))
    block = line * min(1024, max(1, repeats))
    with path.open("xb") as stream:
        stream.write(header)
        while repeats:
            take = min(repeats, len(block) // len(line))
            stream.write(block[:take * len(line)])
            repeats -= take
        stream.write(b" " * padding)
        stream.write(ending)


def generate(output, count, total_bytes, *, encoding="utf-8", layout="nested", seed=20261031, extension="cs"):
    if encoding not in ("utf-8", "cp932") or extension not in ("cs", "c", "cpp", "h", "txt", "md"):
        raise ValueError("未対応の文字コードまたは拡張子です。")
    sizes = file_sizes(count, total_bytes, layout)
    # Validate before creating anything. Iterators otherwise defer parameter checks.
    first_size = next(sizes)
    output = Path(output)
    manifest = output.with_name(output.name + ".manifest.json")
    if output.exists() or output.is_symlink() or manifest.exists() or manifest.is_symlink():
        raise FileExistsError("出力またはマニフェストが既にあります。新しいパスを指定してください。")
    output.mkdir(parents=True, exist_ok=False)
    digest = hashlib.sha256()
    smallest = total_bytes
    largest = 0
    from itertools import chain
    for index, size in enumerate(chain([first_size], sizes)):
        relative = relative_path(index, layout, extension)
        target = output / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        write_source(target, size, encoding, layout == "single-line", seed, index, extension)
        digest.update(f"{relative.as_posix()}:{size}\n".encode("utf-8"))
        smallest = min(smallest, size)
        largest = max(largest, size)
    result = {
        "formatVersion": 1, "generator": "codeView-fixture-v1", "seed": seed,
        "files": count, "totalBytes": total_bytes, "encoding": encoding, "layout": layout,
        "extension": extension, "minFileBytes": smallest, "maxFileBytes": largest,
        "literalQuery": MARKER, "expectedMatches": count, "firstMatch": {"line": 1, "column": 4},
        "notFoundQuery": "CODEVIEW_ABSENT_QUERY", "expectedNotFoundMatches": 0,
        "pathSizeDigest": digest.hexdigest(), "generatedRoot": str(output.resolve()),
        "cacheCondition": "not-measured; generation warms OS cache"
    }
    with manifest.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--profile", choices=["text", *PROFILES], default="text")
    parser.add_argument("--files", type=int, default=1)
    parser.add_argument("--bytes", type=int, default=50 << 20)
    parser.add_argument("--encoding", choices=["utf-8", "cp932"], default="utf-8")
    parser.add_argument("--layout", choices=["nested", "wide", "mixed", "single-line"], default="nested")
    parser.add_argument("--extension", choices=["cs", "c", "cpp", "h", "txt", "md"], default="cs")
    parser.add_argument("--seed", type=int, default=20261031)
    args = parser.parse_args()
    count, size = PROFILES.get(args.profile, (args.files, args.bytes))
    try:
        result = generate(args.output, count, size, encoding=args.encoding, layout=args.layout,
                          seed=args.seed, extension=args.extension)
    except (ValueError, OSError) as error:
        parser.exit(1, str(error) + "\n")
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
