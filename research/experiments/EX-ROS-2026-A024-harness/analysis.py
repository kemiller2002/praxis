#!/usr/bin/env python3
"""EX-ROS-2026-A024 frozen analysis (written and hashed before any arm ran).

Applies the preregistered formulas of EX-ROS-2026-A024 exactly:

  higher-is-better S:  recovery(C) = (S_C - S_B) / (S_A - S_B)
  lower-is-better  L:  recovery(C) = (L_B - L_C) / (L_B - L_A)

A ratio is computed only when A is directionally better than B on that metric;
otherwise the metric is "no recoverable calibration effect". Ratios are not
clipped. Thresholds: recovery >= 0.50 on each applicable primary mechanism
measure; C's acceptance rate no more than 0.05 below B's.

Primary mechanism measures (nominated before execution):
  architecture  = blinded rubric composite (0-10), higher is better
  discovery     = file reads + searches summed over every session of the arm,
                  main transcript plus subagent transcripts (A021 extractor
                  definitions), lower is better. This is the protocol's named
                  fallback proxy; a cross-session repeated-operation aggregate
                  is reported as secondary only.

Input (JSON): {"arms": {"A"|"B"|"C": {"valid": bool, "invalidReasons": [...],
  "acceptance": {"passed": int, "failed": int, "indeterminate": int, "confirmedDefects": int},
  "architecture": {"composite": number|null, "dimensions": [..]},
  "discovery": {"fileReads": int|null, "searches": int|null, "mainOnlyFileReads": ..., "mainOnlySearches": ...},
  "resources": {"costUsd": .., "outputTokens": .., "inputTokens": .., "cacheReadTokens": .., "cacheWriteTokens": ..,
                "elapsedMs": .., "modelRequests": .., "toolCalls": .., "sessions": ..},
  "handoffOverhead": {...} }}}
Missing values are null and stay null (never zero).
Usage: python3 analysis.py INPUT.json OUTPUT.json
"""
import json
import sys
from fractions import Fraction

RECOVERY_THRESHOLD = Fraction(1, 2)
CORRECTNESS_MARGIN = Fraction(1, 20)


def rate(acceptance):
    scorable = acceptance["passed"] + acceptance["failed"]
    return None if scorable == 0 else Fraction(acceptance["passed"], scorable)


def better(higher_is_better, x, y):
    return None if x is None or y is None else (x > y if higher_is_better else x < y)


def recovery(higher_is_better, a, b, c):
    if None in (a, b, c):
        return {"status": "missing data", "ratio": None}
    if not better(higher_is_better, a, b):
        return {"status": "no recoverable calibration effect", "ratio": None}
    a, b, c = (Fraction(str(v)) for v in (a, b, c))
    ratio = (c - b) / (a - b) if higher_is_better else (b - c) / (b - a)
    return {"status": "computed", "ratio": float(ratio), "exact": str(ratio), "meetsThreshold": ratio >= RECOVERY_THRESHOLD}


def discovery(arm, main_only=False):
    d = arm["discovery"]
    reads, searches = (d.get("mainOnlyFileReads"), d.get("mainOnlySearches")) if main_only else (d.get("fileReads"), d.get("searches"))
    return None if reads is None or searches is None else reads + searches


def ratio(x, y):
    return None if x is None or y in (None, 0) else x / y


def analyse(arms, main_only=False):
    A, B, C = arms["A"], arms["B"], arms["C"]
    measures = {
        "architecture": (True, A["architecture"]["composite"], B["architecture"]["composite"], C["architecture"]["composite"]),
        "discovery": (False, discovery(A, main_only), discovery(B, main_only), discovery(C, main_only)),
    }
    results = {name: {"higherIsBetter": hib, "A": a, "B": b, "C": c,
                      "AbetterThanB": better(hib, a, b), "CbetterThanB": better(hib, c, b),
                      "recovery": recovery(hib, a, b, c)}
               for name, (hib, a, b, c) in measures.items()}
    applicable = [n for n, r in results.items() if r["recovery"]["status"] == "computed"]
    rates = {k: rate(arms[k]["acceptance"]) for k in "ABC"}
    rule = {
        "1_calibration_effect": bool(applicable),
        "2_C_better_than_B_on_applicable": bool(applicable) and all(results[n]["CbetterThanB"] for n in applicable),
        "3_recovery_at_least_0.50_on_applicable": bool(applicable) and all(results[n]["recovery"]["meetsThreshold"] for n in applicable),
        "4_C_correctness_within_0.05_of_B": None if None in (rates["B"], rates["C"]) else rates["C"] >= rates["B"] - CORRECTNESS_MARGIN,
        "5_no_arm_invalidated": all(arms[k]["valid"] for k in "ABC"),
    }
    missing = [n for n, r in results.items() if r["recovery"]["status"] == "missing data"] + (["acceptance"] if None in rates.values() else [])
    supported = all(v is True for v in rule.values())
    verdict = ("not valid (an arm is invalidated)" if not rule["5_no_arm_invalidated"]
               else f"indeterminate (missing data: {', '.join(missing)})" if missing
               else "supported" if supported
               else "inconclusive (no recoverable calibration effect)" if not rule["1_calibration_effect"] and rule["5_no_arm_invalidated"]
               else "not valid (an arm is invalidated)" if not rule["5_no_arm_invalidated"]
               else "not supported")
    return {"measures": results, "applicablePrimaryMeasures": applicable, "missingPrimaryData": missing,
            "acceptanceRate": {k: (None if v is None else {"value": float(v), "exact": str(v)}) for k, v in rates.items()},
            "supportRule": rule, "verdict": verdict}


def secondary(arms):
    res = {k: arms[k]["resources"] for k in "ABC"}
    fields = ("costUsd", "elapsedMs", "outputTokens", "inputTokens", "cacheReadTokens", "cacheWriteTokens", "modelRequests", "toolCalls")
    return {
        "resourceRatios": {f: {"A/B": ratio(res["A"].get(f), res["B"].get(f)), "C/B": ratio(res["C"].get(f), res["B"].get(f)),
                               "C/A": ratio(res["C"].get(f), res["A"].get(f))} for f in fields},
        "handoffOverhead": arms["C"].get("handoffOverhead"),
        "acceptance": {k: arms[k]["acceptance"] for k in "ABC"},
        "architectureDimensions": {k: arms[k]["architecture"].get("dimensions") for k in "ABC"},
    }


def main():
    data = json.load(open(sys.argv[1]))
    arms = data["arms"]
    output = {
        "schema": "ex-ros-2026-a024.analysis/1",
        "thresholds": {"recovery": "1/2", "correctnessMargin": "1/20", "arithmetic": "exact rationals (fractions.Fraction)"},
        "primary": analyse(arms),
        "sensitivity": {"discoveryMainTranscriptOnly": analyse(arms, main_only=True)},
        "secondary": secondary(arms),
        "note": "one cohort, one execution per arm: a mechanism demonstration, not a population estimate; no combined agent score is produced",
    }
    json.dump(output, open(sys.argv[2], "w"), indent=2)
    print(json.dumps(output["primary"], indent=2))


if __name__ == "__main__":
    main()
