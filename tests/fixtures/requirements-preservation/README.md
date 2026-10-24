# Requirements-preservation fixtures (REQ-PRES-001)

Fixtures for `praxis requirements preservation verify`, driven by
`tests/Praxis.Tests/RequirementsPreservationTests.fs` (scenarios A-L).
See `docs/requirements-preservation.md` for the contract.

## Provenance of copied material

Source text is copied byte for byte; nothing in `source/` or the Rehearsal 3
derived files was edited.

| Fixture file | Copied from | Pinned at |
|---|---|---|
| `causa/source/CAUSA-UI-EXCERPT.md` | kemiller2002/indy-init `docs/CAUSA-UI-EXPERIENCE-REQUIREMENTS.md`: lines 1-2 (title), 23-31 (UI-004, UI-005, UI-006), 44-46 (UI-011), 56-70 (UI-016) | commit `fc5df1c9fb268db61ee96e59523826d72f824492`; file blob `87ffb8b135040067ad39af989ef2d7cd25b7e7be`; file last changed in `47b397fa6205c183a9df444909e5cc2537afc8b2` |
| `r3/source/CAUSA-UI-016-section.md` | the same document, lines 56-70: the complete "UI-016: Causa semantic color system [P0]" section | same |
| `r3/derived-original/RQ-CAUSA-2026-0001..0008*.md` | the original Causa Rehearsal 3 package, `research/requirements/` | commit `eac585c428801853a746cdf7fe40fadb7d4b14a1` (unchanged at `61c8a5dccd5e65005eb1564665fcce0298113da1`) |
| `r3/derived-corrected/RQ-CAUSA-2026-0001..0009*.md` | the Rehearsal 3 package including the remediation requirement | commit `61c8a5dccd5e65005eb1564665fcce0298113da1` (0009 added in `d256af36927bad1ba5d5f8f83603b027e14521fe`) |

Every UI-016, UI-004, UI-005, UI-006 and UI-011 section hash in the
fixture baselines equals the hash of the same section in the upstream
document at that commit (`praxis requirements preservation sections`).

Rehearsal 3 git blob ids, for verification against the source repository:

| File | Blob |
|---|---|
| RQ-CAUSA-2026-0001--investigation-discovery.md | `357bbc4aaef5a408bea842a6ab8d0d2aaaf6db1b` |
| RQ-CAUSA-2026-0002--integrity-and-reconstruction.md | `f0ced569ceb517c719cef6807c25d27880efafbd` |
| RQ-CAUSA-2026-0003--catalogue-and-summary.md | `bd927660655d5097be2e982b03dd502f15987949` |
| RQ-CAUSA-2026-0004--evidence-and-provenance.md | `b1a01cd754d4b355ffc075cd3280e58147ab6d67` |
| RQ-CAUSA-2026-0005--hypotheses-and-causality.md | `fed20b4be0c4ae32bb9749affdb1c1af87765633` |
| RQ-CAUSA-2026-0006--uncertainty-actions-lifecycle.md | `b129e5a6e99d4007d81e8b038785eddbd92db309` |
| RQ-CAUSA-2026-0007--investigation-workflows.md | `47938885fffdbe88ccf7835edf593f44bcb18457` |
| RQ-CAUSA-2026-0008--interface-security-architecture-deployment.md | `23ed0ab80fb261838d70d5d5afd0ab1ac011e2e0` |
| RQ-CAUSA-2026-0009--rehearsal-003-remediation.md | `67fe4d39f4c86a99a18a757e6ea6f543d4b190bb` |

## Authored test material

Everything else is authored for these tests and is labelled as a test
fixture: the baselines, dispositions, approval records (authority
"operator (test fixture)"), equivalence reviews ("fixture-reviewer"), the
`RQ-TEST-*` derived packages, the drifted source in `causa/G-drift/`, and
the corrective `r3/derived-corrected/RQ-CAUSA-2026-0010` requirement. None of
them is a record of a real approval, review or Rehearsal 3 artifact.
