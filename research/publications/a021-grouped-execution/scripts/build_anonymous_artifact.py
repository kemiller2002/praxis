#!/usr/bin/env python3
"""Build the deterministic A021/R2 artifacts: the anonymous review bundle and the faithful bundle.

Usage (from anywhere inside the repository):

    python3 research/publications/a021-grouped-execution/scripts/build_anonymous_artifact.py
    python3 .../build_anonymous_artifact.py --out /tmp/x --no-publish   # rebuild elsewhere, touch nothing

Every run builds both bundles from the same pinned inputs:

* review (default submission artifact, double-anonymous): person identifiers
  removed, product/organisation names aliased by a mechanical same-length
  rename (baseline tree, patches, records, datasets, file paths), and every
  object id that resolves in the repository replaced by a stable pseudonym.
* faithful (camera-ready artifact): person identifiers removed; product names
  and object ids kept, patches byte-identical to their pinned sources.

Inputs are pinned in artifact_sources.json (commit SHA + path for every Git
input; optional datasets written by other agents are read from the working
tree, hashed, and listed as "pending" when absent).

Outputs (default):
    build/anonymous-artifact/, build/anonymous-artifact.tar     review bundle (git-ignored)
    build/artifact-faithful/,  build/artifact-faithful.tar      faithful bundle (git-ignored)
    artifact/manifest.json, artifact/checksums.txt              review manifest and checksums
    artifact/faithful/manifest.json, artifact/faithful/checksums.txt
    internal/provenance-map.json     PRIVATE alias/pseudonym -> real identifier map
    internal/denylist.json           PRIVATE identifier denylist for the verifier

Determinism: sorted traversal, pinned blobs, fixed tar mtime/uid/gid/mode, LF
line endings for everything this script writes, no clock or environment
input. The review bundle additionally depends on which hex tokens resolve in
the local object store; fetching more objects can only add pseudonyms, and
verify_artifact.py reports any resulting drift.

Functional core / imperative shell: the bundles are computed by pure
functions from collected inputs; I/O happens only in the ``git_*``/``read_*``/
``write_*`` helpers, ``build`` and ``main``.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable, Mapping

sys.dont_write_bytecode = True  # never leave interpreter caches inside a bundle
sys.path.insert(0, str(Path(__file__).resolve().parent))
import anonymize as an  # noqa: E402

SCRIPT_DIR = Path(__file__).resolve().parent
PUB_DIR = SCRIPT_DIR.parent
SOURCES_FILE = SCRIPT_DIR / "artifact_sources.json"
MODES = ("review", "faithful")
PUBLISH_DIR = {"review": Path("artifact"), "faithful": Path("artifact") / "faithful"}


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

    def get(self, key: str, default=None):
        return dict(self.meta).get(key, default)


@dataclass(frozen=True)
class Stage:
    """Person-redacted inputs shared by both modes."""
    content: tuple[BundleFile, ...]  # baseline source, patches, records, work items
    baseline_rows: tuple[tuple[str, str, str], ...]  # kept (mode, blob, path)
    terms: an.PersonTerms
    session_aliases: Mapping[str, str]
    session_urls: tuple[str, ...]
    uuid_aliases: Mapping[str, str]


@dataclass(frozen=True)
class ModeResult:
    mode: str
    bundle_name: str
    files: tuple[BundleFile, ...]
    manifest: dict
    renamed_paths: Mapping[str, str]  # bundle path in this mode -> faithful path
    review_counts: Mapping[str, Mapping[str, int]]


@dataclass(frozen=True)
class BuildResult:
    modes: Mapping[str, ModeResult]
    provenance: dict
    denylist: dict
    review_text: Callable[[str], str]  # the review transform, for the behaviour check


# --------------------------------------------------------------------------
# I/O edge: Git and files
# --------------------------------------------------------------------------


def git(repo: Path, *args: str, check: bool = True, stdin: bytes | None = None) -> bytes:
    proc = subprocess.run(("git", "-C", str(repo), *args), capture_output=True, check=False, input=stdin)
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
    out = git(repo, "cat-file", "--batch", stdin="".join(f"{s}\n" for s in shas).encode())
    pos, result = 0, {}
    for _ in shas:
        header_end = out.index(b"\n", pos)
        sha, _kind, size = out[pos:header_end].decode().split()
        start = header_end + 1
        result[sha] = out[start:start + int(size)]
        pos = start + int(size) + 1
    return result


def git_resolve(repo: Path, tokens: tuple[str, ...]) -> dict[str, str]:
    """token -> full object id, or token -> token when the prefix is ambiguous."""
    if not tokens:
        return {}
    out = git(repo, "cat-file", "--batch-check", stdin="".join(f"{t}\n" for t in tokens).encode())
    lines = out.decode().splitlines()
    pairs = zip(tokens, lines)
    return {t: (line.split()[0] if not line.endswith((" missing", " ambiguous")) else t)
            for t, line in pairs if not line.endswith(" missing")}


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


TITLE_RE = re.compile(r"\\title\{((?:[^{}]|\{[^{}]*\})*)\}")


def paper_title(tex: bytes | None) -> str:
    """The paper's \\title, de-LaTeXed just enough for plain text."""
    m = TITLE_RE.search(tex.decode("utf-8")) if tex else None
    if not m:
        return "the accompanying paper"
    text = re.sub(r"\\[a-zA-Z]+\*?\{([^{}]*)\}", r"\1", m.group(1)).replace("\\", "").replace("~", " ")
    return re.sub(r"\s+", " ", text.replace("{", "").replace("}", "")).strip()


