#!/usr/bin/env python3
"""Verify the A021/R2 artifacts (review bundle and faithful bundle).

Repository mode (default; needs the full repository and its pinned commits):

    python3 research/publications/a021-grouped-execution/scripts/verify_artifact.py [--review|--faithful] [--strict]

  For each selected bundle (default: both):
  1. rebuilds into a temporary directory and requires byte-identical checksums
     against the published checksums (artifact/checksums.txt for review,
     artifact/faithful/checksums.txt for faithful), including the tar;
  2. checks the built bundle under build/ against its own checksums.txt;
  3. scans every bundle file and every tar member with internal/denylist.json.
     Faithful: person identifiers block; product names are reported only.
     Review: person identifiers, the real product/organisation tokens and
     every real object id recorded in the map block;
  4. confirms manifest entries exist with the recorded sha256 and that no
     private or internal-only file is bundled;
  5. confirms every arm patch applies cleanly to baseline/source;
  6. --strict also fails on "pending" inputs and on uncommitted inputs.

  --behavior (slow, about 40 minutes; needs .NET SDK 10): builds the baseline
  and each of the four arms from both bundles, runs the F# test suite, and
  requires identical per-test outcomes between faithful and review (test
  names compared through the review rename) and the expected counts. Writes
  internal/behavior-verification.json.

Bundle mode (works from the unpacked bundle alone):

    python3 analysis/verify_artifact.py --bundle . [--denylist FILE] [--strict]

  runs checks 2-5 on the given bundle directory (mode read from its
  manifest); the denylist scan runs only when a denylist file is supplied.

Exit status is 0 only when every check passes.
"""

from __future__ import annotations

import argparse
import re
import hashlib
import io
import json
import shutil
import subprocess
import sys
import tarfile
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

sys.dont_write_bytecode = True  # never leave interpreter caches inside a bundle
sys.path.insert(0, str(Path(__file__).resolve().parent))
import anonymize as an  # noqa: E402

SCRIPT_DIR = Path(__file__).resolve().parent
PATCHES = ("a021/arm-x.patch", "a021/arm-y.patch", "r2/arm-M.patch", "r2/arm-N.patch")
FORBIDDEN_NAMES = ("provenance-map.json", "denylist.json", "behavior-verification.json",
                   "evidence-index.json", "metrics-sources.md", "check_manuscript.py")
TEST_LINE_RE = re.compile(r"^(PASS|FAIL|SKIP|ERROR) (.*)$")


@dataclass(frozen=True)
class Check:
    name: str
    ok: bool
    detail: str = ""
    advisory: bool = False


# --------------------------------------------------------------------------
# Pure checks
# --------------------------------------------------------------------------


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def parse_checksums(text: str) -> dict[str, str]:
    rows = (line.split("  ", 1) for line in text.splitlines() if line.strip())
    return {path: digest for digest, path in rows}


def compare_maps(expected: dict[str, str], actual: dict[str, str]) -> tuple[str, ...]:
    missing = tuple(f"missing: {p}" for p in sorted(set(expected) - set(actual)))
    extra = tuple(f"unexpected: {p}" for p in sorted(set(actual) - set(expected)))
    changed = tuple(f"differs: {p}" for p in sorted(set(expected) & set(actual)) if expected[p] != actual[p])
    return missing + extra + changed


def summarize(problems: Iterable[str], limit: int = 12) -> str:
    items = tuple(problems)
    return "; ".join(items[:limit]) + (f"; ... {len(items) - limit} more" if len(items) > limit else "")


def scan_files(files: dict[str, bytes], denylist: dict, review: bool) -> tuple[Check, ...]:
    compiled = an.compile_denylist(denylist, review)
    texts = {path: an.decode_for_scan(data) + "\n" + path for path, data in sorted(files.items())}
    blocking = tuple(
        f"{path}:{f.line}: [{f.term}] ...{f.excerpt}..."
        for path, text in texts.items()
        for f in an.scan_text(text, compiled.blocking) + (
            an.scan_shas(text, compiled.real_shas, compiled.sha_prefixes) if review else ())
    )
    advisory = {
        term: sum(len(pattern.findall(t)) for t in texts.values()) for term, pattern in compiled.advisory
    }
    label = "denylist scan (persons, products, real object ids)" if review else "denylist scan (persons)"
    return (
        Check(label, not blocking,
              f"{len(blocking)} hit(s): {summarize(blocking)}" if blocking else f"{len(files)} files clean"),
        *(Check(f"advisory term '{t}' (retained in the faithful bundle)", True, f"{n} occurrence(s)", advisory=True)
          for t, n in sorted(advisory.items())),
    )


