# Group analysis: GROUP-FND-FOUNDATIONS

- Group: `GROUP-FND-FOUNDATIONS`, recorded in the members' `group:GROUP-FND-FOUNDATIONS` tags (work-group commands are not on main; PRX-GRP-040/133). Machine-readable form: [`GROUP-FND-FOUNDATIONS.group-analysis.json`](GROUP-FND-FOUNDATIONS.group-analysis.json).
- Members: `PRAXIS-FND-03`, `PRAXIS-FND-04`, `PRAXIS-FND-05`.
- Execution repository: `kemiller2002/praxis`. Base commit: `f242aae6`.

## 1. Members

| Member | Obligation | Acceptance criteria | Depends on |
|---|---|---|---|
| PRAXIS-FND-03 | SAF-DEP-1, SAF-DEP-2 | 1. foundations.json declares aegis, forma, ordo pinned; limen and folio not applicable with reasons. 2. aegis-boundaries.json declares every code. 3. Verifier accepts a Forma tarball lock and an Ordo.Core package pin. 4. Self-verify test. 5. Status. | - |
| PRAXIS-FND-04 | SAF-AEGIS-1, 5, 6 | 1. Boundary module with per-boundary codes equal to the manifest. 2. Web and hub capture per request. 3. Redaction rules and a no-leak test. 4. Collector-sink tests. 5. Expected outcomes stay typed. 6. Released pin. | - |
| PRAXIS-FND-05 | SAF-FORMA-1, 2, 5, 6; PRX-UI-030, 031 | 1. Forma lock with sha256 and digest test. 2. Serve all.css from the tarball; local CSS removed. 3. Forma markup. 4. Forma fault presentation. 5. Tests. 6. Limen decision. 7. Docs. | - |

## 2. Reuse inventory

| Existing element | Location | What it does | Disposition |
|---|---|---|---|
| `Foundations.verify` | `src/Praxis.Cli/Foundations.fs:447` | Foundations verifier | reused for the self-verify guard |
| `verifyForma` | `src/Praxis.Cli/Foundations.fs:304` | Forma pin via package.json only | extended with the release-tarball lock |
| `verifyAegis` | `src/Praxis.Cli/Foundations.fs:284` | Aegis package, usage, boundary manifest | reused |
| `LimenPin` / `LimenManifestReader` | `src/Praxis.Domain/Foundations/LimenPin.fs`, `src/Praxis.Infrastructure/Foundations/LimenManifestReader.fs` | Pure pin decision plus file reader | pattern reused for a `FormaPin` decision and lock reader |
| Top-level Aegis capture | `src/Praxis.Cli/Program.fs:3084-3115` | One capture, `PRAXIS.CLI.UNHANDLED` | extended into a boundary module; kept as the outermost capture |
| `Payload.embeddedText` | `src/Praxis.Infrastructure/Lifecycle/Payload.fs:103` | Reads embedded payload text | reused pattern for the embedded Forma tarball |
| `WebInterface.stylesheet` | `src/Praxis.Cli/WebInterface.fs:738` | Serves local `web/styles.css` | replaced by the pinned Forma stylesheet |
| `Html.page` | `src/Praxis.Cli/WebHttp.fs:98` | Page shell for web and hub | extended: links Forma, skip link, Forma body classes |
| Strata/Signal/Summa boundary tests | `strata:tests/Strata.Tests/BoundaryTests.fs` | Collector-sink boundary tests | pattern reused |

Searches: `grep -rn "Aegis" src`, `grep -rn "styles.css" src`, `grep -rn "foundations.json" src tests`, `ls */.echelon/foundations.json` across sibling repositories.

New abstractions:

- `Praxis.Infrastructure.Boundary` (Aegis boundary classification): considered extending the inline classifier in `Program.fs:3097`; not reused because the classifier must be shared with web and hub and testable with a collector sink.
- `FormaPin` (pure) and `FormaRelease` (embedded tarball reader): considered `LimenPin` (Limen-specific manifest fields) and `Payload.embeddedText` (text only, no tar extraction).

## 3. Group-level design

- One declaration (`.echelon/foundations.json`) and one verifier; FND-04 and FND-05 supply the evidence that FND-03's self-verify guard checks.
- Forma consumption without npm: the unmodified release tarball is vendored with a lock (version, URL, sha256), embedded in the CLI, and extracted at runtime. No Forma CSS is copied into Praxis source.
- Conflict: the CLI boundary ratchet (`quality/cli-boundary-baseline.json`) forbids CLI growth. New logic lives in Infrastructure; CLI files only call it.
- Risk of solving separately: three verifier special cases and two different stylesheet services.

## 4. Order

PRAXIS-FND-04 (Aegis), PRAXIS-FND-05 (Forma), then PRAXIS-FND-03 (declaration and self-verify guard, once the evidence exists).

## 5. Verification pass

Recorded per member in `GROUP-FND-FOUNDATIONS.group-verification.json` before each completion.
