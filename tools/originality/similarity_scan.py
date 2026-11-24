#!/usr/bin/env python3
"""Source-originality scan (docs/SOURCE_ORIGINALITY.md).

Hashes every window of N consecutive significant, whitespace-normalized lines in a reference tree and
reports windows of Navigator's sources that hash identically. The reference code is never printed:
only statistics and Navigator file names/offsets are shown, so the scan itself never exposes anyone
to the reference implementation.

Usage:
  git clone --depth 1 https://github.com/files-community/Files.git /tmp/files-ref
  python3 tools/originality/similarity_scan.py /tmp/files-ref src [--window 8] [--mask-literals]
  rm -rf /tmp/files-ref

--mask-literals replaces strings/numbers before hashing (catches renamed copies, but also produces
false positives on generic markup such as runs of <Setter> lines; review every hit).
Exit code 1 when matches are found.
"""
import argparse
import hashlib
import os
import re
import sys

TRIVIAL = re.compile(r'^(\{|\}|\);|\};|\]|\[|using [\w.]+;|#.*|/\*.*|\*.*|</?[A-Za-z.:]+>|return;|break;|else|try|finally|)$')


def normalized_lines(path, mask):
    try:
        with open(path, encoding="utf-8", errors="ignore") as handle:
            text = handle.read()
    except OSError:
        return []
    lines = []
    for raw in text.splitlines():
        line = re.sub(r"//.*$", "", re.sub(r"\s+", "", raw))
        if not line or TRIVIAL.match(line):
            continue
        if mask:
            line = re.sub(r'"[^"]*"', '"S"', line)
            line = re.sub(r"\b\d+(\.\d+)?\b", "N", line)
        lines.append(line)
    return lines


def windows(lines, size):
    for index in range(len(lines) - size + 1):
        yield index, hashlib.sha1("\n".join(lines[index:index + size]).encode()).hexdigest()


def source_files(root, extensions):
    for directory, _, files in os.walk(root):
        if any(part in directory for part in ("/.git", "/obj", "/bin")):
            continue
        for name in files:
            if name.endswith(extensions):
                yield os.path.join(directory, name)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("reference")
    parser.add_argument("ours")
    parser.add_argument("--window", type=int, default=8)
    parser.add_argument("--mask-literals", action="store_true")
    args = parser.parse_args()

    reference = set()
    for path in source_files(args.reference, (".cs", ".xaml")):
        reference.update(h for _, h in windows(normalized_lines(path, args.mask_literals), args.window))

    total = 0
    matches = {}
    ours = list(source_files(args.ours, (".cs", ".axaml")))
    for path in ours:
        for index, digest in windows(normalized_lines(path, args.mask_literals), args.window):
            total += 1
            if digest in reference:
                matches.setdefault(os.path.relpath(path, args.ours), []).append(index)

    print(f"reference windows: {len(reference)}; files scanned: {len(ours)}; windows: {total}; matches: {sum(map(len, matches.values()))}")
    for name, offsets in sorted(matches.items()):
        print(f"  {name}: {len(offsets)} window(s), normalized line offsets {offsets[:10]}")
    return 1 if matches else 0


if __name__ == "__main__":
    sys.exit(main())
