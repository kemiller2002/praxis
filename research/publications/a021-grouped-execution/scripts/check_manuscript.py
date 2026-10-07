#!/usr/bin/env python3
"""Consistency checks for manuscript/paper.tex.

Fails (exit 1) when any of these holds:
  1. a `% CLAIM: Cxx` id in the paper is missing from data/evidence-index.json,
     or an index claim is never used in the paper;
  2. a \\cite key is missing from references.bib;
  3. a used macro (a command starting with an upper-case letter, other than
     IEEEtran commands) is defined neither in the generated macro files nor by
     \\newcommand in the paper;
  4. the prose contains a bare result-like number (outside comments, the
     preamble and generated tables): a number followed by %, a $ amount, a
     comma-thousands number, a decimal, or a number followed by \\times;
  5. a forbidden phrase appears in the paper or a figure file (case-insensitive);
     anonymity terms are also checked in references.bib;
  6. a generated table is stale (build_tables.py --check,
     build_architecture_table.py --check).

Stdlib only. Usage: python3 scripts/check_manuscript.py
"""
from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Iterable, NamedTuple, Sequence

PKG = Path(__file__).resolve().parent.parent
PAPER = PKG / "manuscript" / "paper.tex"
FIGURES = PKG / "manuscript" / "figures"
MACRO_FILES = (PKG / "manuscript" / "tables" / "macros.tex",
               PKG / "manuscript" / "tables" / "architecture-macros.tex")
BIB = PKG / "references.bib"
INDEX = PKG / "data" / "evidence-index.json"
GENERATORS = (PKG / "scripts" / "build_tables.py", PKG / "scripts" / "build_architecture_table.py")

# Phrases that must never appear (scientific overclaiming and anonymity leaks).
FORBIDDEN: tuple[tuple[str, str], ...] = (
    ("statistically significant", r"statistically\s+significant"),
    ("p <", r"\bp\s*<"),
    ("p-value", r"\bp-values?\b"),
    ("independent-domain", r"independent[- ]domain"),
    ("waterfall", r"\bwaterfall\b"),
    ("universally", r"\buniversally\b"),
    ("proves", r"\bproves?\b"),
    ("clearly beneficial", r"clearly\s+beneficial"),
    ("blinded replication", r"blinded\s+replication"),
    ("Praxis", r"praxis"),
    ("Echelon", r"echelon"),
    ("kemiller", r"kemiller"),
    ("github.com", r"github\.com"),
    ("session_0", r"session_0"),
    ("claude-opus", r"claude-opus"),
    ("gpt-5", r"gpt-5"),
)
ANONYMITY = frozenset({"Praxis", "Echelon", "kemiller", "session_0"})

# Bare result-like numbers in prose.
NUMBER_PATTERNS: tuple[tuple[str, str], ...] = (
    ("percent", r"\d+(?:\.\d+)?\s*\\?%"),
    ("dollar", r"\\\$\s*\d"),
    ("thousands", r"\b\d{1,3}(?:,\d{3})+\b"),
    ("decimal", r"\b\d+\.\d+\b"),
    ("times", r"\d+(?:\.\d+)?\s*\$?\\times"),
)
# Strings removed before the number scan (explicit, reviewed exceptions).
NUMBER_ALLOWLIST: tuple[str, ...] = ()


class Problem(NamedTuple):
    check: str
    detail: str


# --------------------------------------------------------------------------
# Pure text helpers
# --------------------------------------------------------------------------

def strip_comment(line: str) -> str:
    m = re.search(r"(?<!\\)%", line)
    return line if m is None else line[:m.start()]


def body_of(tex: str) -> str:
    start = tex.find("\\begin{document}")
    return tex[start:] if start >= 0 else tex


def prose_lines(tex: str) -> tuple[tuple[int, str], ...]:
    offset = tex[:tex.find("\\begin{document}")].count("\n") if "\\begin{document}" in tex else 0
    lines = body_of(tex).splitlines()
    return tuple((offset + n + 1, strip_comment(l)) for n, l in enumerate(lines)
                 if not l.lstrip().startswith("\\input"))


def scrub_commands(text: str) -> str:
    """Remove arguments that legitimately carry digits (labels, refs, cites, lengths)."""
    text = re.sub(r"\\(?:label|ref|eqref|cite|input|bibliography|bibliographystyle|url|href)\{[^}]*\}", " ", text)
    text = re.sub(r"\\(?:setlength|vspace|hspace)\{[^}]*\}(?:\{[^}]*\})?", " ", text)
    text = re.sub(r"\{(?:@\{\})?(?:[lcr]|p\{[^}]*\})+(?:@\{\})?\}", " ", text)  # tabular specs
    text = re.sub(r"p\{[0-9.]+\\(?:textwidth|columnwidth)\}", " ", text)
    return text


def claim_ids(tex: str) -> frozenset[str]:
    return frozenset(cid for m in re.finditer(r"%\s*CLAIM:\s*([C0-9 ,]+)", tex)
                     for cid in re.findall(r"C\d+", m.group(1)))


