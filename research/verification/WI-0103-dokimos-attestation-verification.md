# Dokimos Attestation Verification (Task 3A)

Date: 2026-10-25

Work item: `WI-0103`

Fixture: `TASK3A-FIXTURE-20261025`

Verdict: `PASS`

## Release baseline

| Field | Value |
| --- | --- |
| Dokimos version | `0.7.0` (`dokimos-v0.7.0`) |
| Dokimos release SHA | `04b5f856367b47a087285109c1fc5923ec351389` |
| Dokimos artifact | `dokimos-osx-arm64.tar.gz` |
| Dokimos artifact SHA-256 | `45d8bb4d3ac0da8b9ba58a1e29d369eee9ef546b73860057488961d447c6ef69` |
| Praxis version | `3.20.0` (`v3.20.0`) |
| Praxis release SHA | `ee6430af3c03aa958c4e5663d65269457269db3d` |
| Praxis artifact SHA-256 | `56b61dff5c147464e45dcb4f090ec0cfb827e4bd457692a47b0534faf82a1149` |
| Attestation contract | `dokimos.completion-verification/1`; authority `dokimos.completion-authority/1`; Ordo `ecir/1` |
| Ordo contract digest | `sha256:4150ccaea61ac92479911c55ff90f1410b46f641bacd80ddb740daa9b8e48c9e` |
| Freshness enforcement available | `YES` |
| Release compatible | `YES` |

The Dokimos release manifest declares `completion.verify` contract version 1 and
`completion.verify-attested` contract version 1. The released binary identifies
itself as `0.7.0+04b5f856367b47a087285109c1fc5923ec351389`. Praxis 3.20.0 requires
Dokimos 0.7.0 or newer when attested evidence is required. The fixture enabled
both `requireVerifiedCompletion` and `requireAttestedEvidence`, and pinned the
host verifier key ID
`sha256:e5d9be68fce64c0989778c72ad4382af0b45577ac5fb8dbb3a69b01b2e575e7f`.

The downloaded artifact digests matched the publisher checksum manifests and
the GitHub release asset digests. Local `gh attestation verify` could not
initialize a Sigstore verifier, so GitHub build-provenance verification was
unavailable in this environment. This did not replace or weaken the completion
attestation tests: the test matrix used signed Praxis verifier receipts and
Dokimos independently verified their signatures, identities, statuses,
bindings, and freshness.

## Fixture and commands

The fixture was an isolated Git repository; no production work records were
modified. The key source revisions were:

- baseline: `e8ad389d19a529396735ae4c2d35aea19b80fd98`
- valid B1 source revision: `8ebdf9cf704f193e0cd115376d4eff2e00d23edf`
- stale-evidence prior revision: `48cf90a4c61a1b4ca232d1e5fe4f328540b98f9f`
- stale-evidence new revision: `fb9ac860271d533370a1fd46c75e52851d09ee3b`
- source-binding new revision: `4bfb68982d0a54fd4a23b1a30930f3c9eefa2204`
- packet-capture source revision: `b794c46e53feed5678c6ed3f1baaaab3853b3e3a`

The released Praxis binary created claims with this command shape:

```text
praxis work claim --id TASK3A-BN --claimant task3a/implementation-agent --token tok-bN --lease-seconds 3600
```

For cases with a generated receipt, the released Praxis verifier was invoked as:

```text
praxis verifier serve --socket HOST_SOCKET --policy HOST_POLICY --contract-digest sha256:4150ccaea61ac92479911c55ff90f1410b46f641bacd80ddb740daa9b8e48c9e --key-file HOST_PRIVATE_KEY
praxis work verify --id TASK3A-BN --token tok-bN
```

The decisive completion calls were:

```text
B1: praxis work complete-verified --id TASK3A-B1 --token tok-b1
B2: praxis work complete-verified --id TASK3A-B2 --token tok-b2
B3: praxis work complete-verified --id TASK3A-B3 --token tok-b3
B4: praxis work complete-verified --id TASK3A-B4 --token tok-b4
B5: praxis work complete-verified --id TASK3A-B5 --token tok-b5
B6: praxis work complete-verified --id TASK3A-B6 --token tok-b6 --evidence EV-TESTS=.git/praxis/receipts/TASK3A-B7/1-fb9ac860271d-35238faa-c885-4f1f-8d5b-47264e1b99df.json
B7: praxis work complete-verified --id TASK3A-B7 --token tok-b7
B8: praxis work complete-verified --id TASK3A-B8 --token tok-b8 --evidence EV-TESTS=/private/tmp/dokimos-verify.XPxd1O/host/not-present.json
B9: praxis work complete-verified --id TASK3A-B9 --token tok-b9 --validation unit-tests=passed:EV-TESTS --evidence EV-TESTS=agent-success.txt
B9 override: praxis work complete --id TASK3A-B9 --evidence narrative=agent-success.txt
```

The independent Dokimos harness invoked the released artifact directly for each
sanitized packet/authority pair:

