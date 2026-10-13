namespace Praxis.Tests

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open Ordo.Core
open Praxis.Infrastructure.Construction

/// CONSTRUCT-008 mechanical-first construction: grant-bound decision records,
/// `construct prepare` / `construct apply --prepared`, the minimal handoff,
/// capability-scoped extension application, the reuse cache and telemetry.
/// Generation runs through a fake default-plan generator injected at the
/// single seam (it returns Ordo's real timer plan), so everything else is
/// the production path over a real repository, grant and Ordo engine.
[<RequireQualifiedAccess>]
module ConstructionPreparedTests =
    open PraxisCli

    let private t name run = { Name = "construction prepared: " + name; Run = run }
    let private group = ConstructionCliTests.group
    let private target = ConstructionCliTests.target
    let private fixtures = ConstructionCliTests.fixtures
    let private fixture name = File.ReadAllText(Path.Combine(fixtures, name))

    let private k116 = "kemiller2002/time-entry-state-machine/docs/architecture/business-activity-ledger-state-model.md#L116-L129"
    let private k131 = "kemiller2002/time-entry-state-machine/docs/architecture/business-activity-ledger-state-model.md#L131-L144"
    let private k105 = "kemiller2002/time-entry-state-machine/requirements-reconstruction/fsharp-business-requirements-beyond-csharp.md#L105-L107"
    /// Ordo's canonical digest of the fixture blueprint (the plan binds it).
    let private canonicalBlueprint = "sha256:28dd3964f9c49f3492f3c34174ab04d57f4ddcba298276b2a287b4a6f3fdc4cb"

    let private decisionsFor (cohort: string) (onFailure: string) =
        sprintf """{"schemaVersion":"ordo.construction-decisions/1","blueprintDigest":"%s","cohortId":"%s","records":[
  {"nodeId":"DEC-TIMER-STATES","content":{"kind":"stateModel","workflow":"Timer",
    "states":[{"name":"NoTimer","role":"initial"},{"name":"Running","role":"ordinary"},{"name":"Paused","role":"ordinary"}],
    "transitions":[{"command":"Start","from":"NoTimer","to":"Running","requirementKeys":["%s"]},
                   {"command":"Pause","from":"Running","to":"Paused","requirementKeys":["%s"]}]}},
  {"nodeId":"IFC-TIMER-PORTS","content":{"kind":"portContract","ports":[{"port":"currentTime","requirementKeys":["%s"]},
    {"port":"transitionEffect","command":"Stop","onFailure":%s,"requirementKeys":["%s"]}]}}]}
"""         canonicalBlueprint cohort k116 k116 k105 onFailure k131

    let private decisions = decisionsFor "COH-TIMER" "null"

    let private obligations : DefaultPlanObligation list =
        [ { Id = "OBL-COH-TIMER-extensionBehavior-1a2b3c"; Kind = "extensionBehavior"; RequirementKeys = [ k131; k105 ]
            NodeIds = [ "VER-TIMER-COMPLETION" ]
            Statement = "Compute the completed entry's duration from the timer segments, excluding paused time."
            ExtensionSurface = [ "ITimerPorts.Perform/Stop" ] }
          { Id = "OBL-COH-TIMER-undecidedEffectFailure-4d5e6f"; Kind = "undecidedEffectFailure"; RequirementKeys = [ k131 ]
            NodeIds = [ "IFC-TIMER-PORTS" ]
            Statement = "When the Stop completion effect fails, is the timer discarded or kept active?"
            ExtensionSurface = [ "ITimerPorts.Perform/Stop" ] } ]

    /// A fake Ordo default-plan generator: Ordo's real timer plan, the real
    /// constructor's layout, and typed obligations. Counts its calls.
    let private fake (calls: int ref) (version: string) : DefaultPlanGenerator =
        { Id = OrdoDefaultPlan.GeneratorId
          Version = version
          Generate =
            fun prepared decisionText vocabulary ->
                calls.Value <- calls.Value + 1
                let planText = fixture "plan.json"
                match ConstructionPlanWire.readPlan planText with
                | Error reason -> Error [ reason ]
                | Ok draft ->
                    match ConstructionPlan.validate prepared draft with
                    | Error violations -> Error [ sprintf "%A" violations ]
                    | Ok validated ->
                        match Ordo.Construction.Construction.construct validated with
                        | Error refusals -> Error [ sprintf "%A" refusals ]
                        | Ok built ->
                            Ok { PlanText = planText
                                 PlanDigest = ConstructionPlan.digest validated
                                 Obligations = obligations
                                 GeneratedLayout = built.Artifacts |> List.map _.Path
                                 Provenance =
                                   { GeneratorId = OrdoDefaultPlan.GeneratorId; GeneratorVersion = version; VocabularyVersion = vocabulary
                                     InputDigests = [ "decisionRecords", ConstructionDecisions.digest decisionText ]
                                     DefaultPlanDigest = "sha256:" + String('0', 64) } } }

    let private excerpts (clone: string) =
        CliHarness.write clone "requirements/timer-stop-completion.txt" (fixture "requirements/timer-stop-completion.txt")
        CliHarness.write clone "requirements/timer-paused-time-excluded.txt" (fixture "requirements/timer-paused-time-excluded.txt")

    let private decisionsPath (host: ConstructionCliTests.Host) = Path.Combine(host.Parent, "decisions.json")

    /// A host whose signed grant binds `decisions` by digest.
    let private withDecisionsHost test =
        ConstructionCliTests.withHostGranting
            (fun parent _ ->
                let path = Path.Combine(parent, "decisions.json")
                File.WriteAllText(path, decisions)
                [ "--decisions"; path ])
            true excerpts test

    let private prepareWith generator (host: ConstructionCliTests.Host) (decisionsFile: string option) (telemetry: string option) =
        let out = Path.Combine(host.Parent, "prepared.json")
        ConstructionService.prepareWith generator host.Clone host.Config group None None decisionsFile out telemetry |> Result.map (fun node -> node, out)

    let private applyWith generator (host: ConstructionCliTests.Host) (prepared: string) (extensions: string option) =
        ConstructionService.applyPreparedWith generator host.Clone host.Config group prepared target extensions None None

    let private okValue result = match result with Ok value -> value | Error (reason: string) -> failwith reason
    let private refusedWith (fragment: string) result =
        match result with
        | Ok _ -> failwith ("accepted; expected refusal containing: " + fragment)
        | Error (reason: string) -> Assert.isTrue (reason.Contains fragment) reason

    let private str (node: JsonNode) = node.GetValue<string>()
    let private changed (host: ConstructionCliTests.Host) before after =
        GitFixture.git host.Clone [ "diff"; "--name-only"; before; after ] |> fun text -> text.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.map _.Trim() |> Set.ofArray

    let private refusals (host: ConstructionCliTests.Host) =
        let directory = Path.Combine(host.Journal, "refusals")
        if Directory.Exists directory then Directory.GetFiles directory |> Array.toList else []

    // ------------------------------------------------------------ (a) grant

    let private grantTests =
        [ t "grant-draft --decisions binds the exact decision-record digest into the signed scope" (fun () ->
              withDecisionsHost (fun host ->
                  let draft = JsonNode.Parse(File.ReadAllText(Path.Combine(host.Parent, "grant.json")))
                  Assert.equal (ConstructionDecisions.digest decisions) (str draft["approval"].["scope"].["constructionDecisionsDigest"])
                  let calls = ref 0
                  prepareWith (fake calls "fake@1") host (Some(decisionsPath host)) None |> okValue |> ignore
                  Assert.equal 1 calls.Value))
          t "admission refuses decision records whose digest differs from the signed grant" (fun () ->
              withDecisionsHost (fun host ->
                  let edited = Path.Combine(host.Parent, "decisions-edited.json")
                  File.WriteAllText(edited, decisionsFor "COH-TIMER" "\"discard\"")
                  let calls = ref 0
                  prepareWith (fake calls "fake@1") host (Some edited) None |> refusedWith "differ from the digest bound by the signed grant"
                  Assert.equal 0 calls.Value
                  // Not even a whitespace change passes.
                  File.WriteAllText(edited, decisions + " ")
                  prepareWith (fake calls "fake@1") host (Some edited) None |> refusedWith "differ from the digest bound by the signed grant"
                  Assert.equal 0 calls.Value))
          t "a grant without decisions still constructs from a plan (B arm), but prepare refuses it" (fun () ->
              ConstructionCliTests.withHost true excerpts (fun host ->
                  let applied = ConstructionCliTests.construct host "apply" host.Plan |> ok
                  Assert.equal "applied" (text applied.Json["outcome"])
                  let draft = JsonNode.Parse(File.ReadAllText(Path.Combine(host.Parent, "grant.json")))
                  Assert.isTrue (isNull draft["approval"].["scope"].["constructionDecisionsDigest"]) "unexpected decisions digest"
                  File.WriteAllText(decisionsPath host, decisions)
                  prepareWith (fake (ref 0) "fake@1") host (Some(decisionsPath host)) None |> refusedWith "binds no construction decisions"))
          t "a decisions-bound grant also keeps the plan-driven apply path working" (fun () ->
              withDecisionsHost (fun host ->
                  Assert.equal "applied" (text (ConstructionCliTests.construct host "apply" host.Plan |> ok).Json["outcome"])))
          t "grant-draft refuses decision records for another cohort" (fun () ->
              let mutable result = None
              try
                  ConstructionCliTests.withHostGranting
                      (fun parent _ ->
                          let path = Path.Combine(parent, "decisions.json")
                          File.WriteAllText(path, decisionsFor "COH-OTHER" "null")
                          [ "--decisions"; path ])
                      true excerpts ignore
              with error -> result <- Some error.Message
              Assert.isTrue (result |> Option.exists (fun message -> message.Contains "COH-OTHER")) (sprintf "%A" result)) ]

    // ---------------------------------------------------- (b) prepare/handoff

    let private prepareTests =
        [ t "prepare writes the prepared artifact and a minimal handoff with only cited excerpts, decisions, signatures and questions" (fun () ->
              withDecisionsHost (fun host ->
                  let before = ConstructionCliTests.tip host
                  let node, out = prepareWith (fake (ref 0) "fake@1") host (Some(decisionsPath host)) None |> okValue
                  Assert.equal before (ConstructionCliTests.tip host)
                  let prepared = JsonNode.Parse(File.ReadAllText out)
                  Assert.equal "praxis.construction-prepared/1" (str prepared["schemaVersion"])
                  Assert.equal (str node["cacheKey"]) (str prepared["cacheKey"])
                  for field in [ "hostBinding"; "defaultPlan"; "construction"; "permittedExtensionFiles"; "handoff"; "handoffMarkdown" ] do
                      Assert.isTrue (not (isNull prepared[field])) ("missing " + field)
                  Assert.equal "rev-2" (str prepared["hostBinding"].["policyRevision"])
                  Assert.equal [ "src/Domain/Extensions.fs" ] (prepared["permittedExtensionFiles"] :?> JsonArray |> Seq.map str |> Seq.toList)
                  let digests = prepared["defaultPlan"].["provenance"].["inputDigests"]
                  for name in [ "blueprint"; "sourceManifest"; "decisionRecords" ] do Assert.isTrue ((str digests[name]).StartsWith "sha256:") ("missing input digest " + name)
                  Assert.equal canonicalBlueprint (str digests["blueprint"])
                  Assert.equal "ordo.default-plan" (str prepared["defaultPlan"].["provenance"].["generatorId"])
                  Assert.equal (ConstructionDecisions.digest decisions) (str prepared["hostBinding"].["decisionsDigest"])
                  for field in [ "acceptanceValidators"; "validatorPolicyRevision"; "praxisVersion"; "handoffSchema" ] do
                      Assert.isTrue (not (isNull prepared["hostBinding"].[field])) ("cache key omits " + field)
                  Assert.equal "praxis.construction-handoff/2" (str prepared["hostBinding"].["handoffSchema"])
                  Assert.equal 2 (prepared["defaultPlan"].["obligations"] :?> JsonArray).Count
                  let handoff = JsonNode.Parse(File.ReadAllText(str node["handoff"]))
                  let markdown = File.ReadAllText(str node["handoffMarkdown"])
                  Assert.equal "praxis.construction-handoff/2" (str handoff["schemaVersion"])
                  // Only the keys obligations cite, with the pinned source text.
                  let requirements = handoff["requirements"] :?> JsonArray |> Seq.toList
                  // Every key cited anywhere (obligations and acceptance), each once.
                  let keys = requirements |> List.map (fun r -> str r["key"])
                  Assert.isTrue (Set.isSubset (set [ k131; k105; k116 ]) (Set.ofList keys)) (String.concat ", " keys)
                  Assert.equal keys.Length (Set.ofList keys).Count
                  Assert.equal [ "R1"; "R2" ] (requirements |> List.take 2 |> List.map (fun r -> str r["alias"]))
                  let excerptOf key = requirements |> List.find (fun r -> str r["key"] = key) |> fun r -> str r["excerpt"]
                  Assert.equal (fixture "requirements/timer-stop-completion.txt") (excerptOf k131)
                  Assert.equal (fixture "requirements/timer-paused-time-excluded.txt") (excerptOf k105)
                  // Decisions summarised, not the blueprint.
                  let decided = handoff["decisions"] :?> JsonArray |> Seq.toList
                  Assert.isTrue (decided |> List.exists (fun d -> str d["nodeId"] = "IFC-TIMER-PORTS" && (str d["summary"]).Contains "NOT DECIDED")) (handoff.ToJsonString())
                  // Signatures extracted from the generated source.
                  let interfaces = handoff["extensionInterfaces"] :?> JsonArray |> Seq.toList
                  let members = interfaces |> List.collect (fun i -> i["members"] :?> JsonArray |> Seq.map str |> Seq.toList)
                  Assert.isTrue (members |> List.exists (fun m -> m.StartsWith "abstract CurrentTime:")) (String.concat " | " members)
                  Assert.isTrue (members |> List.exists (fun m -> m.StartsWith "abstract Perform:")) (String.concat " | " members)
                  Assert.equal [ "src/Domain/Extensions.fs" ] (handoff["permittedFiles"] :?> JsonArray |> Seq.map (fun f -> str f["path"]) |> Seq.toList)
                  // Every type an extension interface signature names is declared
                  // in the generated API section, without any function body.
                  let api = handoff["generatedApi"] :?> JsonArray |> Seq.collect (fun g -> g["declarations"] :?> JsonArray |> Seq.map str) |> String.concat "\n"
                  let named =
                      members |> List.collect (fun m ->
                          let signature = m.Substring(m.IndexOf ':' + 1).Replace("global.System.DateTimeOffset", "")
                          RegularExpressions.Regex.Matches(signature, "\\b[A-Z][A-Za-z0-9_]*\\b") |> Seq.map _.Value |> Seq.toList)
                      |> List.distinct
                  Assert.isTrue (not named.IsEmpty) "no types named by the interfaces"
                  for name in named do
                      Assert.isTrue (api.Contains("type " + name + " =")) ("generated API does not declare " + name)
                  Assert.isTrue (api.Contains "let decide (state: TimerState) (command: TimerCommand) : Result<TimerDecision, TimerRefusal>") api
                  Assert.isTrue (api.Contains "module TimerOrchestration =") api
                  for body in [ "match state, command with"; "TimerState.NoTimer, TimerCommand"; "ports.CurrentTime()" ] do
                      Assert.isTrue (not (api.Contains body) && not ((File.ReadAllText(str node["handoffMarkdown"])).Contains body)) ("generated body leaked: " + body)
                  let questions = handoff["unresolvedQuestions"] :?> JsonArray |> Seq.choose (fun q -> match q["obligationId"] with null -> None | id -> Some(str id)) |> Seq.toList
                  Assert.isTrue (List.contains "OBL-COH-TIMER-undecidedEffectFailure-4d5e6f" questions) (String.concat ", " questions)
                  Assert.isTrue ((handoff["acceptance"] :?> JsonArray).Count > 0) "no acceptance expectations"
                  // Minimal: no digests, no vocabulary manual, no full source.
                  for rendered in [ handoff.ToJsonString(); markdown ] do
                      Assert.isTrue (not (rendered.Contains "sha256:")) "handoff carries a digest"
                      Assert.isTrue (not (rendered.Contains "ordo.construction-vocabulary")) "handoff carries a vocabulary manual"
                      Assert.isTrue (not (rendered.Contains "auto-generated by ordo.construction")) "handoff carries generated source"
                  Assert.isTrue (markdown.StartsWith "# Construction handoff: COH-TIMER") markdown
                  // One obligation-id scheme; no restated keys or questions.
                  for rendered in [ handoff.ToJsonString(); markdown ] do
                      Assert.isTrue (not (rendered.Contains "IMPLEMENT:") && not (rendered.Contains "VERIFY:")) "a second obligation-id scheme leaked"
                  Assert.isTrue (markdown.Contains "cannot be implemented without an approved decision") markdown
                  let fullKeyMentions = RegularExpressions.Regex.Matches(markdown, RegularExpressions.Regex.Escape k131).Count
                  Assert.equal 1 fullKeyMentions
                  let question = "When the Stop completion effect fails, is the timer discarded or kept active?"
                  Assert.equal 1 (RegularExpressions.Regex.Matches(markdown, RegularExpressions.Regex.Escape question).Count)
                  let allowed = handoff["allowedOperations"] :?> JsonArray |> Seq.map str |> String.concat " "
                  Assert.isTrue (allowed.Contains "Extensions.fs" && allowed.Contains "dispositions.json" && allowed.Contains "at most 3") allowed
                  Assert.isTrue (markdown.Contains "## Allowed operations") markdown
                  Assert.isTrue (markdown.Contains "## Unresolved questions" && markdown.Contains "abstract CurrentTime:") markdown))
          t "prepare and apply through the CLI run Ordo's default-plan generator end to end; tampered decisions fail closed" (fun () ->
              ConstructionCliTests.withHostGranting
                  (fun parent _ ->
                      let path = Path.Combine(parent, "decisions.json")
                      File.WriteAllText(path, fixture "construction-decisions.json")
                      [ "--decisions"; path ])
                  true excerpts (fun host ->
                      let cli arguments = PraxisCli.run host.Clone (Some ConstructionCliTests.coordinator) arguments
                      let out = Path.Combine(host.Parent, "p.json")
                      let prepared = cli [ "construct"; "prepare"; "--host-config"; host.Config; "--group"; group; "--cohort"; "COH-TIMER"; "--decisions"; decisionsPath host; "--out"; out ] |> ok
                      Assert.equal false (prepared.Json["reused"].GetValue<bool>())
                      let artifact = JsonNode.Parse(File.ReadAllText out)
                      let provenance = artifact["defaultPlan"].["provenance"]
                      Assert.equal "ordo.default-plan" (str provenance["generatorId"])
                      Assert.equal "ordo.default-plan/3" (str provenance["generatorVersion"])
                      Assert.equal "ordo.construction-vocabulary/1" (str provenance["vocabularyVersion"])
                      Assert.equal (ConstructionDecisions.digest (fixture "construction-decisions.json")) (str provenance["inputDigests"].["decisionRecords"])
                      let kinds = artifact["defaultPlan"].["obligations"] :?> JsonArray |> Seq.map (fun o -> str o["kind"]) |> Set.ofSeq
                      Assert.isTrue (kinds.Contains "undecidedEffectFailure" && kinds.Contains "extensionBehavior") (sprintf "%A" kinds)
                      let handoffIds = artifact["handoff"].["obligations"] :?> JsonArray |> Seq.map (fun o -> str o["id"]) |> Set.ofSeq
                      Assert.equal (artifact["defaultPlan"].["obligations"] :?> JsonArray |> Seq.map (fun o -> str o["id"]) |> Set.ofSeq) handoffIds
                      Assert.isTrue (File.Exists(Path.Combine(host.Parent, "p.handoff.md"))) "markdown twin missing"
                      let again = cli [ "construct"; "prepare"; "--host-config"; host.Config; "--group"; group; "--out"; out ] |> ok
                      Assert.equal true (again.Json["reused"].GetValue<bool>())
                      let files = ConstructionCliTests.outbox host [ "Extensions.fs", ConstructionCliTests.handwritten ]
                      let applied = cli [ "construct"; "apply"; "--host-config"; host.Config; "--group"; group; "--prepared"; out; "--target"; target; "--extensions"; files ] |> ok
                      Assert.equal "applied" (str applied.Json["extension"].["outcome"])
                      let tampered = Path.Combine(host.Parent, "tampered.json")
                      File.WriteAllText(tampered, (fixture "construction-decisions.json").Replace("\"Timer\"", "\"Timer\" "))
                      let refused = cli [ "construct"; "prepare"; "--host-config"; host.Config; "--group"; group; "--decisions"; tampered; "--out"; Path.Combine(host.Parent, "t.json") ]
                      Assert.equal 3 refused.ExitCode
                      Assert.isTrue (refused.Error.Contains "differ from the digest bound by the signed grant") refused.Error
                      let wrongCohort = cli [ "construct"; "prepare"; "--host-config"; host.Config; "--group"; group; "--cohort"; "COH-OTHER"; "--out"; Path.Combine(host.Parent, "w.json") ]
                      Assert.equal 3 wrongCohort.ExitCode
                      Assert.isTrue (wrongCohort.Error.Contains "not COH-OTHER") wrongCohort.Error
                      let unknown = cli [ "construct"; "prepare"; "--host-config"; host.Config; "--group"; group; "--out"; "x.json"; "--plan"; host.Plan ]
                      Assert.equal 2 unknown.ExitCode
                      let worker = cli [ "construct"; "apply"; "--host-config"; host.Config; "--group"; group; "--prepared"; out; "--target"; target; "--worker"; "w" ]
                      Assert.equal 3 worker.ExitCode
                      Assert.isTrue (worker.Error.Contains "worker execution is disabled") worker.Error)) ]

    // -------------------------------------------------- (c) capability apply

    let private applyTests =
        [ t "apply --prepared lands generated files, then the handwritten file only at its permitted path" (fun () ->
              withDecisionsHost (fun host ->
                  let generator = fake (ref 0) "fake@1"
                  let _, out = prepareWith generator host (Some(decisionsPath host)) None |> okValue
                  let before = ConstructionCliTests.tip host
                  let files = ConstructionCliTests.outbox host [ "Extensions.fs", ConstructionCliTests.handwritten ]
                  let applied = applyWith generator host out (Some files) |> okValue
                  Assert.equal "praxis.construction-prepared-application/1" (str applied["schemaVersion"])
                  Assert.equal "applied" (str applied["extension"].["outcome"])
                  let after = ConstructionCliTests.tip host
                  let paths = changed host before after
                  let generated = applied["writes"] :?> JsonArray |> Seq.map (fun w -> str w["path"]) |> Set.ofSeq
                  Assert.equal (Set.add "src/Domain/Extensions.fs" generated) paths
                  // Idempotent: the same prepared artifact and files change nothing.
                  let again = applyWith generator host out (Some files) |> okValue
                  Assert.equal "already-current" (str again["outcome"])
                  Assert.equal after (ConstructionCliTests.tip host)))
          t "unpermitted, project, symlinked and nested handwritten files are refused and recorded before any effect" (fun () ->
              withDecisionsHost (fun host ->
                  let generator = fake (ref 0) "fake@1"
                  let _, out = prepareWith generator host (Some(decisionsPath host)) None |> okValue
                  let before = ConstructionCliTests.tip host
                  let attempt (files: (string * string) list) (fragment: string) =
                      applyWith generator host out (Some(ConstructionCliTests.outbox host files)) |> refusedWith fragment
                      Assert.equal before (ConstructionCliTests.tip host)
                  attempt [ "Extensions.fs", ConstructionCliTests.handwritten; "Other.fs", "module Other\n" ] "Other.fs: not a host-declared extension slot"
                  attempt [ "Timer.fsproj", "<Project />\n" ] "Timer.fsproj: project or build file"
                  let observed = Path.Combine(host.Parent, "observations.json")
                  ConstructionService.applyPreparedWith generator host.Clone host.Config group out target
                      (Some(ConstructionCliTests.outbox host [ "Extensions.fs", ConstructionCliTests.handwritten; "packages.lock.json", "{}\n" ])) (Some observed) None
                  |> refusedWith "denied operations recorded"
                  let observations = JsonNode.Parse(File.ReadAllText observed)
                  Assert.equal "dokimos.host-observations/1" (str observations["schema"])
                  let entries = observations["observations"] :?> JsonArray |> Seq.toList
                  Assert.equal [ "src/Domain/packages.lock.json" ] (entries |> List.map (fun o -> str o["path"]))
                  Assert.equal "denied" (str entries.Head["outcome"])
                  Assert.equal "host" (str entries.Head["recordedBy"])
                  attempt [ "Completion.fs", "module C\n" ] "implements no extension contract"
                  let linked = ConstructionCliTests.outbox host []
                  File.CreateSymbolicLink(Path.Combine(linked, "Extensions.fs"), Path.Combine(host.Clone, "src/Domain/README.md")) |> ignore
                  applyWith generator host out (Some linked) |> refusedWith "symbolic link"
                  let nested = ConstructionCliTests.outbox host []
                  Directory.CreateDirectory(Path.Combine(nested, "sub")) |> ignore
                  applyWith generator host out (Some nested) |> refusedWith "subdirectory"
                  Assert.equal before (ConstructionCliTests.tip host)
                  Assert.isTrue ((refusals host).Length >= 6) (sprintf "%d refusal records" (refusals host).Length)
                  let record = JsonNode.Parse(File.ReadAllText((refusals host).Head))
                  Assert.equal "praxis.construction-refusal/1" (str record["schemaVersion"])))
          t "a symlink at a slot path on the target refuses before any effect and is recorded as a host observation" (fun () ->
              ConstructionCliTests.withHostGranting
                  (fun parent _ ->
                      let path = Path.Combine(parent, "decisions.json")
                      File.WriteAllText(path, decisions)
                      [ "--decisions"; path ])
                  true
                  (fun clone ->
                      excerpts clone
                      File.CreateSymbolicLink(Path.Combine(clone, "src/Domain/Extensions.fs"), "README.md") |> ignore)
                  (fun host ->
                      let generator = fake (ref 0) "fake@1"
                      let _, out = prepareWith generator host (Some(decisionsPath host)) None |> okValue
                      let before = ConstructionCliTests.tip host
                      let observed = Path.Combine(host.Parent, "observations.json")
                      let files = ConstructionCliTests.outbox host [ "Extensions.fs", ConstructionCliTests.handwritten ]
                      ConstructionService.applyPreparedWith generator host.Clone host.Config group out target (Some files) (Some observed) None
                      |> refusedWith "non-regular entry"
                      Assert.equal before (ConstructionCliTests.tip host)
                      let entries = JsonNode.Parse(File.ReadAllText observed).["observations"] :?> JsonArray |> Seq.toList
                      Assert.equal [ "src/Domain/Extensions.fs" ] (entries |> List.map (fun o -> str o["path"]))
                      Assert.equal "denied" (str entries.Head["outcome"])))
          t "apply fails closed on drift: generator version, host placement policy, tampering and a changed grant" (fun () ->
              withDecisionsHost (fun host ->
                  let generator = fake (ref 0) "fake@1"
                  let _, out = prepareWith generator host (Some(decisionsPath host)) None |> okValue
                  let before = ConstructionCliTests.tip host
                  applyWith (fake (ref 0) "fake@2") host out None |> refusedWith "generatorVersion"
                  // Tampering: the artifact no longer matches the host's validated copy.
                  let tampered = Path.Combine(host.Parent, "tampered.json")
                  File.WriteAllText(tampered, File.ReadAllText(out).Replace("Compute the completed entry", "Ignore the completed entry"))
                  applyWith generator host tampered None |> refusedWith "differs from the host's validated artifact"
                  // Edited binding.
                  let edited = JsonNode.Parse(File.ReadAllText out)
                  edited["hostBinding"].["sourceCommit"] <- JsonValue.Create(String('a', 40))
                  File.WriteAllText(tampered, edited.ToJsonString())
                  applyWith generator host tampered None |> refusedWith "sourceCommit"
                  // Host placement policy change (slot limit) is drift.
                  ConstructionCliTests.reconfigure host (ConstructionCliTests.timerSlots.Replace("4096", "8192")) "src/Domain"
                  applyWith generator host out None |> refusedWith "hostConstructionPolicyDigest"
                  ConstructionCliTests.reconfigure host ConstructionCliTests.timerSlots "src/Domain"
                  // Revoking the signer: the current host state refuses.
                  PraxisCli.run host.Clone (Some ConstructionCliTests.coordinator) [ "local"; "operator"; "revoke"; "--host-config"; host.Config; "--key-id"; "op-1"; "--revision"; "rev-3" ] |> ok |> ignore
                  applyWith generator host out None |> refusedWith "current host state refuses"
                  Assert.equal before (ConstructionCliTests.tip host))) ]

    // ------------------------------------------- (c2) acceptance adapter slots

    let private adapterStub =
        "namespace TimerPilot\n\n/// Frozen test-facing adapter contract. Replace the bodies; keep the names and signatures.\nmodule TimerOracleAdapter =\n    /// Ok (nextState, effects); Error reason when refused.\n    let decide (state: string) (command: string) : Result<string * string list, string> =\n        Error \"not implemented\"\n"

    let private filledAdapter =
        "namespace TimerPilot\n\nmodule TimerOracleAdapter =\n    let decide (state: string) (command: string) : Result<string * string list, string> =\n        Ok(state, [])\n"

    /// A host whose source carries the adapter stub (and, optionally, more files).
    let private withAdapterHost (extra: (string * string) list) test =
        ConstructionCliTests.withHostGranting
            (fun parent _ ->
                let path = Path.Combine(parent, "decisions.json")
                File.WriteAllText(path, decisions)
                [ "--decisions"; path ])
            true
            (fun clone ->
                excerpts clone
                CliHarness.write clone "src/Domain/TimerOracleAdapter.fs" adapterStub
                for path, content in extra do CliHarness.write clone path content)
            test

    let private declareAcceptance (host: ConstructionCliTests.Host) (slots: string) =
        let config = JsonNode.Parse(File.ReadAllText host.Config)
        config["acceptanceSlots"] <- JsonNode.Parse slots
        File.WriteAllText(host.Config, config.ToJsonString())

    let private adapterSlot path = sprintf """[{"path":"%s","acceptanceContract":"TimerPilot.TimerOracleAdapter","maxBytes":4096}]""" path

    let private acceptanceTests =
        [ t "an acceptance adapter slot is bound, handed off with its stub signatures and applied at its existing path" (fun () ->
              withAdapterHost [] (fun host ->
                  declareAcceptance host (adapterSlot "src/Domain/TimerOracleAdapter.fs")
                  let generator = fake (ref 0) "fake@1"
                  let node, out = prepareWith generator host (Some(decisionsPath host)) None |> okValue
                  let prepared = JsonNode.Parse(File.ReadAllText out)
                  Assert.equal [ "src/Domain/Extensions.fs"; "src/Domain/TimerOracleAdapter.fs" ] (prepared["permittedExtensionFiles"] :?> JsonArray |> Seq.map str |> Seq.toList)
                  let kinds = prepared["permittedExtensionSlots"] :?> JsonArray |> Seq.map (fun s -> str s["path"], str s["kind"]) |> Seq.toList
                  Assert.isTrue (List.contains ("src/Domain/TimerOracleAdapter.fs", "acceptanceAdapter") kinds) (sprintf "%A" kinds)
                  Assert.isTrue (List.contains ("src/Domain/Extensions.fs", "constructionContract") kinds) (sprintf "%A" kinds)
                  Assert.equal "src/Domain/TimerOracleAdapter.fs" (str prepared["hostBinding"].["acceptanceSlots"].[0].["path"])
                  let handoff = JsonNode.Parse(File.ReadAllText(str node["handoff"]))
                  let adapter = handoff["acceptanceAdapters"].[0]
                  Assert.equal "TimerPilot.TimerOracleAdapter" (str adapter["acceptanceContract"])
                  let signatures = adapter["signatures"] :?> JsonArray |> Seq.map str |> Seq.toList
                  Assert.isTrue (List.contains "let decide (state: string) (command: string) : Result<string * string list, string>" signatures) (String.concat " | " signatures)
                  Assert.isTrue (signatures |> List.forall (fun line -> not (line.Contains "not implemented"))) "adapter body leaked"
                  let before = ConstructionCliTests.tip host
                  let files = ConstructionCliTests.outbox host [ "Extensions.fs", ConstructionCliTests.handwritten; "TimerOracleAdapter.fs", filledAdapter ]
                  let applied = applyWith generator host out (Some files) |> okValue
                  Assert.equal "applied" (str applied["extension"].["outcome"])
                  let after = ConstructionCliTests.tip host
                  Assert.isTrue ((changed host before after).Contains "src/Domain/TimerOracleAdapter.fs") "adapter not applied"
                  Assert.equal filledAdapter (GitFixture.git host.Clone [ "show"; after + ":src/Domain/TimerOracleAdapter.fs" ] + "\n" |> fun t -> t.TrimEnd('\n') + "\n")
                  // Everything else is still refused exactly as before.
                  applyWith generator host out (Some(ConstructionCliTests.outbox host [ "TimerOracleAdapter.fs", filledAdapter; "Other.fs", "module O\n" ]))
                  |> refusedWith "Other.fs: not a host-declared extension slot"))
          t "acceptance slots at missing, generated, project or non-mount paths are refused" (fun () ->
              withAdapterHost [ "src/Domain/Adapter.fs", Ordo.Construction.Artifacts.GeneratedMarker + " fake>\nmodule A\n" ] (fun host ->
                  let generator = fake (ref 0) "fake@1"
                  declareAcceptance host (adapterSlot "src/Domain/Missing.fs")
                  prepareWith generator host (Some(decisionsPath host)) None |> refusedWith "absent from the admitted source commit"
                  declareAcceptance host (adapterSlot "src/Domain/Adapter.fs")
                  prepareWith generator host (Some(decisionsPath host)) None |> refusedWith "generated file"
                  declareAcceptance host (adapterSlot "src/Domain/Timer.Model.g.fs")
                  prepareWith generator host (Some(decisionsPath host)) None |> refusedWith "generated-file name"
                  declareAcceptance host (adapterSlot "src/Domain/Timer.fsproj")
                  prepareWith generator host (Some(decisionsPath host)) None |> refusedWith "project or build file"
                  declareAcceptance host (adapterSlot "src/Domain/packages.lock.json")
                  prepareWith generator host (Some(decisionsPath host)) None |> refusedWith "package or dependency file"
                  declareAcceptance host (adapterSlot "src/Other/TimerOracleAdapter.fs")
                  prepareWith generator host (Some(decisionsPath host)) None |> refusedWith "directly under the construction mount"
                  declareAcceptance host (adapterSlot "src/Domain/Extensions.fs")
                  prepareWith generator host (Some(decisionsPath host)) None |> refusedWith "must be unique"))
          t "declaring an acceptance slot changes the cache key and invalidates reuse" (fun () ->
              withAdapterHost [] (fun host ->
                  let calls = ref 0
                  let first, _ = prepareWith (fake calls "fake@1") host (Some(decisionsPath host)) None |> okValue
                  declareAcceptance host (adapterSlot "src/Domain/TimerOracleAdapter.fs")
                  let second, _ = prepareWith (fake calls "fake@1") host None None |> okValue
                  Assert.equal false (second["reused"].GetValue<bool>())
                  Assert.isTrue (str first["cacheKey"] <> str second["cacheKey"]) "acceptance slots did not change the key"
                  Assert.equal 2 calls.Value)) ]

    // ------------------------------------------------------------- (d) cache

    let private cacheTests =
        [ t "an identical cache key reuses the validated artifact byte for byte; generator or policy changes invalidate it" (fun () ->
              withDecisionsHost (fun host ->
                  let calls = ref 0
                  let first, out = prepareWith (fake calls "fake@1") host (Some(decisionsPath host)) None |> okValue
                  let bytes = File.ReadAllText out
                  Assert.equal false (first["reused"].GetValue<bool>())
                  // Without --decisions the host's stored, signed copy is used.
                  let second, _ = prepareWith (fake calls "fake@1") host None None |> okValue
                  Assert.equal true (second["reused"].GetValue<bool>())
                  Assert.equal 1 calls.Value
                  Assert.equal bytes (File.ReadAllText out)
                  Assert.equal (str first["cacheKey"]) (str second["cacheKey"])
                  let third, _ = prepareWith (fake calls "fake@2") host None None |> okValue
                  Assert.equal false (third["reused"].GetValue<bool>())
                  Assert.equal 2 calls.Value
                  Assert.isTrue (str third["cacheKey"] <> str first["cacheKey"]) "generator version did not change the key"
                  ConstructionCliTests.reconfigure host (ConstructionCliTests.timerSlots.Replace("4096", "8192")) "src/Domain"
                  let fourth, _ = prepareWith (fake calls "fake@1") host None None |> okValue
                  Assert.equal false (fourth["reused"].GetValue<bool>())
                  Assert.equal 3 calls.Value
                  Assert.isTrue (str fourth["cacheKey"] <> str first["cacheKey"]) "placement policy did not change the key"))
          t "the cache key covers every binding input" (fun () ->
              let binding : (string * JsonNode) list =
                  [ "policyRevision", JsonValue.Create "rev-2"; "blueprintDigest", JsonValue.Create "sha256:b"; "manifestDigest", JsonValue.Create "sha256:m"
                    "cohortId", JsonValue.Create "COH"; "decisionsDigest", JsonValue.Create "sha256:d"; "vocabularyVersion", JsonValue.Create "v2"
                    "generatorVersion", JsonValue.Create "g1" ]
              let key = ConstructionPrepared.cacheKey binding
              Assert.equal key (ConstructionPrepared.cacheKey binding)
              for name, _ in binding do
                  let changed = binding |> List.map (fun (n, v) -> if n = name then n, (JsonValue.Create "other" :> JsonNode) else n, v)
                  Assert.isTrue (ConstructionPrepared.cacheKey changed <> key) ("key ignores " + name)
              let recorded = JsonObject()
              for name, value in binding do recorded[name] <- value.DeepClone()
              Assert.empty (ConstructionPrepared.drift recorded binding)
              let changed = binding |> List.map (fun (n, v) -> if n = "policyRevision" then n, (JsonValue.Create "rev-3" :> JsonNode) else n, v)
              Assert.equal [ "policyRevision \"rev-2\" -> \"rev-3\"" ] (ConstructionPrepared.drift recorded changed)) ]

    // --------------------------------------------------------- (e) telemetry

    let private telemetryTests =
        [ t "prepare and apply are execution-telemetry steps with host compute time and preview, retry and refusal counts" (fun () ->
              withDecisionsHost (fun host ->
                  let run arguments = PraxisCli.run host.Clone (Some ConstructionCliTests.coordinator) arguments |> ok |> ignore
                  run [ "add"; "Telemetry host"; "--id"; "WI-TEL" ]
                  run [ "work"; "backlog-transition"; "--id"; "WI-TEL"; "--action"; "ready"; "--occurred-at"; now () ]
                  run [ "work"; "begin"; "--id"; "WI-TEL"; "--occurred-at"; now () ]
                  let generator = fake (ref 0) "fake@1"
                  let first, out = prepareWith generator host (Some(decisionsPath host)) (Some "WI-TEL") |> okValue
                  let telemetry = first["telemetry"]
                  Assert.equal "recorded" (str telemetry["executionStep"])
                  Assert.equal 1 (telemetry["prepares"].GetValue<int>())
                  Assert.equal 0 (telemetry["retries"].GetValue<int>())
                  Assert.equal "host" (str telemetry["source"])
                  Assert.isTrue ((str telemetry["modelUsage"]).Contains "not observed by the host") "model usage mixed in"
                  let second, _ = prepareWith generator host None (Some "WI-TEL") |> okValue
                  // A repeat after a success is not a retry.
                  Assert.equal 2 (second["telemetry"].["prepares"].GetValue<int>())
                  Assert.equal 0 (second["telemetry"].["retries"].GetValue<int>())
                  Assert.equal false (second["telemetry"].["retry"].GetValue<bool>())
                  Assert.equal "reused" (str second["telemetry"].["outcome"])
                  // A refused call is a failure; the next call of the same operation in the same execution is a retry.
                  ConstructionService.applyPreparedWith (fake (ref 0) "fake@2") host.Clone host.Config group out target None None (Some "WI-TEL")
                  |> refusedWith "\"failures\":1"
                  let applied = ConstructionService.applyPreparedWith generator host.Clone host.Config group out target None None (Some "WI-TEL") |> okValue
                  Assert.equal "recorded" (str applied["telemetry"].["executionStep"])
                  Assert.equal true (applied["telemetry"].["retry"].GetValue<bool>())
                  Assert.equal 1 (applied["telemetry"].["retries"].GetValue<int>())
                  Assert.equal 1 (applied["telemetry"].["failures"].GetValue<int>())
                  let executionId = str applied["telemetry"].["executionId"]
                  let record = JsonNode.Parse(File.ReadAllText(Path.Combine(host.Clone, ".ros", "telemetry", "executions", executionId + ".json")))
                  let steps =
                      record["events"] :?> JsonArray |> Seq.choose (fun e -> match e["stepId"] with null -> None | id -> Some(str e["type"], str id)) |> Seq.toList
                  Assert.isTrue (steps |> List.exists (fun (kind, id) -> kind = "step.completed" && id.StartsWith "STEP-CONSTRUCT-PREPARE-")) (sprintf "%A" steps)
                  Assert.isTrue (steps |> List.exists (fun (kind, id) -> kind = "step.completed" && id.StartsWith "STEP-CONSTRUCT-APPLY-")) (sprintf "%A" steps)
                  let metrics = record["metrics"] :?> JsonArray |> Seq.map (fun m -> str m["id"]) |> Set.ofSeq
                  for id in [ "time.tool_ms"; "tool.calls"; "tool.retries"; "tool.failures" ] do
                      Assert.isTrue (metrics.Contains id) ("missing metric " + id)
                  Assert.isTrue (not (metrics |> Set.exists (fun id -> id.StartsWith "tokens."))) "host recorded model tokens"
                  let hostSourced = record["metrics"] :?> JsonArray |> Seq.filter (fun m -> str m["id"] = "tool.calls") |> Seq.forall (fun m -> str m["source"].["type"] = "host")
                  Assert.isTrue hostSourced "host metrics are not marked source=host"))
          t "without an active execution, telemetry says the step is unavailable and the ledger still counts" (fun () ->
              withDecisionsHost (fun host ->
                  let node, _ = prepareWith (fake (ref 0) "fake@1") host (Some(decisionsPath host)) (Some "WI-NONE") |> okValue
                  Assert.isTrue ((str node["telemetry"].["executionStep"]).StartsWith "unavailable") (node.ToJsonString())
                  Assert.equal "recorded" (str node["telemetry"].["ledger"])
                  // Unknown is never written as zero.
                  for name in [ "retry"; "retries"; "failures" ] do
                      Assert.isTrue (isNull node["telemetry"].[name]) (name + " should be unavailable (null)")
                  Assert.isTrue (File.Exists(Path.Combine(host.Journal, "telemetry", "ledger.jsonl"))) "ledger missing")) ]

    let tests = grantTests @ prepareTests @ applyTests @ acceptanceTests @ cacheTests @ telemetryTests
