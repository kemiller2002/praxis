"""Pure redaction and denylist-scanning functions for the A021 anonymous artifact.

Everything in this module is a pure function of its arguments: no file system,
no subprocess, no clock, no environment. The build script
(build_anonymous_artifact.py) and the verifier (verify_artifact.py) supply the
inputs and do the I/O.

No identifying literal is written in this file. Person identifiers (names,
e-mail addresses, account names) are discovered at build time from Git
metadata and passed in; the session/URL patterns below are generic.

Redaction profiles
------------------
* ``PROFILE_CODE`` (baseline source and arm patches): person identifiers only.
  Product, package and organisation names embedded in code are kept, because
  rewriting them would change the evaluated evidence (see
  reproducibility-and-anonymization.md, "Anonymization policy").
* ``PROFILE_RECORD`` (protocol, prompts, evaluator outputs, metrics, logs):
  person identifiers, plus agent-session identifiers and URLs, transcript and
  tool UUIDs, and commit trailer lines that name a person or a session.
"""

from __future__ import annotations

import csv
import io
import json
import re
from dataclasses import dataclass
from functools import reduce
from typing import Callable, Iterable, Mapping

# --------------------------------------------------------------------------
# Data
# --------------------------------------------------------------------------

ANON_OWNER = "anonymous-owner"
ANON_NAME = "Anonymous Owner"
ANON_NAME_TOKEN = "owner"
ANON_EMAIL = "anonymous@example.invalid"
ANON_SESSION_URL = "<agent-session-url-redacted>"

PROFILE_CODE = "code"
PROFILE_RECORD = "record"

# Generic identifier shapes. Written so that the pattern source never contains
# a denylisted literal (the verifier scans the shipped copy of this file).
SESSION_ID_RE = re.compile(r"\bsession_[0-9A-Za-z]{20,40}\b")
SESSION_URL_RE = re.compile(r"https?://claude\.ai/code[^\s)\]>\"'`]*", re.IGNORECASE)
UUID_RE = re.compile(
    r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b"
)
TRAILER_LINE_RE = re.compile(
    r"^[ \t>*-]*(?:co-authored-by|claude-session)\s*:.*(?:\r?\n|$)",
    re.IGNORECASE | re.MULTILINE,
)
GITHUB_NOREPLY_RE = re.compile(r"^(?:\d+\+)?([^@]+)@users\.noreply\.github\.com$", re.IGNORECASE)
GITHUB_REMOTE_RE = re.compile(r"github\.com[:/]+([^/\s]+)/", re.IGNORECASE)

# Identities whose e-mail domain or name marks them as tools rather than
# people. Tool identities are not anonymised (the paper discloses the tools).
TOOL_EMAIL_RE = re.compile(
    r"(noreply@anthropic\.com|noreply@openai\.com|^noreply@github\.com$|\[bot\]|\.invalid$)",
    re.IGNORECASE,
)
TOOL_NAME_RE = re.compile(r"(\[bot\]$|^github$|^claude$|^openai chatgpt$|blind|blinding)", re.IGNORECASE)


@dataclass(frozen=True)
class Identity:
    name: str
    email: str

    @property
    def is_tool(self) -> bool:
        return bool(TOOL_EMAIL_RE.search(self.email) or TOOL_NAME_RE.search(self.name))


@dataclass(frozen=True)
class PersonTerms:
    """Person identifiers to remove, all discovered from Git metadata."""

    owners: tuple[str, ...]  # account names
    full_names: tuple[str, ...]
    name_tokens: tuple[str, ...]
    emails: tuple[str, ...]


@dataclass(frozen=True)
class Rule:
    name: str
    pattern: re.Pattern[str]
    replace: Callable[[re.Match[str]], str]


@dataclass(frozen=True)
class Redacted:
    text: str
    counts: tuple[tuple[str, int], ...]  # (rule name, matches) for rules that matched

    @property
    def changed(self) -> bool:
        return bool(self.counts)


@dataclass(frozen=True)
class Finding:
    term: str
    line: int
    excerpt: str


# --------------------------------------------------------------------------
# Identity discovery
# --------------------------------------------------------------------------