def manifest_checks(manifest: dict, files: dict[str, bytes], strict: bool) -> tuple[Check, ...]:
    entries = manifest.get("entries", [])
    included = tuple(e for e in entries if e["status"] == "included")
    pending = tuple(e["path"] for e in entries if e["status"] == "pending")
    bad = tuple(
        f"{e['path']}: {'absent' if e['path'] not in files else 'sha256 mismatch'}"
        for e in included
        if e["path"] not in files or sha256(files[e["path"]]) != e["sha256"]
    )
    base_dir = manifest.get("baseline", {}).get("bundle_dir", "baseline/source")
    shipped_baseline = sum(1 for p in files if p.startswith(base_dir + "/"))
    expected_baseline = manifest.get("baseline", {}).get("files_shipped")
    forbidden = tuple(p for p in files if p.split("/")[-1] in FORBIDDEN_NAMES or p.startswith("internal/"))
    return (
        Check("manifest entries exist", not bad, summarize(bad) if bad else f"{len(included)} entries"),
        Check("baseline file count", shipped_baseline == expected_baseline,
              f"{shipped_baseline} shipped, manifest says {expected_baseline}"),
        Check("no private material in bundle", not forbidden, summarize(forbidden)),
        Check("no pending inputs", not (strict and pending),
              ("pending: " + ", ".join(pending)) if pending else "none pending",
              advisory=not strict),
    )


def patch_paths(patch: bytes) -> frozenset[str]:
    lines = patch.decode("utf-8", errors="replace").splitlines()
    return frozenset(
        p[2:] for line in lines if line.startswith(("--- ", "+++ "))
        for p in (line[4:].split("\t")[0],) if p.startswith(("a/", "b/"))
    )


def redacted_baseline(files: dict[str, bytes], base_dir: str) -> frozenset[str]:
    rows = files.get("baseline/FILES.tsv", b"").decode("utf-8").splitlines()
    if not rows:
        return frozenset()
    col = rows[0].split("\t").index("redacted")
    return frozenset(r.split("\t")[0] for r in rows[1:] if r.split("\t")[col] == "yes")


def parse_tests(output: str) -> dict[str, str]:
    return {m.group(2): m.group(1) for m in map(TEST_LINE_RE.match, output.splitlines()) if m}


# --------------------------------------------------------------------------
# I/O edge
# --------------------------------------------------------------------------


def read_tree(root: Path) -> dict[str, bytes]:
    return {p.relative_to(root).as_posix(): p.read_bytes() for p in sorted(root.rglob("*"))
            if p.is_file() and "__pycache__" not in p.relative_to(root).parts}


def read_tar(path: Path) -> dict[str, bytes]:
    with tarfile.open(path) as tar:
        return {m.name.split("/", 1)[1]: tar.extractfile(m).read() for m in tar.getmembers()
                if m.isfile() and "/" in m.name}


def apply_checks(bundle: Path, files: dict[str, bytes]) -> tuple[Check, ...]:
    if shutil.which("git") is None:
        return (Check("patches apply to baseline", True, "skipped: git not installed", advisory=True),)
    base = bundle / "baseline" / "source"
    with tempfile.TemporaryDirectory(prefix="a021-apply-") as tmp:
        work = Path(tmp) / "tree"
        shutil.copytree(base, work, symlinks=True)

        def check(patch: str) -> str | None:
            proc = subprocess.run(("git", "apply", "--check", "--whitespace=nowarn", str(bundle / patch)),
                                  cwd=work, capture_output=True, text=True)
            return None if proc.returncode == 0 else f"{patch}: {proc.stderr.strip()[:300]}"

        failures = tuple(r for r in map(check, (p for p in PATCHES if p in files)) if r)
    touched = frozenset().union(*(patch_paths(files[p]) for p in PATCHES if p in files))
    overlap = sorted(touched & redacted_baseline(files, "baseline/source"))
    present = tuple(p for p in PATCHES if p in files)
    return (
        Check("patches apply to baseline", not failures and len(present) == len(PATCHES),
              summarize(failures) if failures else f"{len(present)}/{len(PATCHES)} patches apply"),
        Check("redacted baseline files also touched by a patch (context verified by git apply)", True,
              summarize(overlap) if overlap else "none", advisory=True),
    )


