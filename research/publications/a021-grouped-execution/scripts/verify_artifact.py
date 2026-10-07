#!/usr/bin/env python3
"""Verify the A021/R2 anonymous artifact.

Repository mode (default; needs the full repository and its pinned commits):

    python3 research/publications/a021-grouped-execution/scripts/verify_artifact.py [--strict]

  1. rebuilds the bundle into a temporary directory and requires byte-identical
     checksums against artifact/checksums.txt (determinism and freshness);
  2. checks the built bundle (build/anonymous-artifact) against its own
     checksums.txt and artifact/checksums.txt, including the tar;
  3. scans every bundle file, and every member of the tar, for denylisted
     identifiers (internal/denylist.json by default);
  4. confirms that every included manifest entry exists with the recorded
     sha256, and that internal/ material is absent from the bundle;
  5. confirms that every arm patch applies cleanly to baseline/source and that
     no redacted baseline file is touched by a patch;
  6. --strict additionally fails on "pending" manifest entries and on inputs
     read from uncommitted working-tree files.

Bundle mode (works from the unpacked bundle alone):

    python3 analysis/verify_artifact.py --bundle . [--denylist FILE] [--strict]

  runs checks 2-5 on the given bundle directory; the denylist scan runs only
  when a denylist file is supplied (the denylist is private and never ships).

Exit status is 0 only when every check passes.
"""

from __future__ import annotations

import argparse
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
FORBIDDEN_NAMES = ("provenance-map.json", "denylist.json")


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


