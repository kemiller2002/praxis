# WI-0075 independent human coding instructions

## Goal

Provide an independent, treatment-blind second coding of the architecture rubric. This is a publication-validity check, not a request to confirm the existing conclusion.

## Who may code

Preferred: a software engineer or software-architecture researcher who:
- did not implement either arm;
- has not read the manuscript, PR #202, the A021/R2 evaluator reports, or the arm mapping;
- is not told which arm is cohort/grouped or per-item/independent;
- is willing to record uncertainty rather than guess.

The repository owner and anyone who already knows the treatment mapping should not be the blind coder.

## What the coder receives

Give the coder only the generated human-coding packet. Do not give them the repository or PR.

The packet contains:
- an anonymized baseline source tree;
- four neutral patches labelled S1-X, S1-Y, S2-M, S2-N;
- the frozen acceptance criteria;
- `CODEBOOK.md`;
- `coding-sheet.csv`;
- checksums and packet instructions.

It intentionally excludes:
- the treatment mapping;
- paper/manuscript;
- architecture findings;
- prior evaluator outputs;
- metrics and cost results;
- prompts and session records.

## Procedure

1. Read `CODEBOOK.md` completely before opening arm patches.
2. Record a stable coder identifier in every row of `coding-sheet.csv`. It can be pseudonymous.
3. For each study, materialize or inspect each arm independently against the supplied baseline.
4. Code all eight dimensions for one arm before comparing it with the other arm.
5. Enter one allowed category and confidence for every row.
6. Cite anonymous file paths/symbols in `evidence_paths`.
7. Explain the category briefly in `rationale`.
8. If you accidentally learn the treatment mapping, prior ratings, or outcome, put that in `exposure_or_conflict` and stop. Return the partial sheet.
9. Return the completed CSV without changing any other packet file.

Allowed categories:
`unified`, `duplicated-compatible`, `divergent`, `missing`, `not-assessable`.

Allowed confidence:
`high`, `medium`, `low`.

## Independence check

Before coding, answer these in a separate note or email to the coordinator:
- Have you seen the mapping from X/Y/M/N to the treatments? yes/no
- Have you read the paper or PR #202? yes/no
- Have you seen the existing architecture ratings? yes/no
- Did you implement or evaluate any A021/R2 arm previously? yes/no

A "yes" does not make the person's technical opinion useless, but it means the coding cannot satisfy the blind independent-coder gate.

## After return

The coordinator:
1. commits the returned sheet unchanged;
2. records the coder's independence answers;
3. only then runs the agreement script;
4. does not reveal the treatment mapping until the coding is frozen;
5. preserves disagreements rather than editing the human sheet.

The frozen adjudication rule in `CODEBOOK.md` controls how disagreements affect the manuscript.
