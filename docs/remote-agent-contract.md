# Praxis remote agent contract

This page is for an agent — or a person, or automation — that can reach
this repository on GitHub but has **no local .NET, F#, or Praxis runtime**.
Praxis governs such work just as strictly as it governs local work. You
send typed requests, and Praxis runs them for you.

The full specification is [`remote-protocol.md`](remote-protocol.md). This
page is all you need to take part.

## 1. Discover

Dispatch a `praxis.describe` request, described in section 2. The response's
`result` tells you:

- whether remote execution is available here;
- the Praxis version and protocol versions;
- every operation, with its required and optional arguments and the
  capability it needs;
- what this repository allows (`repository.capabilities`);
- the open and ready work;
- where results are kept.

If the repository does not have `.github/workflows/praxis-remote.yml`,
remote execution is not installed. Work locally, or ask a maintainer.

## 2. Send a request

Write the request as a document:

```json
{
  "protocol": "praxis.remote",
  "protocolVersion": "1.0",
  "requestId": "req-<something unique you keep>",
  "operation": "work.start",
  "repository": { "ref": "refs/heads/<branch>", "expectedSha": "<the commit you just read>" },
  "actor": { "kind": "agent", "id": "<your stable id>", "provider": "<if known>", "model": "<if known>", "runtime": "<if known>", "sessionId": "<if known>" },
  "arguments": { "workItemIds": ["<ID>"] }
}
```

Dispatch it on the branch it targets:

```
gh workflow run praxis-remote.yml --ref <branch> -f request_id=<requestId> -f request="$(cat request.json)"
```

You can use the REST API instead:
`POST /repos/{owner}/{repo}/actions/workflows/praxis-remote.yml/dispatches`,
with the body `{"ref": "<branch>", "inputs": {"request": "...", "request_id": "..."}}`.

**Several operations in one run.** Use `"operation": "batch"` with
`"protocolVersion": "1.2"` and
`"arguments": {"requests": [{"requestId": "...", "operation": "...", "arguments": {...}}, ...]}`.
Each constituent keeps its own request ID and outcome. The batch stops at
the first constituent that does not succeed, and `result.stoppedAt` names
it.

## 3. Get the result

- **For a mutation, read the journal on the branch.** It is at
  `.ros/remote/requests/<requestId>.json` (a `:` in your request ID becomes
  `~`). This is the durable record. You can also send `request.status` with
  `{"requestId": "..."}`.
- **Or open the run.** It is named `praxis remote <requestId>`. Its summary
  and its `praxis-remote-response` artifact hold the same response.
- **Or read the job log.** The job that executed the request prints the
  same response in a `praxis.remote response` group. This is the way to
  read a read-only result, or a rejection, when you can read job logs but
  cannot download artifacts.

## 4. Rules that keep you governed

- **Say who you are, and nothing more.** Fill in only the actor fields you
  actually know. Leave out anything you do not know; Praxis records it as
  `unknown`. Never reuse another agent's ID or execution. Continue only your
  own execution, through `execution.id`.
- **One intent, one request ID.** If you do not know whether a request ran,
  because of a timeout, a lost result, or an outcome of `unknown`, send
  **the same document with the same `requestId`** again. If it already
  happened, you get the recorded result back with `replayed: true`. Never
  mint a new ID for the same intent, and never reuse an ID for a different
  intent. The second case is refused as `idempotency-conflict`.
- **Bind mutations to what you saw.** `repository.expectedSha` must be the
  commit you read before forming the request. If the branch has moved, you
  get `stale-ref`: read the branch again and form a new request.
- **Operations only.** There is no way to run a command. The operation
  catalog in `praxis.describe` is the whole surface.
- **Never put secrets in a request.** A request that contains what looks
  like a credential is refused as `secret-detected`.
- **Telemetry is what you report.** Values you send through
  `telemetry.record` are labelled as supplied by you. Report only numbers
  you actually have, and omit the rest. A missing value stays unknown; it
  is never zero.

## 5. Continue someone else's work, and what to do when you cannot reach Praxis

- **Taking over from another agent.** Resume the work item as yourself. You
  get your own execution, and Praxis records its `parentExecutionId` as the
  predecessor's execution. You never continue, or record telemetry into, an
  execution that is not yours. Praxis refuses that, and it applies equally
  to another run of your own agent.
- **You could not invoke Praxis at all.** Commit your legitimate work
  normally. When Praxis is reachable again, attribute that work with
  `work.reconcile`, naming the commits. The attribution is recorded as
  `post-hoc`. The original Git author and committer are preserved, and you
  are recorded separately as the reconciliation actor. Never touch or
  recommit files to make them look attributed. Reconciliation is
  idempotent, and a change already reconciled to another work item is a
  conflict. It is never overwritten.

## 6. Read the outcome

| `failure.retry` | What to do |
|---|---|
| `never` | Fix the request. Examples: `invalid-request`, `unauthorized`, `domain-rejected`. |
| `after-refresh` | Re-read the branch, then form a new request with a new `expectedSha`. Examples: `stale-ref`, `concurrency-conflict`. |
| `same-request` | Retry the identical document with the same `requestId`. Examples: `timeout`, `transport-failed`. |

An `outcome` of `unknown` never means success. Retry with the same request
ID, or ask `request.status`.

A later agent can read everything you recorded and continue the work under
its own identity. That handoff is how continuity works here; nobody needs to
impersonate anyone.
