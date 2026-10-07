#!/usr/bin/env python3
"""Validates data/architecture-findings.json.

Checks, without changing anything:
  * the document shape (required keys, allowed category/direction/basis values,
    one finding per study x dimension x implementation, one comparison per
    study x dimension, comparison categories equal to the findings');
  * that the file is in its canonical form (sorted keys, 2-space indent);
  * that every cited commit exists (`git cat-file -e`);
  * that every cited path exists in its commit and that every cited symbol is
    present inside the cited line range of that blob.

Usage: check_architecture_findings.py [PATH] [--repo DIR]
Exit status 0 when every check passes, 1 otherwise.
"""
import json
import subprocess
import sys
from functools import lru_cache
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_PATH = HERE.parent / "data" / "architecture-findings.json"

CATEGORIES = frozenset({"unified", "duplicated-compatible", "divergent", "missing", "not-assessable"})
DIRECTIONS = frozenset({"grouped_more_unified", "independent_more_unified", "equivalent", "mixed", "not_assessable"})
BASES = frozenset({"observed", "evaluated", "inferred"})
STUDIES = ("A021", "R2")
DIMENSIONS = tuple(f"D{n}" for n in range(1, 9))
TOP_KEYS = frozenset({"schema_version", "rubric", "implementations", "findings", "comparisons", "falsification_attempts", "local_correctness"})
FINDING_KEYS = frozenset({"id", "study", "dimension", "implementation", "category", "behavior_or_invariant", "evidence", "basis",
                          "evaluator_agreement", "introduced_by_item", "stale_start_sensitive", "notes"})
COMPARISON_KEYS = frozenset({"study", "dimension", "grouped_category", "independent_category", "direction",
                             "behavioral_difference", "confidence", "rationale"})


def git(repo, *args):
    return subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True)


@lru_cache(maxsize=None)
def commit_exists(repo, commit):
    return git(repo, "cat-file", "-e", f"{commit}^{{commit}}").returncode == 0


@lru_cache(maxsize=None)
def blob_lines(repo, commit, path):
    result = git(repo, "show", f"{commit}:{path}")
    return tuple(result.stdout.splitlines()) if result.returncode == 0 else None


def parse_range(text):
    parts = text.split("-")
    if len(parts) != 2 or not all(part.isdigit() for part in parts):
        return None
    start, end = int(parts[0]), int(parts[1])
    return (start, end) if 1 <= start <= end else None


def missing_keys(where, required, record):
    return [f"{where}: missing key '{key}'" for key in sorted(required - set(record))]


