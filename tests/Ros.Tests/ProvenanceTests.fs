namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Application.Artifacts
open Ros.Contracts.Provenance
open Ros.Domain.Artifacts
open Ros.Domain.Provenance
open Ros.Domain.Telemetry
open Ros.Infrastructure.Artifacts

[<RequireQualifiedAccess>]
module ProvenanceTests =
    let private agent id provider execution =
        { Actor.unknown with
            Kind = ActorKind.Agent
            Id = Attribute.Known id
            Provider = Attribute.Known provider
            Runtime = Attribute.Known id
            ExecutionId = Attribute.Known execution }

    let private codex = agent "openai-codex" "openai" "EXE-20260925T100000000Z-aaaaaaaa"
    let private claude = agent "claude-code" "anthropic" "EXE-20260925T110000000Z-bbbbbbbb"
    let private alice = Actor.human "alice"

    let private contribution actor operation at =
        ProvenanceFixtures.contributionBy actor operation at

    let private provenanceOf contributions =
        contributions |> List.fold (fun provenance item -> Provenance.append item provenance) Provenance.empty

    let private identity provider runtime session agentId : Identity =
        { Provider = provider
          Model = None
          ModelVersion = None
          Runtime = runtime
          RuntimeVersion = None
          SessionId = session
          ConversationId = None
          RunId = None
          AgentId = agentId
          SubagentId = None
          ParentExecutionId = None }

    let private candidate executionId status identity : ExecutionCandidate =
        { ExecutionId = executionId
          WorkItemId = Some "WI-1"
          Status = status
          Identity = identity
          ActorKind = Some ActorKind.Agent }

    let private subject kind createdAt declared : ProvenanceSubject =
        { Kind = kind
          RecordId = "RQ-TEST-2026-A001"
          Path = "requirements/RQ-TEST-2026-A001--x.md"
          Field = "provenance"
          CreatedAt = createdAt
          Declared = declared
          LegacyAuthor = None }

    let private provenanceValue provenance =
        ProvenanceCodec.provenanceValue provenance |> ProvenanceCodec.toArtifactValue

    let private codes (findings: ProvenanceFinding list) = findings |> List.map (fun finding -> Severity.code finding.Severity, finding.Code)

    let private enforcedSince (cutoff: string) =
        { Enforce = true
          RequiredSince = Timestamp.tryParse cutoff }

    let private withTemporaryDirectory action =
        let root = Path.Combine(Path.GetTempPath(), $"ros-provenance-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            action root
        finally
            Directory.Delete(root, true)

    // ---- seeded generators for property tests --------------------------------

    let private pick (random: Random) (values: 'value list) = values[random.Next values.Length]

    let private randomText (random: Random) =
        pick random [ "plain"; "with: colon"; "[bracketed]"; "true"; "42"; "quote \"inside\""; "unicode ñ ✓"; "multi\nline"; "#hash" ]

    let private randomActor (random: Random) =
        let kind = pick random ActorKind.all
        let attribute () = if random.Next 3 = 0 then Attribute.Unknown else Attribute.Known(pick random [ "a1"; "codex"; "claude-code"; "gemini-cli"; "local-llm" ])

        { Kind = kind
          Id = attribute ()
          Provider = attribute ()
          Model = attribute ()
          ModelVersion = if random.Next 2 = 0 then None else Some "2026-09"
          Runtime = attribute ()
          RuntimeVersion = None
          ExecutionId = if random.Next 3 = 0 then Attribute.Unknown else Attribute.Known $"EXE-20260925T{random.Next(100000, 999999)}000Z-{random.Next():x8}"
          SessionId = if random.Next 2 = 0 then None else Some "session-1"
          Assurance = if random.Next 5 = 0 then Assurance.Declared "ci-attested" else Assurance.SelfReported }

    /// Only values the canonical form can carry survive a round trip: a
    /// non-required unknown attribute is omitted on write and read back as
    /// unknown, which is the same value.
    let private randomContribution (random: Random) index =
        { Operation = pick random Operation.all
          At = DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero).AddMinutes(float index).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
          Actor = randomActor random
          WorkItem = if random.Next 2 = 0 then None else Some "WI-0070"
          Reason = if random.Next 2 = 0 then None else Some((randomText random).Replace("\n", " "))
          Evidence = if random.Next 2 = 0 then [] else [ "EV-ROS-2026-A001"; "docs/x.md" ]
          Basis = if random.Next 3 = 0 then Some "abc123" else None }

    let private document =
        String.concat
            "\n"
            [ "---"
              "id: RQ-TEST-2026-A001"
              "title: Example requirement"
              "status: accepted"
              "created: 2026-09-25"
              "tags: [provenance, identity]"
              "derived_from:"
              "  - RQ-TEST-2026-A000"
              "# an operator comment that must survive"
              "---"
              ""
              "# Requirement"
              ""
              "Body text: with a colon." ]

    let tests =
        [ // ---- identity and execution identity -------------------------------
          { Name = "provenance: unknown and blank attributes are the explicit unknown token, never fabricated"
            Run = fun () ->
                Assert.equal Attribute.Unknown (Attribute.ofOption None)
                Assert.equal Attribute.Unknown (Attribute.ofOption (Some "  "))
                Assert.equal Attribute.Unknown (Attribute.ofOption (Some "UNKNOWN"))
                Assert.equal (Attribute.Known "codex") (Attribute.ofOption (Some " codex "))
                Assert.equal "unknown" (Attribute.render Attribute.Unknown) }

          { Name = "provenance: an agent actor always renders provider/model/runtime/execution, as unknown when not known"
            Run = fun () ->
                let actor = { Actor.unknown with Kind = ActorKind.Agent; Id = Attribute.Known "local-agent" }

                Assert.equal
                    [ "kind", "agent"
                      "id", "local-agent"
                      "provider", "unknown"
                      "model", "unknown"
                      "runtime", "unknown"
                      "executionId", "unknown"
                      "assurance", "self-reported" ]
                    (Actor.fields actor) }

          { Name = "provenance: a human actor carries no fabricated provider, model, or runtime"
            Run = fun () ->
                Assert.equal [ "kind", "human"; "id", "alice"; "assurance", "self-reported" ] (Actor.fields alice) }

          { Name = "provenance: a whitelisted agent environment resolves to an agent whose stable id is its runtime"
            Run = fun () ->
                let discovered = Identity.discover { IdentityInputs.empty with ClaudeCodeSessionId = Some "s1" }
                let actor = ActorResolution.resolve ActorResolution.emptyInputs discovered (ExecutionBinding.Matched "EXE-1")
                Assert.equal ActorKind.Agent actor.Kind
                Assert.equal (Attribute.Known "claude-code") actor.Id
                Assert.equal (Attribute.Known "anthropic") actor.Provider
                Assert.equal Attribute.Unknown actor.Model
                Assert.equal (Attribute.Known "EXE-1") actor.ExecutionId }

          { Name = "provenance: an explicit ROS_ACTOR id and ROS_ACTOR_KIND win over inference, for any provider"
            Run = fun () ->
                let discovered =
                    Identity.discover { IdentityInputs.empty with RosActor = Some "research-bot"; RosTelemetryProvider = Some "acme-ai"; RosTelemetryRuntime = Some "acme-runner" }

                let actor = ActorResolution.resolve { ExplicitKind = Some "agent"; ExplicitExecutionId = None } discovered ExecutionBinding.Unbound
                Assert.equal ActorKind.Agent actor.Kind
                Assert.equal (Attribute.Known "research-bot") actor.Id
                Assert.equal (Attribute.Known "acme-ai") actor.Provider
                Assert.equal Attribute.Unknown actor.ExecutionId }

          { Name = "provenance: CI without an agent session is automation; an unmapped environment stays unknown"
            Run = fun () ->
                let ci = Identity.discover { IdentityInputs.empty with GitHubActions = true; GitHubRunId = Some "9" }
                Assert.equal ActorKind.Automation (ActorResolution.resolve ActorResolution.emptyInputs ci ExecutionBinding.Unbound).Kind

                let bare = Identity.discover IdentityInputs.empty
                let actor = ActorResolution.resolve ActorResolution.emptyInputs bare ExecutionBinding.Unbound
                Assert.equal ActorKind.Unknown actor.Kind
                Assert.equal Attribute.Unknown actor.Id }

          { Name = "provenance: a human declares kind and id once; nothing is inferred from the environment"
            Run = fun () ->
                let discovered = Identity.discover { IdentityInputs.empty with RosActor = Some "alice" }
                let actor = ActorResolution.resolve { ExplicitKind = Some "human"; ExplicitExecutionId = None } discovered ExecutionBinding.Unbound
                Assert.equal ActorKind.Human actor.Kind
                Assert.equal (Attribute.Known "alice") actor.Id }

          { Name = "provenance: execution binding prefers ROS_EXECUTION_ID, else the latest compatible active execution"
            Run = fun () ->
                let current = identity "anthropic" "claude-code" (Some "s1") None

                let candidates =
                    [ candidate "EXE-20260925T100000000Z-00000001" "active" current
                      candidate "EXE-20260925T120000000Z-00000002" "active" current
                      candidate "EXE-20260925T130000000Z-00000003" "finalized" current ]

                Assert.equal
                    (ExecutionBinding.Matched "EXE-20260925T120000000Z-00000002")
                    (ActorResolution.bindExecution ActorResolution.emptyInputs current candidates)

                Assert.equal
                    (ExecutionBinding.Explicit "EXE-PINNED")
                    (ActorResolution.bindExecution { ExplicitKind = None; ExplicitExecutionId = Some "EXE-PINNED" } current candidates) }

          { Name = "provenance: another session, another agent, or an unidentified process never inherits an execution"
            Run = fun () ->
                let recorded = identity "anthropic" "claude-code" (Some "s1") (Some "claude-code")
                let candidates = [ candidate "EXE-20260925T100000000Z-00000001" "active" recorded ]
                let bind current = ActorResolution.bindExecution ActorResolution.emptyInputs current candidates

                Assert.equal ExecutionBinding.Unbound (bind (identity "anthropic" "claude-code" (Some "s2") None))
                Assert.equal ExecutionBinding.Unbound (bind (identity "openai" "codex" None None))
                Assert.equal ExecutionBinding.Unbound (bind (identity "unknown" "unknown" None None)) }

          { Name = "provenance: two runs of the same agent share a stable identity but never an execution identity"
            Run = fun () ->
                let discovered = Identity.discover { IdentityInputs.empty with CodexSessionId = Some "s" }
                let first = ActorResolution.resolve ActorResolution.emptyInputs discovered (ExecutionBinding.Explicit "EXE-20260925T100000000Z-00000001")
                let second = ActorResolution.resolve ActorResolution.emptyInputs discovered (ExecutionBinding.Explicit "EXE-20260925T110000000Z-00000002")
                Assert.equal (Actor.stableKey first) (Actor.stableKey second)
                Assert.isTrue (first.ExecutionId <> second.ExecutionId) "executions must differ" }

          // ---- contributions --------------------------------------------------
          { Name = "provenance: appending preserves every prior contributor and is idempotent for a retried contribution"
            Run = fun () ->
                let created = contribution codex Operation.Created "2026-09-25T10:00:00Z"
                let modified = contribution claude Operation.Modified "2026-09-25T11:00:00Z"
                let once = provenanceOf [ created; modified ]
                let twice = Provenance.append modified once
                Assert.equal once twice
                Assert.isTrue (Provenance.preserves (provenanceOf [ created ]) once) "prefix must be preserved"
                Assert.isTrue (not (Provenance.preserves once (provenanceOf [ modified ]))) "dropping the creator is a rewrite" }

          { Name = "provenance: the original creator is never the last modifier"
            Run = fun () ->
                let provenance =
                    provenanceOf
                        [ contribution codex Operation.Created "2026-09-25T10:00:00Z"
                          contribution claude Operation.Modified "2026-09-25T11:00:00Z"
                          contribution alice Operation.Modified "2026-09-25T12:00:00Z" ]

                Assert.equal (Some codex) (Provenance.originalCreator provenance |> Option.map _.Actor)
                Assert.equal (Some alice) (Provenance.lastContribution provenance |> Option.map _.Actor)
                Assert.equal [ codex; claude; alice ] (Provenance.contributors provenance)
                Assert.equal [ "EXE-20260925T100000000Z-aaaaaaaa"; "EXE-20260925T110000000Z-bbbbbbbb" ] (Provenance.executions provenance) }

          { Name = "provenance: human and agent involvement is distinguishable in every combination"
            Run = fun () ->
                let labels contributions = provenanceOf contributions |> Provenance.involvementLabels
                let at hour = $"2026-09-25T{hour:D2}:00:00Z"

                Assert.equal [ "agent-created" ] (labels [ contribution codex Operation.Created (at 1) ])
                Assert.equal [ "human-created" ] (labels [ contribution alice Operation.Created (at 1) ])

                Assert.equal
                    [ "agent-created"; "human-approved" ]
                    (labels [ contribution codex Operation.Created (at 1); contribution alice Operation.Approved (at 2) ])

                Assert.equal
                    [ "human-created"; "agent-modified" ]
                    (labels [ contribution alice Operation.Created (at 1); contribution codex Operation.Modified (at 2) ])

                Assert.equal
                    [ "agent-created"; "agent-modified"; "agent-to-agent-revision" ]
                    (labels [ contribution codex Operation.Created (at 1); contribution claude Operation.Modified (at 2) ])

                Assert.equal
                    [ "agent-created"; "human-modified"; "human-corrected-agent-work" ]
                    (labels [ contribution codex Operation.Created (at 1); contribution alice Operation.Modified (at 2) ])

                Assert.equal [ "creator-unknown"; "agent-modified" ] (labels [ contribution codex Operation.Modified (at 1) ])
                Assert.equal [ "unattributed" ] (labels []) }

          { Name = "provenance: a new record defaults to created; a legacy or existing record defaults to modified"
            Run = fun () ->
                Assert.equal Operation.Created (Provenance.defaultOperation true Provenance.empty)
                Assert.equal Operation.Modified (Provenance.defaultOperation false Provenance.empty)

                Assert.equal
                    Operation.Modified
                    (Provenance.defaultOperation true (provenanceOf [ contribution codex Operation.Created "2026-09-25" ])) }

          // ---- canonical serialization ------------------------------------------
          { Name = "provenance property: rendered provenance reads back identically (400 seeded cases)"
            Run = fun () ->
                let random = Random 20260925

                for case in 1..400 do
                    let contributions = List.init (random.Next(1, 5)) (randomContribution random)
                    let provenance = { Contributions = contributions }
                    let read, issues = ProvenanceCodec.readProvenance (provenanceValue provenance)

                    Assert.empty issues
                    Assert.equal provenance read
                    ignore case }

          { Name = "provenance property: JSON serialization round-trips through the neutral reader (200 seeded cases)"
            Run = fun () ->
                let random = Random 7

                for _ in 1..200 do
                    let expected = { Contributions = List.init (random.Next(1, 4)) (randomContribution random) }
                    let node = ProvenanceJson.provenanceNode expected
                    let reparsed = JsonNode.Parse(node.ToJsonString())

                    match ProvenanceJson.ofNode reparsed with
                    | Some value -> Assert.equal expected (ProvenanceCodec.readProvenance value |> fst)
                    | None -> failwith "provenance node vanished" }

          { Name = "provenance: missing required actor fields are reported, not defaulted"
            Run = fun () ->
                let value = ArtifactValue.Mapping(Map.ofList [ "kind", ArtifactValue.Text "agent"; "id", ArtifactValue.Text "codex" ])
                let _, issues = ProvenanceCodec.readActor "actor" value

                Assert.equal
                    [ "actor.provider"; "actor.model"; "actor.runtime"; "actor.executionId" ]
                    (issues |> List.map _.Field) }

          { Name = "provenance: invalid kind, operation, timestamp, and whitespace tokens are structural issues"
            Run = fun () ->
                let value =
                    ArtifactValue.Mapping(
                        Map.ofList
                            [ "operation", ArtifactValue.Text "rewrote"
                              "at", ArtifactValue.Text "yesterday"
                              "actor", ArtifactValue.Mapping(Map.ofList [ "kind", ArtifactValue.Text "robot"; "id", ArtifactValue.Text "two words" ]) ]
                    )

                let _, issues = ProvenanceCodec.readContribution "c" value

                Assert.equal
                    [ "invalid-operation"; "invalid-timestamp"; "invalid-kind"; "invalid-token" ]
                    (issues |> List.map _.Code) }

          // ---- validation policy ------------------------------------------------
          { Name = "provenance policy: legacy mode reports absent provenance as informational only"
            Run = fun () ->
                let findings = ProvenanceValidation.validate ProvenancePolicy.legacy [ subject RecordKind.Artifact (Some "2026-09-25") Declared.Absent ]
                Assert.equal [ "info", "legacy-unattributed" ] (codes findings) }

          { Name = "provenance policy: a record created after the cutoff without provenance is an error; older records stay legacy"
            Run = fun () ->
                let policy = enforcedSince "2026-09-25T18:00:00Z"
                let newRecord = subject RecordKind.Event (Some "2026-09-25T18:00:01Z") Declared.Absent

                let oldRecord =
                    { subject RecordKind.Artifact (Some "2026-09-20") Declared.Absent with
                        LegacyAuthor = Some "openai-codex" }

                Assert.equal [ "error", "missing-provenance" ] (codes (ProvenanceValidation.validate policy [ newRecord ]))

                let legacy = ProvenanceValidation.validate policy [ oldRecord ] |> Assert.single
                Assert.equal Severity.Info legacy.Severity
                Assert.isTrue (legacy.Message.Contains "openai-codex") "legacy author must be reported"
                Assert.isTrue (legacy.Message.Contains "not converted") "legacy author must not be converted" }

          { Name = "provenance policy: a date-only creation on the cutoff day counts as new; enforcement without a cutoff covers everything"
            Run = fun () ->
                Assert.isTrue (ProvenancePolicy.isNew (enforcedSince "2026-09-25T18:00:00Z") (Some "2026-09-25")) "cutoff day is new"
                Assert.isTrue (not (ProvenancePolicy.isNew (enforcedSince "2026-09-25T18:00:00Z") (Some "2026-09-24"))) "day before is legacy"
                Assert.isTrue (ProvenancePolicy.isNew { Enforce = true; RequiredSince = None } None) "no cutoff covers undated records"
                Assert.isTrue (not (ProvenancePolicy.isNew ProvenancePolicy.legacy (Some "2030-01-01"))) "legacy mode never requires" }

          { Name = "provenance policy: malformed provenance is an error even in legacy mode"
            Run = fun () ->
                let bad = ArtifactValue.Mapping(Map.ofList [ "kind", ArtifactValue.Text "martian" ])
                let findings = ProvenanceValidation.validate ProvenancePolicy.legacy [ subject RecordKind.Event None (Declared.ActorRecord bad) ]
                Assert.isTrue (findings |> List.exists (fun finding -> finding.Severity = Severity.Error && finding.Code = "invalid-kind")) $"{findings}" }

          { Name = "provenance policy: duplicate or misplaced authorship and a creatorless new record are errors"
            Run = fun () ->
                let policy = enforcedSince "2026-09-01"
                let check contributions = ProvenanceValidation.validate policy [ subject RecordKind.Artifact (Some "2026-09-25") (Declared.ProvenanceRecord(provenanceValue (provenanceOf contributions))) ] |> codes

                let created = contribution codex Operation.Created "2026-09-25T10:00:00Z"
                let modified = contribution claude Operation.Modified "2026-09-25T11:00:00Z"

                Assert.isTrue (check [ created; contribution claude Operation.Created "2026-09-25T12:00:00Z" ] |> List.contains ("error", "duplicate-authorship")) "duplicate"
                Assert.isTrue (check [ modified; contribution codex Operation.Created "2026-09-25T12:00:00Z" ] |> List.contains ("error", "authorship-not-first")) "not first"
                Assert.isTrue (check [ modified ] |> List.contains ("error", "missing-creator")) "creatorless"
                Assert.empty (check [ created; modified ]) }

          { Name = "provenance policy: honest unknowns on new records are visible warnings, not silent and not errors"
            Run = fun () ->
                let anonymous = { Actor.unknown with Kind = ActorKind.Agent }
                let findings = ProvenanceValidation.validate (enforcedSince "2026-09-01") [ subject RecordKind.Event (Some "2026-09-25T00:00:00Z") (Declared.ActorRecord(ProvenanceCodec.actorValue anonymous |> ProvenanceCodec.toArtifactValue)) ]
                Assert.equal [ "warning", "unbound-execution"; "warning", "unknown-actor-id" ] (codes findings) }

          { Name = "provenance policy: out-of-order contributions warn; an unverified assurance level is informational"
            Run = fun () ->
                let attested = { codex with Assurance = Assurance.Declared "ci-attested" }

                let provenance =
                    provenanceOf
                        [ contribution codex Operation.Created "2026-09-25T12:00:00Z"
                          contribution attested Operation.Modified "2026-09-25T11:00:00Z" ]

                let findings = ProvenanceValidation.validate ProvenancePolicy.legacy [ subject RecordKind.Artifact None (Declared.ProvenanceRecord(provenanceValue provenance)) ]
                Assert.equal [ "warning", "non-chronological"; "info", "unverified-assurance" ] (codes findings) }

          { Name = "provenance policy: removing or altering a committed contribution is a rewrite error"
            Run = fun () ->
                let before = provenanceOf [ contribution codex Operation.Created "2026-09-25T10:00:00Z" ]
                let rewritten = provenanceOf [ contribution claude Operation.Created "2026-09-25T10:00:00Z" ]
                Assert.equal [ "error", "provenance-rewritten" ] (codes (ProvenanceValidation.preservation "a.md" "provenance" before rewritten))
                Assert.empty (ProvenanceValidation.preservation "a.md" "provenance" before (Provenance.append (contribution alice Operation.Approved "2026-09-26") before)) }

          { Name = "provenance policy: an enforced record changed without a new contribution is an unrecorded-modification warning"
            Run = fun () ->
                let before = provenanceOf [ contribution codex Operation.Created "2026-09-25T10:00:00Z" ]
                let after = Provenance.append (contribution claude Operation.Modified "2026-09-25T11:00:00Z") before
                let policy = enforcedSince "2026-09-01"
                Assert.equal [ "warning", "unrecorded-modification" ] (codes (ProvenanceValidation.unrecordedModification policy "a.md" "provenance" before before))
                Assert.empty (ProvenanceValidation.unrecordedModification policy "a.md" "provenance" before after)
                Assert.empty (ProvenanceValidation.unrecordedModification ProvenancePolicy.legacy "a.md" "provenance" before before) }

          // ---- summary / metrics ---------------------------------------------
          { Name = "provenance summary: attribution by actor, corrections, agent-to-agent revisions, lineage, and hotspots"
            Run = fun () ->
                let record id contributions derived : AttributedRecord =
                    { Kind = RecordKind.Artifact
                      RecordId = id
                      Path = $"requirements/{id}.md"
                      Provenance = provenanceOf contributions
                      DerivedFrom = derived }

                let summary =
                    ProvenanceSummary.summarize
                        [ record "RQ-1" [ contribution codex Operation.Created "2026-09-25T01:00:00Z"; contribution alice Operation.Modified "2026-09-25T02:00:00Z" ] []
                          record "RQ-2" [ contribution codex Operation.Created "2026-09-25T01:00:00Z"; contribution claude Operation.Modified "2026-09-25T02:00:00Z"; contribution alice Operation.Approved "2026-09-25T03:00:00Z" ] [ "RQ-1" ]
                          record "RQ-3" [] [] ]

                Assert.equal 3 summary.Records
                Assert.equal 1 summary.UnattributedRecords
                Assert.equal [ "RQ-1" ] summary.HumanCorrectionsOfAgentWork
                Assert.equal [ "RQ-2" ] summary.AgentToAgentRevisions
                Assert.equal [ "RQ-2" ] summary.HumanApprovedAgentWork
                Assert.equal [ "RQ-2", [ "RQ-1" ] ] summary.DerivedRecords
                Assert.equal [ "RQ-2", 3; "RQ-1", 2 ] summary.Hotspots

                let codexTotals = summary.Actors |> List.find (fun totals -> totals.Id = "openai-codex")
                Assert.equal 2 codexTotals.RecordsCreated
                Assert.equal [ "openai" ] codexTotals.Providers }

          // ---- JSON records (events, backlog items) -----------------------------
          { Name = "provenance JSON: appending creates the block, preserves unmodeled fields, and is idempotent"
            Run = fun () ->
                let record = JsonNode.Parse("""{"id":"WI-1","custom":{"keep":true}}""") :?> JsonObject
                let created = contribution codex Operation.Created "2026-09-25T10:00:00Z"
                Assert.equal (Ok()) (ProvenanceJson.appendContribution record created)
                Assert.equal (Ok()) (ProvenanceJson.appendContribution record created)
                Assert.equal (Ok()) (ProvenanceJson.appendContribution record (contribution alice Operation.Modified "2026-09-25T11:00:00Z"))
                Assert.equal "true" (record.["custom"].["keep"].ToJsonString())

                match ProvenanceJson.ofNode record["provenance"] with
                | Some value ->
                    let provenance, issues = ProvenanceCodec.readProvenance value
                    Assert.empty issues
                    Assert.equal [ Operation.Created; Operation.Modified ] (provenance.Contributions |> List.map _.Operation)
                | None -> failwith "no provenance" }

          { Name = "provenance JSON: a malformed existing block is refused rather than overwritten"
            Run = fun () ->
                let record = JsonNode.Parse("""{"id":"WI-1","provenance":{"contributions":[{"operation":"created"}]}}""") :?> JsonObject
                let before = record.ToJsonString()

                match ProvenanceJson.appendContribution record (contribution codex Operation.Modified "2026-09-25T10:00:00Z") with
                | Error message -> Assert.isTrue (message.Contains "malformed") message
                | Ok() -> failwith "malformed provenance must be refused"

                Assert.equal before (record.ToJsonString()) }

          // ---- front matter -----------------------------------------------------
          { Name = "front matter: list items of the form 'key: value' parse as mappings; plain and quoted items stay scalars"
            Run = fun () ->
                let text =
                    "---\nid: X\nitems:\n  - plain\n  - \"quoted: text\"\n  - https://example.test/a\nprovenance:\n  contributions:\n    - operation: created\n      at: \"2026-09-25T10:00:00Z\"\n      actor:\n        kind: human\n        id: alice\n      evidence:\n        - EV-1\n    - operation: approved\n      at: 2026-09-26\n      actor:\n        kind: human\n        id: bob\n---\n"

                match FrontMatter.parse "x.md" text with
                | Error message -> failwith message
                | Ok document ->
                    Assert.equal
                        (Some(ArtifactValue.Sequence [ ArtifactValue.Text "plain"; ArtifactValue.Text "quoted: text"; ArtifactValue.Text "https://example.test/a" ]))
                        (ArtifactDocument.tryMetadata "items" document)

                    match ArtifactDocument.tryMetadata "provenance" document with
                    | Some value ->
                        let provenance, issues = ProvenanceCodec.readProvenance value
                        Assert.empty issues
                        Assert.equal [ "alice"; "bob" ] (provenance.Contributions |> List.map (fun item -> Attribute.render item.Actor.Id))
                        Assert.equal [ "EV-1" ] provenance.Contributions.Head.Evidence
                    | None -> failwith "provenance missing" }

          { Name = "front matter provenance: appending inserts lines only, preserving every other byte"
            Run = fun () ->
                let created = contribution codex Operation.Created "2026-09-25T10:00:00Z"

                match FrontMatterProvenance.append "x.md" document created with
                | Error message -> failwith message
                | Ok updated ->
                    let originalLines = document.Split('\n') |> Set.ofArray
                    let updatedLines = updated.Split('\n')
                    Assert.isTrue (originalLines |> Set.forall (fun line -> updatedLines |> Array.contains line)) "an original line was lost"
                    Assert.isTrue (updated.Contains "# an operator comment that must survive") "comment lost"

                    match FrontMatterProvenance.read "x.md" updated with
                    | Ok(provenance, []) -> Assert.equal [ created ] provenance.Contributions
                    | other -> failwith $"{other}" }

          { Name = "front matter provenance: later contributors append after earlier ones and a retry changes nothing"
            Run = fun () ->
                let steps =
                    [ contribution codex Operation.Created "2026-09-25T10:00:00Z"
                      contribution claude Operation.Modified "2026-09-25T11:00:00Z"
                      contribution alice Operation.Approved "2026-09-25T12:00:00Z" ]

                let final =
                    steps
                    |> List.fold
                        (fun text step ->
                            match FrontMatterProvenance.append "x.md" text step with
                            | Ok updated -> updated
                            | Error message -> failwith message)
                        document

                Assert.equal (Ok final) (FrontMatterProvenance.append "x.md" final steps[1])

                match FrontMatterProvenance.read "x.md" final with
                | Ok(provenance, []) -> Assert.equal steps provenance.Contributions
                | other -> failwith $"{other}" }

          { Name = "front matter provenance property: any sequence of appended contributions reads back in order (150 seeded cases)"
            Run = fun () ->
                let random = Random 42

                for _ in 1..150 do
                    let steps = List.init (random.Next(1, 6)) (randomContribution random)

                    let final =
                        steps
                        |> List.fold
                            (fun text step ->
                                match FrontMatterProvenance.append "x.md" text step with
                                | Ok updated -> updated
                                | Error message -> failwith message)
                            document

                    let expected =
                        steps
                        |> List.map (fun step -> { step with Reason = step.Reason |> Option.map (fun reason -> reason.Trim()) })
                        |> List.fold (fun provenance step -> Provenance.append step provenance) Provenance.empty

                    match FrontMatterProvenance.read "x.md" final with
                    | Ok(provenance, issues) ->
                        Assert.empty issues
                        Assert.equal expected provenance
                    | Error message -> failwith message }

          { Name = "front matter provenance: an inline empty contributions list is replaced, and malformed provenance is refused"
            Run = fun () ->
                let empty = document.Replace("---\n\n# Requirement", "provenance:\n  contributions: []\n---\n\n# Requirement")

                match FrontMatterProvenance.append "x.md" empty (contribution alice Operation.Created "2026-09-25") with
                | Ok updated ->
                    Assert.isTrue (not (updated.Contains "contributions: []")) "inline empty list must be replaced"
                    Assert.equal 1 (FrontMatterProvenance.read "x.md" updated |> Result.map (fst >> _.Contributions.Length) |> Result.defaultValue 0)
                | Error message -> failwith message

                let malformed = document.Replace("---\n\n# Requirement", "provenance:\n  contributions:\n    - operation: created\n---\n\n# Requirement")

                match FrontMatterProvenance.append "x.md" malformed (contribution alice Operation.Modified "2026-09-25") with
                | Error message -> Assert.isTrue (message.Contains "malformed") message
                | Ok _ -> failwith "malformed provenance must be refused" }

          // ---- requirements as canonical artifacts -------------------------------
          { Name = "requirements: RQ identifiers and statuses are canonical; prose files in requirements/ are not artifacts"
            Run = fun () ->
                Assert.isTrue (ArtifactPolicy.isValidIdentifier "RQ-ROS-2026-A001") "RQ id"

                withTemporaryDirectory (fun root ->
                    let directory = Path.Combine(root, "requirements")
                    Directory.CreateDirectory directory |> ignore
                    File.WriteAllText(Path.Combine(directory, "NOTES.md"), "# free-form prose, no front matter\n")

                    File.WriteAllText(
                        Path.Combine(directory, "RQ-TEST-2026-A001--example.md"),
                        "---\nid: RQ-TEST-2026-A001\ntitle: Example\nstatus: shipped\n---\n"
                    )

                    match ArtifactOperations.validate (FileArtifactRepository.create root) with
                    | ValidationOutcome.Completed findings ->
                        Assert.equal [ "status" ] (findings |> List.map _.Field)
                        Assert.isTrue (findings.Head.Path.EndsWith "RQ-TEST-2026-A001--example.md") "only the RQ file is an artifact"
                    | outcome -> failwith $"{outcome}") }

          { Name = "requirements: an absent registry is not stale while no requirement exists, and is stale once one does"
            Run = fun () ->
                withTemporaryDirectory (fun root ->
                    let repository = FileArtifactRepository.create root

                    let staleRegistries () =
                        match ArtifactOperations.checkRegistries repository with
                        | RegistryCheckOutcome.Completed findings -> findings |> List.map _.Path
                        | outcome -> failwith $"{outcome}"

                    Assert.isTrue (not (staleRegistries () |> List.contains "registries/requirements.json")) "empty optional registry is not stale"

                    Directory.CreateDirectory(Path.Combine(root, "requirements")) |> ignore

                    File.WriteAllText(
                        Path.Combine(root, "requirements", "RQ-TEST-2026-A001--example.md"),
                        "---\nid: RQ-TEST-2026-A001\ntitle: Example\nstatus: accepted\n---\n"
                    )

                    Assert.isTrue (staleRegistries () |> List.contains "registries/requirements.json") "a requirement needs its registry") }

          // ---- integration boundary --------------------------------------------
          { Name = "integration: a handoff carries the producing actor across the boundary"
            Run = fun () ->
                let handoff =
                    Ros.Domain.Ordo.Projection.handoff
                        "abc"
                        "test"
                        []
                        []
                        []
                        []
                        []
                        []
                        []
                        []
                        { Resolution = None
                          Assessment = None
                          BasisCount = 0
                          SupersededResolutionIds = [] }

                let rendered = Ros.Contracts.Ordo.ObservationJson.renderHandoff (Some codex) handoff |> JsonNode.Parse
                Assert.equal "\"openai-codex\"" (rendered.["producedBy"].["id"].ToJsonString())
                Assert.equal "\"EXE-20260925T100000000Z-aaaaaaaa\"" (rendered.["producedBy"].["executionId"].ToJsonString())

                let anonymous = Ros.Contracts.Ordo.ObservationJson.renderHandoff None handoff |> JsonNode.Parse
                Assert.isTrue (isNull anonymous["producedBy"]) "producedBy is optional" } ]
