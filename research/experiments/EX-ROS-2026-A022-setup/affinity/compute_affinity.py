#!/usr/bin/env python3
"""EX-ROS-2026-A022 pairwise work-item affinity matrix (see model.txt).

Pure and deterministic: reads observations.json and relations.json from this
directory and writes affinity-matrix.json and affinity-matrix.txt next to
them. No clock, randomness, network or repository state is read, so identical
inputs give byte-identical outputs.

Usage: python3 compute_affinity.py [--check]
  --check  exit 1 if the committed outputs differ from a fresh computation.
"""
import itertools
import json
import pathlib
import statistics
import sys

HERE = pathlib.Path(__file__).resolve().parent

# (dimension, observation field or relation kind, strong?)
SET_DIMENSIONS = (
    ("D1-concepts", "concepts", False),
    ("D2-state", "state", True),
    ("D3-invariants", "invariants", True),
    ("D4-files", "files", False),
    ("D5-surfaces", "surfaces", False),
    ("D8-validation", "validation", False),
    ("D9-errors", "errors", False),
    ("D10-transactions", "transactions", True),
    ("D11-tests", "tests", False),
)
RELATION_DIMENSIONS = (
    ("D6-producer", "producer-consumer", True),
    ("D7-ordering", "ordering", False),
    ("D12-assumptions", "assumption-propagation", True),
)
DIMENSION_ORDER = tuple(sorted(
    [d for d, _, _ in SET_DIMENSIONS] + [d for d, _, _ in RELATION_DIMENSIONS],
    key=lambda name: int(name.split("-")[0][1:])))
STRONG = frozenset(d for d, _, s in SET_DIMENSIONS + RELATION_DIMENSIONS if s)
GENERIC = frozenset({"src/Ros.Cli/Program.fs", "docs/cli.md", "cli:validate", "err:validate-finding"})
COHORTS = ("high", "low")
THRESHOLDS = {"H2-min-median-count": 5, "L3-max-median-count": 1, "L2-max-strong-pair-fraction": 0.2}


def load(name):
    return json.loads((HERE / name).read_text(encoding="utf-8"))


def split_state(token):
    """'state:X(mode)' -> ('state:X', mode); a token without a mode writes."""
    if token.endswith(")") and "(" in token:
        store, mode = token[:-1].split("(", 1)
        return store, mode
    return token, "write"


def shared_state(a, b):
    stores_a = {split_state(t) for t in a}
    stores_b = {split_state(t) for t in b}
    common = sorted({sa for sa, _ in stores_a} & {sb for sb, _ in stores_b} - GENERIC)
    writes = lambda pairs, store: any(s == store and m != "read" for s, m in pairs)
    return [s for s in common if writes(stores_a, s) or writes(stores_b, s)]


def shared(field, a, b):
    if field == "state":
        return shared_state(a.get(field, []), b.get(field, []))
    return sorted((set(a.get(field, [])) & set(b.get(field, []))) - GENERIC)


def generic_overlap(a, b):
    fields = [f for _, f, _ in SET_DIMENSIONS]
    every = lambda item: {split_state(t)[0] for f in fields for t in item.get(f, [])}
    return sorted(every(a) & every(b) & GENERIC)


def relations_between(relations, i, j, strength):
    return [r for r in relations
            if {r["from"], r["to"]} == {i, j} and r["strength"] == strength]


def pair_record(items, relations, i, j):
    a, b = items[i], items[j]
    set_dims = {name: shared(field, a, b) for name, field, _ in SET_DIMENSIONS}
    definite = relations_between(relations, i, j, "definite")
    rel_dims = {name: sorted(f'{r["from"]} -> {r["to"]}: {r["rationale"]}'
                             for r in definite if r["kind"] == kind)
                for name, kind, _ in RELATION_DIMENSIONS}
    dims = {name: values for name, values in {**set_dims, **rel_dims}.items() if values}
    possible = sorted(f'{r["kind"]} {r["from"]} -> {r["to"]}: {r["rationale"]}'
                      for r in relations_between(relations, i, j, "possible"))
    return {
        "pair": [i, j],
        "count": len(dims),
        "strong": len(STRONG & set(dims)),
        "dimensions": {name: dims[name] for name in DIMENSION_ORDER if name in dims},
        "possibleRelations": possible,
        "genericOverlaps": generic_overlap(a, b),
    }