def shape_errors(doc):
    implementations = {impl.get("id"): impl for impl in doc.get("implementations", [])}
    findings = doc.get("findings", [])
    comparisons = doc.get("comparisons", [])

    def finding_errors(finding):
        where = f"finding {finding.get('id')}"
        return (
            missing_keys(where, FINDING_KEYS, finding)
            + ([f"{where}: unknown category '{finding.get('category')}'"] if finding.get("category") not in CATEGORIES else [])
            + ([f"{where}: unknown basis '{finding.get('basis')}'"] if finding.get("basis") not in BASES else [])
            + ([f"{where}: unknown implementation"] if finding.get("implementation") not in implementations else [])
            + ([f"{where}: implementation belongs to another study"]
               if implementations.get(finding.get("implementation"), {}).get("study") not in (None, finding.get("study")) else [])
            + ([f"{where}: no evidence"] if not finding.get("evidence") else [])
            + ([f"{where}: stale_start_sensitive must be true, false or null"]
               if finding.get("stale_start_sensitive") not in (True, False, None) else [])
        )

    def comparison_errors(comparison):
        where = f"comparison {comparison.get('study')}/{comparison.get('dimension')}"
        key = (comparison.get("study"), comparison.get("dimension"))
        by_arm = {implementations[f["implementation"]]["arm"]: f["category"]
                  for f in findings if (f.get("study"), f.get("dimension")) == key and f.get("implementation") in implementations}
        return (
            missing_keys(where, COMPARISON_KEYS, comparison)
            + ([f"{where}: unknown direction"] if comparison.get("direction") not in DIRECTIONS else [])
            + ([f"{where}: grouped_category disagrees with the finding"] if by_arm.get("grouped") != comparison.get("grouped_category") else [])
            + ([f"{where}: independent_category disagrees with the finding"] if by_arm.get("independent") != comparison.get("independent_category") else [])
        )

    expected_findings = {(s, d, i) for s in STUDIES for d in DIMENSIONS for i, impl in implementations.items() if impl.get("study") == s}
    actual_findings = [(f.get("study"), f.get("dimension"), f.get("implementation")) for f in findings]
    expected_comparisons = {(s, d) for s in STUDIES for d in DIMENSIONS}
    actual_comparisons = [(c.get("study"), c.get("dimension")) for c in comparisons]
    ids = [f.get("id") for f in findings]

    return (
        missing_keys("document", TOP_KEYS, doc)
        + [error for finding in findings for error in finding_errors(finding)]
        + [error for comparison in comparisons for error in comparison_errors(comparison)]
        + [f"finding missing for {key}" for key in sorted(expected_findings - set(actual_findings))]
        + [f"duplicate finding for {key}" for key in sorted({k for k in actual_findings if actual_findings.count(k) > 1})]
        + [f"comparison missing for {key}" for key in sorted(expected_comparisons - set(actual_comparisons))]
        + [f"duplicate comparison for {key}" for key in sorted({k for k in actual_comparisons if actual_comparisons.count(k) > 1})]
        + [f"duplicate finding id {i}" for i in sorted({i for i in ids if ids.count(i) > 1})]
    )


def evidence_errors(repo, doc):
    def check(owner, item):
        where = f"{owner} {item.get('path')}:{item.get('lines')}"
        commit, path, symbol, span = item.get("commit"), item.get("path"), item.get("symbol"), parse_range(str(item.get("lines")))
        if not commit or not commit_exists(repo, commit):
            return [f"{where}: commit {commit} does not exist"]
        lines = blob_lines(repo, commit, path)
        if lines is None:
            return [f"{where}: path not in {commit[:12]}"]
        if span is None or span[1] > len(lines):
            return [f"{where}: bad line range for a {len(lines)}-line blob"]
        window = "\n".join(lines[span[0] - 1: span[1]])
        return [] if symbol and symbol in window else [f"{where}: symbol {symbol!r} not in lines {span[0]}-{span[1]} of {commit[:12]}"]

    def commit_check(owner, commit):
        return [] if commit_exists(repo, commit) else [f"{owner}: commit {commit} does not exist"]

    return (
        [error for f in doc.get("findings", []) for item in f.get("evidence", []) for error in check(f"finding {f.get('id')}", item)]
        + [error for f in doc.get("findings", []) for c in f.get("introducing_commits", []) for error in commit_check(f"finding {f.get('id')}", c)]
        + [error for impl in doc.get("implementations", []) for error in commit_check(f"implementation {impl.get('id')}", impl.get("head_commit", ""))]
    )


def canonical_errors(path, doc):
    canonical = json.dumps(doc, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    return [] if path.read_text(encoding="utf-8") == canonical else [f"{path}: not in canonical form (sorted keys, 2-space indent, trailing newline)"]


def main(argv):
    args = argv[1:]
    repo = Path(args[args.index("--repo") + 1]) if "--repo" in args else HERE.parents[3]
    positional = [a for i, a in enumerate(args) if a != "--repo" and (i == 0 or args[i - 1] != "--repo")]
    path = Path(positional[0]) if positional else DEFAULT_PATH
    doc = json.loads(path.read_text(encoding="utf-8"))
    errors = canonical_errors(path, doc) + shape_errors(doc) + evidence_errors(repo, doc)
    evidence_count = sum(len(f.get("evidence", [])) for f in doc.get("findings", []))
    print("\n".join(errors) if errors else f"OK: {len(doc['findings'])} findings, {len(doc['comparisons'])} comparisons, {evidence_count} evidence citations verified")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
