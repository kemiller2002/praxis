#!/usr/bin/env python3
"""EX-ROS-2026-A022 blind mapping, salted commitment and reveal verification.

See commitment-reveal.txt for the protocol. Canonical form (CANONICAL-1):
  JSON, UTF-8, object keys sorted by Unicode code point at every level,
  separators ',' and ':' with no whitespace, non-ASCII escaped as \\uXXXX,
  no trailing newline. The commitment is the lowercase hex SHA-256 of exactly
  those bytes. The reveal file holds exactly those bytes.

Usage:
  a022_commitment.py generate --high-grouped T --high-independent T \\
      --low-grouped T --low-independent T \\
      --final T=SHA [--final T=SHA ...] --out DIR
      Draws neutral arm and kit identifiers and a 256-bit salt with the OS
      CSPRNG, writes DIR/reveal.json (canonical bytes) and
      DIR/commitment.sha256, prints the commitment and the evaluator-visible
      kit table (kit -> two neutral arms) on stdout. DIR must be outside the
      repository working tree.
  a022_commitment.py verify --reveal FILE --commitment HEX
      Exit 0 only if FILE is in canonical form and SHA-256(FILE) == HEX.
  a022_commitment.py selftest
      Checks canonicalization and verification on a fixed example.
"""
import argparse
import hashlib
import json
import pathlib
import secrets
import sys

EXPERIMENT = "EX-ROS-2026-A022"
PAYLOAD_VERSION = 1