def parse_identities(git_log_output: str) -> tuple[Identity, ...]:
    """Parse ``git log --format=%an%x00%ae%x00%cn%x00%ce`` output."""
    fields = (line.split("\x00") for line in git_log_output.splitlines() if line.strip())
    pairs = (
        pair
        for parts in fields
        if len(parts) == 4
        for pair in ((parts[0], parts[1]), (parts[2], parts[3]))
    )
    return tuple(sorted({Identity(n.strip(), e.strip()) for n, e in pairs if n.strip() or e.strip()},
                        key=lambda i: (i.name.lower(), i.email.lower())))


def owner_from_remote(url: str) -> tuple[str, ...]:
    match = GITHUB_REMOTE_RE.search(url or "")
    return (match.group(1),) if match else ()


def account_from_email(email: str) -> tuple[str, ...]:
    match = GITHUB_NOREPLY_RE.match(email)
    return (match.group(1),) if match else ()


def person_terms(identities: Iterable[Identity], remote_owners: Iterable[str]) -> PersonTerms:
    people = tuple(i for i in identities if not i.is_tool)
    emails = tuple(sorted({p.email.lower() for p in people if "@" in p.email}))
    owners = tuple(sorted(
        {o.lower() for o in remote_owners}
        | {a.lower() for e in emails for a in account_from_email(e)}
        | {e.split("@", 1)[0] for e in emails if not GITHUB_NOREPLY_RE.match(e)}
    ))
    # Account stems ("name1234" -> "name") also identify; keep stems of 5+ characters.
    owners = tuple(sorted(set(owners) | {re.sub(r"\d+$", "", o) for o in owners if len(re.sub(r"\d+$", "", o)) >= 5}))
    full_names = tuple(sorted({p.name for p in people if " " in p.name.strip()}, key=str.lower))
    name_tokens = tuple(sorted(
        {tok for n in (p.name for p in people) for tok in re.split(r"\s+", n.strip()) if len(tok) >= 3}
        - {o for o in owners},
        key=str.lower,
    ))
    return PersonTerms(owners=owners, full_names=full_names, name_tokens=name_tokens, emails=emails)


# --------------------------------------------------------------------------
# Rules
# --------------------------------------------------------------------------


def _match_case(template: str) -> Callable[[re.Match[str]], str]:
    def replace(m: re.Match[str]) -> str:
        found = m.group(0)
        return (template.upper() if found.isupper() and len(found) > 1
                else template[:1].upper() + template[1:] if found[:1].isupper()
                else template)
    return replace


def _const(value: str) -> Callable[[re.Match[str]], str]:
    return lambda _m: value


def _lookup(mapping: Mapping[str, str]) -> Callable[[re.Match[str]], str]:
    return lambda m: mapping.get(m.group(0), m.group(0))


def _words(terms: Iterable[str]) -> str:
    return "|".join(re.escape(t) for t in sorted(set(terms), key=lambda t: (-len(t), t)))


def person_rules(terms: PersonTerms) -> tuple[Rule, ...]:
    """Ordered rules: longest identifiers first so e-mails go before account names."""
    candidates = (
        ("email", terms.emails, _const(ANON_EMAIL), r"{}"),
        ("full-name", terms.full_names, _const(ANON_NAME), r"\b(?:{})\b"),
        ("account", terms.owners, _const(ANON_OWNER), r"(?<![A-Za-z0-9])(?:{})(?![A-Za-z0-9])"),
        ("name-token", terms.name_tokens, _match_case(ANON_NAME_TOKEN), r"\b(?:{})\b"),
    )
    return tuple(
        Rule(name, re.compile(shape.format(_words(values)), re.IGNORECASE), repl)
        for name, values, repl, shape in candidates
        if values
    )


def record_rules(session_aliases: Mapping[str, str], uuid_aliases: Mapping[str, str]) -> tuple[Rule, ...]:
    return (
        Rule("commit-trailer", TRAILER_LINE_RE, _const("")),
        Rule("session-url", SESSION_URL_RE, _const(ANON_SESSION_URL)),
        Rule("session-id", SESSION_ID_RE, _lookup(session_aliases)),
        Rule("uuid", UUID_RE, _lookup(uuid_aliases)),
    )


