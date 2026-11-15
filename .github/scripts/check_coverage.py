"""Merge Cobertura reports and fail when line or branch coverage is under the floor.

Usage: check_coverage.py <report.cobertura.xml or folder>...
A folder is searched for *.cobertura.xml files. Delete old TestResults folders first: a stale
report's line numbers may no longer match the code.
Each test project writes its own report. Reports are merged line by line: a line counts as
covered if any report covered it, and a branch point counts the most branches any report
took, so code exercised by several test projects is counted once. The floors come from
MIN_LINE and MIN_BRANCH (fractions, such as 0.95). On GitHub Actions the rates are also
written to the job summary.
"""

import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

CONDITION = re.compile(r"\((\d+)/(\d+)\)")
# The repository's root, two folders up from this script: paths are judged relative to it, so a
# clone under a folder named "src" counts the same as any other.
ROOT = os.path.normcase(os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..")))


def merge(paths):
    lines = {}  # (file, line) -> hit
    branches = {}  # (file, line) -> (covered, total)
    for path in paths:
        root = ET.parse(path).getroot()
        for cls in root.iter("class"):
            filename = os.path.normcase(cls.get("filename", ""))
            try:
                relative = os.path.relpath(filename, ROOT) if os.path.isabs(filename) else filename
            except ValueError:  # another drive: not this repository's code
                continue
            # Only the shipped packages count: source-generated code (logging, JSON, regular
            # expressions) is not ours to test, and the tools under tools/ have tests of their
            # own logic but are not held to the floor.
            if filename.endswith(".g.cs") or f"{os.sep}obj{os.sep}" in relative or not relative.startswith("src" + os.sep):
                continue
            for line in cls.iter("line"):
                key = (filename, int(line.get("number", "0")))
                hit = int(line.get("hits", "0")) > 0
                lines[key] = lines.get(key, False) or hit
                if line.get("branch") == "True":
                    match = CONDITION.search(line.get("condition-coverage", ""))
                    if match:
                        covered, total = int(match.group(1)), int(match.group(2))
                        previous = branches.get(key, (0, total))
                        branches[key] = (max(previous[0], covered), max(previous[1], total))
    line_rate = sum(lines.values()) / len(lines) if lines else 1.0
    branch_total = sum(t for _, t in branches.values())
    branch_rate = sum(c for c, _ in branches.values()) / branch_total if branch_total else 1.0
    return line_rate, branch_rate


def main() -> int:
    paths = []
    for arg in sys.argv[1:]:
        if os.path.isdir(arg):
            paths.extend(glob.glob(os.path.join(arg, "**", "*.cobertura.xml"), recursive=True))
        elif os.path.isfile(arg):
            paths.append(arg)
    if not paths:
        print("No coverage report found.", file=sys.stderr)
        return 1

    line, branch = merge(paths)
    floors = {"line": float(os.environ.get("MIN_LINE", "0")), "branch": float(os.environ.get("MIN_BRANCH", "0"))}
    rates = {"line": line, "branch": branch}

    report = f"Line coverage {line:.1%}, branch coverage {branch:.1%} ({len(paths)} reports merged)"
    print(report)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(report + "\n")

    low = [f"{kind} {rates[kind]:.1%} < {floors[kind]:.0%}" for kind in rates if rates[kind] < floors[kind]]
    if low:
        print("Coverage under the floor: " + "; ".join(low), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