def canonical_bytes(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("utf-8")


def commitment(raw):
    return hashlib.sha256(raw).hexdigest()


def distinct_ids(prefix, count, draw):
    """`count` distinct identifiers prefix-<6 hex>, drawn with `draw`."""
    def grow(acc):
        return acc if len(acc) == count else grow(acc | {f"{prefix}-{draw(3)}"})
    return sorted(grow(frozenset()))


def shuffled(values, draw_below):
    """Fisher-Yates over a tuple, returning a new tuple (no mutation of input)."""
    def step(acc, i):
        j = draw_below(i + 1)
        swapped = list(acc)
        swapped[i], swapped[j] = swapped[j], swapped[i]
        return tuple(swapped)
    from functools import reduce
    return reduce(step, range(len(values) - 1, 0, -1), tuple(values))


def build_payload(conditions, finals, draw_hex=secrets.token_hex, draw_below=secrets.randbelow):
    """conditions: {(cohort, mode): runToken}; finals: {runToken: finalSha}."""
    arm_ids = shuffled(distinct_ids("arm", 4, draw_hex), draw_below)
    kit_ids = shuffled(distinct_ids("kit", 2, draw_hex), draw_below)
    order = (("high", "grouped"), ("high", "independent"), ("low", "grouped"), ("low", "independent"))
    arms = {arm: {"cohort": cohort, "mode": mode, "runToken": conditions[(cohort, mode)],
                  "executionBranch": f"experiment/a022-run-{conditions[(cohort, mode)]}",
                  "finalSha": finals[conditions[(cohort, mode)]]}
            for arm, (cohort, mode) in zip(arm_ids, order)}
    kits = {kit_ids[0]: "high", kit_ids[1]: "low"}
    return {"experiment": EXPERIMENT, "payloadVersion": PAYLOAD_VERSION,
            "blindArms": arms, "kits": kits, "salt": draw_hex(32)}


def evaluator_table(payload):
    """What evaluators may see: each kit with its two arms, nothing else."""
    by_cohort = lambda c: sorted(a for a, v in payload["blindArms"].items() if v["cohort"] == c)
    return {kit: by_cohort(cohort) for kit, cohort in sorted(payload["kits"].items())}


def verify(raw, expected_hex):
    parsed = json.loads(raw.decode("utf-8"))
    problems = [
        *(["reveal file is not in canonical form CANONICAL-1"] if canonical_bytes(parsed) != raw else []),
        *([f"SHA-256 {commitment(raw)} != commitment {expected_hex}"] if commitment(raw) != expected_hex.strip().lower() else []),
        *(["experiment field mismatch"] if parsed.get("experiment") != EXPERIMENT else []),
        *(["salt is not 64 hex characters"] if len(parsed.get("salt", "")) != 64 else []),
    ]
    return problems


def cmd_generate(args):
    out = pathlib.Path(args.out).resolve()
    repo = pathlib.Path(__file__).resolve().parents[4]
    if out == repo or repo in out.parents:
        sys.exit(f"refusing to write the reveal inside the repository working tree ({repo})")
    finals = dict(pair.split("=", 1) for pair in args.final)
    conditions = {("high", "grouped"): args.high_grouped, ("high", "independent"): args.high_independent,
                  ("low", "grouped"): args.low_grouped, ("low", "independent"): args.low_independent}
    missing = sorted(set(conditions.values()) - set(finals))
    if missing or len(set(conditions.values())) != 4:
        sys.exit(f"need four distinct run tokens each with --final TOKEN=SHA (missing: {missing})")
    payload = build_payload(conditions, finals)
    raw = canonical_bytes(payload)
    out.mkdir(parents=True, exist_ok=True)
    (out / "reveal.json").write_bytes(raw)
    (out / "commitment.sha256").write_text(commitment(raw) + "\n", encoding="ascii")
    print(json.dumps({"commitment": commitment(raw), "kits": evaluator_table(payload)}, indent=2, sort_keys=True))
    return 0


def cmd_verify(args):
    problems = verify(pathlib.Path(args.reveal).read_bytes(), args.commitment)
    print("\n".join(problems) if problems else f"verified: SHA-256 matches {args.commitment.strip().lower()}")
    return 1 if problems else 0


def cmd_selftest(_args):
    counter = iter(range(1000))
    fixed_hex = lambda n: format(next(counter), "x").rjust(2 * n, "0")
    payload = build_payload({("high", "grouped"): "aaaa", ("high", "independent"): "bbbb",
                             ("low", "grouped"): "cccc", ("low", "independent"): "dddd"},
                            {t: t * 10 for t in ("aaaa", "bbbb", "cccc", "dddd")},
                            draw_hex=fixed_hex, draw_below=lambda n: 0)
    raw = canonical_bytes(payload)
    checks = {
        "canonical round trip": canonical_bytes(json.loads(raw)) == raw,
        "no whitespace": b" " not in raw and b"\n" not in raw,
        "keys sorted": list(json.loads(raw)) == sorted(json.loads(raw)),
        "verify accepts": verify(raw, commitment(raw)) == [],
        "verify rejects other hash": verify(raw, "0" * 64) != [],
        "verify rejects non-canonical": verify(json.dumps(payload, indent=1).encode(), commitment(raw)) != [],
        "four distinct arms": len(set(payload["blindArms"])) == 4,
        "two kits, one per cohort": sorted(payload["kits"].values()) == ["high", "low"],
        "each kit two arms": all(len(v) == 2 for v in evaluator_table(payload).values()),
    }
    print("\n".join(f"{'ok  ' if ok else 'FAIL'} {name}" for name, ok in checks.items()))
    return 0 if all(checks.values()) else 1


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    g = sub.add_parser("generate")
    for name in ("high-grouped", "high-independent", "low-grouped", "low-independent"):
        g.add_argument(f"--{name}", required=True)
    g.add_argument("--final", action="append", default=[], required=True)
    g.add_argument("--out", required=True)
    v = sub.add_parser("verify")
    v.add_argument("--reveal", required=True)
    v.add_argument("--commitment", required=True)
    sub.add_parser("selftest")
    args = parser.parse_args(argv)
    return {"generate": cmd_generate, "verify": cmd_verify, "selftest": cmd_selftest}[args.command](args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