def rules_for(profile: str, persons: tuple[Rule, ...], records: tuple[Rule, ...]) -> tuple[Rule, ...]:
    # Record rules run first: a session URL must be removed whole before the
    # account rule rewrites any part of it.
    return persons if profile == PROFILE_CODE else records + persons


def apply_rules(text: str, rules: Iterable[Rule]) -> Redacted:
    def step(acc: tuple[str, tuple[tuple[str, int], ...]], rule: Rule):
        current, counts = acc
        new_text, n = rule.pattern.subn(rule.replace, current)
        return (new_text, counts + ((rule.name, n),) if n else counts)

    text_out, counts = reduce(step, tuple(rules), (text, ()))
    return Redacted(text_out, counts)


# --------------------------------------------------------------------------
# Pseudonyms (deterministic: sorted unique values, numbered from 1)
# --------------------------------------------------------------------------


def pseudonyms(texts: Iterable[str], pattern: re.Pattern[str], prefix: str) -> dict[str, str]:
    found = sorted({m.group(0) for t in texts for m in pattern.finditer(t)})
    return {value: f"{prefix}-{index:03d}" for index, value in enumerate(found, start=1)}


# --------------------------------------------------------------------------
# Structured transforms for datasets written by other agents
# --------------------------------------------------------------------------

INTERNAL_KEYS = frozenset({"internal_ref", "internal_refs", "session_url", "session_urls"})


def strip_keys(value, keys: frozenset[str] = INTERNAL_KEYS):
    if isinstance(value, dict):
        return {k: strip_keys(v, keys) for k, v in value.items() if k not in keys}
    if isinstance(value, list):
        return [strip_keys(v, keys) for v in value]
    return value


def json_strip_internal(text: str) -> str:
    return json.dumps(strip_keys(json.loads(text)), indent=2, ensure_ascii=False) + "\n"


def csv_strip_internal(text: str) -> str:
    rows = tuple(csv.reader(io.StringIO(text)))
    if not rows:
        return text
    keep = tuple(i for i, h in enumerate(rows[0]) if h.strip() not in INTERNAL_KEYS)
    out = io.StringIO()
    writer = csv.writer(out, lineterminator="\n")
    tuple(map(writer.writerow, ([r[i] for i in keep if i < len(r)] for r in rows)))
    return out.getvalue()


TRANSFORMS: Mapping[str, Callable[[str], str]] = {
    "none": lambda t: t,
    "json-strip-internal": json_strip_internal,
    "csv-strip-internal": csv_strip_internal,
}


def normalize_newlines(text: str) -> str:
    return text.replace("\r\n", "\n").replace("\r", "\n")


# --------------------------------------------------------------------------
# Denylist
# --------------------------------------------------------------------------

STATIC_DENY_LITERALS = (
    "session" + "_01",
    "claude.ai" + "/code",
)


def build_denylist(terms: PersonTerms, advisory: Iterable[str]) -> dict:
    """The denylist is private: it lists the very identifiers it guards."""
    literals = sorted(
        set(STATIC_DENY_LITERALS)
        | set(terms.emails)
        | set(terms.owners)
        | {n.lower() for n in terms.full_names}
        | {f"github.com/{o}" for o in terms.owners}
        | {f"{o}.github.io" for o in terms.owners}
    )
    words = sorted({t.lower() for t in terms.name_tokens})
    return {
        "schema": "a021-anonymous-artifact.denylist/1",
        "literals": literals,
        "words": words,
        "regexes": [SESSION_ID_RE.pattern, SESSION_URL_RE.pattern],
        "advisory": sorted(set(advisory)),
    }


@dataclass(frozen=True)
class CompiledDenylist:
    blocking: tuple[tuple[str, re.Pattern[str]], ...]
    advisory: tuple[tuple[str, re.Pattern[str]], ...]
    real_shas: frozenset[str] = frozenset()
    sha_prefixes: frozenset[str] = frozenset()