def cite_keys(tex: str) -> frozenset[str]:
    return frozenset(k.strip() for m in re.finditer(r"\\cite[a-z]*\{([^}]*)\}", body_of(tex))
                     for k in m.group(1).split(",") if k.strip())


def bib_keys(bib: str) -> frozenset[str]:
    return frozenset(re.findall(r"^@\w+\{([^,\s]+),", bib, flags=re.M))


def defined_macros(texts: Iterable[str]) -> frozenset[str]:
    return frozenset(n for t in texts for n in re.findall(r"\\(?:re)?newcommand\{\\([A-Za-z]+)\}", t))


def used_uppercase_commands(tex: str) -> frozenset[str]:
    body = "\n".join(strip_comment(l) for l in body_of(tex).splitlines())
    return frozenset(n for n in re.findall(r"\\([A-Z][A-Za-z]*)", body) if not n.startswith("IEEE"))


# --------------------------------------------------------------------------
# Checks (pure: text in, problems out)
# --------------------------------------------------------------------------

def check_claims(tex: str, index: dict) -> tuple[Problem, ...]:
    used = claim_ids(tex)
    indexed = frozenset(c["claim_id"] for c in index["claims"])
    dupes = sorted({c["claim_id"] for c in index["claims"]
                    if sum(x["claim_id"] == c["claim_id"] for x in index["claims"]) > 1})
    return (tuple(Problem("claims", f"{c} used in paper but not in evidence index") for c in sorted(used - indexed)) +
            tuple(Problem("claims", f"{c} in evidence index but never used in paper") for c in sorted(indexed - used)) +
            tuple(Problem("claims", f"{c} duplicated in evidence index") for c in dupes))


def check_cites(tex: str, bib: str) -> tuple[Problem, ...]:
    return tuple(Problem("cites", f"\\cite key '{k}' not in references.bib") for k in sorted(cite_keys(tex) - bib_keys(bib)))


def check_macros(tex: str, macro_texts: Sequence[str]) -> tuple[Problem, ...]:
    defined = defined_macros((*macro_texts, tex))
    return tuple(Problem("macros", f"\\{n} used but not defined") for n in sorted(used_uppercase_commands(tex) - defined))


def check_numbers(tex: str) -> tuple[Problem, ...]:
    def scan(lineno: int, line: str) -> tuple[Problem, ...]:
        text = scrub_commands(line)
        for allowed in NUMBER_ALLOWLIST:
            text = text.replace(allowed, " ")
        return tuple(Problem("numbers", f"line {lineno}: bare {kind} '{m.group(0)}'")
                     for kind, pat in NUMBER_PATTERNS for m in re.finditer(pat, text))
    return tuple(p for n, l in prose_lines(tex) for p in scan(n, l))


def check_forbidden(name: str, text: str, terms: Iterable[tuple[str, str]]) -> tuple[Problem, ...]:
    return tuple(Problem("forbidden", f"{name}: '{label}' at line {text[:m.start()].count(chr(10)) + 1}")
                 for label, pat in terms for m in re.finditer(pat, text, flags=re.I))


# --------------------------------------------------------------------------
# I/O edge
# --------------------------------------------------------------------------

def stale_tables() -> tuple[Problem, ...]:
    def run(script: Path) -> tuple[Problem, ...]:
        r = subprocess.run([sys.executable, str(script), "--check"], capture_output=True, text=True)
        return () if r.returncode == 0 else (Problem("tables", f"{script.name} --check: {r.stdout.strip() or r.stderr.strip()}"),)
    return tuple(p for g in GENERATORS for p in run(g))


def main() -> int:
    tex = PAPER.read_text(encoding="utf-8")
    bib = BIB.read_text(encoding="utf-8")
    index = json.loads(INDEX.read_text(encoding="utf-8"))
    macro_texts = tuple(f.read_text(encoding="utf-8") for f in MACRO_FILES)
    figures = tuple((f.name, f.read_text(encoding="utf-8")) for f in sorted(FIGURES.glob("*.tex")))
    anonymity_terms = tuple(t for t in FORBIDDEN if t[0] in ANONYMITY)
    problems = (check_claims(tex, index) + check_cites(tex, bib) + check_macros(tex, macro_texts) +
                check_numbers(tex) + check_forbidden("paper.tex", tex, FORBIDDEN) +
                tuple(p for n, t in figures for p in check_forbidden(f"figures/{n}", t, FORBIDDEN)) +
                check_forbidden("references.bib", bib, anonymity_terms) + stale_tables())
    print("\n".join(f"FAIL [{p.check}] {p.detail}" for p in problems))
    summary = (f"claims used {len(claim_ids(tex))}, indexed {len(index['claims'])}; cites {len(cite_keys(tex))}; "
               f"macros used {len(used_uppercase_commands(tex))}; figures {len(figures)}; problems {len(problems)}")
    print(("OK: " if not problems else "FAILED: ") + summary)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
