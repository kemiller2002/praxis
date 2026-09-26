namespace Ros.Tests

open System
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Artifacts
open Ros.Domain.Provenance
open Ros.Domain.Telemetry
open Ros.Infrastructure.Artifacts

/// Pure provenance behavior: actor identity, execution-keyed contributions,
/// accumulation, involvement, lineage, policy findings, and the canonical
/// JSON / front-matter serializations (including round-trip invariants).
[<RequireQualifiedAccess>]
module ProvenanceTests =
    let private claude = { IdentityInputs.empty with ClaudeCodeSessionId = Some "session-1" }
    let private codex = { IdentityInputs.empty with CodexSessionId = Some "codex-1"; RosTelemetryModel = Some "gpt-5-codex" }

    let private resolve inputs =
        match ActorResolution.resolve inputs with
        | Ok actor -> actor
        | Error message -> failwith message

    let private agent id provider model runtime =
        { Kind = ActorKind.Agent
          Id = id
          Provider = Some provider
          Model = Some model
          Runtime = Some runtime }

    let private human id =
        { Kind = ActorKind.Human
          Id = id
          Provider = None
          Model = None
          Runtime = None }

    let private claudeAgent = agent "anthropic/claude-code" "anthropic" "unknown" "claude-code"
    let private codexAgent = agent "openai/codex" "openai" "gpt-5-codex" "codex"

    let private contribution key operation at actor =
        { Key = key
          Operations = [ operation ]
          At = at
          Last = None
          Actor = actor
          Reason = None
          Evidence = [] }

    let private record item provenance =
        match ArtifactProvenance.record item provenance with
        | Ok updated -> updated
        | Error message -> failwith message

    let private build contributions =
        contributions |> List.fold (fun provenance item -> record item provenance) ArtifactProvenance.empty

    let private document (path: string) (text: string) =
        match FrontMatter.parse path text with
        | Ok document -> document
        | Error message -> failwith message

    let private requirementText (created: string) (updated: string) =
        $"---\nid: RQ-TEST-2026-A001\ntitle: Requirement\nstatus: draft\ncreated: {created}\nupdated: {updated}\n---\n\n# Body\n"

    let private write item text =
        match ProvenanceFrontMatter.writeContribution item text with
        | Ok updated -> updated
        | Error message -> failwith message

    let private parseProvenance text =
        match ArtifactProvenance.parse (document "research/requirements/RQ-TEST-2026-A001--r.md" text).Metadata with
        | Ok(Some provenance) -> provenance
        | Ok None -> failwith "expected provenance"
        | Error problems -> failwith $"{problems}"

    let private policy =
        { Enforced = true
          RequiredFrom = Some "2026-09-25"
          RequireOriginator = ProvenancePolicy.defaultRequireOriginator }

    let private validation documents executions =
        ProvenanceValidation.findings
            { Policy = policy
              Documents = documents
              Executions = executions
              KnownIdentifiers = documents |> List.map ArtifactDocument.identifier |> Set.ofList }

    let private exe1 = "EXE-20260925T100000000Z-aaaaaaaa"
    let private exe2 = "EXE-20260925T110000000Z-bbbbbbbb"
    let private exe3 = "EXE-20260925T120000000Z-cccccccc"

    let private withStatus severity (findings: ProvenanceFinding list) =
        findings |> List.filter (fun finding -> finding.Severity = severity)

    // ---- property helpers (deterministic pseudo-random generation) --------

    let private actors = [ claudeAgent; codexAgent; human "kevin"; { human "automation-bot" with Kind = ActorKind.Automation; Provider = Some "github"; Model = Some "unknown"; Runtime = Some "github-actions" } ]

    let private operations =
        [ ContributionOperation.Modified; ContributionOperation.Reviewed; ContributionOperation.Approved; ContributionOperation.Superseded ]

    let private randomFresh (random: Random) =
        let count = random.Next(1, 8)

        [ 0 .. count - 1 ]
        |> List.map (fun index ->
            let actor = actors[random.Next actors.Length]

            let key =
                if actor.Kind = ActorKind.Agent || random.Next 2 = 0 then
                    $"EXE-20260925T1{index:D2}000000Z-{random.Next(0x10000000, Int32.MaxValue):x8}"
                else
                    $"CTB-20260925T1{index:D2}000000Z-{random.Next(0x10000000, Int32.MaxValue):x8}"

            let operation = if index = 0 then ContributionOperation.Created else operations[random.Next operations.Length]

            { contribution key operation $"2026-09-25T1{index}:00:00.000Z" actor with
                Reason = (if random.Next 2 = 0 then Some $"reason {index}: with, punctuation \"quoted\" # hash" else None)
                Evidence = (if random.Next 2 = 0 then [ "EV-TEST-2026-A001" ] else []) })

    /// A random contribution history in which later entries sometimes reuse
    /// an earlier execution (same key and actor), exercising merges and
    /// `last` updates as well as new contributors.
    let private randomHistory (random: Random) =
        let fresh = randomFresh random

        fresh
        |> List.mapi (fun index item ->
            if index > 0 && random.Next 3 = 0 then
                let earlier = fresh[random.Next index]
                { item with Key = earlier.Key; Actor = earlier.Actor }
            else
                item)

    let tests =
        [ { Name = "actor kinds round-trip, accept namespaced extensions, and reject everything else"
            Run =
              fun () ->
                  for kind in [ ActorKind.Agent; ActorKind.Human; ActorKind.Automation; ActorKind.Unknown; ActorKind.Extension "x-review-board" ] do
                      Assert.equal (Some kind) (ActorKind.tryParse (ActorKind.code kind))

                  Assert.equal None (ActorKind.tryParse "robot")
                  Assert.equal None (ActorKind.tryParse "x-")
                  Assert.isTrue (ActorKind.resolve (Some "robot") "any" |> Result.isError) "an invalid explicit kind must be rejected, never coerced" }
          { Name = "agent identity is resolved from a known agent runtime without fabricating the model"
            Run =
              fun () ->
                  let actor = resolve claude
                  Assert.equal claudeAgent actor
                  Assert.equal (Some "unknown") actor.Model }
          { Name = "identity resolution is provider-neutral: codex, CI automation, local, and a future provider"
            Run =
              fun () ->
                  Assert.equal codexAgent (resolve codex)

                  Assert.equal
                      (agent "github/github-actions" "github" "unknown" "github-actions" |> fun actor -> { actor with Kind = ActorKind.Automation })
                      (resolve { IdentityInputs.empty with GitHubActions = true })

                  // A local model server existing proves nothing about who acts.
                  Assert.equal ActorKind.Unknown (resolve { IdentityInputs.empty with OllamaHost = Some "http://localhost:11434" }).Kind

                  let future =
                      resolve
                          { IdentityInputs.empty with
                              Provider = Some "future-ai"
                              Runtime = Some "future-cli"
                              Model = Some "fx-1"
                              ActorKind = Some "agent" }

                  Assert.equal (agent "future-ai/future-cli" "future-ai" "fx-1" "future-cli") future }
          { Name = "explicit declarations win: stable agent id, human kind, and environment actor kind"
            Run =
              fun () ->
                  Assert.equal "reviewer-bot" (resolve { claude with AgentId = Some "reviewer-bot" }).Id
                  Assert.equal (human "kevin") (resolve { claude with ActorKind = Some "human"; RosActor = Some "kevin" })
                  Assert.equal ActorKind.Human (resolve { IdentityInputs.empty with RosActorKind = Some "human" }).Kind
                  Assert.equal (Some "agent") (ActorResolution.explicitKind { IdentityInputs.empty with ActorKind = Some "agent"; RosActorKind = Some "human" }) }
          { Name = "nothing known resolves to an explicit unknown actor, never a guess"
            Run = fun () -> Assert.equal Actor.unknown (resolve IdentityInputs.empty) }
          { Name = "legacy execution records without actorKind project from their recorded discovery mechanism"
            Run =
              fun () ->
                  let identity, _ = Identity.discover claude
                  Assert.equal claudeAgent (ActorResolution.ofExecutionRecord None (Some "whitelisted-claude-environment") identity)
                  Assert.equal ActorKind.Unknown (ActorResolution.ofExecutionRecord None None identity).Kind
                  Assert.equal ActorKind.Human (ActorResolution.ofExecutionRecord (Some "human") (Some "whitelisted-claude-environment") identity).Kind }
          { Name = "a process with no identity is undeclared; a session or CI run mismatch is a different run"
            Run =
              fun () ->
                  Assert.equal false (ActorResolution.isDeclared Actor.unknown)
                  Assert.equal true (ActorResolution.isDeclared { Actor.unknown with Id = "kevin" })
                  Assert.equal true (ActorResolution.isDeclared claudeAgent)
                  let identityA, _ = Identity.discover { claude with SessionId = Some "A" }
                  let identityB, _ = Identity.discover { claude with SessionId = Some "B" }
                  let noSession, _ = Identity.discover { IdentityInputs.empty with Provider = Some "anthropic"; Runtime = Some "claude-code" }
                  Assert.equal true (ActorResolution.sameRun identityA identityA)
                  Assert.equal false (ActorResolution.sameRun identityA identityB)
                  Assert.equal true (ActorResolution.sameRun noSession identityA)
                  let threadA, _ = Identity.discover { IdentityInputs.empty with CodexThreadId = Some "t-A" }
                  let threadB, _ = Identity.discover { IdentityInputs.empty with CodexThreadId = Some "t-B" }
                  Assert.equal false (ActorResolution.sameRun threadA threadB)

                  // Declaring only a kind agrees with every agent but is no
                  // evidence of being a particular run.
                  let kindOnlyIdentity, _ = Identity.discover { IdentityInputs.empty with ActorKind = Some "agent" }
                  let kindOnly = resolve { IdentityInputs.empty with ActorKind = Some "agent" }
                  let noSessionExecution, _ = Identity.discover claude
                  let noSessionExecution = { noSessionExecution with SessionId = None }
                  Assert.equal true (Actor.agrees kindOnly claudeAgent)
                  Assert.equal false (ActorResolution.evidentlySameRun kindOnly kindOnlyIdentity claudeAgent noSessionExecution)
                  Assert.equal true (ActorResolution.evidentlySameRun claudeAgent identityA claudeAgent identityA)
                  Assert.equal true (ActorResolution.evidentlySameRun claudeAgent noSession claudeAgent noSessionExecution)

                  let runOne, _ = Identity.discover { IdentityInputs.empty with GitHubActions = true; GitHubRunId = Some "1" }
                  let runTwo, _ = Identity.discover { IdentityInputs.empty with GitHubActions = true; GitHubRunId = Some "2" }
                  Assert.equal false (ActorResolution.sameRun runOne runTwo) }
          { Name = "two executions of the same agent share a stable identity but remain distinct contributions"
            Run =
              fun () ->
                  let provenance =
                      build
                          [ contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent
                            contribution exe2 ContributionOperation.Modified "2026-09-25T11:00:00.000Z" claudeAgent ]

                  Assert.equal [ exe1; exe2 ] (ArtifactProvenance.executions provenance)
                  Assert.equal [ claudeAgent ] (ArtifactProvenance.contributors provenance) }
          { Name = "requirement creation records its originator and a later modifier never replaces it"
            Run =
              fun () ->
                  let provenance =
                      build
                          [ contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent
                            contribution exe2 ContributionOperation.Modified "2026-09-25T11:00:00.000Z" codexAgent ]

                  Assert.equal (Some exe1) (ArtifactProvenance.originator provenance |> Option.map _.Key)
                  Assert.equal (Some exe2) (ArtifactProvenance.latest provenance |> Option.map _.Key)
                  Assert.equal claudeAgent (ArtifactProvenance.originator provenance).Value.Actor }
          { Name = "a second creation, or a creation after existing history, is refused"
            Run =
              fun () ->
                  let created = build [ contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent ]
                  Assert.isTrue (ArtifactProvenance.record (contribution exe2 ContributionOperation.Created "2026-09-25T11:00:00.000Z" codexAgent) created |> Result.isError) "second creation"

                  let modifiedOnly = build [ contribution exe1 ContributionOperation.Modified "2026-09-25T10:00:00.000Z" claudeAgent ]
                  Assert.isTrue (ArtifactProvenance.record (contribution exe2 ContributionOperation.Created "2026-09-25T11:00:00.000Z" codexAgent) modifiedOnly |> Result.isError) "late creation" }
          { Name = "re-recording within one execution merges operations, tracks the latest one, and is idempotent for an identical call"
            Run =
              fun () ->
                  let created = build [ contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent ]
                  let again = record (contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent) created
                  Assert.equal created again

                  let merged = record (contribution exe1 ContributionOperation.Modified "2026-09-25T10:30:00.000Z" claudeAgent) created
                  let entry = merged.Contributions |> List.exactlyOne
                  Assert.equal [ ContributionOperation.Created; ContributionOperation.Modified ] entry.Operations
                  Assert.equal "2026-09-25T10:00:00.000Z" entry.At
                  Assert.equal (Some "2026-09-25T10:30:00.000Z") entry.Last

                  // A later modification by the same execution stays visible.
                  let later = record (contribution exe1 ContributionOperation.Modified "2026-09-25T10:45:00.000Z" claudeAgent) merged
                  Assert.equal (Some "2026-09-25T10:45:00.000Z") (later.Contributions |> List.exactlyOne).Last
                  Assert.isTrue (later <> merged) "a later recorded modification must change provenance"

                  // An out-of-order (earlier) recording never moves `last` backwards.
                  let earlier = record (contribution exe1 ContributionOperation.Modified "2026-09-25T10:20:00.000Z" claudeAgent) later
                  Assert.equal later earlier

                  Assert.isTrue
                      (ArtifactProvenance.record (contribution exe1 ContributionOperation.Modified "2026-09-25T10:40:00.000Z" codexAgent) created |> Result.isError)
                      "an execution can never be re-attributed to another actor" }
          { Name = "structural problems: agent without execution, bad timestamp, agent without model field, duplicate creation"
            Run =
              fun () ->
                  let problems =
                      ArtifactProvenance.problems
                          { Contributions =
                              [ contribution "CTB-1" ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent
                                contribution exe2 ContributionOperation.Created "yesterday" { claudeAgent with Model = None } ] }

                  let messages = problems |> List.map _.Message |> String.concat "\n"
                  Assert.isTrue (messages.Contains "keyed by the execution") messages
                  Assert.isTrue (messages.Contains "ISO-8601") messages
                  Assert.isTrue (messages.Contains "must record model") messages
                  Assert.isTrue (messages.Contains "more than one contribution claims 'created'") messages }
          { Name = "unknown provider and model are representable and valid"
            Run =
              fun () ->
                  let unknownAgent = { Actor.unknown with Kind = ActorKind.Agent }
                  Assert.empty (Actor.problems unknownAgent)
                  Assert.empty (ArtifactProvenance.problems (build [ contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" unknownAgent ])) }
          { Name = "involvement distinguishes agent-created, human-created, approvals, and agent-to-agent revision"
            Run =
              fun () ->
                  let agentCreatedHumanApproved =
                      build
                          [ contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent
                            contribution "CTB-20260925T110000000Z-00000001" ContributionOperation.Approved "2026-09-25T11:00:00.000Z" (human "kevin") ]
                      |> Involvement.describe

                  Assert.equal "agent-created, human-approved" agentCreatedHumanApproved.Label
                  Assert.equal false agentCreatedHumanApproved.HumanCorrectionOfAgentWork

                  let humanCreatedAgentModified =
                      build
                          [ contribution "CTB-20260925T090000000Z-00000001" ContributionOperation.Created "2026-09-25T09:00:00.000Z" (human "kevin")
                            contribution exe1 ContributionOperation.Modified "2026-09-25T10:00:00.000Z" claudeAgent ]
                      |> Involvement.describe

                  Assert.equal "human-created, agent-modified" humanCreatedAgentModified.Label
                  Assert.equal (Some(human "kevin")) humanCreatedAgentModified.Origin

                  let agentToAgent =
                      build
                          [ contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent
                            contribution exe2 ContributionOperation.Modified "2026-09-25T11:00:00.000Z" codexAgent
                            contribution "CTB-20260925T120000000Z-00000001" ContributionOperation.Modified "2026-09-25T12:00:00.000Z" (human "kevin") ]
                      |> Involvement.describe

                  Assert.equal "agent-created, agent+human-modified" agentToAgent.Label
                  Assert.equal true agentToAgent.AgentToAgentRevision
                  Assert.equal true agentToAgent.HumanCorrectionOfAgentWork

                  Assert.equal "unattributed" (Involvement.describe ArtifactProvenance.empty).Label

                  Assert.equal
                      "origin-unknown, agent-modified"
                      (build [ contribution exe1 ContributionOperation.Modified "2026-09-25T10:00:00.000Z" claudeAgent ] |> Involvement.describe).Label }
          { Name = "lineage is separate from authorship: a derived artifact names its source, not the source's author"
            Run =
              fun () ->
                  let source = document "research/evidence/EV-TEST-2026-A001--a.md" "---\nid: EV-TEST-2026-A001\ntitle: Source\n---\n"

                  let derived =
                      document
                          "research/requirements/RQ-TEST-2026-A001--b.md"
                          "---\nid: RQ-TEST-2026-A001\ntitle: Derived\nderived_from: [EV-TEST-2026-A001, ordo:resolution/r-17]\n---\n"

                  Assert.equal [ "EV-TEST-2026-A001"; "ordo:resolution/r-17" ] (Lineage.sources derived)
                  Assert.equal [ "RQ-TEST-2026-A001" ] (Lineage.derivatives "EV-TEST-2026-A001" [ source; derived ])
                  Assert.equal [] (Lineage.sources source) }
          { Name = "actor JSON is canonical, omits not-applicable fields for humans, and round-trips"
            Run =
              fun () ->
                  Assert.equal
                      """{"kind":"agent","id":"anthropic/claude-code","provider":"anthropic","model":"unknown","runtime":"claude-code"}"""
                      ((ActorJson.node claudeAgent).ToJsonString())

                  Assert.equal """{"kind":"human","id":"kevin"}""" ((ActorJson.node (human "kevin")).ToJsonString())

                  for actor in actors do
                      Assert.equal (Ok(Some actor)) (ActorJson.tryParse (ActorJson.node actor))

                  Assert.equal (Ok None) (ActorJson.tryParse null)
                  Assert.isTrue (ActorJson.tryParse (JsonNode.Parse """{"kind":"robot"}""") |> Result.isError) "unknown kind" }
          { Name = "front-matter writer creates the provenance block without touching any other byte"
            Run =
              fun () ->
                  let original = requirementText "2026-09-25" "2026-09-25"
                  let item = { contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent with Reason = Some "Initial: capture, [draft]" }
                  let updated = write item original
                  Assert.isTrue (updated.EndsWith "---\n\n# Body\n") "body preserved"
                  Assert.isTrue (updated.StartsWith "---\nid: RQ-TEST-2026-A001\ntitle: Requirement\nstatus: draft\ncreated: 2026-09-25\nupdated: 2026-09-25\nprovenance:\n") "fields preserved"
                  Assert.equal (build [ item ]) (parseProvenance updated) }
          { Name = "front-matter writer appends a second contributor and never rewrites the first contributor's entry"
            Run =
              fun () ->
                  let first = contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent

                  // A field this version does not model (for example a future
                  // attestation) must survive another agent's contribution.
                  let withExtension =
                      (write first (requirementText "2026-09-25" "2026-09-25"))
                          .Replace("        runtime: claude-code\n", "        runtime: claude-code\n      attestation: sigstore-bundle-123\n")

                  let second = contribution exe2 ContributionOperation.Modified "2026-09-25T11:00:00.000Z" codexAgent
                  let updated = write second withExtension
                  Assert.isTrue (updated.Contains "      attestation: sigstore-bundle-123\n") "unmodeled field preserved"
                  Assert.equal (build [ first; second ]) (parseProvenance updated) }
          { Name = "front-matter writer merges into an existing entry, handles CRLF and block-style lists"
            Run =
              fun () ->
                  let first = { contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent with Evidence = [ "EV-A" ] }
                  let crlf = (write first (requirementText "2026-09-25" "2026-09-25")).Replace("\n", "\r\n")
                  let blockStyle = crlf.Replace("evidence: [EV-A]", "evidence:\r\n        - EV-A")

                  let merged =
                      match ArtifactProvenance.record { contribution exe1 ContributionOperation.Modified "2026-09-25T10:10:00.000Z" claudeAgent with Evidence = [ "EV-B" ] } (build [ first ]) with
                      | Ok provenance -> provenance
                      | Error message -> failwith message

                  let updated = write (merged.Contributions |> List.exactlyOne) blockStyle
                  Assert.isTrue (not (updated.Replace("\r\n", "").Contains "\n")) "line endings preserved"
                  Assert.equal merged (parseProvenance (updated.Replace("\r\n", "\n"))) }
          { Name = "derived_from lineage merges into inline, scalar, block, and absent fields"
            Run =
              fun () ->
                  let add references (text: string) =
                      let existing = Lineage.sources (document "research/requirements/RQ-TEST-2026-A001--r.md" text)

                      match ProvenanceFrontMatter.addDerivedFrom existing references text with
                      | Ok updated -> Lineage.sources (document "research/requirements/RQ-TEST-2026-A001--r.md" updated)
                      | Error message -> failwith message

                  let baseText = requirementText "2026-09-25" "2026-09-25"
                  Assert.equal [ "EV-A" ] (add [ "EV-A" ] baseText)
                  Assert.equal [ "EV-A"; "EV-B" ] (add [ "EV-B"; "EV-A" ] (baseText.Replace("updated:", "derived_from: [EV-A]\nupdated:")))
                  Assert.equal [ "EV-A"; "EV-B" ] (add [ "EV-B" ] (baseText.Replace("updated:", "derived_from: EV-A\nupdated:")))
                  Assert.equal [ "EV-A"; "EV-B" ] (add [ "EV-B" ] (baseText.Replace("updated:", "derived_from:\n  - EV-A\nupdated:")))

                  // A value the reader sees as one source (quoted, with a
                  // comma) is never split; rewriting it is refused instead.
                  let quoted = baseText.Replace("updated:", "derived_from: \"EV-A, rev 2\"\nupdated:")
                  Assert.equal [ "EV-A, rev 2" ] (Lineage.sources (document "research/requirements/RQ-TEST-2026-A001--r.md" quoted))
                  Assert.isTrue (ProvenanceFrontMatter.addDerivedFrom [ "EV-A, rev 2" ] [ "EV-B" ] quoted |> Result.isError) "unsafe lineage must be refused" }
          { Name = "front-matter writer preserves the document body byte for byte, including mixed line endings"
            Run =
              fun () ->
                  let original = (requirementText "2026-09-25" "2026-09-25").Replace("# Body\n", "# Body\r\nsecond line\n")
                  let updated = write (contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent) original
                  Assert.equal (ProvenanceFrontMatter.bodyOf original) (ProvenanceFrontMatter.bodyOf updated)
                  Assert.equal 1 (updated.Split('\r').Length - 1) }
          { Name = "policy: new artifacts must be attributed, requirements must name their originator"
            Run =
              fun () ->
                  let unattributed = document "research/requirements/RQ-TEST-2026-A001--r.md" (requirementText "2026-09-25" "2026-09-25")
                  let errors = validation [ unattributed ] Map.empty |> withStatus FindingSeverity.Error
                  Assert.isTrue ((Assert.single errors).Message.Contains "records no provenance") "missing provenance is an error"

                  let modifiedOnly =
                      write (contribution exe1 ContributionOperation.Modified "2026-09-25T10:00:00.000Z" claudeAgent) (requirementText "2026-09-25" "2026-09-25")
                      |> document "research/requirements/RQ-TEST-2026-A001--r.md"

                  let findings = validation [ modifiedOnly ] (Map.ofList [ exe1, claudeAgent ])
                  Assert.isTrue ((Assert.single (withStatus FindingSeverity.Error findings)).Message.Contains "originator is unknown") "RQ requires an originator"

                  let evidenceText =
                      write (contribution exe1 ContributionOperation.Modified "2026-09-25T10:00:00.000Z" claudeAgent) ("---\nid: EV-TEST-2026-A009\ntitle: E\ncreated: 2026-09-25\n---\n")
                      |> document "research/evidence/EV-TEST-2026-A009--e.md"

                  Assert.equal 1 (validation [ evidenceText ] (Map.ofList [ exe1, claudeAgent ]) |> withStatus FindingSeverity.Warning).Length }
          { Name = "policy: legacy artifacts stay valid, are reported honestly, and later edits are flagged"
            Run =
              fun () ->
                  let legacy =
                      document
                          "research/requirements/RQ-TEST-2026-A002--l.md"
                          "---\nid: RQ-TEST-2026-A002\ntitle: Legacy\nauthor_agent: openai-codex\ncreated: 2026-01-02\nupdated: 2026-02-01\n---\n"

                  let findings = validation [ legacy ] Map.empty
                  Assert.empty (withStatus FindingSeverity.Error findings)
                  Assert.empty (withStatus FindingSeverity.Warning findings)
                  let info = Assert.single (withStatus FindingSeverity.Info findings)
                  Assert.isTrue (info.Message.Contains "author_agent=openai-codex" && info.Message.Contains "unverified") info.Message

                  let editedLegacy =
                      document
                          "research/requirements/RQ-TEST-2026-A002--l.md"
                          "---\nid: RQ-TEST-2026-A002\ntitle: Legacy\ncreated: 2026-01-02\nupdated: 2026-09-26\n---\n"

                  Assert.equal 1 (validation [ editedLegacy ] Map.empty |> withStatus FindingSeverity.Warning).Length

                  let notConfigured =
                      ProvenanceValidation.findings
                          { Policy = ProvenancePolicy.notConfigured
                            Documents = [ document "research/requirements/RQ-TEST-2026-A001--r.md" (requirementText "2026-09-25" "2026-09-25") ]
                            Executions = Map.empty
                            KnownIdentifiers = Set.empty }

                  Assert.empty notConfigured }
          { Name = "policy: an unattributed later modification of a new artifact is an error"
            Run =
              fun () ->
                  let stale =
                      write (contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent) (requirementText "2026-09-25" "2026-09-27")
                      |> document "research/requirements/RQ-TEST-2026-A001--r.md"

                  let error = Assert.single (validation [ stale ] (Map.ofList [ exe1, claudeAgent ]) |> withStatus FindingSeverity.Error)
                  Assert.equal "updated" error.Field

                  // A local `updated` date one day ahead of a UTC contribution
                  // is time-zone skew (e.g. UTC+10 morning), not a change.
                  let eastOfUtc =
                      write (contribution exe1 ContributionOperation.Created "2026-09-25T22:30:00.000Z" claudeAgent) (requirementText "2026-09-26" "2026-09-26")
                      |> document "research/requirements/RQ-TEST-2026-A001--r.md"

                  Assert.empty (validation [ eastOfUtc ] (Map.ofList [ exe1, claudeAgent ]) |> withStatus FindingSeverity.Error) }
          { Name = "policy: execution cross-check rejects impersonation and warns on unknown executions and broken evidence"
            Run =
              fun () ->
                  let item = { contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent with Evidence = [ "EV-TEST-2026-A404" ] }
                  let artifact = write item (requirementText "2026-09-25" "2026-09-25") |> document "research/requirements/RQ-TEST-2026-A001--r.md"

                  let impersonation = validation [ artifact ] (Map.ofList [ exe1, codexAgent ]) |> withStatus FindingSeverity.Error
                  Assert.isTrue (impersonation |> List.exists (fun finding -> finding.Message.Contains "contradicts execution")) "impersonation"
                  Assert.isTrue (impersonation |> List.exists (fun finding -> finding.Message.Contains "broken reference 'EV-TEST-2026-A404'")) "broken evidence"

                  let unknownExecution = validation [ artifact ] Map.empty |> withStatus FindingSeverity.Warning
                  Assert.isTrue ((Assert.single unknownExecution).Message.Contains "has no record") "missing execution" }
          { Name = "malformed provenance is an error, never silently treated as absent"
            Run =
              fun () ->
                  let malformed =
                      document
                          "research/requirements/RQ-TEST-2026-A001--r.md"
                          "---\nid: RQ-TEST-2026-A001\ntitle: R\ncreated: 2026-01-01\nprovenance:\n  contributions:\n    EXE-1:\n      operations: [invented]\n      at: 2026-09-25T10:00:00Z\n      actor:\n        kind: robot\n---\n"

                  let errors = validation [ malformed ] Map.empty |> withStatus FindingSeverity.Error
                  Assert.isTrue (errors.Length >= 2) $"{errors}" }
          { Name = "change detection: content changed since base without a new contribution is unattributed"
            Run =
              fun () ->
                  let attributed =
                      write (contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent) (requirementText "2026-09-25" "2026-09-25")

                  let before = (document "research/requirements/RQ-TEST-2026-A001--r.md" attributed).Metadata
                  let editedOnly = document "research/requirements/RQ-TEST-2026-A001--r.md" (attributed.Replace("title: Requirement", "title: Requirement (edited)"))

                  let recorded =
                      write (contribution exe2 ContributionOperation.Modified "2026-09-25T11:00:00.000Z" codexAgent) (attributed.Replace("title: Requirement", "title: Requirement (edited)"))
                      |> document "research/requirements/RQ-TEST-2026-A001--r.md"

                  let unattributed = ProvenanceChanges.findings policy [ { Document = editedOnly; Before = Some before } ]
                  Assert.equal FindingSeverity.Error (Assert.single unattributed).Severity
                  Assert.empty (ProvenanceChanges.findings policy [ { Document = recorded; Before = Some before } ])
                  Assert.empty (ProvenanceChanges.findings policy [ { Document = editedOnly; Before = None } ])
                  Assert.empty (ProvenanceChanges.findings ProvenancePolicy.notConfigured [ { Document = editedOnly; Before = Some before } ])

                  let legacyBefore = (document "research/evidence/EV-TEST-2026-A001--l.md" "---\nid: EV-TEST-2026-A001\ntitle: L\ncreated: 2026-01-01\n---\n").Metadata
                  let legacyAfter = document "research/evidence/EV-TEST-2026-A001--l.md" "---\nid: EV-TEST-2026-A001\ntitle: L2\ncreated: 2026-01-01\n---\n"
                  Assert.equal FindingSeverity.Warning (Assert.single (ProvenanceChanges.findings policy [ { Document = legacyAfter; Before = Some legacyBefore } ])).Severity }
          { Name = "events: legacy events without actors are informational; malformed actors are errors"
            Run =
              fun () ->
                  let view actor executions =
                      { EventId = "e1"
                        EventType = "work.started"
                        OccurredAt = "2026-09-25T10:00:00Z"
                        TelemetryExecutionIds = executions
                        Actor = actor }

                  Assert.equal FindingSeverity.Info (Assert.single (EventProvenance.findings [ view (Ok None) [ exe1 ] ])).Severity
                  Assert.empty (EventProvenance.findings [ view (Ok None) [] ])
                  Assert.empty (EventProvenance.findings [ view (Ok(Some claudeAgent)) [ exe1 ] ])
                  Assert.equal FindingSeverity.Error (Assert.single (EventProvenance.findings [ view (Error "bad") [] ])).Severity }
          { Name = "metrics index: contributions by actor across artifacts"
            Run =
              fun () ->
                  let first =
                      write (contribution exe1 ContributionOperation.Created "2026-09-25T10:00:00.000Z" claudeAgent) (requirementText "2026-09-25" "2026-09-25")
                      |> write (contribution exe2 ContributionOperation.Modified "2026-09-25T11:00:00.000Z" codexAgent)
                      |> document "research/requirements/RQ-TEST-2026-A001--r.md"

                  let facts = ProvenanceIndex.facts [ first ]
                  Assert.equal 2 facts.Length
                  let summaries = ProvenanceIndex.byActor facts
                  let claudeSummary = summaries |> List.find (fun item -> item.Actor = claudeAgent)
                  Assert.equal (1, 0, 1) (claudeSummary.Created, claudeSummary.Modified, claudeSummary.Executions)
                  let codexSummary = summaries |> List.find (fun item -> item.Actor = codexAgent)
                  Assert.equal (0, 1) (codexSummary.Created, codexSummary.Modified) }
          { Name = "property: recording never loses, reorders, or re-attributes earlier contributions"
            Run =
              fun () ->
                  for seed in 1..200 do
                      let random = Random seed
                      let history = randomHistory random

                      history
                      |> List.fold
                          (fun (provenance: ArtifactProvenance) item ->
                              match ArtifactProvenance.record item provenance with
                              | Error _ -> provenance
                              | Ok updated ->
                                  for earlier in provenance.Contributions do
                                      let after = updated.Contributions |> List.find (fun candidate -> candidate.Key = earlier.Key)
                                      Assert.equal earlier.Actor after.Actor
                                      Assert.equal earlier.At after.At
                                      Assert.isTrue (earlier.Operations |> List.forall (fun operation -> List.contains operation after.Operations)) $"seed {seed}: operations lost"

                                  Assert.equal
                                      (ArtifactProvenance.originator provenance |> Option.map _.Key |> Option.orElse (ArtifactProvenance.originator updated |> Option.map _.Key))
                                      (ArtifactProvenance.originator updated |> Option.map _.Key)

                                  Assert.equal (updated.Contributions |> List.map _.Key |> List.distinct).Length updated.Contributions.Length
                                  updated)
                          ArtifactProvenance.empty
                      |> ignore }
          { Name = "property: every recorded history serializes to front matter and reads back identically"
            Run =
              fun () ->
                  for seed in 1..200 do
                      let random = Random seed

                      let text, expected =
                          randomHistory random
                          |> List.fold
                              (fun (text, provenance) item ->
                                  match ArtifactProvenance.record item provenance with
                                  | Error _ -> text, provenance
                                  | Ok updated ->
                                      let merged = updated.Contributions |> List.find (fun candidate -> candidate.Key = item.Key)
                                      write merged text, updated)
                              (requirementText "2026-09-25" "2026-09-25", ArtifactProvenance.empty)

                      Assert.equal expected (parseProvenance text)
                      Assert.isTrue (text.EndsWith "---\n\n# Body\n") $"seed {seed}: body changed"
                      Assert.empty (ArtifactProvenance.problems expected) } ]