def compile_denylist(denylist: Mapping, review: bool = False) -> CompiledDenylist:
    """Faithful mode: person identifiers block, product names are advisory.
    Review mode: product/organisation tokens and real object ids block too."""
    literal = tuple((t, re.compile(re.escape(t), re.IGNORECASE)) for t in denylist.get("literals", ()))
    words = tuple((t, re.compile(rf"\b{re.escape(t)}\b", re.IGNORECASE)) for t in denylist.get("words", ()))
    regexes = tuple((p, re.compile(p, re.IGNORECASE)) for p in denylist.get("regexes", ()))
    advisory = tuple((t, re.compile(re.escape(t), re.IGNORECASE)) for t in denylist.get("advisory", ()))
    if not review:
        return CompiledDenylist(blocking=literal + words + regexes, advisory=advisory)
    product = tuple((t, re.compile(re.escape(t), re.IGNORECASE)) for t in denylist.get("review_literals", ())) + tuple(
        (p, re.compile(p, re.IGNORECASE)) for p in denylist.get("review_regexes", ()))
    shas = frozenset(denylist.get("review_shas", ()))
    return CompiledDenylist(blocking=literal + words + regexes + product, advisory=(),
                            real_shas=shas, sha_prefixes=frozenset(s[:7] for s in shas))


def decode_for_scan(data: bytes) -> str:
    # latin-1 maps every byte, so binary files are scanned too.
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("latin-1")


def scan_text(text: str, patterns: Iterable[tuple[str, re.Pattern[str]]]) -> tuple[Finding, ...]:
    return tuple(
        Finding(term, text.count("\n", 0, m.start()) + 1, text[max(0, m.start() - 20): m.end() + 20].replace("\n", " "))
        for term, pattern in patterns
        for m in pattern.finditer(text)
    )


def is_text(data: bytes) -> bool:
    if b"\x00" in data:
        return False
    try:
        data.decode("utf-8")
        return True
    except UnicodeDecodeError:
        return False


# --------------------------------------------------------------------------
# Review mode: product/organisation aliasing and commit-SHA pseudonyms
# --------------------------------------------------------------------------
#
# The real tokens are configuration (scripts/artifact_sources.json, never
# bundled); nothing below names them.

HEX_TOKEN_RE = re.compile(r"(?<![0-9A-Za-z])[0-9a-f]{7,40}(?![0-9A-Za-z])")


@dataclass(frozen=True)
class AliasConfig:
    tokens: tuple[tuple[str, str], ...]  # (real, alias), same length, matched as substrings
    phrases: tuple[tuple[tuple[str, ...], tuple[str, ...]], ...]  # word-wise, same lengths
    bounded: tuple[tuple[str, str], ...] = ()  # (real, alias): Capitalised/UPPER anywhere, lower only after a non-letter


def alias_config(raw: Mapping) -> AliasConfig:
    tokens = tuple((r.lower(), a.lower()) for r, a in raw["token_aliases"])
    bounded = tuple((r.lower(), a.lower()) for r, a in raw.get("bounded_token_aliases", ()))
    phrases = tuple((tuple(w.lower() for w in r), tuple(w.lower() for w in a)) for r, a in raw["phrase_aliases"])
    bad = tuple(f"{r}->{a}" for r, a in tokens + bounded if len(r) != len(a)) + tuple(
        f"{' '.join(r)}->{' '.join(a)}" for r, a in phrases
        if len(r) != len(a) or any(len(x) != len(y) for x, y in zip(r, a)))
    if bad:
        raise ValueError(f"aliases must preserve length (byte offsets, padding, hunk geometry): {bad}")
    return AliasConfig(tokens, phrases, bounded)


def bounded_regex(real: str) -> str:
    """Case-sensitive: `Real` and `REAL` anywhere (camelCase / CONSTANT parts),
    `real` only when not preceded by a letter (so `recordOk` is untouched)."""
    cap, up = real[:1].upper() + real[1:], real.upper()
    return f"(?-i:{re.escape(cap)}|{re.escape(up)}|(?<![A-Za-z]){re.escape(real)})"


def case_like(source: str, alias: str) -> str:
    """Positional case transfer between same-length strings."""
    return "".join(a.upper() if s.isupper() else a.lower() for s, a in zip(source, alias))


def phrase_regex(words: tuple[str, ...]) -> str:
    """Words as capture groups, each pair joined by an optional captured separator."""
    return "".join(f"({re.escape(w)})" + (r"([-_\s]?)" if i < len(words) - 1 else "")
                   for i, w in enumerate(words))