def patch_identity_checks(manifest: dict) -> tuple[Check, ...]:
    """Evidence integrity: no person redaction touched the patches; in review mode
    the only change is the mechanical rename (and the patches still apply)."""
    entries = {e["path"]: e for e in manifest.get("entries", [])}
    altered = tuple(p for p in PATCHES if p in entries and entries[p].get("byte_identical_to_source") is not True)
    absent = tuple(p for p in PATCHES if p not in entries)
    name = ("arm patches = pinned source + mechanical review rename only" if manifest.get("mode") == "review"
            else "arm patches byte-identical to their pinned source")
    return (Check(name, not altered and not absent,
                  summarize(altered + absent) if altered or absent else f"{len(PATCHES)} patches"),)


def bundle_checks(bundle: Path, denylist: dict | None, strict: bool) -> tuple[Check, ...]:
    files = read_tree(bundle)
    listed = parse_checksums(files.get("checksums.txt", b"").decode("utf-8"))
    actual = {p: sha256(d) for p, d in files.items() if p != "checksums.txt"}
    problems = compare_maps(listed, actual)
    manifest = json.loads(files.get("manifest.json", b"{}"))
    review = manifest.get("mode") == "review"
    scan = scan_files(files, denylist, review) if denylist is not None else (
        Check("denylist scan", True, "skipped: no --denylist given", advisory=True),)
    return (
        Check(f"bundle mode: {manifest.get('mode', 'unknown')}", manifest.get("mode") in ("review", "faithful")),
        Check("bundle checksums.txt matches files", bool(listed) and not problems,
              summarize(problems) if problems else f"{len(listed)} files"),
        *manifest_checks(manifest, files, strict),
        *patch_identity_checks(manifest),
        *scan,
        *apply_checks(bundle, files),
    )


def uncommitted_inputs(repo: Path, manifest: dict, extra: Iterable[str]) -> tuple[str, ...]:
    paths = sorted({e["origin"]["worktree"] for e in manifest.get("entries", [])
                    if isinstance(e.get("origin"), dict) and "worktree" in e["origin"] and e["status"] == "included"}
                   | set(extra))
    def dirty(p: str) -> bool:
        tracked = subprocess.run(("git", "-C", str(repo), "ls-files", "--error-unmatch", p), capture_output=True)
        clean = subprocess.run(("git", "-C", str(repo), "diff", "--quiet", "HEAD", "--", p), capture_output=True)
        return tracked.returncode != 0 or clean.returncode != 0
    return tuple(p for p in paths if dirty(p))


