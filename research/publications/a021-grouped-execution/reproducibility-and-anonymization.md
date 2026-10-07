# Reproducibility and anonymization plan

## Goal

Make the study independently inspectable while complying with double-anonymous review.

The public Praxis repository is identifying. Do not link it directly from an anonymous SANER submission.

## Artifact layers

### Layer 1: immutable evidence manifest

Create a manifest containing:
- artifact logical name;
- original repository path;
- commit SHA/blob SHA;
- SHA-256 of exported bytes;
- role in the study;
- whether safe for anonymous release.

### Layer 2: anonymized study bundle

Include neutral names only:

- `baseline/`
- `arm-a.patch`
- `arm-b.patch`
- `acceptance-criteria.txt`
- `evaluation-protocol.txt`
- `evaluation-findings.json`
- `metrics.csv`
- `analysis/`
- `checksums.txt`

Remove or replace:
- owner/user names;
- GitHub account names;
- email addresses;
- agent-session URLs;
- repository URLs;
- identifying branch names if they reveal ownership;
- acknowledgments;
- organization-specific prose not necessary to reproduce the study.

Do not alter the code semantics needed for evaluation. If an identifier cannot be safely anonymized without changing evidence, omit that artifact from the anonymous bundle and describe how it will be released after review.

### Layer 3: camera-ready provenance

After acceptance, replace anonymous artifact references with canonical public repository paths and exact commits.

## Required reproducibility checks

1. Checksums of blind arm patches match the recorded bundle.
2. Baseline tree corresponds to `8b4ffa392e93b19bf39f6672a608954c934cb815`.
3. Paper metric table can be regenerated from `metrics.csv` plus analysis script.
4. Every qualitative architecture row has source locations for both arms.
5. Evaluator output remains unchanged after unblinding.
6. Mapping reveal is stored separately from blind findings.
7. Protocol deviations are included, not removed from the anonymous artifact.
8. Unknown/missing metrics remain missing.

## AI-authorship disclosure

Before submission, check the selected venue's current policy.

The paper must:
- list only human authors who satisfy the venue/IEEE authorship policy;
- treat AI systems as tools, not authors;
- disclose AI-assisted writing/coding when required;
- independently verify references, factual claims, tables, and generated text.

## Double-anonymous checklist

Before submission:

- [ ] no author names or affiliations in PDF;
- [ ] no acknowledgments;
- [ ] self-references written in third person;
- [ ] artifact URL is anonymous or omitted;
- [ ] PDF metadata does not identify authors;
- [ ] repository screenshots do not expose user/org names;
- [ ] branch/commit links in the paper do not point to identifying public URLs;
- [ ] supplemental archive filenames are neutral;
- [ ] no session URL contains user identity;
- [ ] all citations are real and manually checked.

## Post-review release

If accepted, publish:
- canonical evidence manifest;
- anonymization mapping;
- final metrics dataset;
- analysis scripts;
- exact artifact SHAs;
- final paper;
- a replication guide for applying the protocol to another repository.
