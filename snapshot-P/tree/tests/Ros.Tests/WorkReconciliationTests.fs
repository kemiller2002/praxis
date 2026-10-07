namespace Ros.Tests

open System.Text.Json.Nodes
open Ros.Contracts.Work
open Ros.Domain.Git
open Ros.Domain.Provenance
open Ros.Domain.Work
open Ros.Infrastructure.Git

/// Pure post-hoc attribution reconciliation rules (issue #80): selector
/// parsing, the `diff-tree` evidence parser, the reconciliation decision,
/// the event contract, and validation of recorded reconciliation events.
[<RequireQualifiedAccess>]
module WorkReconciliationTests =
    let private person name =
        { Name = name
          Email = $"{name.ToLowerInvariant()}@example.invalid"
          Date = "2026-09-20T10:00:00+00:00" }

    let private change kind path original =
        { Kind = kind
          Path = path
          OriginalPath = original
          Blob = if kind = GitCommitChangeKind.Deleted then None else Some $"blob-{path}" }

    let private commit sha parents changes =
        { Sha = sha
          Parents = parents
          Author = person "Alice"
          Committer = person "Carol"
          Subject = $"subject {sha}"
          Changes = changes }

    let private evidenceOf (commits: GitCommit list) =
        { Head = "head"
          Selectors =
            commits
            |> List.map (fun commit ->
                { Selector = GitEvidenceSelector.Commit commit.Sha
                  BaseCommit = None
                  HeadCommit = commit.Sha
                  Commits = [ commit.Sha ] })
          Commits = commits }

    let private config =
        { MeaningfulPatterns = [ "**" ]
          IgnoredPatterns = [ ".ros/events/**"; "generated/**" ] }

    let private request commits : ReconciliationRequest =
        { WorkItemId = "FEAT-1"
          Target = ReconciliationTarget.Live LiveWorkState.Complete
          Reason = "committed without an active work item"
          OccurredAt = "2026-09-27T00:00:00.000Z"
          Evidence = evidenceOf commits
          PathRestriction = []
          PathFilterConfig = config
          AttributedPaths = Set.empty
          ExistingClaims = [] }

    let private plan decision =
        match decision with
        | ReconciliationDecision.Reconcile plan -> plan
        | other -> failwith $"expected a reconciliation plan, got {other}"

    let private rejections decision =
        match decision with
        | ReconciliationDecision.Rejected rejections -> rejections
        | other -> failwith $"expected a rejection, got {other}"

    let private dispositionOf path (assessments: ReconciliationAssessment list) =
        assessments |> List.filter (fun assessment -> assessment.Path = path) |> List.map _.Disposition

    let private actor: Actor =
        { Kind = ActorKind.Human
          Id = "kevin"
          Provider = None
          Model = None
          Runtime = None }

    let private recordOf (plan: ReconciliationPlan) : ReconciliationRecord =
        { EventId = "event-1"
          WorkItemId = plan.WorkItemId
          OccurredAt = "2026-09-27T00:00:00.000Z"
          Attribution = WorkReconciliation.PostHoc
          Reason = plan.Reason
          Paths = plan.ReconciledPaths
          Evidence = { plan.Evidence with Commits = plan.RecordedCommits }
          Actor = Some actor
          IntegrityVerified = true }

    let private knownTargets id =
        match id with
        | "FEAT-1"
        | "FEAT-2" -> ReconciliationTarget.Live LiveWorkState.Complete
        | "WI-0002" -> ReconciliationTarget.Backlog "abandoned"
        | _ -> ReconciliationTarget.Unknown

    let private addedCommit = commit "c1" [ "c0" ] [ change GitCommitChangeKind.Added "src/a.fs" None ]

    let tests =
        [ { Name = "reconciliation selectors accept a revision and a two-dot range"
            Run =
              fun () ->
                  Assert.equal (Ok(GitEvidenceSelector.Commit "abc123")) (GitEvidenceSelector.tryParseCommit "abc123")
                  Assert.equal (Ok(GitEvidenceSelector.Range("main", "HEAD"))) (GitEvidenceSelector.tryParseRange "main..HEAD") }
          { Name = "reconciliation selectors refuse option-like, ranged, symmetric, and half-open input"
            Run =
              fun () ->
                  for value in [ "-p"; "--all"; "a..b"; ""; "a b" ] do
                      Assert.isTrue (GitEvidenceSelector.tryParseCommit value |> Result.isError) $"--commit '{value}' should be rejected"

                  for value in [ "a...b"; "..b"; "a.."; "a..b..c"; "-x..b" ] do
                      Assert.isTrue (GitEvidenceSelector.tryParseRange value |> Result.isError) $"--range '{value}' should be rejected" }
          { Name = "diff-tree parser reads added, modified, deleted, type-changed, renamed, and copied entries"
            Run =
              fun () ->
                  let zero = String.replicate 40 "0"
                  let blob letter = String.replicate 40 letter
                  let a, b, c, d, e, f, g, h = blob "a", blob "b", blob "c", blob "d", blob "e", blob "f", blob "g", blob "h"

                  let output =
                      String.concat
                          "\000"
                          [ $":000000 100644 {zero} {a} A"
                            "src/new.fs"
                            $":100644 100644 {b} {c} M"
                            "src/changed.fs"
                            $":100644 000000 {d} {zero} D"
                            "src/gone.fs"
                            $":100644 120000 {e} {f} T"
                            "src/link"
                            $":100644 100644 {g} {g} R100"
                            "src/old.fs"
                            "src/renamed.fs"
                            $":100644 100644 {h} {h} C075"
                            "src/source.fs"
                            "src/copy.fs"
                            "" ]

                  match GitDiffTreeParser.parse output with
                  | Error failure -> failwith failure.Message
                  | Ok changes ->
                      Assert.equal
                          [ GitCommitChangeKind.Added
                            GitCommitChangeKind.Modified
                            GitCommitChangeKind.Deleted
                            GitCommitChangeKind.TypeChanged
                            GitCommitChangeKind.Renamed
                            GitCommitChangeKind.Copied ]
                          (changes |> List.map _.Kind)

                      Assert.equal None (changes |> List.item 2).Blob
                      Assert.equal (Some a) (changes |> List.head).Blob
                      Assert.equal ("src/renamed.fs", Some "src/old.fs") ((changes |> List.item 4).Path, (changes |> List.item 4).OriginalPath)
                      Assert.equal [ "src/renamed.fs"; "src/old.fs" ] (GitCommitChange.touchedPaths (changes |> List.item 4))
                      Assert.equal [ "src/copy.fs" ] (GitCommitChange.touchedPaths (changes |> List.item 5)) }
          { Name = "diff-tree parser fails closed on an unmerged entry or a rename without both paths"
            Run =
              fun () ->
                  let zero = String.replicate 40 "0"
                  Assert.isTrue (GitDiffTreeParser.parse $":100644 100644 {zero} {zero} U\000src/a.fs\000" |> Result.isError) "unmerged must fail"
                  Assert.isTrue (GitDiffTreeParser.parse $":100644 100644 {zero} {zero} R100\000src/a.fs\000" |> Result.isError) "half rename must fail" }
          { Name = "reconciliation attributes added, modified, and deleted paths from one commit"
            Run =
              fun () ->
                  let evidence =
                      commit
                          "c1"
                          [ "c0" ]
                          [ change GitCommitChangeKind.Added "src/a.fs" None
                            change GitCommitChangeKind.Modified "src/b.fs" None
                            change GitCommitChangeKind.Deleted "src/c.fs" None ]

                  let result = plan (WorkReconciliation.decide (request [ evidence ]))
                  Assert.equal [ "src/a.fs"; "src/b.fs"; "src/c.fs" ] result.ReconciledPaths
                  Assert.equal [ "c1" ] (result.RecordedCommits |> List.map _.Sha)
                  Assert.equal "Alice" (List.head result.RecordedCommits).Author.Name }
          { Name = "reconciliation attributes both sides of a rename but only the destination of a copy"
            Run =
              fun () ->
                  let evidence =
                      commit
                          "c1"
                          [ "c0" ]
                          [ change GitCommitChangeKind.Renamed "src/new.fs" (Some "src/old.fs")
                            change GitCommitChangeKind.Copied "src/copy.fs" (Some "src/template.fs") ]

                  let result = plan (WorkReconciliation.decide (request [ evidence ]))
                  Assert.equal [ "src/copy.fs"; "src/new.fs"; "src/old.fs" ] result.ReconciledPaths }
          { Name = "reconciliation never records non-meaningful paths and reports nothing to do for an ignored-only commit"
            Run =
              fun () ->
                  let mixed =
                      commit
                          "c1"
                          [ "c0" ]
                          [ change GitCommitChangeKind.Added "src/a.fs" None
                            change GitCommitChangeKind.Modified "generated/out.js" None
                            change GitCommitChangeKind.Modified ".ros/events/events.jsonl" None ]

                  let result = plan (WorkReconciliation.decide (request [ mixed ]))
                  Assert.equal [ "src/a.fs" ] result.ReconciledPaths
                  Assert.equal [ ReconciliationDisposition.NotMeaningful ] (dispositionOf "generated/out.js" result.Assessments)
                  Assert.equal [ "src/a.fs" ] ((List.head result.RecordedCommits).Changes |> List.map _.Path)

                  let ignoredOnly = commit "c2" [ "c1" ] [ change GitCommitChangeKind.Modified "generated/out.js" None ]

                  match WorkReconciliation.decide (request [ ignoredOnly ]) with
                  | ReconciliationDecision.NothingToReconcile(_, assessments) ->
                      Assert.equal [ ReconciliationDisposition.NotMeaningful ] (assessments |> List.map _.Disposition)
                  | other -> failwith $"expected nothing to reconcile, got {other}" }
          { Name = "reconciliation across several commits records only the commits that justify a path"
            Run =
              fun () ->
                  let first = commit "c1" [ "c0" ] [ change GitCommitChangeKind.Added "src/a.fs" None ]
                  let second = commit "c2" [ "c1" ] [ change GitCommitChangeKind.Modified "generated/x" None ]
                  let third = commit "c3" [ "c2" ] [ change GitCommitChangeKind.Modified "src/a.fs" None; change GitCommitChangeKind.Added "src/b.fs" None ]
                  let result = plan (WorkReconciliation.decide (request [ first; second; third ]))
                  Assert.equal [ "src/a.fs"; "src/b.fs" ] result.ReconciledPaths
                  Assert.equal [ "c1"; "c3" ] (result.RecordedCommits |> List.map _.Sha) }
          { Name = "reconciliation of a partially attributed range skips already-attributed paths"
            Run =
              fun () ->
                  let evidence =
                      commit "c1" [ "c0" ] [ change GitCommitChangeKind.Modified "src/a.fs" None; change GitCommitChangeKind.Added "src/b.fs" None ]

                  let result =
                      plan (WorkReconciliation.decide { request [ evidence ] with AttributedPaths = set [ "src/a.fs" ] })

                  Assert.equal [ "src/b.fs" ] result.ReconciledPaths
                  Assert.equal [ ReconciliationDisposition.AlreadyAttributed ] (dispositionOf "src/a.fs" result.Assessments)
                  Assert.equal [ "src/b.fs" ] ((List.head result.RecordedCommits).Changes |> List.map _.Path) }
          { Name = "reconciliation rejects a missing, invalid, or abandoned work item and never creates one"
            Run =
              fun () ->
                  Assert.equal
                      [ ReconciliationRejection.UnknownWorkItem "FEAT-1" ]
                      (rejections (WorkReconciliation.decide { request [ addedCommit ] with Target = ReconciliationTarget.Unknown }))

                  Assert.equal
                      [ ReconciliationRejection.AbandonedWorkItem "FEAT-1" ]
                      (rejections (WorkReconciliation.decide { request [ addedCommit ] with Target = ReconciliationTarget.Backlog "abandoned" }))

                  Assert.equal
                      [ ReconciliationRejection.InvalidWorkItemId "feat 1" ]
                      (rejections (WorkReconciliation.decide { request [ addedCommit ] with WorkItemId = "feat 1" })) }
          { Name = "reconciliation accepts active, blocked, complete, and ready backlog work items"
            Run =
              fun () ->
                  for target in
                      [ ReconciliationTarget.Live LiveWorkState.Active
                        ReconciliationTarget.Live LiveWorkState.Blocked
                        ReconciliationTarget.Live LiveWorkState.Complete
                        ReconciliationTarget.Backlog "ready" ] do
                      plan (WorkReconciliation.decide { request [ addedCommit ] with Target = target }) |> ignore }
          { Name = "reconciliation requires a reason and Git evidence"
            Run =
              fun () ->
                  Assert.equal [ ReconciliationRejection.ReasonRequired ] (rejections (WorkReconciliation.decide { request [ addedCommit ] with Reason = "  " }))
                  Assert.equal [ ReconciliationRejection.NoEvidence ] (rejections (WorkReconciliation.decide (request []))) }
          { Name = "reconciliation rejects a merge commit as ambiguous evidence"
            Run =
              fun () ->
                  let merge = commit "m1" [ "c1"; "c2" ] []
                  Assert.equal [ ReconciliationRejection.MergeCommit "m1" ] (rejections (WorkReconciliation.decide (request [ addedCommit; merge ]))) }
          { Name = "a path restriction narrows Git evidence and rejects any path Git does not prove"
            Run =
              fun () ->
                  let evidence =
                      commit "c1" [ "c0" ] [ change GitCommitChangeKind.Added "src/a.fs" None; change GitCommitChangeKind.Added "src/b.fs" None ]

                  let narrowed = plan (WorkReconciliation.decide { request [ evidence ] with PathRestriction = [ "./src/a.fs" ] })
                  Assert.equal [ "src/a.fs" ] narrowed.ReconciledPaths
                  Assert.equal [ ReconciliationDisposition.NotSelected ] (dispositionOf "src/b.fs" narrowed.Assessments)

                  Assert.equal
                      [ ReconciliationRejection.PathNotInEvidence "src/unrelated.fs" ]
                      (rejections (WorkReconciliation.decide { request [ evidence ] with PathRestriction = [ "src/unrelated.fs" ] })) }
          { Name = "repeating a reconciliation for the same work item is a no-op"
            Run =
              fun () ->
                  let claim: ReconciliationClaim =
                      { EventId = "e1"
                        WorkItemId = "FEAT-1"
                        Commit = "c1"
                        Path = "src/a.fs" }

                  match WorkReconciliation.decide { request [ addedCommit ] with ExistingClaims = [ claim ]; AttributedPaths = set [ "src/a.fs" ] } with
                  | ReconciliationDecision.NothingToReconcile(_, assessments) ->
                      Assert.equal [ ReconciliationDisposition.AlreadyReconciled "e1" ] (assessments |> List.map _.Disposition)
                  | other -> failwith $"expected nothing to reconcile, got {other}" }
          { Name = "a change another work item already reconciled is a conflict, never overwritten"
            Run =
              fun () ->
                  let claim: ReconciliationClaim =
                      { EventId = "e1"
                        WorkItemId = "FEAT-2"
                        Commit = "c1"
                        Path = "src/a.fs" }

                  Assert.equal
                      [ ReconciliationRejection.ConflictingAttribution("src/a.fs", "c1", "FEAT-2", "e1") ]
                      (rejections (WorkReconciliation.decide { request [ addedCommit ] with ExistingClaims = [ claim ]; AttributedPaths = set [ "src/a.fs" ] })) }
          { Name = "the reconciliation event separates change author, work item, and reconciliation actor and reads back"
            Run =
              fun () ->
                  let result = plan (WorkReconciliation.decide (request [ addedCommit ]))
                  let node = ReconciliationEventContract.node "repo" "1.0.0" "2026-09-27T00:00:00.000Z" actor result
                  node["eventId"] <- JsonValue.Create "event-1"
                  Assert.equal "work.attribution.reconciled" (node["type"].GetValue<string>())
                  Assert.equal "post-hoc" (node["attribution"].GetValue<string>())
                  Assert.equal "kevin" (node["actor"].["id"].GetValue<string>())
                  Assert.equal "Alice" (node["gitEvidence"].["commits"].[0].["author"].["name"].GetValue<string>())
                  Assert.equal "Carol" (node["gitEvidence"].["commits"].[0].["committer"].["name"].GetValue<string>())
                  Assert.isTrue (isNull node["evidence"]) "the completion-evidence field name must not be reused"

                  match ReconciliationEventContract.read 1 true node with
                  | ReconciliationEventRead.Parsed record -> Assert.equal (recordOf result) record
                  | ReconciliationEventRead.Malformed(_, message) -> failwith message }
          { Name = "a valid recorded reconciliation attributes its paths"
            Run =
              fun () ->
                  let record = recordOf (plan (WorkReconciliation.decide (request [ addedCommit ])))
                  let result = ReconciliationValidation.assess knownTargets [ ReconciliationEventRead.Parsed record ]
                  Assert.empty result.Findings
                  Assert.equal [ record ] result.Valid }
          { Name = "an edited, unjustified, contemporaneous-labelled, or actorless reconciliation attributes nothing"
            Run =
              fun () ->
                  let record = recordOf (plan (WorkReconciliation.decide (request [ addedCommit ])))

                  for invalid in
                      [ { record with IntegrityVerified = false }
                        { record with Paths = [ "src/a.fs"; "src/unrelated.fs" ] }
                        { record with Attribution = "contemporaneous" }
                        { record with Actor = None }
                        { record with WorkItemId = "FEAT-404" }
                        { record with WorkItemId = "WI-0002" }
                        { record with Evidence = { record.Evidence with Commits = [ commit "m1" [ "a"; "b" ] [ change GitCommitChangeKind.Added "src/a.fs" None ] ] } } ] do
                      let result = ReconciliationValidation.assess knownTargets [ ReconciliationEventRead.Parsed invalid ]
                      Assert.empty result.Valid
                      let finding = Assert.single result.Findings
                      Assert.equal "work_reconciliation" finding.Field

                  let malformed = ReconciliationValidation.assess knownTargets [ ReconciliationEventRead.Malformed(3, "'paths' must be an array") ]
                  Assert.equal 1 malformed.Findings.Length }
          { Name = "two recorded reconciliations claiming one change for different work items are both invalid"
            Run =
              fun () ->
                  let first = recordOf (plan (WorkReconciliation.decide (request [ addedCommit ])))
                  let second = { first with EventId = "event-2"; WorkItemId = "FEAT-2" }
                  let result = ReconciliationValidation.assess knownTargets [ ReconciliationEventRead.Parsed first; ReconciliationEventRead.Parsed second ]
                  Assert.empty result.Valid
                  let finding = Assert.single result.Findings
                  Assert.isTrue (finding.Message.Contains "more than one work item") finding.Message }
          { Name = "reconciled attribution covers a path only while its content matches the recorded evidence"
            Run =
              fun () ->
                  let evidence =
                      commit
                          "c1"
                          [ "c0" ]
                          [ change GitCommitChangeKind.Added "src/a.fs" None
                            change GitCommitChangeKind.Deleted "src/gone.fs" None
                            change GitCommitChangeKind.Renamed "src/new.fs" (Some "src/old.fs") ]

                  let record = recordOf (plan (WorkReconciliation.decide (request [ evidence ])))
                  let accepted = ReconciliationCoverage.acceptedStates [ record ]
                  Assert.equal [ PathContentState.Present "blob-src/a.fs" ] accepted["src/a.fs"]
                  Assert.equal [ PathContentState.Absent ] accepted["src/gone.fs"]
                  Assert.equal [ PathContentState.Absent ] accepted["src/old.fs"]

                  let unchanged =
                      Map.ofList
                          [ "src/a.fs", PathContentState.Present "blob-src/a.fs"
                            "src/gone.fs", PathContentState.Absent
                            "src/new.fs", PathContentState.Present "blob-src/new.fs"
                            "src/old.fs", PathContentState.Absent ]

                  Assert.equal (set [ "src/a.fs"; "src/gone.fs"; "src/new.fs"; "src/old.fs" ]) (ReconciliationCoverage.covered accepted unchanged)

                  let changedAgain =
                      unchanged
                      |> Map.add "src/a.fs" (PathContentState.Present "later-edit")
                      |> Map.add "src/gone.fs" (PathContentState.Present "recreated")
                      |> Map.add "src/new.fs" PathContentState.Unreadable

                  Assert.equal (set [ "src/old.fs" ]) (ReconciliationCoverage.covered accepted changedAgain) }
          { Name = "a reconciliation dated before the commits it reconciles is rejected and, if recorded, invalid"
            Run =
              fun () ->
                  let backdated = { request [ addedCommit ] with OccurredAt = "2026-09-01T00:00:00.000Z" }

                  Assert.equal
                      [ ReconciliationRejection.OccurredBeforeEvidence("2026-09-01T00:00:00.000Z", "c1", "2026-09-20T10:00:00+00:00") ]
                      (rejections (WorkReconciliation.decide backdated))

                  let record = recordOf (plan (WorkReconciliation.decide (request [ addedCommit ])))
                  let result = ReconciliationValidation.assess knownTargets [ ReconciliationEventRead.Parsed { record with OccurredAt = "2026-09-01T00:00:00.000Z" } ]
                  Assert.empty result.Valid
                  Assert.equal 1 result.Findings.Length } ]