def mode_checks(build_mod, result, tars, mode: str, denylist: dict, strict: bool) -> tuple[Check, ...]:
    pub = build_mod.PUB_DIR
    mres = result.modes[mode]
    name = mres.bundle_name
    published = pub / build_mod.PUBLISH_DIR[mode]
    recorded_path = published / "checksums.txt"
    recorded = parse_checksums(recorded_path.read_text(encoding="utf-8")) if recorded_path.is_file() else {}
    rebuilt = parse_checksums(build_mod.published_checksums(mres, tars[mode]))
    determinism = compare_maps(recorded, rebuilt)
    built = pub / "build" / name
    built_files = read_tree(built) if built.is_dir() else {}
    tar_path = pub / "build" / f"{name}.tar"
    on_disk = {f"{name}/{p}": sha256(d) for p, d in built_files.items()} | (
        {f"{name}.tar": sha256(tar_path.read_bytes())} if tar_path.is_file() else {})
    stale = compare_maps(recorded, on_disk)
    manifest_file = published / "manifest.json"
    manifest_same = manifest_file.is_file() and manifest_file.read_bytes() == build_mod.dumps(mres.manifest)
    tar_scan = (scan_files({f"<tar>/{k}": v for k, v in read_tar(tar_path).items()}, denylist, mode == "review")
                if tar_path.is_file() else ())
    rel = recorded_path.relative_to(pub).as_posix()
    tag = lambda c: Check(f"[{mode}] {c.name}", c.ok, c.detail, c.advisory)  # noqa: E731
    return tuple(map(tag, (
        Check(f"rebuild is byte-identical to {rel}", bool(recorded) and not determinism,
              summarize(determinism) if determinism else f"{len(rebuilt)} entries identical"),
        Check(f"{published.relative_to(pub).as_posix()}/manifest.json matches rebuild", manifest_same,
              "" if manifest_same else "stale; rebuild"),
        Check(f"build/{name} matches {rel}", bool(on_disk) and not stale,
              summarize(stale) if stale else "bundle and tar present and current"),
        *(Check(f"tar: {c.name}", c.ok, c.detail, c.advisory) for c in tar_scan[:1]),
        *(bundle_checks(built, denylist, strict) if built.is_dir() else
          (Check("bundle present", False, f"{built} missing; run build_anonymous_artifact.py"),)),
    )))


def repo_checks(strict: bool, denylist_path: Path | None, modes: tuple[str, ...]) -> tuple[Check, ...]:
    import build_anonymous_artifact as build_mod  # needs the repository

    pub = build_mod.PUB_DIR
    src = json.loads(build_mod.SOURCES_FILE.read_text(encoding="utf-8"))
    with tempfile.TemporaryDirectory(prefix="a021-rebuild-") as tmp:
        result, _bundles, tars = build_mod.build(Path(tmp), publish=False)
    denylist = json.loads((denylist_path or pub / "internal" / "denylist.json").read_text(encoding="utf-8"))
    pub_rel = pub.relative_to(build_mod.repo_root()).as_posix()
    own = (src["authored"]["readme"], f"{pub_rel}/scripts/artifact_sources.json",
           *(f"{pub_rel}/{build_mod.PUBLISH_DIR[m].as_posix()}/{n}" for m in modes for n in ("manifest.json", "checksums.txt")))
    dirty = (uncommitted_inputs(build_mod.repo_root(), result.modes["review"].manifest, own) if strict else ())
    return tuple(c for m in modes for c in mode_checks(build_mod, result, tars, m, denylist, strict)) + (
        (Check("inputs committed", not dirty, summarize(dirty) if dirty else "all inputs committed"),)
        if strict else ())


# --------------------------------------------------------------------------
# Behaviour preservation (slow; .NET SDK required)
# --------------------------------------------------------------------------

DOTNET_BUILD = ("dotnet", "build", "Ros.slnx", "-c", "Release", "-p:FSharpCoreImplicitPackageVersion=10.1.400")
TEST_DLL = "tests/Ros.Tests/bin/Release/net10.0/Ros.Tests.dll"


def run_target(bundle: Path, patch: str | None, work: Path) -> dict:
    shutil.copytree(bundle / "baseline" / "source", work, symlinks=True)
    if patch:
        applied = subprocess.run(("git", "apply", "--whitespace=nowarn", str(bundle / patch)), cwd=work,
                                 capture_output=True, text=True)
        if applied.returncode != 0:
            return {"apply": applied.stderr.strip()[:500], "tests": {}}
    built = subprocess.run(DOTNET_BUILD, cwd=work, capture_output=True, text=True, timeout=1800)
    if built.returncode != 0:
        return {"build": built.stdout[-2000:], "tests": {}}
    tested = subprocess.run(("dotnet", TEST_DLL), cwd=work, capture_output=True, text=True, timeout=3600)
    shutil.rmtree(work, ignore_errors=True)
    return {"exit": tested.returncode, "tests": parse_tests(tested.stdout + tested.stderr)}


