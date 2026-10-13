Verbatim copies of `tests/fixtures/construction/time-entry-timer/{source-manifest.json,blueprint.json,plan.json}`
from `kemiller2002/ordo` at commit 92bde54 (PR #68). They are test inputs for Praxis host-controlled
construction (Praxis #242); Ordo remains their owner. Do not edit them by hand; re-copy from Ordo.

`plan.v2-unresolved.json` is a verbatim copy of the same-named Ordo fixture at `kemiller2002/ordo@e6cbeff`
(PR #71, Ordo 1.7.0, vocabulary 2). It binds the same manifest and blueprint and leaves the Stop effect's
failure disposition `unresolved`.

`requirements/timer-stop-completion.txt` and `requirements/timer-paused-time-excluded.txt` are verbatim
requirement excerpts of `kemiller2002/time-entry-state-machine@73a63f1` (L131-L144 and L105-L107), copied from the
frozen pilot skeleton in `kemiller2002/research-documents`
(`research/construction-by-design/time-entry-timer-01/harness/skeleton/requirements/`). Their SHA-256 equals the
`contentDigest` pinned in `source-manifest.json`; CONSTRUCT-008 handoff tests read them from the admitted source.

`construction-decisions.json` is a verbatim copy of the CONSTRUCT-008 operator-approved
`ordo.construction-decisions/1` record set
(`kemiller2002/research-documents`, `research/construction-by-design/CONSTRUCT-008/harness/decisions/`).
It binds this blueprint (canonical digest `sha256:28dd3964…`) and drives Ordo 1.8.1's default-plan generator.
