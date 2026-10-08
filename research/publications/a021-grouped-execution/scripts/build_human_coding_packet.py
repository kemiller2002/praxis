#!/usr/bin/env python3
"""Build the WI-0075 treatment-blind human architecture-coding packet.

The packet is intentionally narrower than the anonymous review artifact. It
contains no mapping, evaluator output, metrics, prompts, manuscript, or prior
architecture ratings.
"""
from __future__ import annotations

import hashlib
import json
import shutil
import subprocess
import sys
import tarfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
PUB = HERE.parent
BUILD = PUB / "build"
SOURCE = BUILD / "anonymous-artifact"
DEST = BUILD / "human-coding-packet"
TAR = BUILD / "human-coding-packet.tar"
EPOCH = 1790726400

COPY_FILES = [
    ("BASELINE.txt", "BASELINE.txt"),
    ("FILES.tsv", "FILES.tsv"),
    ("acceptance-criteria.txt", "acceptance-criteria.txt"),
    ("a021/arm-x.patch", "study-1/arm-X.patch"),
    ("a021/arm-y.patch", "study-1/arm-Y.patch"),
    ("r2/arm-M.patch", "study-2/arm-M.patch"),
    ("r2/arm-N.patch", "study-2/arm-N.patch"),
]
LOCAL_FILES = [
    ("human-coding/CODEBOOK.md", "CODEBOOK.md"),
    ("human-coding/INSTRUCTIONS.md", "INSTRUCTIONS.md"),
    ("human-coding/coding-sheet.csv", "coding-sheet.csv"),
    ("human-coding/FREEZE.json", "FREEZE.json"),
]


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def copy_file(src: Path, dst: Path) -> None:
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(src, dst)


def build_review_artifact() -> None:
    subprocess.run([sys.executable, str(HERE / "build_anonymous_artifact.py")], check=True)


def write_readme() -> None:
    text = """# Independent human architecture-coding packet

This packet is for WI-0075. It intentionally does not reveal which anonymous
arm received which execution treatment.

Read INSTRUCTIONS.md and CODEBOOK.md before inspecting patches.

Contents:
- baseline/: anonymized baseline source tree;
- study-1/arm-X.patch and arm-Y.patch;
- study-2/arm-M.patch and arm-N.patch;
- acceptance-criteria.txt;
- coding-sheet.csv;
- FREEZE.json and checksums.txt.

Do not search the public repository, experiment IDs, branch names, paper, or
prior evaluations while coding. If treatment mapping or prior ratings become
known, record the exposure and stop.
"""
    (DEST / "README.md").write_text(text, encoding="utf-8", newline="
")


def write_checksums() -> None:
    paths = sorted(
        p for p in DEST.rglob("*")
        if p.is_file() and p.name != "checksums.txt"
    )
    lines = [f"{sha256(p)}  {p.relative_to(DEST).as_posix()}" for p in paths]
    (DEST / "checksums.txt").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")


def add_tar_member(tf: tarfile.TarFile, path: Path) -> None:
    rel = Path("human-coding-packet") / path.relative_to(DEST)
    info = tf.gettarinfo(str(path), arcname=rel.as_posix())
    info.mtime = EPOCH
    info.uid = info.gid = 0
    info.uname = info.gname = ""
    with path.open("rb") as f:
        tf.addfile(info, f)


def write_tar() -> None:
    if TAR.exists():
        TAR.unlink()
    with tarfile.open(TAR, "w", format=tarfile.GNU_FORMAT) as tf:
        dirs = sorted((p for p in DEST.rglob("*") if p.is_dir()), key=lambda p: p.as_posix())
        for d in dirs:
            rel = Path("human-coding-packet") / d.relative_to(DEST)
            info = tf.gettarinfo(str(d), arcname=rel.as_posix())
            info.mtime = EPOCH
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            tf.addfile(info)
        for p in sorted((p for p in DEST.rglob("*") if p.is_file()), key=lambda p: p.as_posix()):
            add_tar_member(tf, p)


def verify_absence() -> None:
    forbidden = [
        "MAPPING.json",
        "architecture-findings.json",
        "evaluation.txt",
        "findings.json",
        "paper.tex",
        "metrics.json",
        "POST-UNBLINDING-METRICS",
    ]
    joined = "\n".join(p.relative_to(DEST).as_posix() for p in DEST.rglob("*"))
    bad = [token for token in forbidden if token in joined]
    if bad:
        raise SystemExit(f"forbidden packet material present: {bad}")


def main() -> int:
    if "--no-build" not in sys.argv:
        build_review_artifact()
    if not SOURCE.is_dir():
        raise SystemExit(f"review artifact missing: {SOURCE}")

    if DEST.exists():
        shutil.rmtree(DEST)
    DEST.mkdir(parents=True)

    shutil.copytree(SOURCE / "baseline", DEST / "baseline")

    for src, dst in COPY_FILES:
        copy_file(SOURCE / src, DEST / dst)
    for src, dst in LOCAL_FILES:
        copy_file(PUB / src, DEST / dst)

    write_readme()
    verify_absence()
    write_checksums()
    write_tar()
    print(json.dumps({
        "packet": str(DEST),
        "tar": str(TAR),
        "files": sum(1 for p in DEST.rglob("*") if p.is_file()),
        "tar_sha256": sha256(TAR),
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