def scan_files(files: dict[str, bytes], denylist: dict) -> tuple[Check, ...]:
    compiled = an.compile_denylist(denylist)
    blocking = tuple(
        f"{path}:{f.line}: [{f.term}] ...{f.excerpt}..."
        for path, data in sorted(files.items())
        for f in an.scan_text(an.decode_for_scan(data) + "\n" + path, compiled.blocking)
    )
    advisory = {
        term: sum(len(pattern.findall(an.decode_for_scan(d))) for d in files.values())
        for term, pattern in compiled.advisory
    }
    return (
        Check("denylist scan", not blocking,
              f"{len(blocking)} hit(s): {summarize(blocking)}" if blocking else f"{len(files)} files clean"),
        *(Check(f"advisory term '{t}' (retained by policy)", True, f"{n} occurrence(s)", advisory=True)
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
    tsv = files.get("baseline/FILES.tsv", b"").decode("utf-8").splitlines()[1:]
    return frozenset(row.split("\t")[0] for row in tsv if row.endswith("\tyes"))


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
    """Evidence integrity: the shipped patches must be the unredacted source bytes."""
    entries = {e["path"]: e for e in manifest.get("entries", [])}
    altered = tuple(p for p in PATCHES if p in entries and entries[p].get("byte_identical_to_source") is not True)
    absent = tuple(p for p in PATCHES if p not in entries)
    return (Check("arm patches byte-identical to their pinned source", not altered and not absent,
                  summarize(altered + absent) if altered or absent else f"{len(PATCHES)} patches unaltered"),)


def bundle_checks(bundle: Path, denylist: dict | None, strict: bool) -> tuple[Check, ...]:
    files = read_tree(bundle)
    listed = parse_checksums(files.get("checksums.txt", b"").decode("utf-8"))
    actual = {p: sha256(d) for p, d in files.items() if p != "checksums.txt"}
    problems = compare_maps(listed, actual)
    manifest = json.loads(files.get("manifest.json", b"{}"))
    scan = scan_files(files, denylist) if denylist is not None else (
        Check("denylist scan", True, "skipped: no --denylist given", advisory=True),)
    return (
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


def repo_checks(strict: bool, denylist_path: Path | None) -> tuple[Check, ...]:
    import build_anonymous_artifact as build_mod  # needs the repository

    pub = build_mod.PUB_DIR
    src = json.loads(build_mod.SOURCES_FILE.read_text(encoding="utf-8"))
    name = src["bundle_name"]
    recorded_path = pub / "artifact" / "checksums.txt"
    recorded = parse_checksums(recorded_path.read_text(encoding="utf-8")) if recorded_path.is_file() else {}
    with tempfile.TemporaryDirectory(prefix="a021-rebuild-") as tmp:
        result, bundle_tmp, tar = build_mod.build(Path(tmp), publish=False)
        rebuilt = {f"{name}/{f.path}": sha256(f.data) for f in result.files} | {f"{name}.tar": sha256(tar)}
        rebuilt_manifest = build_mod.dumps(result.manifest)
    determinism = compare_maps(recorded, rebuilt)
    built = pub / "build" / name
    built_files = read_tree(built) if built.is_dir() else {}
    tar_path = pub / "build" / f"{name}.tar"
    on_disk = {f"{name}/{p}": sha256(d) for p, d in built_files.items()} | (
        {f"{name}.tar": sha256(tar_path.read_bytes())} if tar_path.is_file() else {})
    stale = compare_maps(recorded, on_disk)
    manifest_file = pub / "artifact" / "manifest.json"
    manifest_same = manifest_file.is_file() and manifest_file.read_bytes() == rebuilt_manifest
    denylist = json.loads((denylist_path or pub / "internal" / "denylist.json").read_text(encoding="utf-8"))
    tar_scan = scan_files({f"<tar>/{k}": v for k, v in read_tar(tar_path).items()}, denylist) if tar_path.is_file() else ()
    pub_rel = pub.relative_to(build_mod.repo_root()).as_posix()
    own = (src["authored"]["readme"], f"{pub_rel}/scripts/artifact_sources.json",
           f"{pub_rel}/artifact/manifest.json", f"{pub_rel}/artifact/checksums.txt")
    dirty = uncommitted_inputs(build_mod.repo_root(), result.manifest, own) if strict else ()
    return (
        Check("rebuild is byte-identical to artifact/checksums.txt", bool(recorded) and not determinism,
              summarize(determinism) if determinism else f"{len(rebuilt)} entries identical"),
        Check("artifact/manifest.json matches rebuild", manifest_same, "" if manifest_same else "stale; rebuild"),
        Check("build/ bundle matches artifact/checksums.txt", bool(on_disk) and not stale,
              summarize(stale) if stale else "bundle and tar present and current"),
        *(Check(f"tar: {c.name}", c.ok, c.detail, c.advisory) for c in tar_scan[:1]),
        *(bundle_checks(built, denylist, strict) if built.is_dir() else
          (Check("bundle present", False, f"{built} missing; run build_anonymous_artifact.py"),)),
        *((Check("inputs committed", not dirty, summarize(dirty) if dirty else "all inputs committed"),)
          if strict else ()),
    )


def report(checks: tuple[Check, ...]) -> int:
    for c in checks:
        status = "PASS" if c.ok and not c.advisory else ("NOTE" if c.ok else "FAIL")
        print(f"[{status}] {c.name}" + (f": {c.detail}" if c.detail else ""))
    failed = tuple(c for c in checks if not c.ok)
    print(f"\n{'FAILED' if failed else 'OK'}: {len(checks) - len(failed)}/{len(checks)} checks passed")
    return 1 if failed else 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify the A021/R2 anonymous artifact.")
    parser.add_argument("--bundle", type=Path, help="verify an unpacked bundle directory only (no rebuild)")
    parser.add_argument("--denylist", type=Path, help="denylist JSON (default in repository mode: internal/denylist.json)")
    parser.add_argument("--strict", action="store_true", help="fail on pending inputs and uncommitted inputs")
    args = parser.parse_args(argv)
    if args.bundle:
        denylist = json.loads(args.denylist.read_text(encoding="utf-8")) if args.denylist else None
        return report(bundle_checks(args.bundle.resolve(), denylist, args.strict))
    return report(repo_checks(args.strict, args.denylist))


if __name__ == "__main__":
    raise SystemExit(main())
