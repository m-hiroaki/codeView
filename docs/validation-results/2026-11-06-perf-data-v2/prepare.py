"""Data preparation/independent validation only. Run from a checkout, using a NEW output root."""
import codecs
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))
from generate_fixture import MARKER, PROFILES, describe, generate, verify


def independently_read(root, encoding):
    files = size = characters = matches = lines = 0
    for path in root.rglob("*.cs"):
        files += 1
        lines += 1
        decoder = codecs.getincrementaldecoder(encoding)(errors="strict")
        carry = ""
        previous_cr = False
        with path.open("rb") as stream:
            while True:
                data = stream.read(64 * 1024)
                text = decoder.decode(data, final=not data)
                size += len(data)
                characters += len(text.encode("utf-16-le")) // 2
                matches += (carry + text).count(MARKER)
                carry = (carry + text)[-(len(MARKER) - 1):]
                lines += text.count("\n") + text.count("\r") - text.count("\r\n")
                if previous_cr and text.startswith("\n"):
                    lines -= 1
                if text:
                    previous_cr = text.endswith("\r")
                if not data:
                    break
    return {"files": files, "totalBytes": size, "sourceUtf16Units": characters, "expectedMatches": matches, "sourceLines": lines}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--resume", action="store_true", help="Verify/reuse matching corpora instead of overwriting them")
    parser.add_argument("--freeze-directories", action="store_true", help="POSIX: make generated corpus directories read-only before verification")
    args = parser.parse_args()
    if args.freeze_directories and os.name == "nt":
        parser.error("--freeze-directories is a POSIX preparation option")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=args.resume)
    cases = []
    for encoding in ["utf-8", "cp932"]:
        for profile in ["text10", "text50", "text100"]:
            cases.append((profile + "-" + encoding, profile, {"encoding": encoding}))
        cases.append(("tabs50-" + encoding, "tabs50", {"encoding": encoding, "layout": "wide"}))
        cases.append(("long50-" + encoding, "text50", {"encoding": encoding, "layout": "single-line"}))
        cases.append(("many50-" + encoding, "text50", {"encoding": encoding, "content": "many-matches", "newlines": "mixed"}))
        cases.append(("l1-" + encoding, "l1", {"encoding": encoding, "layout": "wide", "distribution": "mixed", "depth": 10}))
    cases.append(("unicode50", "text50", {"content": "unicode", "newlines": "crlf"}))
    result = {"sourceSha256": {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in
              ["tools/generate_fixture.py", "tools/test_generate_fixture.py"]}, "preparationOnly": True,
              "preparationScriptSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "directoriesFrozen": args.freeze_directories,
              "cacheCondition": "generation, hashing and independent strict decoding warmed inputs; not Cold", "generated": [], "plannedOnly": []}
    for name, profile, options in cases:
        root = output / name
        count, size = PROFILES[profile]
        if root.exists() and args.resume:
            manifest = json.loads(root.with_name(root.name + ".manifest.json").read_text(encoding="utf-8"))
            planned = describe(root, count, size, seed=20261106, **options)
            if any(manifest.get(key) != value for key, value in planned.items() if key != "generatedRoot"):
                raise ValueError("Existing data parameters do not match; choose a new output root")
        else:
            manifest = generate(root, count, size, seed=20261106, **options)
        if args.freeze_directories:
            if root.is_symlink():
                raise ValueError("Cannot freeze a symlink root")
            for folder, _, _ in os.walk(root, topdown=False, followlinks=False):
                Path(folder).chmod(0o555)
        checked = verify(root)
        actual = independently_read(root, manifest["encoding"])
        for key, value in actual.items():
            if value != manifest[key]:
                raise ValueError(f"Independent {key} mismatch: {name}")
        result["generated"].append({"name": name, "profile": profile, "options": options, "currentRoot": str(root),
                                    "manifest": manifest, "verification": checked, "independent": actual})
        (output / "preparation.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"verified {name}: {count} files, {size} bytes, {actual['expectedMatches']} matches", flush=True)
    for profile in ["l2", "l3"]:
        for encoding in ["utf-8", "cp932"]:
            count, size = PROFILES[profile]
            plan = describe(output / (profile + "-" + encoding), count, size, seed=20261106,
                            encoding=encoding, layout="wide", distribution="mixed", depth=10)
            plan.update(operation="plan", writesData=False, cacheCondition="planning only; no input bytes read or written")
            result["plannedOnly"].append(plan)
            print(f"planned only {profile}/{encoding}: {count} files, {size} bytes", flush=True)
    (output / "preparation.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