def cohort_summary(pairs, members):
    in_cohort = [p for p in pairs if set(p["pair"]) <= set(members)]
    counts = [p["count"] for p in in_cohort]
    strongs = [p["strong"] for p in in_cohort]
    coverage = {d: sum(1 for p in in_cohort if d in p["dimensions"]) for d in DIMENSION_ORDER}
    n = len(in_cohort)
    rules = {
        "H1-every-pair-strong>=1": min(strongs) >= 1,
        "H2-median-count>=5": statistics.median(counts) >= THRESHOLDS["H2-min-median-count"],
        "H3-half-pairs-strong>=2": sum(1 for s in strongs if s >= 2) * 2 >= n,
        "L1-no-pair-strong>1": max(strongs) <= 1,
        "L2-strong-pairs<=1/5": sum(1 for s in strongs if s >= 1) <= THRESHOLDS["L2-max-strong-pair-fraction"] * n,
        "L3-median-count<=1": statistics.median(counts) <= THRESHOLDS["L3-max-median-count"],
    }
    high = all(v for k, v in rules.items() if k.startswith("H"))
    low = all(v for k, v in rules.items() if k.startswith("L"))
    return {
        "members": list(members),
        "pairs": n,
        "countDistribution": dict(sorted(((c, counts.count(c)) for c in set(counts)))),
        "strongDistribution": dict(sorted(((s, strongs.count(s)) for s in set(strongs)))),
        "medianCount": statistics.median(counts),
        "minStrong": min(strongs),
        "maxStrong": max(strongs),
        "pairsWithStrong": sum(1 for s in strongs if s >= 1),
        "dimensionCoverage": coverage,
        "rules": rules,
        "classification": "high" if high else "low" if low else "neither",
    }


def compute(observations, relations):
    items = {item["id"]: item for item in observations["items"]}
    ids = sorted(items)
    pairs = [pair_record(items, relations["relations"], i, j) for i, j in itertools.combinations(ids, 2)]
    cohorts = {c: sorted(i for i in ids if items[i]["cohortCandidate"] == c) for c in COHORTS}
    return {
        "experiment": observations["experiment"],
        "schema": "a022-affinity-matrix/1",
        "baseline": observations["baseline"],
        "inputs": ["observations.json", "relations.json"],
        "strongDimensions": sorted(STRONG),
        "genericExcluded": sorted(GENERIC),
        "thresholds": THRESHOLDS,
        "cohorts": {c: cohort_summary(pairs, m) for c, m in cohorts.items()},
        "pairs": pairs,
    }


def short(item_id):
    return item_id.replace("PRAXIS-", "").replace("ATTR-RECONCILE-", "")


def render_grid(matrix, members, key):
    lookup = {tuple(p["pair"]): p for p in matrix["pairs"]}
    cell = lambda i, j: "." if i == j else str(lookup[tuple(sorted((i, j)))][key])
    width = max(len(short(m)) for m in members) + 2
    header = " " * width + "".join(f"{n + 1:>4}" for n in range(len(members)))
    rows = [f"{short(m):<{width}}" + "".join(f"{cell(m, o):>4}" for o in members) for m in members]
    legend = [f"  {n + 1} = {m}" for n, m in enumerate(members)]
    return [header, *rows, *legend]


def render_text(matrix):
    lines = [
        "EX-ROS-2026-A022 -- affinity matrix (generated by compute_affinity.py; do not edit)",
        "=" * 82,
        f"Baseline: {matrix['baseline']}",
        f"Strong dimensions: {', '.join(matrix['strongDimensions'])}",
        f"Generic elements excluded from counts: {', '.join(matrix['genericExcluded'])}",
        "",
    ]
    for cohort, summary in matrix["cohorts"].items():
        members = summary["members"]
        lines += [f"Cohort candidate '{cohort}' ({len(members)} items, {summary['pairs']} pairs)",
                  "-" * 60, "Dimensions present per pair (count, 0..12):"]
        lines += render_grid(matrix, members, "count")
        lines += ["", "Strong dimensions present per pair (0..5):"]
        lines += render_grid(matrix, members, "strong")
        lines += ["",
                  f"median count {summary['medianCount']}; strong min {summary['minStrong']} max {summary['maxStrong']}; "
                  f"pairs with a strong dimension {summary['pairsWithStrong']}/{summary['pairs']}",
                  "dimension coverage (pairs): " + ", ".join(f"{d} {n}" for d, n in summary["dimensionCoverage"].items()),
                  "rules: " + ", ".join(f"{k}={'yes' if v else 'no'}" for k, v in summary["rules"].items()),
                  f"classification under model.txt section 6: {summary['classification'].upper()}", ""]
    lines += ["Pair detail (every present dimension with its shared elements)", "-" * 60]
    for p in matrix["pairs"]:
        lines.append(f"{p['pair'][0]} x {p['pair'][1]}: count {p['count']}, strong {p['strong']}")
        lines += [f"    {d}: {'; '.join(v)}" for d, v in p["dimensions"].items()]
        lines += [f"    possible (not counted): {r}" for r in p["possibleRelations"]]
        if p["genericOverlaps"]:
            lines.append(f"    generic overlaps (not counted): {', '.join(p['genericOverlaps'])}")
    return "\n".join(lines) + "\n"


def outputs():
    matrix = compute(load("observations.json"), load("relations.json"))
    return {
        "affinity-matrix.json": json.dumps(matrix, indent=2, sort_keys=False, ensure_ascii=True) + "\n",
        "affinity-matrix.txt": render_text(matrix),
    }


def main(argv):
    fresh = outputs()
    if "--check" in argv:
        stale = [n for n, text in fresh.items()
                 if not (HERE / n).exists() or (HERE / n).read_text(encoding="utf-8") != text]
        print("stale: " + ", ".join(stale) if stale else "affinity outputs are current")
        return 1 if stale else 0
    for name, text in fresh.items():
        (HERE / name).write_text(text, encoding="utf-8")
        print(f"wrote {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
