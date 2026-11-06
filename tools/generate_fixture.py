#!/usr/bin/env python3
"""Generate isolated, reproducible codeView test data without modifying existing files."""

import argparse
import hashlib
import json
import os
from pathlib import Path

PROFILES = {"text10": (1, 10 << 20), "text50": (1, 50 << 20), "text100": (1, 100 << 20),
            "tabs50": (5, 5 * (50 << 20)), "l1": (50_000, 1 << 30),
            "l2": (200_000, 5 << 30), "l3": (1_000_000, 20 << 30)}
MARKER = "CODEVIEW_MATCH"
MIN_FILE_BYTES = 64


def file_sizes(count, total_bytes, layout, distribution=None):
    if count <= 0 or total_bytes < count * MIN_FILE_BYTES:
        raise ValueError("各ファイルに64バイト以上を割り当ててください。")
    if layout not in ("nested", "wide", "mixed", "single-line"):
        raise ValueError("未知の配置です。")
    distribution = distribution or ("mixed" if layout == "mixed" else "uniform")
    if distribution not in ("uniform", "mixed") or (layout == "mixed" and distribution != "mixed"):
        raise ValueError("未知または矛盾するサイズ分布です。")
    for maximum in ((50 << 20), (1 << 20)) if distribution == "mixed" else ():
        if count <= 1:
            break
        first = min(maximum, total_bytes // 4)
        first = max(MIN_FILE_BYTES, min(first, total_bytes - (count - 1) * MIN_FILE_BYTES))
        yield first
        count -= 1
        total_bytes -= first
    size, remainder = divmod(total_bytes, count)
    for index in range(count):
        yield size + (index < remainder)


def relative_path(index, layout, extension, depth=1):
    name = f"source-{index:07d}.{extension}"
    if layout == "single-line" or (layout == "wide" and index < 10_000):
        return Path(name)
    return Path(*(f"level-{level:02d}" for level in range(1, depth)), f"group-{index // 500:05d}", name)


def source_parts(encoding, single_line, seed, index, extension, newlines="lf", content="normal"):
    endings = {"lf": ["\n"], "crlf": ["\r\n"], "mixed": ["\n", "\r\n", "\r"]}[newlines]
    header = f"// {MARKER} {seed}:{index}" + (" " if single_line else endings[0])
    if single_line:
        line = "sample_value = 123; /* 注文データ */ "
        ending = ""
    else:
        # Real tokens as well as comments exercise syntax highlighting; no marker is repeated.
        line = f"value += {(seed + index) % 97}; // 注文明細を集計する"
        ending = endings[-1]
    if extension in ("txt", "md"):
        line = "注文データ sample_value 123" + (" " if single_line else "")
    if content == "unicode":
        line = "sample_value = 123; // a😀b が か\u3099" + (" " if single_line else "")
    elif content == "many-matches":
        line = MARKER + " 注文明細" + (" " if single_line else "")
        # Leave a marker on the final line, including when exact-size padding is required.
        ending = MARKER
    pattern = line if single_line else "".join(line + eol for eol in endings)
    return header, pattern, ending


def write_source(path, size, encoding, single_line, seed, index, extension, newlines="lf", content="normal"):
    header, pattern, ending = (value.encode(encoding) for value in source_parts(encoding, single_line, seed, index, extension, newlines, content))
    remaining = size - len(header) - len(ending)
    if remaining < 0:
        raise ValueError("ヘッダーを格納するサイズがありません。")
    repeats, padding = divmod(remaining, len(pattern))
    block = pattern * min(max(1, (64 * 1024) // len(pattern)), max(1, repeats))
    digest = hashlib.sha256()
    with path.open("xb") as stream:
        def write(data):
            stream.write(data)
            digest.update(data)

        write(header)
        while repeats:
            take = min(repeats, len(block) // len(pattern))
            write(block[:take * len(pattern)])
            repeats -= take
        write(b" " * padding)
        write(ending)
    return digest.hexdigest()


def line_breaks(value):
    return value.count("\n") + value.count("\r") - value.count("\r\n")


def content_digest(parameters):
    keys = ("generator", "formatVersion", "seed", "files", "totalBytes", "encoding", "layout", "extension", "newlines", "content", "depth", "distribution")
    header = json.dumps({key: parameters[key] for key in keys}, sort_keys=True, separators=(",", ":")) + "\n"
    return hashlib.sha256(header.encode("utf-8"))


def describe(output, count, total_bytes, *, encoding="utf-8", layout="nested", seed=20261031, extension="cs",
             newlines="lf", content="normal", depth=1, distribution=None):
    if encoding not in ("utf-8", "cp932") or extension not in ("cs", "c", "cpp", "h", "txt", "md"):
        raise ValueError("未対応の文字コードまたは拡張子です。")
    if newlines not in ("lf", "crlf", "mixed") or content not in ("normal", "unicode", "many-matches"):
        raise ValueError("未対応の改行または本文です。")
    if not 1 <= depth <= 10 or (layout == "single-line" and (depth != 1 or newlines != "lf")):
        raise ValueError("階層は1〜10です。単一行では階層/改行の指定を変更できません。")
    if encoding == "cp932" and content == "unicode":
        raise ValueError("Unicode派生の絵文字/結合文字はUTF-8で生成してください。")
    distribution = distribution or ("mixed" if layout == "mixed" else "uniform")
    digest = hashlib.sha256()
    smallest = total_bytes
    largest = 0
    matches = characters = lines = maximum_depth = 0
    for index, size in enumerate(file_sizes(count, total_bytes, layout, distribution)):
        relative = relative_path(index, layout, extension, depth)
        header, pattern, ending = source_parts(encoding, layout == "single-line", seed, index, extension, newlines, content)
        remaining = size - len(header.encode(encoding)) - len(ending.encode(encoding))
        if remaining < 0:
            raise ValueError("ヘッダーと終端を格納するサイズがありません。seed/サイズを確認してください。")
        repeats, padding = divmod(remaining, len(pattern.encode(encoding)))
        matches += header.count(MARKER) + repeats * pattern.count(MARKER) + ending.count(MARKER)
        characters += len(header.encode("utf-16-le")) // 2 + repeats * (len(pattern.encode("utf-16-le")) // 2) + padding + len(ending.encode("utf-16-le")) // 2
        lines += 1 + line_breaks(header) + repeats * line_breaks(pattern) + line_breaks(ending)
        digest.update(f"{relative.as_posix()}:{size}\n".encode("utf-8"))
        smallest = min(smallest, size)
        largest = max(largest, size)
        maximum_depth = max(maximum_depth, len(relative.parts) - 1)
    groups = 0 if layout == "single-line" else max(0, (count + 499) // 500 - (20 if layout == "wide" else 0))
    return {
        "formatVersion": 2, "generator": "codeView-fixture-v2", "seed": seed,
        "files": count, "totalBytes": total_bytes, "encoding": encoding, "layout": layout,
        "extension": extension, "minFileBytes": smallest, "maxFileBytes": largest,
        "newlines": "none" if layout == "single-line" else newlines, "content": content, "depth": depth,
        "directories": groups + (depth - 1 if groups else 0), "maximumDepth": maximum_depth,
        "sourceUtf16Units": characters, "sourceLines": lines,
        "distribution": distribution,
        "sizeDistribution": "large <=50 MiB, medium <=1 MiB, remainder uniform" if distribution == "mixed" else "uniform +/-1 byte",
        "literalQuery": MARKER, "expectedMatches": matches, "firstMatch": {"line": 1, "column": 4, "offset": 3},
        "notFoundQuery": "CODEVIEW_ABSENT_QUERY", "expectedNotFoundMatches": 0,
        "pathSizeDigest": digest.hexdigest(), "generatedRoot": str(Path(output).resolve()),
        "cacheCondition": "not-measured; generation warms OS cache"
    }


def generate(output, count, total_bytes, **options):
    result = describe(output, count, total_bytes, **options)
    output = Path(output)
    manifest = output.with_name(output.name + ".manifest.json")
    if output.exists() or output.is_symlink() or manifest.exists() or manifest.is_symlink():
        raise FileExistsError("出力またはマニフェストが既にあります。新しいパスを指定してください。")
    output.mkdir(parents=True, exist_ok=False)
    digest = content_digest(result)
    for index, size in enumerate(file_sizes(count, total_bytes, result["layout"], result["distribution"])):
        relative = relative_path(index, result["layout"], result["extension"], result["depth"])
        target = output / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        file_digest = write_source(target, size, result["encoding"], result["layout"] == "single-line", result["seed"], index,
                                   result["extension"], "lf" if result["newlines"] == "none" else result["newlines"], result["content"])
        digest.update(f"{relative.as_posix()}:{size}:{file_digest}\n".encode("utf-8"))
    result["contentDigest"] = digest.hexdigest()
    with manifest.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    return result


def verify(output):
    output = Path(output)
    if output.is_symlink():
        raise ValueError("検証ルートにリンクは指定できません。")
    result = json.loads(output.with_name(output.name + ".manifest.json").read_text(encoding="utf-8"))
    if not isinstance(result, dict) or result.get("generator") != "codeView-fixture-v2" or result.get("formatVersion") != 2:
        raise ValueError("内容ハッシュを持つv2データを指定してください。")
    try:
        options = {key: result[key] for key in ("encoding", "layout", "seed", "extension", "content", "depth", "distribution")}
        options["newlines"] = "lf" if result["newlines"] == "none" else result["newlines"]
        planned = describe(output, result["files"], result["totalBytes"], **options)
        for key in planned:
            if key != "generatedRoot" and result.get(key) != planned[key]:
                raise ValueError("マニフェストの期待値が生成条件と一致しません。")
    except (KeyError, TypeError) as error:
        raise ValueError("マニフェストの必須項目が不正です。") from error
    files = directories = 0
    for folder, names, filenames in os.walk(output, followlinks=False):
        if any((Path(folder) / name).is_symlink() for name in [*names, *filenames]):
            raise ValueError("標準データにリンクが追加されています。")
        files += len(filenames)
        directories += len(names)
    if files != result["files"] or directories != result["directories"]:
        raise ValueError("実ファイル/ディレクトリ数が一致しません。")
    digest = content_digest(planned)
    for index, size in enumerate(file_sizes(result["files"], result["totalBytes"], result["layout"], result["distribution"])):
        relative = relative_path(index, result["layout"], result["extension"], result["depth"])
        path = output / relative
        if not path.is_file() or path.stat().st_size != size:
            raise ValueError("通常ファイルのサイズが一致しません。")
        file_digest = hashlib.sha256()
        with path.open("rb") as stream:
            while data := stream.read(64 * 1024):
                file_digest.update(data)
        digest.update(f"{relative.as_posix()}:{size}:{file_digest.hexdigest()}\n".encode("utf-8"))
    if digest.hexdigest() != result.get("contentDigest"):
        raise ValueError("内容ハッシュが一致しません。")
    return {"verified": True, "files": files, "totalBytes": result["totalBytes"], "contentDigest": digest.hexdigest(),
            "cacheCondition": "verification read every byte; not Cold"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--profile", choices=["text", *PROFILES], default="text")
    parser.add_argument("--files", type=int)
    parser.add_argument("--bytes", type=int)
    parser.add_argument("--encoding", choices=["utf-8", "cp932"], default="utf-8")
    parser.add_argument("--layout", choices=["nested", "wide", "mixed", "single-line"], default="nested")
    parser.add_argument("--extension", choices=["cs", "c", "cpp", "h", "txt", "md"], default="cs")
    parser.add_argument("--seed", type=int, default=20261031)
    parser.add_argument("--newlines", choices=["lf", "crlf", "mixed"], default="lf")
    parser.add_argument("--content", choices=["normal", "unicode", "many-matches"], default="normal")
    parser.add_argument("--depth", type=int, default=1)
    parser.add_argument("--distribution", choices=["uniform", "mixed"])
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--plan", action="store_true", help="Print expected counts without writing data")
    mode.add_argument("--verify", action="store_true", help="Re-read an existing v2 corpus and verify its digest")
    args = parser.parse_args()
    if args.profile != "text" and (args.files is not None or args.bytes is not None):
        parser.error("--profileと--files/--bytesを同時に指定しないでください。")
    if args.verify and (args.profile != "text" or args.files is not None or args.bytes is not None
                        or args.encoding != "utf-8" or args.layout != "nested" or args.extension != "cs"
                        or args.seed != 20261031 or args.newlines != "lf" or args.content != "normal" or args.depth != 1 or args.distribution is not None):
        parser.error("--verifyには生成条件を指定しないでください。保存済み条件を使います。")
    count, size = PROFILES.get(args.profile, (args.files if args.files is not None else 1, args.bytes if args.bytes is not None else 50 << 20))
    try:
        operation = describe if args.plan else generate
        result = verify(args.output) if args.verify else operation(args.output, count, size, encoding=args.encoding, layout=args.layout,
                          seed=args.seed, extension=args.extension, newlines=args.newlines, content=args.content, depth=args.depth,
                          distribution=args.distribution)
        if args.plan:
            result.update(operation="plan", writesData=False, cacheCondition="planning only; no input bytes read or written")
    except (ValueError, OSError) as error:
        parser.exit(1, str(error) + "\n")
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
