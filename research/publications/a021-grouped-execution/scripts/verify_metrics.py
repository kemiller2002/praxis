#!/usr/bin/env python3
"""Verify the committed metrics and every figure the A021/R2 records report.

1. Re-runs the extraction in memory and asserts data/metrics.json and
   data/metrics.csv are byte-identical to what is committed in the tree.
2. Checks each figure reported in EV-ROS-2026-A064, the A021 protocol's
   Results, EV-ROS-2026-A070, POST-UNBLINDING-METRICS.txt and the publication
   README against the extracted data, printing MATCH / MISMATCH /
   NOT-COMPARABLE. The record text is read from pinned commits and each
   reported string must actually occur in it.
3. A MISMATCH or NOT-COMPARABLE result is allowed only if a conflict in
   data/metric-conflicts.json lists that check id; anything else exits 1.
   Conflict entries that name unknown checks also fail.

Usage: python3 scripts/verify_metrics.py [--quiet]
"""
from __future__ import annotations

import importlib.util
import json
import re
import sys
from pathlib import Path
from types import MappingProxyType
from typing import Any, Callable, Mapping, NamedTuple, Optional, Sequence

HERE = Path(__file__).resolve().parent
PKG = HERE.parent
REL = "research/publications/a021-grouped-execution"


def load_extractor() -> Any:
    spec = importlib.util.spec_from_file_location("extract_metrics", HERE / "extract_metrics.py")
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


EX = load_extractor()

PUB_COMMIT = "eb51f4adc76b175218507a13b3a4eb6098fac9c3"  # origin/research/a021-publication-package when checked

RECORDS: Mapping[str, Any] = MappingProxyType({
    "EV-A064": EX.Src(EX.MAIN, "research/evidence/EV-ROS-2026-A064--grouping-experiment-results.md"),
    "EX-A021": EX.Src(EX.MAIN, "research/experiments/EX-ROS-2026-A021--grouped-versus-independent-execution.md"),
    "EV-A070": EX.Src(EX.MAIN, "research/evidence/EV-ROS-2026-A070--a021-r2-blind-evaluation-and-replication.md"),
    "POST": EX.Src(EX.MAIN, "research/experiments/EX-ROS-2026-A021-R2-blind/output/POST-UNBLINDING-METRICS.txt"),
    "README": EX.Src(PUB_COMMIT, f"{REL}/README.md"),
})

# --------------------------------------------------------------------------
# Formatting of extracted values the way each record reports them
# --------------------------------------------------------------------------


def hms(v: float) -> str:
    s = int(round(v))
    h, m, sec = s // 3600, (s % 3600) // 60, s % 60
    return f"{h}h{m:02d}m{sec:02d}s" if h else f"{m}m{sec:02d}s"


FORMATS: Mapping[str, Callable[[Any], str]] = MappingProxyType({
    "int": lambda v: f"{int(v):,}",
    "usd2": lambda v: f"${v:.2f}",
    "usd7": lambda v: f"${v:.7f}",
    "M1": lambda v: f"{v / 1e6:.1f} M",
    "k0": lambda v: f"{round(v / 1000)} k",
    "min0": lambda v: f"{round(v / 60)} min",
    "min1": lambda v: f"{v / 60:.1f}",
    "h_min": lambda v: f"{int(v) // 3600} h {round((int(v) % 3600) / 60)} min",
    "hms": hms,
    "pct0": lambda v: f"{round(v * 100)} %",
    "pct2": lambda v: f"{v * 100:.2f}%",
    "x4": lambda v: f"{v:.4f}x",
    "share_1M": lambda v: f"{round(v / 1e6 * 100)} %",
})


class Check(NamedTuple):
    id: str
    record: str
    needle: str          # text that must occur in the record (whitespace-normalized)
    reported: str        # the reported figure as written (must occur in needle)
    selector: tuple      # see resolve()
    fmt: str
    flag: Optional[str] = None   # "NOT-COMPARABLE" when the figure's definition differs from the canonical one
    why: str = ""


def R(study: str, arm: str, unit: str, metric: str) -> tuple:
    return ("row", study, arm, unit, metric)