MODE_BLOCK_RE = re.compile(r"<!-- mode:(\w+) -->\n?(.*?)<!-- /mode -->\n?", re.DOTALL)


def select_mode_blocks(text: str, mode: str) -> str:
    """Keep `<!-- mode:X -->...<!-- /mode -->` blocks for this mode, drop the others."""
    return MODE_BLOCK_RE.sub(lambda m: m.group(2) if m.group(1) == mode else "", text)


# --------------------------------------------------------------------------
# Inputs
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
    analysis = src["analysis_glob"]
    analysis_dir = repo / analysis["dir"]
    analysis_files = tuple(sorted(
        (p.name, p.read_bytes()) for p in analysis_dir.iterdir()
        if p.is_file() and p.suffix in analysis["suffixes"] and p.name not in analysis.get("exclude_names", ())
    ))
    return {
        "tree": tree,
        "kept": kept,
        "blobs": blobs,
        "tree_sha": git(repo, "rev-parse", f"{base['commit']}^{{tree}}").decode().strip(),
        "queue": git_blob(repo, base["commit"], base["work_items"]["path"]),
        "patches": {p["bundle_path"]: (git_diff(repo, p["base"], p["head"], p["pathspec"]) if p["kind"] == "git-diff"
                                       else git_blob(repo, p["commit"], p["path"])) for p in src["patches"]},
        "records": {r["bundle_path"]: git_blob(repo, r["commit"], r["path"]) for r in src["records"]},
        "optional": {o["bundle_path"]: read_optional(repo / o["path"]) for o in src["optional_worktree"]},
        "analysis": analysis_files,
        "readme": (repo / src["authored"]["readme"]).read_bytes(),
        "paper": read_optional(repo / src["authored"]["paper"]),
        "identities": git_identities(repo, (c for c, _ in pinned_commits(src))),
        "remote": git_remote(repo),
    }


def work_items(queue: bytes, prefix: str) -> bytes:
    return dumps([i for i in json.loads(queue)["items"] if str(i.get("id", "")).startswith(prefix)])


# --------------------------------------------------------------------------
# Stage 1 (pure): person redaction, shared by both modes
# --------------------------------------------------------------------------