def alias_rules(cfg: AliasConfig) -> tuple[Rule, ...]:
    def phrase_replace(alias_words: tuple[str, ...]):
        def replace(m: re.Match[str]) -> str:
            groups = m.groups()
            words, seps = groups[0::2], groups[1::2] + ("",)
            return "".join(case_like(w, a) + s for w, a, s in zip(words, alias_words, seps))
        return replace

    phrase = tuple(Rule(f"alias-phrase:{i}", re.compile(phrase_regex(r), re.IGNORECASE), phrase_replace(a))
                   for i, (r, a) in enumerate(cfg.phrases, start=1))
    token = tuple(Rule(f"alias-token:{i}", re.compile(re.escape(r), re.IGNORECASE),
                       (lambda alias: lambda m: case_like(m.group(0), alias))(a))
                  for i, (r, a) in enumerate(cfg.tokens, start=1))
    bounded = tuple(Rule(f"alias-bounded:{i}", re.compile(bounded_regex(r)),
                         (lambda alias: lambda m: case_like(m.group(0), alias))(a))
                    for i, (r, a) in enumerate(cfg.bounded, start=1))
    return phrase + token + bounded


def alias_collisions(texts: Iterable[str], cfg: AliasConfig) -> tuple[str, ...]:
    """Alias forms already present in the faithful corpus (the rename must be invertible)."""
    probes = tuple((a, re.compile(re.escape(a), re.IGNORECASE)) for _, a in cfg.tokens + cfg.bounded) + tuple(
        (" ".join(a), re.compile(phrase_regex(a), re.IGNORECASE)) for _, a in cfg.phrases)
    corpus = tuple(texts)
    return tuple(name for name, p in probes if any(p.search(t) for t in corpus))


def hex_candidates(texts: Iterable[str]) -> tuple[str, ...]:
    """Hex tokens (7-40 chars, including all-digit ones) that may be object ids;
    only those that resolve in the repository are pseudonymised."""
    return tuple(sorted({m.group(0) for t in texts for m in HEX_TOKEN_RE.finditer(t)}))


def sha_pseudonym(index: int) -> str:
    """40-hex pseudonym; abbreviations use its prefix, so prefixes stay consistent."""
    return ((f"{index:05x}" + "c") * 7)[:40]


@dataclass(frozen=True)
class ShaMap:
    resolved: Mapping[str, str]  # token -> real full object id (or the token itself if ambiguous)
    pseudo: Mapping[str, str]  # real full object id (or ambiguous token) -> 40-hex pseudonym


def sha_map(resolved: Mapping[str, str]) -> ShaMap:
    keys = sorted(set(resolved.values()))
    return ShaMap(dict(resolved), {k: sha_pseudonym(i) for i, k in enumerate(keys, start=1)})


def sha_rule(smap: ShaMap) -> Rule:
    def replace(m: re.Match[str]) -> str:
        token = m.group(0)
        key = smap.resolved.get(token)
        return smap.pseudo[key][:len(token)] if key else token
    return Rule("commit-sha", HEX_TOKEN_RE, replace)


def review_rules(cfg: AliasConfig, smap: ShaMap) -> tuple[Rule, ...]:
    return alias_rules(cfg) + (sha_rule(smap),)


def review_denylist(cfg: AliasConfig, real_shas: Iterable[str]) -> dict:
    return {
        "review_literals": sorted({r for r, _ in cfg.tokens}),
        "review_regexes": sorted({phrase_regex(r) for r, _ in cfg.phrases} | {bounded_regex(r) for r, _ in cfg.bounded}),
        "review_shas": sorted(set(real_shas)),
    }


def scan_shas(text: str, real_shas: frozenset[str], prefixes: frozenset[str]) -> tuple[Finding, ...]:
    """Any hex token of 7+ chars that abbreviates a real object id is a leak."""
    return tuple(
        Finding("real-sha", text.count("\n", 0, m.start()) + 1, m.group(0))
        for m in HEX_TOKEN_RE.finditer(text)
        if m.group(0)[:7] in prefixes and any(s.startswith(m.group(0)) for s in real_shas)
    )