```text
dokimos completion verify --packet BN.packet.json --authority BN.authority.json
```

## Verification matrix

| Test | Expected | Praxis actual | Praxis exit | Dokimos actual | Dokimos exit | Result |
| --- | --- | --- | ---: | --- | ---: | --- |
| B1 Valid completion | ACCEPT | completed; signed receipt consumed | 0 | `verified` | 0 | PASS |
| B2 Missing attestation | REJECT | `PRX.COMPLETION.EVIDENCE_MISSING` | 3 | `DOK.COMPLETION.EVIDENCE_UNATTESTED` | 4 | PASS |
| B3 Stale verification | REJECT | `PRX.COMPLETION.HEAD_MISMATCH` | 3 | `DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH`, `DOK.COMPLETION.STALE_EVIDENCE` | 4 | PASS |
| B4 Blocked verification | REJECT | `PRX.COMPLETION.RECEIPT_NOT_PASSED` | 3 | `DOK.COMPLETION.ATTESTATION_NOT_PASSED`, `DOK.COMPLETION.EVIDENCE_UNAVAILABLE`, `DOK.COMPLETION.VALIDATION_UNAVAILABLE` | 4 | PASS |
| B5 Failed verification | REJECT | `PRX.COMPLETION.RECEIPT_NOT_PASSED` | 3 | `DOK.COMPLETION.ATTESTATION_NOT_PASSED`, `DOK.COMPLETION.EVIDENCE_FAILED`, `DOK.COMPLETION.VALIDATION_FAILED` | 4 | PASS |
| B6 Wrong work item | REJECT | `PRX.COMPLETION.RECEIPT_BINDING_MISMATCH` | 3 | `DOK.COMPLETION.WORK_ITEM_MISMATCH` | 4 | PASS |
| B7 Wrong source SHA | REJECT | `PRX.COMPLETION.HEAD_MISMATCH` | 3 | `DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH` | 4 | PASS |
| B8 Missing evidence artifact | REJECT | `PRX.COMPLETION.EVIDENCE_MISSING` | 3 | `DOK.COMPLETION.EVIDENCE_UNOBSERVED` | 4 | PASS |
| B9 Unauthorized override | REJECT | `PRX.COMPLETION.RECEIPT_MISSING`; plain completion: `PRX.COMPLETION.VERIFIED_COMPLETION_REQUIRED` | 3 | `DOK.COMPLETION.EVIDENCE_UNATTESTED` | 4 | PASS |

The final claim-state query showed B1 `completed` with one consumed packet;
B2 through B9 remained `claimed`, with no completion record and no consumed
packet. Thus rejection was enforced by state-transition behavior rather than
reported only as advisory diagnostics.

## Evidence identities

- B1 evidence digest:
  `sha256:e18987bba6b559238fed53085ee5fd225fe611b793a9d9895637f347cbd7eda4`
- B1 completion packet digest:
  `sha256:4bb213a10abc6c64945f6132e79fff8d046e6327eff3b41c82425586249ee6b8`
- B1 Dokimos completion receipt digest:
  `sha256:df4141a16504fe0fda06890732198bbb37059a36815bf1cc23fd6b4d3f0b0db1`
- B1 metadata commit:
  `48cf90a4c61a1b4ca232d1e5fe4f328540b98f9f`; its parent is the verified
  source revision and it changed only `.ros/events/events.jsonl`.
- B3 stale receipt evidence digest:
  `sha256:cd88f8958c944afabce1e848a7d1b442f090656da0ac74e5560454ed7de2df99`
- B4 blocked receipt evidence digest:
  `sha256:49e6b216ab1389364de500f309149bcaffb59e97eb0472b30093fb5dd0354090`
- B5 failed receipt evidence digest:
  `sha256:ee56fae57d1d3de4fd1fccd5f7640c09345e2e89e794dd61641da03b14272cb9`
- B6/B7 signed receipt evidence digest:
  `sha256:6cca9b13e89cf73cb4c167a6ebae0c6430b7eb3054e2e1f2accc70adae7ca585`
- Packet-capture control evidence digest:
  `sha256:1dff0fc9278e5091366cef6728c497d65387ed67c7b7e3debd9dcb7ca2e4e122`
- Packet-capture control packet digest:
  `sha256:a471b6fbe50ef68d0053bbfe06800ff34de0e8340ef50d68b0c79c47156d70c3`
- Packet-capture control receipt digest:
  `sha256:3fed6d9fb2649c097c61c2b9a771f1430c568743802c12d4039e8e0fa0513f29`

## Changes and repair decision

No Dokimos, Praxis, Ordo, or Indy implementation files were changed. No repair
or new release was necessary because the selected published artifacts passed
the entire focused matrix. The only repository changes are this durable
verification record and the Praxis work-protocol state for `WI-0103`.

## Remaining blockers

None for evidence-bound completion verification. GitHub/Sigstore build
provenance verification was unavailable locally as noted above, but the
published assets were independently checksum-verified and the requested
runtime completion-attestation behavior was fully executed.