def stage(src: Mapping, raw: Mapping) -> Stage:
    terms = an.person_terms(an.parse_identities(raw["identities"]), an.owner_from_remote(raw["remote"]))
    persons = an.person_rules(terms)

    optional_present = {k: v for k, v in raw["optional"].items() if v is not None}
    transforms = {o["bundle_path"]: o.get("transform", "none") for o in src["optional_worktree"]}
    adir = src["analysis_glob"]["bundle_dir"]
    record_inputs = (
        tuple((p, text_lf(d), "none") for p, d in sorted(raw["records"].items()))
        + tuple((p, text_lf(d), transforms[p]) for p, d in sorted(optional_present.items()))
        + tuple((f"{adir}/{n}", text_lf(d), "none") for n, d in raw["analysis"])
    )
    transformed = tuple((p, an.TRANSFORMS[t](txt), t) for p, txt, t in record_inputs)
    corpus = tuple(t for _, t, _ in transformed)
    session_aliases = an.pseudonyms(corpus, an.SESSION_ID_RE, "agent-session")
    session_urls = tuple(sorted({m.group(0) for t in corpus for m in an.SESSION_URL_RE.finditer(t)}))
    uuid_aliases = an.pseudonyms(corpus, an.UUID_RE, "uuid")
    record_rules = an.rules_for(an.PROFILE_RECORD, persons, an.record_rules(session_aliases, uuid_aliases))
    code_rules = an.rules_for(an.PROFILE_CODE, persons, ())

    origins = {
        **{r["bundle_path"]: {"commit": r["commit"], "path": r["path"], "role": r["role"]} for r in src["records"]},
        **{o["bundle_path"]: {"worktree": o["path"], "role": o["role"]} for o in src["optional_worktree"]},
        **{f"{adir}/{n}": {"worktree": f"{src['analysis_glob']['dir']}/{n}", "role": src["analysis_glob"]["role"]}
           for n, _ in raw["analysis"]},
    }

    def record_file(path: str, text: str, transform: str) -> BundleFile:
        result = an.apply_rules(text, record_rules)
        mode = 0o755 if path.endswith((".sh", ".py")) and text.startswith("#!") else 0o644
        return BundleFile(path, result.text.encode("utf-8"), mode, an.PROFILE_RECORD,
                          (("redactions", dict(result.counts)), ("transform", transform), ("origin", origins[path])))

    records = tuple(record_file(p, t, tr) for p, t, tr in transformed)

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

    base = src["baseline"]

    def baseline_file(row: tuple[str, str, str]) -> BundleFile:
        mode, blob, path = row
        out, counts = redact_bytes(raw["blobs"][blob], code_rules, keep_newlines=True)
        return BundleFile(f"{base['bundle_dir']}/{path}", out, git_mode(mode), an.PROFILE_CODE,
                          (("redactions", dict(counts)), ("git_blob", blob), ("git_path", path)))

    baseline_files = tuple(baseline_file(r) for r in raw["kept"])
    wi = base["work_items"]
    wi_out, wi_counts = redact_bytes(work_items(raw["queue"], wi["id_prefix"]), record_rules, keep_newlines=False)
    wi_file = BundleFile(wi["bundle_path"], wi_out, 0o644, an.PROFILE_RECORD,
                         (("redactions", dict(wi_counts)),
                          ("origin", {"commit": base["commit"], "path": wi["path"],
                                      "filter": f"items whose id starts with {wi['id_prefix']}"})))
    return Stage(baseline_files + patches + records + (wi_file,), raw["kept"], terms,
                 session_aliases, session_urls, uuid_aliases)


def stage_corpus(st: Stage, extra_texts: Iterable[str]) -> tuple[str, ...]:
    texts = tuple(f.data.decode("utf-8") for f in st.content if an.is_text(f.data))
    return texts + tuple(f.path for f in st.content) + tuple(extra_texts)


# --------------------------------------------------------------------------
# Stage 2 (pure): per-mode bundle
# --------------------------------------------------------------------------


def baseline_txt(src: Mapping, raw: Mapping, shipped: int, redacted: int, mode: str) -> str:
    base = src["baseline"]
    counts = {p: sum(1 for r in raw["tree"] if r[2].startswith(p)) for p in base["exclude_prefixes"]}
    check = (
        "Every shipped file is the exact Git blob of the baseline commit unless FILES.tsv\n"
        "marks it redacted. An unredacted file can be checked against the baseline with\n"
        "`git hash-object <file>` == git_blob_at_baseline.\n"
        if mode == "faithful" else
        "In this review bundle the subject system's product and organisation names are\n"
        "replaced by same-length aliases and every object id by a pseudonym, in file\n"
        "contents and in file paths; FILES.tsv marks each file so changed (aliased) and\n"
        "each file whose person identifiers were replaced (redacted).\n"
    )
    return "".join((
        "Baseline snapshot\n=================\n\n",
        f"Commit:            {base['commit']}\n",
        f"Git tree:          {raw['tree_sha']}\n",
        f"Files in commit:   {len(raw['tree'])}\n",
        f"Files shipped:     {shipped} (under source/)\n",
        f"Files redacted:    {redacted} (person identifiers only; see FILES.tsv)\n\n",
        "Excluded top-level paths (whole subtrees, not shipped):\n",
        *(f"  {p:<18} {counts[p]:>4} files  {base['exclude_reason'][p]}\n" for p in base["exclude_prefixes"]),
        f"\n{base.get('exclude_note', '')}\n\n" if base.get("exclude_note") else "\n",
        check,
        "Redactions replace person identifiers (names, e-mail addresses, account names)\n",
        "with neutral placeholders. All four arm patches apply cleanly to source/\n",
        "(checked by analysis/verify_artifact.py), and the F# test suite gives the same\n",
        "per-test results on the original and on this baseline (see README.md).\n",
    ))