def behavior_checks() -> tuple[Check, ...]:
    import build_anonymous_artifact as build_mod

    src = json.loads(build_mod.SOURCES_FILE.read_text(encoding="utf-8"))
    expected = src["review"]["expected_test_counts"]
    targets = (("baseline", None),) + tuple((p.removesuffix(".patch"), p) for p in PATCHES)
    with tempfile.TemporaryDirectory(prefix="a021-behavior-") as tmp:
        result, bundles, _tars = build_mod.build(Path(tmp) / "out", publish=False)
        runs = {(mode, label): run_target(bundles[mode], patch, Path(tmp) / f"{mode}-{label.replace('/', '-')}")
                for mode in build_mod.MODES for label, patch in targets}
    dotnet = subprocess.run(("dotnet", "--version"), capture_output=True, text=True).stdout.strip()

    def compare(label: str) -> dict:
        faithful = {result.review_text(n): o for n, o in runs[("faithful", label)]["tests"].items()}
        review = runs[("review", label)]["tests"]
        diff = sorted(n for n in set(faithful) | set(review) if faithful.get(n) != review.get(n))
        count = lambda r: sum(1 for o in r.values() if o == "PASS")  # noqa: E731
        return {"faithful_pass": count(faithful), "review_pass": count(review),
                "faithful_total": len(faithful), "review_total": len(review),
                "expected": expected[label], "per_test_identical": not diff, "differences": diff[:50],
                "problems": {m: {k: v for k, v in runs[(m, label)].items() if k in ("apply", "build")}
                             for m in build_mod.MODES}}

    summary = {label: compare(label) for label, _ in targets}
    record = {"schema": "a021-anonymous-artifact.behavior/1", "dotnet_sdk": dotnet,
              "build": " ".join(DOTNET_BUILD), "tests": f"dotnet {TEST_DLL}", "targets": summary}
    (build_mod.PUB_DIR / "internal" / "behavior-verification.json").write_bytes(build_mod.dumps(record))
    return tuple(
        Check(f"behaviour {label}: faithful {r['faithful_pass']}/{r['faithful_total']}, "
              f"review {r['review_pass']}/{r['review_total']}, expected {r['expected']}",
              r["per_test_identical"] and r["faithful_pass"] == r["review_pass"] == r["expected"]
              and r["faithful_total"] == r["expected"],
              summarize(r["differences"]) if r["differences"] else "per-test outcomes identical")
        for label, r in summary.items()
    )


def report(checks: tuple[Check, ...]) -> int:
    for c in checks:
        status = "PASS" if c.ok and not c.advisory else ("NOTE" if c.ok else "FAIL")
        print(f"[{status}] {c.name}" + (f": {c.detail}" if c.detail else ""))
    failed = tuple(c for c in checks if not c.ok)
    print(f"\n{'FAILED' if failed else 'OK'}: {len(checks) - len(failed)}/{len(checks)} checks passed")
    return 1 if failed else 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify the A021/R2 artifacts.")
    parser.add_argument("--bundle", type=Path, help="verify an unpacked bundle directory only (no rebuild)")
    parser.add_argument("--denylist", type=Path, help="denylist JSON (default in repository mode: internal/denylist.json)")
    parser.add_argument("--strict", action="store_true", help="fail on pending inputs and uncommitted inputs")
    which = parser.add_mutually_exclusive_group()
    which.add_argument("--review", action="store_true", help="repository mode: verify only the review bundle")
    which.add_argument("--faithful", action="store_true", help="repository mode: verify only the faithful bundle")
    parser.add_argument("--behavior", action="store_true",
                        help="also build and test baseline and arms from both bundles (slow; needs .NET SDK 10)")
    args = parser.parse_args(argv)
    if args.bundle:
        denylist = json.loads(args.denylist.read_text(encoding="utf-8")) if args.denylist else None
        return report(bundle_checks(args.bundle.resolve(), denylist, args.strict))
    modes = ("review",) if args.review else ("faithful",) if args.faithful else ("review", "faithful")
    checks = repo_checks(args.strict, args.denylist, modes)
    return report(checks + (behavior_checks() if args.behavior else ()))


if __name__ == "__main__":
    raise SystemExit(main())
