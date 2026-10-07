#!/usr/bin/env python3
"""Build the deterministic, anonymised A021/R2 reviewer artifact.

Usage (from anywhere inside the repository):

    python3 research/publications/a021-grouped-execution/scripts/build_anonymous_artifact.py
    python3 .../build_anonymous_artifact.py --out /tmp/x --no-publish   # rebuild elsewhere, touch nothing

Inputs are pinned in artifact_sources.json (commit SHA + path for every Git
input; optional datasets written by other agents are read from the working
tree, hashed, and listed as "pending" when absent).

Outputs (default):
    build/anonymous-artifact/          the bundle directory (git-ignored)
    build/anonymous-artifact.tar       deterministic tar of that directory
    artifact/manifest.json             manifest (also shipped inside the bundle)
    artifact/checksums.txt             sha256 of every bundle file and of the tar
    internal/provenance-map.json       PRIVATE alias -> real identifier map
    internal/denylist.json             PRIVATE identifier denylist for the verifier

Determinism: sorted traversal, pinned blobs, fixed tar mtime/uid/gid/mode,
LF line endings for everything this script writes, no clock or environment
input. Running it twice yields byte-identical outputs.

Functional core / imperative shell: the plan is computed by pure functions
from pinned inputs; I/O happens only in the ``git_*``/``read_*``/``write_*``
helpers and in ``main``.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tarfile
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Iterable, Mapping

sys.path.insert(0, str(Path(__file__).resolve().parent))
import anonymize as an  # noqa: E402

SCRIPT_DIR = Path(__file__).resolve().parent
PUB_DIR = SCRIPT_DIR.parent
SOURCES_FILE = SCRIPT_DIR / "artifact_sources.json"
ADVISORY_TERMS = ("praxis", "echelon")  # product/organisation names: retained by policy, reported


# --------------------------------------------------------------------------
# Data
# --------------------------------------------------------------------------


@dataclass(frozen=True)
class BundleFile:
    path: str  # bundle-relative, POSIX
    data: bytes
    mode: int  # 0o644 or 0o755
    profile: str  # anonymize.PROFILE_CODE / PROFILE_RECORD / "authored"
    meta: tuple[tuple[str, object], ...] = ()


@dataclass(frozen=True)
class BuildResult:
    files: tuple[BundleFile, ...]
    manifest: dict
    provenance: dict
    denylist: dict


# --------------------------------------------------------------------------
# I/O edge: Git and files
# --------------------------------------------------------------------------


def git(repo: Path, *args: str, check: bool = True) -> bytes:
    proc = subprocess.run(("git", "-C", str(repo), *args), capture_output=True, check=False)
    if check and proc.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {proc.stderr.decode(errors='replace').strip()}")
    return proc.stdout


def has_commit(repo: Path, sha: str) -> bool:
    return subprocess.run(("git", "-C", str(repo), "cat-file", "-e", f"{sha}^{{commit}}"),
                          capture_output=True).returncode == 0


def ensure_commits(repo: Path, wanted: Iterable[tuple[str, str | None]]) -> None:
    missing = tuple((sha, ref) for sha, ref in sorted(set(wanted), key=lambda p: (p[0], p[1] or ""))
                    if not has_commit(repo, sha))
    for sha, ref in missing:
        if ref:
            git(repo, "fetch", "--quiet", "origin", ref, check=False)
    still = tuple(sha for sha, _ in missing if not has_commit(repo, sha))
    if still:
        raise RuntimeError(f"pinned commits not available locally (git fetch origin <branch>): {', '.join(still)}")


def git_blob(repo: Path, commit: str, path: str) -> bytes:
    return git(repo, "show", f"{commit}:{path}")


def git_tree(repo: Path, commit: str) -> tuple[tuple[str, str, str], ...]:
    """(mode, blob sha, path) for every blob in the commit tree, sorted by path."""
    out = git(repo, "ls-tree", "-r", "-z", "--full-tree", commit).decode("utf-8")
    entries = (e.split("\t", 1) for e in out.split("\x00") if e)
    rows = ((meta.split()[0], meta.split()[2], path) for meta, path in entries if meta.split()[1] == "blob")
    return tuple(sorted(rows, key=lambda r: r[2]))


def git_blobs(repo: Path, shas: tuple[str, ...]) -> dict[str, bytes]:
    proc = subprocess.run(("git", "-C", str(repo), "cat-file", "--batch"),
                          input="".join(f"{s}\n" for s in shas).encode(), capture_output=True, check=True)
    out, pos, result = proc.stdout, 0, {}
    for _ in shas:
        header_end = out.index(b"\n", pos)
        sha, _kind, size = out[pos:header_end].decode().split()
        start = header_end + 1
        result[sha] = out[start:start + int(size)]
        pos = start + int(size) + 1
    return result


def git_diff(repo: Path, base: str, head: str, pathspec: Iterable[str]) -> bytes:
    return git(repo, "-c", "core.quotepath=false", "-c", "diff.noprefix=false", "-c", "diff.relative=false",
               "-c", "diff.mnemonicprefix=false", "diff", "--no-color", "--no-ext-diff", "--no-textconv",
               "--no-renames", "--full-index", "--binary", "--diff-algorithm=myers",
               "--src-prefix=a/", "--dst-prefix=b/", base, head, "--", *pathspec)


def git_identities(repo: Path, commits: Iterable[str]) -> str:
    return git(repo, "log", "--format=%an%x00%ae%x00%cn%x00%ce", *sorted(set(commits))).decode("utf-8")


def git_remote(repo: Path) -> str:
    return git(repo, "remote", "get-url", "origin", check=False).decode().strip()


def repo_root() -> Path:
    return Path(subprocess.run(("git", "-C", str(SCRIPT_DIR), "rev-parse", "--show-toplevel"),
                               capture_output=True, check=True, text=True).stdout.strip())


def read_optional(path: Path) -> bytes | None:
    return path.read_bytes() if path.is_file() else None


# --------------------------------------------------------------------------
# Pure helpers
# --------------------------------------------------------------------------


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def text_lf(data: bytes) -> str:
    return an.normalize_newlines(data.decode("utf-8"))


def dumps(value) -> bytes:
    return (json.dumps(value, indent=2, ensure_ascii=False, sort_keys=False) + "\n").encode("utf-8")


def excluded(path: str, prefixes: Iterable[str]) -> bool:
    return any(path.startswith(p) for p in prefixes)


def git_mode(mode: str) -> int:
    return 0o755 if mode == "100755" else 0o644


def redact_bytes(data: bytes, rules: tuple[an.Rule, ...], keep_newlines: bool) -> tuple[bytes, tuple]:
    """Redact text; binary data passes through untouched (and is still scanned later)."""
    if not an.is_text(data):
        return data, ()
    text = data.decode("utf-8")
    result = an.apply_rules(text if keep_newlines else an.normalize_newlines(text), rules)
    return result.text.encode("utf-8"), result.counts


def checksums_text(files: Iterable[BundleFile], prefix: str = "") -> str:
    return "".join(f"{sha256(f.data)}  {prefix}{f.path}\n" for f in sorted(files, key=lambda f: f.path))


# --------------------------------------------------------------------------
# Plan
# --------------------------------------------------------------------------


def pinned_commits(src: Mapping) -> tuple[tuple[str, str | None], ...]:
    patch_commits = tuple(
        pair
        for p in src["patches"]
        for pair in (((p["base"], None), (p["head"], p.get("fetch"))) if p["kind"] == "git-diff"
                     else ((p["commit"], None),))
    )
    record_commits = tuple((r["commit"], None) for r in src["records"])
    return ((src["baseline"]["commit"], None),) + patch_commits + record_commits


def collect_raw(repo: Path, src: Mapping) -> dict:
    """All raw inputs (I/O). Returns plain data for the pure planner."""
    base = src["baseline"]
    tree = git_tree(repo, base["commit"])
    kept = tuple(r for r in tree if not excluded(r[2], base["exclude_prefixes"]))
    blobs = git_blobs(repo, tuple(sorted({r[1] for r in kept})))
    wi = base["work_items"]
    analysis = src["analysis_glob"]
    analysis_dir = repo / analysis["dir"]
    analysis_files = tuple(sorted(
        (p.name, p.read_bytes()) for p in analysis_dir.iterdir()
        if p.is_file() and p.suffix in analysis["suffixes"]
    ))
    return {
        "tree": tree,
        "kept": kept,
        "blobs": blobs,
        "tree_sha": git(repo, "rev-parse", f"{base['commit']}^{{tree}}").decode().strip(),
        "queue": git_blob(repo, base["commit"], wi["path"]),
        "patches": {p["bundle_path"]: (git_diff(repo, p["base"], p["head"], p["pathspec"]) if p["kind"] == "git-diff"
                                       else git_blob(repo, p["commit"], p["path"])) for p in src["patches"]},
        "records": {r["bundle_path"]: git_blob(repo, r["commit"], r["path"]) for r in src["records"]},
        "optional": {o["bundle_path"]: read_optional(repo / o["path"]) for o in src["optional_worktree"]},
        "analysis": analysis_files,
        "readme": (repo / src["authored"]["readme"]).read_bytes(),
        "identities": git_identities(repo, (c for c, _ in pinned_commits(src))),
        "remote": git_remote(repo),
    }


def work_items(queue: bytes, prefix: str) -> bytes:
    items = [i for i in json.loads(queue)["items"] if str(i.get("id", "")).startswith(prefix)]
    return dumps(items)


def plan(src: Mapping, raw: Mapping) -> BuildResult:
    terms = an.person_terms(an.parse_identities(raw["identities"]), an.owner_from_remote(raw["remote"]))
    persons = an.person_rules(terms)

    # Record-profile texts (everything except baseline source and patches).
    optional_present = {k: v for k, v in raw["optional"].items() if v is not None}
    transforms = {o["bundle_path"]: o.get("transform", "none") for o in src["optional_worktree"]}
    record_inputs = (
        tuple((p, text_lf(d), "none") for p, d in sorted(raw["records"].items()))
        + tuple((p, text_lf(d), transforms[p]) for p, d in sorted(optional_present.items()))
        + tuple((f"{src['analysis_glob']['bundle_dir']}/{n}", text_lf(d), "none") for n, d in raw["analysis"])
    )
    transformed = tuple((p, an.TRANSFORMS[t](txt), t) for p, txt, t in record_inputs)
    corpus = tuple(t for _, t, _ in transformed)
    session_aliases = an.pseudonyms(corpus, an.SESSION_ID_RE, "agent-session")
    session_urls = sorted({m.group(0) for t in corpus for m in an.SESSION_URL_RE.finditer(t)})
    uuid_aliases = an.pseudonyms(corpus, an.UUID_RE, "uuid")
    record_rules = an.rules_for(an.PROFILE_RECORD, persons, an.record_rules(session_aliases, uuid_aliases))
    code_rules = an.rules_for(an.PROFILE_CODE, persons, ())

    def record_file(path: str, text: str, transform: str, origin: Mapping) -> BundleFile:
        result = an.apply_rules(text, record_rules)
        mode = 0o755 if path.endswith((".sh", ".py")) and text.startswith("#!") else 0o644
        return BundleFile(path, result.text.encode("utf-8"), mode, an.PROFILE_RECORD,
                          (("redactions", dict(result.counts)), ("transform", transform), ("origin", dict(origin))))

    origins = {
        **{r["bundle_path"]: {"commit": r["commit"], "path": r["path"], "role": r["role"]} for r in src["records"]},
        **{o["bundle_path"]: {"worktree": o["path"], "role": o["role"]} for o in src["optional_worktree"]},
        **{f"{src['analysis_glob']['bundle_dir']}/{n}": {"worktree": f"{src['analysis_glob']['dir']}/{n}",
                                                        "role": src["analysis_glob"]["role"]} for n, _ in raw["analysis"]},
    }
    records = tuple(record_file(p, t, tr, origins[p]) for p, t, tr in transformed)

    # Patches: code profile, newline bytes preserved exactly.
    patch_specs = {p["bundle_path"]: p for p in src["patches"]}

    def patch_file(path: str, data: bytes) -> BundleFile:
        out, counts = redact_bytes(data, code_rules, keep_newlines=True)
        spec = patch_specs[path]
        origin = ({"kind": "git-diff", "base": spec["base"], "head": spec["head"], "pathspec": spec["pathspec"]}
                  if spec["kind"] == "git-diff" else {"kind": "blob", "commit": spec["commit"], "path": spec["path"]})
        return BundleFile(path, out, 0o644, an.PROFILE_CODE,
                          (("redactions", dict(counts)), ("origin", {**origin, "role": spec["role"]}),
                           ("source_sha256", sha256(data)), ("byte_identical_to_source", out == data)))

    patches = tuple(patch_file(p, d) for p, d in sorted(raw["patches"].items()))

    # Baseline source: code profile, blob bytes preserved except person identifiers.
    base = src["baseline"]

    def baseline_file(row: tuple[str, str, str]) -> BundleFile:
        mode, blob, path = row
        data = raw["blobs"][blob]
        out, counts = redact_bytes(data, code_rules, keep_newlines=True)
        return BundleFile(f"{base['bundle_dir']}/{path}", out, git_mode(mode), an.PROFILE_CODE,
                          (("redactions", dict(counts)), ("git_blob", blob), ("git_path", path)))

    baseline_files = tuple(baseline_file(r) for r in raw["kept"])
    redacted_baseline = tuple(f for f in baseline_files if dict(f.meta)["redactions"])
    files_tsv = "path\tmode\tgit_blob_at_baseline\tsha256_shipped\tredacted\n" + "".join(
        f"{dict(f.meta)['git_path']}\t{f.mode:o}\t{dict(f.meta)['git_blob']}\t{sha256(f.data)}\t"
        f"{'yes' if dict(f.meta)['redactions'] else 'no'}\n"
        for f in baseline_files
    )
    excluded_counts = {
        p: sum(1 for r in raw["tree"] if r[2].startswith(p)) for p in base["exclude_prefixes"]
    }
    baseline_txt = "".join((
        "Baseline snapshot\n=================\n\n",
        f"Commit:            {base['commit']}\n",
        f"Git tree:          {raw['tree_sha']}\n",
        f"Files in commit:   {len(raw['tree'])}\n",
        f"Files shipped:     {len(baseline_files)} (under source/)\n",
        f"Files redacted:    {len(redacted_baseline)} (person identifiers only; see FILES.tsv)\n\n",
        "Excluded top-level paths (whole subtrees, not shipped):\n",
        *(f"  {p:<18} {excluded_counts[p]:>4} files  {base['exclude_reason'][p]}\n" for p in base["exclude_prefixes"]),
        "\nEvery shipped file is the exact Git blob of the baseline commit unless FILES.tsv\n",
        "marks it redacted. An unredacted file can be checked against the baseline with\n",
        "`git hash-object <file>` == git_blob_at_baseline. Redacted files differ only in\n",
        "person identifiers (names, e-mail addresses, account names) replaced by neutral\n",
        "placeholders; none of them is a file the arm patches modify, and both studies'\n",
        "patches apply cleanly to source/ (checked by analysis/verify_artifact.py).\n",
    ))
    queue_items = work_items(raw["queue"], base["work_items"]["id_prefix"])
    wi_out, wi_counts = redact_bytes(queue_items, record_rules, keep_newlines=False)
    baseline_meta = (
        BundleFile("baseline/BASELINE.txt", baseline_txt.encode(), 0o644, "authored"),
        BundleFile("baseline/FILES.tsv", files_tsv.encode(), 0o644, "authored"),
        BundleFile(base["work_items"]["bundle_path"], wi_out, 0o644, an.PROFILE_RECORD,
                   (("redactions", dict(wi_counts)),
                    ("origin", {"commit": base["commit"], "path": base["work_items"]["path"],
                                "filter": f"items whose id starts with {base['work_items']['id_prefix']}"}))),
    )

    mapping_file = BundleFile("MAPPING.json", dumps({
        "schema": "a021-anonymous-artifact.mapping/1",
        "note": "Evaluation is complete in both studies; the paper reports this mapping. "
                "Blind file names are kept so that evaluator outputs can be read verbatim.",
        **src["mapping"],
    }), 0o644, "authored")
    readme_file = BundleFile("README.md", text_lf(raw["readme"]).encode(), 0o644, "authored")

    content = (readme_file, mapping_file) + baseline_meta + baseline_files + patches + records
    pending = tuple(o for o in src["optional_worktree"] if raw["optional"][o["bundle_path"]] is None)

    manifest = {
        "schema": "a021-anonymous-artifact.manifest/1",
        "bundle": src["bundle_name"],
        "source_date_epoch": src["source_date_epoch"],
        "baseline": {
            "commit": base["commit"], "tree": raw["tree_sha"], "bundle_dir": base["bundle_dir"],
            "files_in_commit": len(raw["tree"]), "files_shipped": len(baseline_files),
            "files_redacted": len(redacted_baseline), "excluded_prefixes": list(base["exclude_prefixes"]),
            "file_list": "baseline/FILES.tsv",
        },
        "policy": {
            "removed": ["personal names", "e-mail addresses", "account names and account-scoped URLs",
                        "agent-session identifiers and URLs", "transcript/tool UUIDs (records only)",
                        "Co-Authored-By / session trailer lines (records only)", "fields named internal_ref"],
            "retained": ["product, package and organisation names (embedded in code, CLI names, work-item ids "
                         "and the evaluated patches)", "commit SHAs (the study's provenance anchors)",
                         "tool identities (model and evaluator names, disclosed by the paper)"],
            "document": "README.md, section 'Anonymization'",
        },
        "entries": [
            {"path": f.path, "status": "included", "profile": f.profile, "sha256": sha256(f.data),
             "bytes": len(f.data), **{k: v for k, v in f.meta if k not in ("git_blob", "git_path")}}
            for f in sorted(content, key=lambda f: f.path) if not f.path.startswith(base["bundle_dir"] + "/")
        ] + [
            {"path": o["bundle_path"], "status": "pending", "origin": {"worktree": o["path"], "role": o["role"]},
             "note": "input not present at build time; verify --strict fails until it exists"}
            for o in pending
        ],
    }
    manifest["entries"].sort(key=lambda e: e["path"])
    manifest_file = BundleFile("manifest.json", dumps(manifest), 0o644, "authored")
    with_manifest = content + (manifest_file,)
    checksum_file = BundleFile("checksums.txt", checksums_text(with_manifest).encode(), 0o644, "authored")
    files = tuple(sorted(with_manifest + (checksum_file,), key=lambda f: f.path))

    owners_alias = {o: an.ANON_OWNER for o in terms.owners}
    provenance = {
        "schema": "a021-anonymous-artifact.provenance/1",
        "PRIVATE": "Never copy this file into the anonymous bundle. It maps every alias back to real identifiers.",
        "aliases": {
            "accounts": owners_alias,
            "names": {**{n: an.ANON_NAME for n in terms.full_names},
                      **{t: an.ANON_NAME_TOKEN for t in terms.name_tokens}},
            "emails": {e: an.ANON_EMAIL for e in terms.emails},
            "agent_sessions": {v: k for k, v in session_aliases.items()},
            "agent_session_urls": {u: an.ANON_SESSION_URL for u in session_urls},
            "uuids": {v: k for k, v in uuid_aliases.items()},
            "blind_arms": {
                "a021/arm-x": {"mode": "grouped", "blind_branch": "experiment/a021-arm-x",
                               "blind_head": patch_specs["a021/arm-x.patch"]["head"],
                               "original_branch": "experiment/a021-grouped"},
                "a021/arm-y": {"mode": "control", "blind_branch": "experiment/a021-arm-y",
                               "blind_head": patch_specs["a021/arm-y.patch"]["head"],
                               "original_branch": "experiment/a021-control"},
                "r2/arm-M": {"mode": "control", "blind_branch": "blind/a021-r2/arm-M",
                             "original_branch": "experiment/a021-r2-control"},
                "r2/arm-N": {"mode": "grouped", "blind_branch": "blind/a021-r2/arm-N",
                             "original_branch": "experiment/a021-r2-grouped"},
            },
        },
        "repository": {"remote": raw["remote"]},
        "bundle_sources": {
            f.path: dict(f.meta).get("origin", {}) for f in files if dict(f.meta).get("origin")
        },
        "redacted_baseline_files": {
            dict(f.meta)["git_path"]: {"git_blob": dict(f.meta)["git_blob"], "rules": dict(f.meta)["redactions"],
                                       "shipped_sha256": sha256(f.data)}
            for f in redacted_baseline
        },
        "identities_seen": [{"name": i.name, "email": i.email, "tool": i.is_tool}
                            for i in an.parse_identities(raw["identities"])],
    }
    denylist = an.build_denylist(terms, ADVISORY_TERMS)
    return BuildResult(files, manifest, provenance, denylist)


# --------------------------------------------------------------------------
# Output (I/O edge)
# --------------------------------------------------------------------------


def tar_bytes(files: Iterable[BundleFile], root: str, epoch: int) -> bytes:
    dirs = sorted({f"{root}/{'/'.join(f.path.split('/')[:i])}" for f in files
                   for i in range(1, len(f.path.split("/")))} | {root})

    def info(name: str, kind: bytes, size: int, mode: int) -> tarfile.TarInfo:
        ti = tarfile.TarInfo(name)
        ti.type, ti.size, ti.mode, ti.mtime = kind, size, mode, epoch
        ti.uid = ti.gid = 0
        ti.uname = ti.gname = ""
        return ti

    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.GNU_FORMAT) as tar:
        entries = sorted([(d, None) for d in dirs] + [(f"{root}/{f.path}", f) for f in files], key=lambda e: e[0])
        for name, f in entries:
            if f is None:
                tar.addfile(info(name, tarfile.DIRTYPE, 0, 0o755))
            else:
                tar.addfile(info(name, tarfile.REGTYPE, len(f.data), f.mode), io.BytesIO(f.data))
    return buf.getvalue()


def write_bundle(result: BuildResult, out_dir: Path, name: str, epoch: int) -> tuple[Path, bytes]:
    bundle = out_dir / name
    if bundle.exists():
        shutil.rmtree(bundle)
    for f in result.files:
        target = bundle / f.path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(f.data)
        os.chmod(target, f.mode)
    tar = tar_bytes(result.files, name, epoch)
    (out_dir / f"{name}.tar").write_bytes(tar)
    return bundle, tar


def write_published(result: BuildResult, name: str, tar: bytes) -> None:
    (PUB_DIR / "artifact").mkdir(exist_ok=True)
    (PUB_DIR / "internal").mkdir(exist_ok=True)
    (PUB_DIR / "artifact" / "manifest.json").write_bytes(dumps(result.manifest))
    (PUB_DIR / "artifact" / "checksums.txt").write_text(
        checksums_text(result.files, prefix=f"{name}/") + f"{sha256(tar)}  {name}.tar\n", encoding="utf-8")
    (PUB_DIR / "internal" / "provenance-map.json").write_bytes(dumps(result.provenance))
    (PUB_DIR / "internal" / "denylist.json").write_bytes(dumps(result.denylist))


def build(out_dir: Path, publish: bool) -> tuple[BuildResult, Path, bytes]:
    repo = repo_root()
    src = json.loads(SOURCES_FILE.read_text(encoding="utf-8"))
    ensure_commits(repo, pinned_commits(src))
    result = plan(src, collect_raw(repo, src))
    out_dir.mkdir(parents=True, exist_ok=True)
    bundle, tar = write_bundle(result, out_dir, src["bundle_name"], src["source_date_epoch"])
    if publish:
        write_published(result, src["bundle_name"], tar)
    return result, bundle, tar


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--out", type=Path, default=PUB_DIR / "build", help="output directory (default: build/)")
    parser.add_argument("--no-publish", action="store_true",
                        help="do not write artifact/manifest.json, artifact/checksums.txt or internal/*")
    args = parser.parse_args(argv)
    result, bundle, tar = build(args.out.resolve(), publish=not args.no_publish)
    pending = [e["path"] for e in result.manifest["entries"] if e["status"] == "pending"]
    print(f"bundle: {bundle} ({len(result.files)} files)")
    print(f"tar:    {bundle}.tar sha256={sha256(tar)}")
    print(f"pending optional inputs: {', '.join(pending) if pending else 'none'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