def files_tsv(rows: Iterable[tuple[BundleFile, BundleFile]], mode: str) -> str:
    """rows: (faithful-stage file, shipped file)."""
    if mode == "faithful":
        return "path\tmode\tgit_blob_at_baseline\tsha256_shipped\tredacted\n" + "".join(
            f"{f.get('git_path')}\t{s.mode:o}\t{f.get('git_blob')}\t{sha256(s.data)}\t"
            f"{'yes' if f.get('redactions') else 'no'}\n" for f, s in rows)
    return "path\tmode\tsha256_shipped\tredacted\taliased\n" + "".join(
        f"{s.path.split('/', 2)[2]}\t{s.mode:o}\t{sha256(s.data)}\t{'yes' if f.get('redactions') else 'no'}\t"
        f"{'yes' if (s.data != f.data or s.path != f.path) else 'no'}\n" for f, s in rows)


def bundle_for_mode(src: Mapping, raw: Mapping, st: Stage, mode: str,
                    review_text: Callable[[str], str], review_counts: Callable[[str], dict]) -> ModeResult:
    review = mode == "review"
    xf = review_text if review else (lambda t: t)

    def ship(f: BundleFile) -> BundleFile:
        if not review:
            return f
        data = xf(f.data.decode("utf-8")).encode("utf-8") if an.is_text(f.data) else f.data
        return BundleFile(xf(f.path), data, f.mode, f.profile,
                          f.meta + (("review_changes", review_counts(f.data.decode("utf-8"))
                                     if an.is_text(f.data) else {}),))

    shipped = tuple((f, ship(f)) for f in st.content)
    base_dir = src["baseline"]["bundle_dir"]
    baseline_pairs = tuple((f, s) for f, s in shipped if f.path.startswith(base_dir + "/"))
    redacted = sum(1 for f, _ in baseline_pairs if f.get("redactions"))
    authored = (
        BundleFile("README.md", xf(select_mode_blocks(text_lf(raw["readme"]), mode)
                                   .replace("{{PAPER_TITLE}}", paper_title(raw["paper"]))).encode(), 0o644, "authored"),
        BundleFile("MAPPING.json", xf(dumps({
            "schema": "a021-anonymous-artifact.mapping/1",
            "note": "Evaluation is complete in both studies; the paper reports this mapping. "
                    "Blind file names are kept so that evaluator outputs can be read verbatim.",
            **src["mapping"],
        }).decode()).encode(), 0o644, "authored"),
        BundleFile("baseline/BASELINE.txt",
                   xf(baseline_txt(src, raw, len(baseline_pairs), redacted, mode)).encode(), 0o644, "authored"),
        BundleFile("baseline/FILES.tsv", xf(files_tsv(baseline_pairs, mode)).encode(), 0o644, "authored"),
    )
    content = authored + tuple(s for _, s in shipped)
    pending = tuple(o for o in src["optional_worktree"] if raw["optional"][o["bundle_path"]] is None)
    hidden = ("git_blob", "git_path")
    manifest = {
        "schema": "a021-anonymous-artifact.manifest/2",
        "mode": mode,
        "bundle": src["bundle_name"][mode],
        "source_date_epoch": src["source_date_epoch"],
        "baseline": {
            "commit": src["baseline"]["commit"], "tree": raw["tree_sha"], "bundle_dir": base_dir,
            "files_in_commit": len(raw["tree"]), "files_shipped": len(baseline_pairs),
            "files_redacted": redacted, "excluded_prefixes": list(src["baseline"]["exclude_prefixes"]),
            "file_list": "baseline/FILES.tsv",
        },
        "policy": {
            "removed": ["personal names", "e-mail addresses", "account names and account-scoped URLs",
                        "agent-session identifiers and URLs", "transcript/tool UUIDs (records only)",
                        "Co-Authored-By / session trailer lines (records only)", "fields named internal_ref"]
                       + (["product and organisation names (same-length aliases, contents and paths)",
                           "object ids that resolve in the study repository (same-length pseudonyms)"]
                          if review else []),
            "retained": (["the three-letter project prefix used in namespaces and record ids (generic acronym)",
                          "tool identities (model and evaluator names, disclosed by the paper)"]
                         if review else
                         ["product, package and organisation names", "commit SHAs",
                          "tool identities (model and evaluator names, disclosed by the paper)"]),
            "patches": ("person redaction (none needed) then the mechanical review rename; they apply to "
                        "baseline/source" if review else "byte-identical to their pinned sources"),
            "document": "README.md, section 'Anonymization'",
        },
        "entries": sorted([
            {"path": s.path, "status": "included", "profile": s.profile, "sha256": sha256(s.data),
             "bytes": len(s.data), **{k: v for k, v in s.meta if k not in hidden}}
            for s in content if not s.path.startswith(base_dir + "/")
        ] + [
            {"path": xf(o["bundle_path"]), "status": "pending",
             "origin": {"worktree": o["path"], "role": o["role"]},
             "note": "input not present at build time; verify --strict fails until it exists"}
            for o in pending
        ], key=lambda e: e["path"]),
    }
    manifest_file = BundleFile("manifest.json", xf(dumps(manifest).decode()).encode(), 0o644, "authored")
    with_manifest = content + (manifest_file,)
    checksum_file = BundleFile("checksums.txt", checksums_text(with_manifest).encode(), 0o644, "authored")
    files = tuple(sorted(with_manifest + (checksum_file,), key=lambda f: f.path))
    return ModeResult(
        mode, src["bundle_name"][mode], files, json.loads(manifest_file.data),
        {s.path: f.path for f, s in shipped if s.path != f.path},
        {s.path: dict(s.get("review_changes", {})) for _, s in shipped if s.get("review_changes")},
    )