def D(study: str, unit: str, metric: str, field: str) -> tuple:
    return ("derived", study, unit, metric, field)


def S(*keys: tuple) -> tuple:
    return ("sum", keys)


def DIFF(a: tuple, b: tuple) -> tuple:
    return ("diff", a, b)


W, I, A = "agg:workers", "independent", "agg:arm"
WO = "agg:workers+orchestration"
LB = "transcript-script figure (lower bound for the independent arm); EV-A064 reports platform usage and qualifies script counts with 'at least'"

CHECKS: tuple[Check, ...] = (
    # ---- EV-A064 resources table ------------------------------------------------
    Check("A064-sessions-g", "EV-A064", "| Sessions (executions) | 1 | 7", "1",
          S(R("A021", "grouped", A, "sessions_implementation"), R("A021", "grouped", A, "sessions_failed_attempt")), "int"),
    Check("A064-sessions-i", "EV-A064", "| Sessions (executions) | 1 | 7 (5 items, 2 failed attempts)", "7",
          S(R("A021", I, A, "sessions_implementation"), R("A021", I, A, "sessions_failed_attempt")), "int"),
    Check("A064-cost-g", "EV-A064", "| Cost, platform-reported | $9.48 |", "$9.48", R("A021", "grouped", W, "cost_usd_platform"), "usd2"),
    Check("A064-cost-i", "EV-A064", "| $9.48 | $21.51 |", "$21.51", R("A021", I, W, "cost_usd_platform"), "usd2"),
    Check("A064-out-g", "EV-A064", "| Output tokens | 112,377 |", "112,377", R("A021", "grouped", W, "output_tokens_platform"), "int"),
    Check("A064-out-i", "EV-A064", "| 112,377 | 244,630 |", "244,630", R("A021", I, W, "output_tokens_platform"), "int"),
    Check("A064-cr-g", "EV-A064", "| Cache-read tokens | 24.5 M |", "24.5 M", R("A021", "grouped", W, "cache_read_tokens_platform"), "M1"),
    Check("A064-cr-i", "EV-A064", "| 24.5 M | 45.1 M |", "45.1 M", R("A021", I, W, "cache_read_tokens_platform"), "M1"),
    Check("A064-active-g", "EV-A064", "| Active session time | 61 min |", "61 min", R("A021", "grouped", W, "active_session_sum_s"), "min0"),
    Check("A064-active-i", "EV-A064", "| 61 min | 112 min (sum of sessions) |", "112 min", R("A021", I, W, "active_session_sum_s"), "min0"),
    Check("A064-elapsed-g", "EV-A064", "| Elapsed, first start to last finish | 61 min |", "61 min", R("A021", "grouped", W, "wall_clock_span_s"), "min0"),
    Check("A064-elapsed-i", "EV-A064", "| 61 min | 4 h 52 min (includes orchestration gaps", "4 h 52 min", R("A021", I, W, "wall_clock_span_s"), "h_min"),
    Check("A064-agents-g", "EV-A064", "| Reads of AGENTS.md | 1 |", "1", R("A021", "grouped", W, "agents_md_reads_transcript"), "int"),
    Check("A064-agents-i", "EV-A064", "| Reads of AGENTS.md | 1 | 5 (one per measured session) |", "5", R("A021", I, W, "agents_md_reads_transcript"), "int"),
    Check("A064-gov-g", "EV-A064", "| Reads of governance and planning documents | 6 |", "6", R("A021", "grouped", W, "governance_reads_transcript"), "int"),
    Check("A064-gov-i", "EV-A064", "| Reads of governance and planning documents | 6 | 15 |", "15", R("A021", I, W, "governance_reads_transcript"), "int"),
    Check("A064-ttfc-g", "EV-A064", "| 9.6 min, once, including the analysis |", "9.6", R("A021", "grouped", "grouped", "time_to_first_code_s"), "min1"),
    Check("A064-ttfc-c01", "EV-A064", "| 4.6, 5.6, 6.4 and 4.8 min in the four sessions", "4.6", R("A021", I, "control-01", "time_to_first_code_s"), "min1"),
    Check("A064-ttfc-c02", "EV-A064", "| 4.6, 5.6, 6.4 and 4.8 min in the four sessions", "5.6", R("A021", I, "control-02", "time_to_first_code_s"), "min1"),
    Check("A064-ttfc-c03", "EV-A064", "| 4.6, 5.6, 6.4 and 4.8 min in the four sessions", "6.4", R("A021", I, "control-03", "time_to_first_code_s"), "min1"),
    Check("A064-ttfc-c05", "EV-A064", "| 4.6, 5.6, 6.4 and 4.8 min in the four sessions", "4.8", R("A021", I, "control-05", "time_to_first_code_s"), "min1"),
    Check("A064-mr-g", "EV-A064", "| Model requests, script | 114 |", "114", R("A021", "grouped", W, "model_requests_transcript"), "int"),
    Check("A064-mr-i", "EV-A064", "| Model requests, script | 114 | at least 237 |", "237", R("A021", I, W, "model_requests_transcript"), "int"),
    Check("A064-reads-g", "EV-A064", "| File reads / searches, script | 52 / 53 |", "52", R("A021", "grouped", W, "file_reads_transcript"), "int"),
    Check("A064-search-g", "EV-A064", "| File reads / searches, script | 52 / 53 |", "53", R("A021", "grouped", W, "searches_transcript"), "int"),
    Check("A064-reads-i", "EV-A064", "| 52 / 53 | at least 134 / 159 |", "134", R("A021", I, W, "file_reads_transcript"), "int"),
    Check("A064-search-i", "EV-A064", "| 52 / 53 | at least 134 / 159 |", "159", R("A021", I, W, "searches_transcript"), "int"),
    Check("A064-compact-g", "EV-A064", "| Compactions, context resets | 0 | 0 |", "0", R("A021", "grouped", W, "compactions_transcript"), "int"),
    Check("A064-compact-i", "EV-A064", "| Compactions, context resets | 0 | 0 |", "0", R("A021", I, W, "compactions_transcript"), "int"),
    Check("A064-ctx-g", "EV-A064", "| Peak context in one session | 319 k of 1 M tokens |", "319 k", R("A021", "grouped", W, "context_tokens_at_end_platform"), "k0",
          "NOT-COMPARABLE", "labelled 'peak' but the source is get_session context_usage.used_tokens at recording time"),
    Check("A064-ctx-i", "EV-A064", "| 319 k of 1 M tokens | 263 k (control-05) |", "263 k", R("A021", I, "control-05", "context_tokens_at_end_platform"), "k0",
          "NOT-COMPARABLE", "labelled 'peak' but the source is get_session context_usage.used_tokens at recording time"),
    Check("A064-merge-g", "EV-A064", "| Merge conflicts | 0 |", "0", R("A021", "grouped", A, "merges_with_conflicts"), "int"),
    Check("A064-merge-i", "EV-A064", "| Merge conflicts | 0 | 1 (control-05, `Program.fs`) |", "1", R("A021", I, A, "merges_with_conflicts"), "int"),
    Check("A064-tests-g", "EV-A064", "| Tests at the end | 811 pass |", "811", R("A021", "grouped", A, "fsharp_tests_passed_final"), "int"),
    Check("A064-tests-i", "EV-A064", "| Tests at the end | 811 pass | 829 pass |", "829", R("A021", I, A, "fsharp_tests_passed_final"), "int"),
    Check("A064-tests-added", "EV-A064", "It added 37 tests against 19.", "37", R("A021", I, A, "tests_added"), "int"),
    Check("A064-tests-added-g", "EV-A064", "It added 37 tests against 19.", "19", R("A021", "grouped", A, "tests_added"), "int"),
    Check("A064-cost-red", "EV-A064", "and 56 % lower platform cost", "56 %", D("A021", W, "cost_usd_platform", "reduction_grouped_vs_independent"), "pct0"),
    Check("A064-agents-fewer", "EV-A064", "Measured: 4 fewer AGENTS.md reads", "4",
          DIFF(R("A021", I, W, "agents_md_reads_transcript"), R("A021", "grouped", W, "agents_md_reads_transcript")), "int"),
    Check("A064-gov-fewer", "EV-A064", "9 fewer governance reads", "9",
          DIFF(R("A021", I, W, "governance_reads_transcript"), R("A021", "grouped", W, "governance_reads_transcript")), "int"),
    Check("A064-ctx-share", "EV-A064", "32 % of the context window at peak", "32 %", R("A021", "grouped", W, "context_tokens_at_end_platform"), "share_1M"),
    # ---- A021 protocol Results ---------------------------------------------------
    Check("EX-g-summary-min", "EX-A021", "Grouped arm: 1 session, 61 min, $9.48 (platform), 811 tests pass.", "61 min", R("A021", "grouped", W, "active_session_sum_s"), "min0"),
    Check("EX-g-summary-cost", "EX-A021", "Grouped arm: 1 session, 61 min, $9.48 (platform), 811 tests pass.", "$9.48", R("A021", "grouped", W, "cost_usd_platform"), "usd2"),
    Check("EX-i-summary-min", "EX-A021", "112 min of session time, $21.51, 829 tests pass.", "112 min", R("A021", I, W, "active_session_sum_s"), "min0"),
    Check("EX-i-summary-cost", "EX-A021", "112 min of session time, $21.51, 829 tests pass.", "$21.51", R("A021", I, W, "cost_usd_platform"), "usd2"),
    Check("EX-agents", "EX-A021", "AGENTS.md read 1 versus 5 times, governance documents 6 versus 15", "5", R("A021", I, W, "agents_md_reads_transcript"), "int"),
    Check("EX-gov", "EX-A021", "AGENTS.md read 1 versus 5 times, governance documents 6 versus 15", "15", R("A021", I, W, "governance_reads_transcript"), "int"),
    Check("EX-tests-added", "EX-A021", "added 37 tests against 19", "37", R("A021", I, A, "tests_added"), "int"),
    # ---- EV-A070 -------------------------------------------------------------------
    Check("A070-g-elapsed", "EV-A070", "- 55m53s elapsed", "55m53s", R("R2", "grouped", W, "wall_clock_span_s"), "hms"),
    Check("A070-g-cost", "EV-A070", "- $10.7536464 platform cost", "$10.7536464", R("R2", "grouped", W, "cost_usd_platform"), "usd7"),
    Check("A070-g-out", "EV-A070", "- 132,902 output tokens", "132,902", R("R2", "grouped", W, "output_tokens_platform"), "int"),
    Check("A070-g-cr", "EV-A070", "- 27,911,832 cache-read tokens", "27,911,832", R("R2", "grouped", W, "cache_read_tokens_platform"), "int"),
    Check("A070-i-elapsed", "EV-A070", "- 2h23m35s elapsed", "2h23m35s", R("R2", I, WO, "wall_clock_span_from_common_start_s"), "hms",
          "NOT-COMPARABLE", "wall-clock span from the common arm start to the control orchestrator's end; includes the orchestrator and gaps"),
    Check("A070-i-cost", "EV-A070", "- $26.0492768 platform cost", "$26.0492768", R("R2", I, WO, "cost_usd_platform"), "usd7",
          "NOT-COMPARABLE", "includes the control orchestrator session"),
    Check("A070-i-out", "EV-A070", "- 262,893 output tokens", "262,893", R("R2", I, WO, "output_tokens_platform"), "int",
          "NOT-COMPARABLE", "includes the control orchestrator session"),
    Check("A070-i-cr", "EV-A070", "- 62,960,709 cache-read tokens", "62,960,709", R("R2", I, WO, "cache_read_tokens_platform"), "int",
          "NOT-COMPARABLE", "includes the control orchestrator session"),
    Check("A070-ratio-cost", "EV-A070", "- cost 2.4224x", "2.4224x", D("R2", WO, "cost_usd_platform", "ratio_independent_over_grouped"), "x4",
          "NOT-COMPARABLE", "includes the control orchestrator session"),
    Check("A070-ratio-elapsed", "EV-A070", "- elapsed 2.5693x", "2.5693x", D("R2", WO, "wall_clock_span_from_common_start_s", "ratio_independent_over_grouped"), "x4",
          "NOT-COMPARABLE", "wall-clock span including orchestration"),
    Check("A070-ratio-out", "EV-A070", "- output 1.9781x", "1.9781x", D("R2", WO, "output_tokens_platform", "ratio_independent_over_grouped"), "x4",
          "NOT-COMPARABLE", "includes the control orchestrator session"),
    Check("A070-ratio-cr", "EV-A070", "- cache-read 2.2557x", "2.2557x", D("R2", WO, "cache_read_tokens_platform", "ratio_independent_over_grouped"), "x4",
          "NOT-COMPARABLE", "includes the control orchestrator session"),
    Check("A070-red-cost", "EV-A070", "- cost 58.72%", "58.72%", D("R2", WO, "cost_usd_platform", "reduction_grouped_vs_independent"), "pct2",
          "NOT-COMPARABLE", "includes the control orchestrator session"),
    Check("A070-red-elapsed", "EV-A070", "- elapsed 61.08%", "61.08%", D("R2", WO, "wall_clock_span_from_common_start_s", "reduction_grouped_vs_independent"), "pct2",
          "NOT-COMPARABLE", "wall-clock span including orchestration"),
    Check("A070-a021-mr-g", "EV-A070", "114 versus 237 model requests", "114", R("A021", "grouped", W, "model_requests_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("A070-a021-mr-i", "EV-A070", "114 versus 237 model requests", "237", R("A021", I, W, "model_requests_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("A070-a021-out-g", "EV-A070", "110,627 versus 176,304 output tokens", "110,627", R("A021", "grouped", W, "output_tokens_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("A070-a021-out-i", "EV-A070", "110,627 versus 176,304 output tokens", "176,304", R("A021", I, W, "output_tokens_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("A070-a021-reads-i", "EV-A070", "52 versus 134 file reads", "134", R("A021", I, W, "file_reads_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("A070-a021-search-i", "EV-A070", "53 versus 159 searches", "159", R("A021", I, W, "searches_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("A070-a021-builds-g", "EV-A070", "16 versus 34 builds", "16", R("A021", "grouped", W, "builds_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("A070-a021-builds-i", "EV-A070", "16 versus 34 builds", "34", R("A021", I, W, "builds_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("A070-defects-g", "EV-A070", "confirmed acceptance-defect count is tied at two each", "two", R("R2", "grouped", A, "confirmed_acceptance_defects_r2"), "word"),
    Check("A070-defects-i", "EV-A070", "confirmed acceptance-defect count is tied at two each", "two", R("R2", I, A, "confirmed_acceptance_defects_r2"), "word"),
    # ---- POST-UNBLINDING-METRICS ---------------------------------------------------
    Check("POST-g-sessions", "POST", "- sessions: 1 implementing session", "1", R("R2", "grouped", A, "sessions_implementation"), "int"),
    Check("POST-i-sessions", "POST", "- sessions: 5 implementing sessions + 1 control orchestrator", "5", R("R2", I, A, "sessions_implementation"), "int"),
    Check("POST-i-orch", "POST", "- sessions: 5 implementing sessions + 1 control orchestrator", "1", R("R2", I, A, "sessions_orchestration"), "int"),
    Check("POST-g-elapsed", "POST", "- elapsed: 55m53s", "55m53s", R("R2", "grouped", W, "wall_clock_span_s"), "hms"),
    Check("POST-g-input", "POST", "- input: 262", "262", R("R2", "grouped", W, "input_tokens_platform"), "int"),
    Check("POST-g-cw", "POST", "- cache-write: 314,024", "314,024", R("R2", "grouped", W, "cache_write_tokens_platform"), "int"),
    Check("POST-g-cost", "POST", "- cost: $10.7536464", "$10.7536464", R("R2", "grouped", W, "cost_usd_platform"), "usd7"),
    Check("POST-i-elapsed", "POST", "- elapsed: 2h23m35s", "2h23m35s", R("R2", I, WO, "wall_clock_span_from_common_start_s"), "hms",
          "NOT-COMPARABLE", "wall-clock span including orchestration"),
    Check("POST-i-input", "POST", "- input: 956", "956", R("R2", I, WO, "input_tokens_platform"), "int", "NOT-COMPARABLE", "includes the control orchestrator session"),
    Check("POST-i-cw", "POST", "- cache-write: 1,074,120", "1,074,120", R("R2", I, WO, "cache_write_tokens_platform"), "int", "NOT-COMPARABLE", "includes the control orchestrator session"),
    Check("POST-i-cost", "POST", "- cost: $26.0492768", "$26.0492768", R("R2", I, WO, "cost_usd_platform"), "usd7", "NOT-COMPARABLE", "includes the control orchestrator session"),
    Check("POST-red-cost", "POST", "- cost: 58.72%", "58.72%", D("R2", WO, "cost_usd_platform", "reduction_grouped_vs_independent"), "pct2", "NOT-COMPARABLE", "includes the control orchestrator session"),
    Check("POST-red-elapsed", "POST", "- elapsed: 61.08%", "61.08%", D("R2", WO, "wall_clock_span_from_common_start_s", "reduction_grouped_vs_independent"), "pct2", "NOT-COMPARABLE", "wall-clock span including orchestration"),
    Check("POST-a021-cost-unavailable", "POST", "though platform cost was unavailable", "unavailable",
          ("claim_unavailable", R("A021", "grouped", W, "cost_usd_platform"), R("A021", I, W, "cost_usd_platform")), "claim"),
    Check("POST-a021-cr-g", "POST", "110,627 output, 23,872,205 cache-read", "23,872,205", R("A021", "grouped", W, "cache_read_tokens_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("POST-a021-cr-i", "POST", "176,304 output, 32,211,852 cache-read", "32,211,852", R("A021", I, W, "cache_read_tokens_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("POST-a021-out-i", "POST", "237 model requests, 176,304 output", "176,304", R("A021", I, W, "output_tokens_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("POST-a021-tests-g", "POST", "16 builds, 13 test runs", "13", R("A021", "grouped", W, "test_runs_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("POST-a021-tests-i", "POST", "34 builds, 15 test runs", "15", R("A021", I, W, "test_runs_transcript"), "int", "NOT-COMPARABLE", LB),
    Check("POST-defects", "POST", "two confirmed acceptance defects in each arm", "two", R("R2", I, A, "confirmed_acceptance_defects_r2"), "word"),
    # ---- publication README ----------------------------------------------------------
    Check("README-red-cost", "README", "measured a 58.72% platform-cost reduction", "58.72%",
          D("R2", WO, "cost_usd_platform", "reduction_grouped_vs_independent"), "pct2", "NOT-COMPARABLE", "includes the control orchestrator session"),
    Check("README-red-elapsed", "README", "61.08% elapsed-time reduction", "61.08%",
          D("R2", WO, "wall_clock_span_from_common_start_s", "reduction_grouped_vs_independent"), "pct2", "NOT-COMPARABLE", "wall-clock span including orchestration"),
)

WORDS = MappingProxyType({0: "zero", 1: "one", 2: "two", 3: "three", 4: "four", 5: "five"})


# --------------------------------------------------------------------------
# Pure evaluation
# --------------------------------------------------------------------------

def normalize(text: str) -> str:
    return re.sub(r"\s+", " ", text)


def resolve(sel: tuple, rows: Mapping[tuple, Mapping[str, Any]], derived: Mapping[tuple, Mapping[str, Any]]) -> Any:
    kind = sel[0]
    if kind == "row":
        r = rows.get(sel[1:])
        return None if r is None else r["value"]
    if kind == "derived":
        d = derived.get(sel[1:4])
        return None if d is None else d[sel[4]]
    if kind == "sum":
        vals = tuple(resolve(k, rows, derived) for k in sel[1])
        return None if any(v is None for v in vals) else sum(vals)
    if kind == "diff":
        a, b = resolve(sel[1], rows, derived), resolve(sel[2], rows, derived)
        return None if a is None or b is None else a - b
    if kind == "claim_unavailable":
        return tuple(resolve(k, rows, derived) for k in sel[1:])
    raise ValueError(kind)


class Result(NamedTuple):
    check: Check
    status: str
    extracted: str
    explanation: str


def evaluate(check: Check, texts: Mapping[str, str], rows: Mapping, derived: Mapping) -> Result:
    text = normalize(texts[check.record])
    if check.reported not in check.needle:
        return Result(check, "ERROR", "", "reported string not in needle (check definition bug)")
    if normalize(check.needle) not in text:
        return Result(check, "ERROR", "", f"needle not found in {check.record}")
    value = resolve(check.selector, rows, derived)
    if check.fmt == "claim":
        present = all(v is not None for v in value)
        status = "MISMATCH" if present else "MATCH"
        return Result(check, status, " / ".join(str(v) for v in value),
                      "record claims the figure is unavailable, but the committed sources contain it" if present else "")
    if value is None:
        return Result(check, "MISMATCH", "null", "extracted value is unknown")
    shown = WORDS.get(value, str(value)) if check.fmt == "word" else FORMATS[check.fmt](value)
    reported = check.reported
    same = shown == reported or (check.fmt == "int" and shown.replace(",", "") == reported.replace(",", ""))
    if not same:
        return Result(check, "MISMATCH", shown, f"extracted {value!r} formats as {shown!r}")
    if check.flag:
        return Result(check, check.flag, shown, check.why)
    return Result(check, "MATCH", shown, "")


def judge(results: Sequence[Result], conflicts: Sequence[Mapping[str, Any]]) -> tuple[tuple[str, ...], tuple[str, ...]]:
    allowed = {cid: c["id"] for c in conflicts for cid in c.get("checks", ())}
    known = {r.check.id for r in results}
    unknown_refs = tuple(sorted(f"{c['id']} -> {cid}" for c in conflicts for cid in c.get("checks", ()) if cid not in known))
    failures = tuple(f"{r.check.id}: {r.status} ({r.explanation})" for r in results
                     if r.status == "ERROR" or (r.status in ("MISMATCH", "NOT-COMPARABLE") and r.check.id not in allowed))
    return failures, unknown_refs


# --------------------------------------------------------------------------
# I/O boundary
# --------------------------------------------------------------------------

def main(argv: Sequence[str]) -> int:
    root = EX.repo_root()
    quiet = "--quiet" in argv
    expected = EX.outputs(root)
    stale = tuple(p for p, text in sorted(expected.items())
                  if not (root / p).exists() or (root / p).read_text(encoding="utf-8") != text)
    for p in stale:
        print(f"STALE   {p} differs from a fresh extraction (run scripts/extract_metrics.py)")
    doc = json.loads(expected[f"{REL}/data/metrics.json"])
    rows = {(r["study"], r["arm"], r["unit"], r["metric"]): r for r in doc["rows"]}
    derived = {(d["study"], d["unit"], d["metric"]): d for d in doc["derived"]}
    texts = {k: EX.git_out(root, "show", f"{s.commit}:{s.path}") for k, s in RECORDS.items()}
    conflicts = json.loads((root / REL / "data/metric-conflicts.json").read_text(encoding="utf-8"))["conflicts"]
    results = tuple(evaluate(c, texts, rows, derived) for c in CHECKS)
    allowed = {cid: c["id"] for c in conflicts for cid in c.get("checks", ())}
    for r in results:
        if quiet and r.status == "MATCH":
            continue
        tag = f" [{allowed[r.check.id]}]" if r.check.id in allowed else ""
        print(f"{r.status:<15}{r.check.id:<28}{r.check.record:<8} reported {r.check.reported!r:<16} extracted {r.extracted!r}"
              f"{tag}{(' -- ' + r.explanation) if r.explanation else ''}")
    failures, unknown_refs = judge(results, conflicts)
    counts = {s: sum(1 for r in results if r.status == s) for s in ("MATCH", "MISMATCH", "NOT-COMPARABLE", "ERROR")}
    print("summary: " + ", ".join(f"{k}={v}" for k, v in counts.items()) + f", stale_outputs={len(stale)}")
    for f in failures:
        print(f"UNEXPLAINED {f}")
    for u in unknown_refs:
        print(f"BAD-CONFLICT-REF {u}")
    return 1 if (stale or failures or unknown_refs) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
