#!/usr/bin/env python3
"""Compute WI-0075 agreement after a blind human coding sheet is frozen.

This script must not be run to coach the coder. It compares the returned human
categories with the already-frozen AI architecture audit only after the human
sheet has been committed unchanged.
"""
from __future__ import annotations

import argparse
import csv
import json
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
PUB = HERE.parent
DEFAULT_AI = PUB / "data" / "architecture-findings.json"
CATEGORIES = {"unified", "duplicated-compatible", "divergent", "missing", "not-assessable"}
CONFIDENCE = {"high", "medium", "low"}
EXPECTED = {(s, a, f"D{d}") for s, arms in (("S1", ("X", "Y")), ("S2", ("M", "N"))) for a in arms for d in range(1, 9)}


def kappa(pairs):
    if not pairs:
        return None
    n = len(pairs)
    po = sum(a == b for a, b in pairs) / n
    left = Counter(a for a, _ in pairs)
    right = Counter(b for _, b in pairs)
    pe = sum((left[c] / n) * (right[c] / n) for c in CATEGORIES)
    if abs(1.0 - pe) < 1e-12:
        return None
    return (po - pe) / (1.0 - pe)


def load_human(path: Path):
    rows = {}
    coder_ids = set()
    with path.open(newline="", encoding="utf-8") as f:
        for row in csv.DictReader(f):
            key = (row["study"].strip(), row["blind_alias"].strip(), row["dimension"].strip())
            if key in rows:
                raise SystemExit(f"duplicate human row: {key}")
            category = row["category"].strip()
            conf = row["confidence"].strip()
            if category not in CATEGORIES:
                raise SystemExit(f"{key}: invalid or empty category {category!r}")
            if conf not in CONFIDENCE:
                raise SystemExit(f"{key}: invalid or empty confidence {conf!r}")
            if not row["rationale"].strip() or not row["evidence_paths"].strip():
                raise SystemExit(f"{key}: evidence_paths and rationale are required")
            rows[key] = row
            if row["coder_id"].strip():
                coder_ids.add(row["coder_id"].strip())
    missing = EXPECTED - set(rows)
    extra = set(rows) - EXPECTED
    if missing or extra:
        raise SystemExit(f"coding sheet shape mismatch; missing={sorted(missing)}, extra={sorted(extra)}")
    if len(coder_ids) != 1:
        raise SystemExit(f"expected exactly one coder_id, got {sorted(coder_ids)}")
    return rows, next(iter(coder_ids))


def alias_token(text: str) -> str:
    # architecture-findings blind_alias begins "arm X ...", etc.
    parts = text.split()
    return parts[1] if len(parts) >= 2 and parts[0].lower() == "arm" else ""


def load_ai(path: Path):
    doc = json.loads(path.read_text(encoding="utf-8"))
    impls = {i["id"]: i for i in doc["implementations"]}
    by_key = {}
    for f in doc["findings"]:
        impl = impls[f["implementation"]]
        study = "S1" if f["study"] == "A021" else "S2"
        arm = alias_token(impl["blind_alias"])
        key = (study, arm, f["dimension"])
        by_key[key] = {
            "category": f["category"],
            "finding_id": f["id"],
            "implementation": f["implementation"],
        }
    if set(by_key) != EXPECTED:
        raise SystemExit("AI audit does not resolve to the expected 32 blind coding units")
    return by_key


def summarize(human, ai):
    pairs = []
    disagreements = []
    per_dimension = defaultdict(list)
    per_study = defaultdict(list)
    for key in sorted(EXPECTED):
        h = human[key]["category"].strip()
        a = ai[key]["category"]
        pair = (a, h)
        pairs.append(pair)
        per_dimension[key[2]].append(pair)
        per_study[key[0]].append(pair)
        if a != h:
            disagreements.append({
                "study": key[0],
                "blind_alias": key[1],
                "dimension": key[2],
                "ai_category": a,
                "human_category": h,
                "human_confidence": human[key]["confidence"].strip(),
                "human_evidence": human[key]["evidence_paths"].strip(),
                "human_rationale": human[key]["rationale"].strip(),
                "ai_finding_id": ai[key]["finding_id"],
            })

    def block(ps):
        return {
            "n": len(ps),
            "agreements": sum(a == b for a, b in ps),
            "raw_agreement": sum(a == b for a, b in ps) / len(ps),
            "cohens_kappa": kappa(ps),
        }

    return {
        "overall": block(pairs),
        "by_dimension": {d: block(per_dimension[d]) for d in sorted(per_dimension)},
        "by_study": {s: block(per_study[s]) for s in sorted(per_study)},
        "disagreements": disagreements,
    }


def markdown(result, coder_id):
    def fmt(v):
        return "undefined" if v is None else f"{v:.3f}"

    out = [
        "# WI-0075 human/AI architecture-coding agreement",
        "",
        f"Human coder: `{coder_id}`",
        "",
        "The human sheet was coded under blind aliases. Disagreements remain disagreements; this report does not adjudicate them.",
        "",
        "## Overall",
        "",
        f"- raw agreement: {result['overall']['agreements']}/{result['overall']['n']} = {result['overall']['raw_agreement']:.3f}",
        f"- Cohen's kappa: {fmt(result['overall']['cohens_kappa'])}",
        "",
        "## By dimension",
        "",
        "| Dimension | Agreement | Raw | Kappa |",
        "|---|---:|---:|---:|",
    ]
    for d, b in result["by_dimension"].items():
        out.append(f"| {d} | {b['agreements']}/{b['n']} | {b['raw_agreement']:.3f} | {fmt(b['cohens_kappa'])} |")
    out += ["", "## Disagreements", ""]
    if not result["disagreements"]:
        out.append("None.")
    else:
        out += [
            "| Study | Arm | Dim | AI | Human | Human confidence |",
            "|---|---|---|---|---|---|",
        ]
        for x in result["disagreements"]:
            out.append(f"| {x['study']} | {x['blind_alias']} | {x['dimension']} | {x['ai_category']} | {x['human_category']} | {x['human_confidence']} |")
    out += [
        "",
        "Unresolved disagreements are not independently corroborated under the frozen codebook rule.",
        "",
    ]
    return "\n".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("human_csv", type=Path)
    ap.add_argument("--ai", type=Path, default=DEFAULT_AI)
    ap.add_argument("--json-out", type=Path)
    ap.add_argument("--md-out", type=Path)
    args = ap.parse_args()

    human, coder_id = load_human(args.human_csv)
    ai = load_ai(args.ai)
    result = summarize(human, ai)
    result["coder_id"] = coder_id

    print(json.dumps(result, indent=2, sort_keys=True))
    if args.json_out:
        args.json_out.parent.mkdir(parents=True, exist_ok=True)
        args.json_out.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    if args.md_out:
        args.md_out.parent.mkdir(parents=True, exist_ok=True)
        args.md_out.write_text(markdown(result, coder_id) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