def plan(src: Mapping, raw: Mapping, st: Stage, resolved: Mapping[str, str]) -> BuildResult:
    cfg = an.alias_config(src["review"])
    smap = an.sha_map(resolved)
    rules = an.review_rules(cfg, smap)
    review_text = lambda t: an.apply_review(t, cfg, rules).text  # noqa: E731
    review_counts = lambda t: dict(an.apply_review(t, cfg, rules).counts)  # noqa: E731
    modes = {m: bundle_for_mode(src, raw, st, m, review_text, review_counts) for m in MODES}
    terms = st.terms
    patch_specs = {p["bundle_path"]: p for p in src["patches"]}
    faithful = modes["faithful"]
    provenance = {
        "schema": "a021-anonymous-artifact.provenance/2",
        "PRIVATE": "Never copy this file into an anonymous bundle. It maps every alias back to real identifiers.",
        "aliases": {
            "accounts": {o: an.ANON_OWNER for o in terms.owners},
            "names": {**{n: an.ANON_NAME for n in terms.full_names},
                      **{t: an.ANON_NAME_TOKEN for t in terms.name_tokens}},
            "emails": {e: an.ANON_EMAIL for e in terms.emails},
            "agent_sessions": {v: k for k, v in st.session_aliases.items()},
            "agent_session_urls": {u: an.ANON_SESSION_URL for u in st.session_urls},
            "uuids": {v: k for k, v in st.uuid_aliases.items()},
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
        "review": {
            "token_aliases": {a: r for r, a in cfg.tokens + cfg.bounded},
            "phrase_aliases": {" ".join(a): " ".join(r) for r, a in cfg.phrases},
            "retained_tokens": src["review"]["retained_tokens"],
            "protected_literals": list(cfg.protected),
            "object_pseudonyms": {p: real for real, p in sorted(smap.pseudo.items())},
            "object_tokens": {t: smap.pseudo[k][:len(t)] for t, k in sorted(smap.resolved.items())},
            "renamed_paths": dict(sorted(modes["review"].renamed_paths.items())),
            "changes_per_file": dict(sorted(modes["review"].review_counts.items())),
        },
        "repository": {"remote": raw["remote"]},
        "bundle_sources": {f.path: f.get("origin") for f in faithful.files if f.get("origin")},
        "redacted_baseline_files": {
            f.get("git_path"): {"git_blob": f.get("git_blob"), "rules": f.get("redactions"),
                                "shipped_sha256": sha256(f.data)}
            for f in st.content if f.get("git_path") and f.get("redactions")
        },
        "identities_seen": [{"name": i.name, "email": i.email, "tool": i.is_tool}
                            for i in an.parse_identities(raw["identities"])],
    }
    denylist = {**an.build_denylist(terms, tuple(r for r, _ in cfg.tokens + cfg.bounded)),
                **an.review_denylist(cfg, smap.pseudo.keys())}
    return BuildResult(modes, provenance, denylist, review_text)


# --------------------------------------------------------------------------
# Output (I/O edge)
# --------------------------------------------------------------------------


def tar_bytes(files: Iterable[BundleFile], root: str, epoch: int) -> bytes:
    files = tuple(files)
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


def write_bundle(result: ModeResult, out_dir: Path, epoch: int) -> tuple[Path, bytes]:
    bundle = out_dir / result.bundle_name
    if bundle.exists():
        shutil.rmtree(bundle)
    for f in result.files:
        target = bundle / f.path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(f.data)
        os.chmod(target, f.mode)
    tar = tar_bytes(result.files, result.bundle_name, epoch)
    (out_dir / f"{result.bundle_name}.tar").write_bytes(tar)
    return bundle, tar


def published_checksums(result: ModeResult, tar: bytes) -> str:
    return checksums_text(result.files, prefix=f"{result.bundle_name}/") + f"{sha256(tar)}  {result.bundle_name}.tar\n"


def write_published(result: BuildResult, tars: Mapping[str, bytes]) -> None:
    for mode, mres in result.modes.items():
        target = PUB_DIR / PUBLISH_DIR[mode]
        target.mkdir(parents=True, exist_ok=True)
        (target / "manifest.json").write_bytes(dumps(mres.manifest))
        (target / "checksums.txt").write_text(published_checksums(mres, tars[mode]), encoding="utf-8")
    (PUB_DIR / "internal").mkdir(exist_ok=True)
    (PUB_DIR / "internal" / "provenance-map.json").write_bytes(dumps(result.provenance))
    (PUB_DIR / "internal" / "denylist.json").write_bytes(dumps(result.denylist))


def resolve_objects(repo: Path, src: Mapping, st: Stage, raw: Mapping) -> dict[str, str]:
    """I/O: which hex tokens are object ids; refuses aliases or pseudonyms that collide."""
    # The authored README names the aliases on purpose, so it is excluded from the collision probe.
    corpus = stage_corpus(st, ())
    cfg = an.alias_config(src["review"])
    collisions = an.alias_collisions(corpus, cfg)
    corpus = corpus + (text_lf(raw["readme"]), paper_title(raw["paper"]))
    if collisions:
        raise RuntimeError(f"review aliases already occur in the faithful corpus: {collisions}")
    # Object ids may also sit only in generated text (manifest origins, BASELINE.txt).
    candidates = an.hex_candidates(corpus + (json.dumps(src), raw["tree_sha"]))
    resolved = git_resolve(repo, candidates)
    smap = an.sha_map(resolved)
    pseudo_tokens = tuple(sorted({p[:len(t)] for t, k in smap.resolved.items() for p in (smap.pseudo[k],)}))
    clash = tuple(p for p in pseudo_tokens if p in set(candidates))
    real = git_resolve(repo, pseudo_tokens)
    if clash or real:
        raise RuntimeError(f"object pseudonyms collide with existing tokens/objects: {sorted(set(clash) | set(real))}")
    return resolved


def build(out_dir: Path, publish: bool) -> tuple[BuildResult, dict[str, Path], dict[str, bytes]]:
    repo = repo_root()
    src = json.loads(SOURCES_FILE.read_text(encoding="utf-8"))
    ensure_commits(repo, pinned_commits(src))
    raw = collect_raw(repo, src)
    st = stage(src, raw)
    result = plan(src, raw, st, resolve_objects(repo, src, st, raw))
    out_dir.mkdir(parents=True, exist_ok=True)
    written = {m: write_bundle(r, out_dir, src["source_date_epoch"]) for m, r in result.modes.items()}
    bundles = {m: w[0] for m, w in written.items()}
    tars = {m: w[1] for m, w in written.items()}
    if publish:
        write_published(result, tars)
    return result, bundles, tars


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--out", type=Path, default=PUB_DIR / "build", help="output directory (default: build/)")
    parser.add_argument("--no-publish", action="store_true",
                        help="do not write artifact/*, artifact/faithful/* or internal/*")
    args = parser.parse_args(argv)
    result, bundles, tars = build(args.out.resolve(), publish=not args.no_publish)
    for mode in MODES:
        mres = result.modes[mode]
        pending = [e["path"] for e in mres.manifest["entries"] if e["status"] == "pending"]
        print(f"[{mode}] bundle: {bundles[mode]} ({len(mres.files)} files)")
        print(f"[{mode}] tar:    {bundles[mode]}.tar sha256={sha256(tars[mode])}")
        print(f"[{mode}] pending optional inputs: {', '.join(pending) if pending else 'none'}")
    print(f"[review] object pseudonyms: {len(result.provenance['review']['object_pseudonyms'])}, "
          f"renamed paths: {len(result.provenance['review']['renamed_paths'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
