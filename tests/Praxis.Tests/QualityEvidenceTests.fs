namespace Praxis.Tests

open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work

/// PRX-QUAL-023: Praxis consumes Dokimos ratchet and Ordo boundary evidence
/// at completion. Pure decision tests cover every verdict, recommendation
/// and coverage combination; contract tests decode the real reports the
/// tools produced (tests/fixtures/quality-evidence); the CLI tests drive
/// `work complete` against a temporary repository.
[<RequireQualifiedAccess>]
module QualityEvidenceTests =
    let private str (node: JsonNode) = node.GetValue<string>()
    let private flag (node: JsonNode) = node.GetValue<bool>()

    let private t name run = { Name = $"quality evidence: {name}"; Run = run }

    let rec private repositoryRoot (directory: DirectoryInfo) =
        if Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "quality-evidence")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate tests/fixtures/quality-evidence"
        else
            repositoryRoot directory.Parent

    let private fixturePath (relative: string) =
        Path.Combine(repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory())), "tests", "fixtures", "quality-evidence", relative)

    let private fixture relative = File.ReadAllText(fixturePath relative)

    let private parsed reading =
        match reading with
        | EvidenceReading.Parsed value -> value
        | other -> failwith $"expected a parsed document, got {other}"

    let private dokimos name = parsed (QualityEvidenceJson.decodeDokimos (fixture $"dokimos/{name}.json"))
    let private ordo name = parsed (QualityEvidenceJson.decodeOrdo (fixture $"ordo/{name}.json"))

    let private report verdict =
        { DokimosVersion = "0.2.0"
          CheckedAt = "2026-10-05T12:00:00+00:00"
          Repository = Some "owner/repo"
          Verdict = verdict
          ExitCode = CompletionReadiness.verdictExitCode verdict
          Reasons = (if verdict = DokimosVerdict.Pass then [] else [ "reason from dokimos" ])
          BaselinePath = "quality/baseline.json"
          BaselineDigest = Some "sha256:00"
          Regressions = (if verdict = DokimosVerdict.Regression then 1 else 0)
          Excepted = 0
          Improvements = 0
          RulesMeasured = 8
          RulesUnavailable = (if verdict = DokimosVerdict.Unavailable then 1 else 0)
          ActiveExceptions = []
          ExpiredExceptions = []
          InvalidExceptions = [] }

    let private assessment recommendation coverage =
        { WorkItem = "WI-1"
          Risk = OrdoRiskLevel.High
          Score = 6
          RiskEvidence = [ "ORDO-BA-001" ]
          Recommendation = recommendation
          SplitAlong = [ "cli" ]
          Coverage = coverage
          DowngradedFrom = None
          UnexpectedCrossings = [ "cli" ]
          Unclassified = []
          GeneratorVersion = Some "1.4.0" }

    let private supplied value = SourceObservation.Supplied("evidence.json", EvidenceReading.Parsed value)

    let private allVerdicts =
        [ DokimosVerdict.Pass; DokimosVerdict.Regression; DokimosVerdict.InvalidExceptions; DokimosVerdict.Unavailable ]

    let private allRecommendations =
        [ OrdoRecommendation.NoAction; OrdoRecommendation.ConsiderSplitAlong; OrdoRecommendation.RequireDesignReview ]

    let private allCoverages =
        [ OrdoExceptionCoverage.NotApplicable; OrdoExceptionCoverage.NoCoverage; OrdoExceptionCoverage.Partial; OrdoExceptionCoverage.Full ]

    let private policy dokimosRequirement ordoRequirement =
        { QualityEvidencePolicies.legacyDefault with
            Dokimos = dokimosRequirement
            OrdoBoundary = ordoRequirement }

    let private architecture (readiness: ItemReadiness) =
        readiness.Facets |> List.find (fun facet -> facet.Facet = CompletionFacet.ArchitectureVerified)

    let private noObligationEvidence: ObligationObservations =
        { DesignDebt = SourceObservation.NotSupplied
          VerificationMatrix = SourceObservation.NotSupplied
          ReleaseReadiness = SourceObservation.NotSupplied }

    /// The policy-only assessment: no risk metadata, no obligation evidence.
    let private assessItem p item provided dokimosObservation ordoObservation =
        CompletionReadinessOperations.assess p CompletionFacts.none (fun _ -> true) item provided dokimosObservation ordoObservation noObligationEvidence [] None

    let private assess p dokimosObservation ordoObservation =
        assessItem p ("WI-1", "task") [] dokimosObservation ordoObservation

    let private isSatisfied =
        function
        | FacetStatus.Satisfied _ -> true
        | _ -> false

    let private nonPassObservations: (string * SourceObservation<DokimosRatchetEvidence>) list =
        [ "not supplied", SourceObservation.NotSupplied
          "ambiguous", SourceObservation.Ambiguous [ "a.json"; "b.json" ]
          "unsupported", SourceObservation.Supplied("a.json", EvidenceReading.Unsupported "contract 'x'")
          "malformed", SourceObservation.Supplied("a.json", EvidenceReading.Malformed "not valid JSON")
          "regression", supplied (report DokimosVerdict.Regression)
          "invalid-exceptions", supplied (report DokimosVerdict.InvalidExceptions)
          "unavailable", supplied (report DokimosVerdict.Unavailable)
          "inconsistent exit code", supplied { report DokimosVerdict.Pass with ExitCode = 4 }
          "pass with unexcepted regressions", supplied { report DokimosVerdict.Pass with Regressions = 2 } ]

    let private judgementTests =
        [ t "every dokimos verdict maps to Dokimos's own judgement, never recomputed" (fun () ->
              for verdict in allVerdicts do
                  let judgement = CompletionReadiness.judgeDokimos EvidenceRequirement.Required None (supplied (report verdict))

                  match verdict, judgement with
                  | DokimosVerdict.Pass, SourceJudgement.Passed _
                  | DokimosVerdict.Regression, SourceJudgement.Failed _
                  | DokimosVerdict.InvalidExceptions, SourceJudgement.Failed _
                  | DokimosVerdict.Unavailable, SourceJudgement.Unavailable _ -> ()
                  | _ -> failwith $"{verdict} judged as {judgement}")

          t "a pass with excepted regressions is trusted as a pass" (fun () ->
              match CompletionReadiness.judgeDokimos EvidenceRequirement.Required None (supplied { report DokimosVerdict.Pass with Excepted = 3 }) with
              | SourceJudgement.Passed evidence -> Assert.isTrue (evidence.Contains "3 excepted") evidence
              | other -> failwith $"{other}")

          t "unavailable evidence is never a pass, whatever the requirement" (fun () ->
              for requirement in [ EvidenceRequirement.Required; EvidenceRequirement.Optional ] do
                  for name, observation in nonPassObservations do
                      let readiness = assess (policy requirement EvidenceRequirement.Off) observation SourceObservation.NotSupplied
                      let facet = architecture readiness
                      Assert.isTrue (not (isSatisfied facet.Status)) $"{name} under {requirement} must not satisfy architecture: {facet.Status}")

          t "a required dokimos source blocks on every non-pass observation" (fun () ->
              for name, observation in nonPassObservations do
                  let readiness = assess (policy EvidenceRequirement.Required EvidenceRequirement.Off) observation SourceObservation.NotSupplied
                  Assert.isTrue (not (CompletionReadiness.isReady readiness)) $"{name} must refuse completion")

          t "an optional dokimos source blocks only on failing evidence" (fun () ->
              for name, observation in nonPassObservations do
                  let readiness = assess (policy EvidenceRequirement.Optional EvidenceRequirement.Off) observation SourceObservation.NotSupplied
                  let failing = name = "regression" || name = "invalid-exceptions"
                  Assert.equal (not failing) (CompletionReadiness.isReady readiness))

          t "invalid-exceptions is not satisfied, not unavailable" (fun () ->
              let readiness =
                  assess (policy EvidenceRequirement.Required EvidenceRequirement.Off) (supplied (report DokimosVerdict.InvalidExceptions)) SourceObservation.NotSupplied

              match (architecture readiness).Status with
              | FacetStatus.NotSatisfied reasons -> Assert.isTrue (reasons |> List.exists _.Contains("invalid-exceptions")) $"{reasons}"
              | other -> failwith $"{other}")

          t "a dokimos verdict of unavailable is facet-unavailable and blocks when required" (fun () ->
              let readiness =
                  assess (policy EvidenceRequirement.Required EvidenceRequirement.Off) (supplied (report DokimosVerdict.Unavailable)) SourceObservation.NotSupplied

              let facet = architecture readiness

              match facet.Status with
              | FacetStatus.Unavailable _ -> Assert.isTrue facet.Blocking "required unavailable must block"
              | other -> failwith $"{other}")

          t "a pinned dokimos baseline (profile) rejects a report for another baseline" (fun () ->
              let other = supplied { report DokimosVerdict.Pass with BaselinePath = "other/baseline.json" }

              match CompletionReadiness.judgeDokimos EvidenceRequirement.Required (Some "quality/baseline.json") other with
              | SourceJudgement.Unavailable _ -> ()
              | judgement -> failwith $"{judgement}"

              match CompletionReadiness.judgeDokimos EvidenceRequirement.Required (Some "./quality/baseline.json") (supplied (report DokimosVerdict.Pass)) with
              | SourceJudgement.Passed _ -> ()
              | judgement -> failwith $"{judgement}")

          t "ordo fails exactly on require-design-review without full exception coverage" (fun () ->
              for recommendation in allRecommendations do
                  for coverage in allCoverages do
                      let judgement = CompletionReadiness.judgeOrdo EvidenceRequirement.Required "WI-1" (supplied (assessment recommendation coverage))

                      let shouldFail = recommendation = OrdoRecommendation.RequireDesignReview && coverage <> OrdoExceptionCoverage.Full

                      match judgement with
                      | SourceJudgement.Failed _ when shouldFail -> ()
                      | SourceJudgement.Passed _ when not shouldFail -> ()
                      | other -> failwith $"{recommendation}/{coverage}: {other}")

          t "an ordo assessment of another work item is unavailable, not a pass" (fun () ->
              match CompletionReadiness.judgeOrdo EvidenceRequirement.Required "WI-OTHER" (supplied (assessment OrdoRecommendation.NoAction OrdoExceptionCoverage.NotApplicable)) with
              | SourceJudgement.Unavailable _ -> ()
              | other -> failwith $"{other}")

          t "an off source is ignored" (fun () ->
              Assert.equal SourceJudgement.Ignored (CompletionReadiness.judgeDokimos EvidenceRequirement.Off None SourceObservation.NotSupplied)
              Assert.equal SourceJudgement.Ignored (CompletionReadiness.judgeOrdo EvidenceRequirement.Off "WI-1" SourceObservation.NotSupplied)) ]

    let private combinationTests =
        [ t "architecture is satisfied only when dokimos passes and ordo does not demand review (all combinations)" (fun () ->
              for verdict in allVerdicts do
                  for recommendation in allRecommendations do
                      for coverage in allCoverages do
                          let readiness =
                              assess
                                  (policy EvidenceRequirement.Required EvidenceRequirement.Required)
                                  (supplied (report verdict))
                                  (supplied (assessment recommendation coverage))

                          let expected =
                              verdict = DokimosVerdict.Pass
                              && (recommendation <> OrdoRecommendation.RequireDesignReview || coverage = OrdoExceptionCoverage.Full)

                          let facet = architecture readiness
                          Assert.equal expected (isSatisfied facet.Status)
                          Assert.equal expected (CompletionReadiness.isReady readiness))

          t "required dokimos pass with optional ordo absent is satisfied" (fun () ->
              let readiness =
                  assess (policy EvidenceRequirement.Required EvidenceRequirement.Optional) (supplied (report DokimosVerdict.Pass)) SourceObservation.NotSupplied

              Assert.isTrue (isSatisfied (architecture readiness).Status) "satisfied"
              Assert.isTrue (CompletionReadiness.isReady readiness) "ready")

          t "optional sources that are both absent are recorded unavailable and do not block" (fun () ->
              let readiness = assess (policy EvidenceRequirement.Optional EvidenceRequirement.Optional) SourceObservation.NotSupplied SourceObservation.NotSupplied
              let facet = architecture readiness

              match facet.Status with
              | FacetStatus.Unavailable _ -> Assert.isTrue (not facet.Blocking) "optional absence must not block"
              | other -> failwith $"{other}")

          t "a required architecture facet with no enabled source is unavailable and blocks" (fun () ->
              let p =
                  { QualityEvidencePolicies.legacyDefault with
                      RequiredFacets = set [ CompletionFacet.ArchitectureVerified ] }

              let readiness = assess p SourceObservation.NotSupplied SourceObservation.NotSupplied
              let facet = architecture readiness

              match facet.Status with
              | FacetStatus.Unavailable _ -> Assert.isTrue facet.Blocking "must block"
              | other -> failwith $"{other}")

          t "facets are independent: implementation and behaviour are attested by evidence, release is never assumed" (fun () ->
              let p =
                  { QualityEvidencePolicies.legacyDefault with
                      RequiredFacets = set [ CompletionFacet.ImplementationComplete; CompletionFacet.BehaviorVerified; CompletionFacet.ReleaseReady ] }

              let readiness =
                  assessItem p ("WI-1", "task") [ { Type = "implementation"; Path = "src/a.fs" } ] SourceObservation.NotSupplied SourceObservation.NotSupplied

              let status facet = (readiness.Facets |> List.find (fun entry -> entry.Facet = facet)).Status
              Assert.isTrue (isSatisfied (status CompletionFacet.ImplementationComplete)) "implementation"

              match status CompletionFacet.BehaviorVerified, status CompletionFacet.ReleaseReady, status CompletionFacet.ArchitectureVerified with
              | FacetStatus.NotSatisfied _, FacetStatus.Unavailable _, FacetStatus.NotRequired -> ()
              | other -> failwith $"{other}"

              Assert.equal 2 (CompletionReadiness.blockingReasons readiness).Length) ]

    let private failingSources: QualityEvidenceSources =
        { ReadDokimos = fun _ -> failwith "the legacy default must not read evidence"
          ReadOrdo = fun _ -> failwith "the legacy default must not read evidence"
          ReadDesignDebt = fun _ -> failwith "the legacy default must not read evidence"
          ReadVerificationMatrix = fun _ -> failwith "the legacy default must not read evidence"
          ReadReleaseReadiness = fun _ -> failwith "the legacy default must not read evidence"
          Digest = fun _ -> failwith "the legacy default must not read evidence"
          LocationExists = fun _ -> failwith "the legacy default must not read evidence" }

    let private policyTests =
        [ t "the default policy leaves existing completion behaviour unchanged" (fun () ->
              Assert.isTrue (not (QualityEvidencePolicies.isActive QualityEvidencePolicies.legacyDefault)) "inactive"

              let outcome =
                  CompletionReadinessOperations.gate
                      failingSources
                      CompletionFacts.none
                      (Ok QualityEvidencePolicies.legacyDefault)
                      [ "WI-1", "task" ]
                      [ { Type = "dokimos-ratchet"; Path = "x.json" } ]
                      (fun _ -> None)

              Assert.equal CompletionGateOutcome.NotApplicable outcome)

          t "the policy applies only to its declared work types" (fun () ->
              let p =
                  { policy EvidenceRequirement.Required EvidenceRequirement.Off with
                      WorkTypes = Some(set [ "feature" ]) }

              Assert.equal CompletionGateOutcome.NotApplicable (CompletionReadinessOperations.gate failingSources CompletionFacts.none (Ok p) [ "WI-1", "mechanical" ] [] (fun _ -> None)))

          t "an invalid policy fails closed" (fun () ->
              Assert.equal (CompletionGateOutcome.PolicyInvalid "bad") (CompletionReadinessOperations.gate failingSources CompletionFacts.none (Error "bad") [ "WI-1", "task" ] [] (fun _ -> None)))

          t "more than one report of a type is ambiguous" (fun () ->
              let sources =
                  { failingSources with
                        ReadDokimos = fun _ -> EvidenceReading.Parsed(report DokimosVerdict.Pass)
                        Digest = fun _ -> Some "sha256:0" }

              let provided = [ { Type = "dokimos-ratchet"; Path = "a.json" }; { Type = "dokimos-ratchet"; Path = "b.json" } ]

              match CompletionReadinessOperations.gate sources CompletionFacts.none (Ok(policy EvidenceRequirement.Required EvidenceRequirement.Off)) [ "WI-1", "task" ] provided (fun _ -> None) with
              | CompletionGateOutcome.Refused [ readiness ] -> Assert.equal (SourceObservation.Ambiguous [ "a.json"; "b.json" ]) readiness.Dokimos
              | other -> failwith $"{other}")

          t "policy decoding: defaults, members and fail-closed errors" (fun () ->
              let decode (text: string) =
                  use document = JsonDocument.Parse text
                  QualityEvidenceJson.decodePolicy document.RootElement

              match decode """{ "version": "1.0.0", "dokimos": "required", "dokimosBaseline": "quality/baseline.json", "requiredFacets": ["behavior-verified"], "workTypes": ["feature"] }""" with
              | Ok p ->
                  Assert.equal EvidenceRequirement.Required p.Dokimos
                  Assert.equal EvidenceRequirement.Off p.OrdoBoundary
                  Assert.equal (Some "quality/baseline.json") p.DokimosBaseline
                  Assert.equal (set [ CompletionFacet.BehaviorVerified ]) p.RequiredFacets
                  Assert.equal (Some(set [ "feature" ])) p.WorkTypes
              | Error reason -> failwith reason

              for invalid in
                  [ """{ "dokimos": "requird" }"""
                    """{ "dokimos": true }"""
                    """{ "dokimoss": "required" }"""
                    """{ "requiredFacets": ["done"] }"""
                    """{ "version": "2.0.0" }"""
                    """[]""" ] do
                  match decode invalid with
                  | Error _ -> ()
                  | Ok p -> failwith $"{invalid} must be rejected, got {p}") ]

    let private contractTests =
        [ t "real dokimos reports decode with their own verdicts" (fun () ->
              Assert.equal DokimosVerdict.Pass (dokimos "pass").Verdict
              Assert.equal DokimosVerdict.Pass (dokimos "excepted-pass").Verdict
              Assert.equal 1 (dokimos "excepted-pass").Excepted
              Assert.equal [ "EXC-0001" ] (dokimos "excepted-pass").ActiveExceptions
              Assert.equal DokimosVerdict.Regression (dokimos "regression").Verdict
              Assert.equal 4 (dokimos "regression").ExitCode
              Assert.equal DokimosVerdict.InvalidExceptions (dokimos "invalid-exceptions").Verdict
              Assert.equal DokimosVerdict.Unavailable (dokimos "unavailable").Verdict
              Assert.equal 1 (dokimos "unavailable").RulesUnavailable)

          t "real ordo assessments decode with their own recommendations" (fun () ->
              let review = ordo "require-design-review"
              Assert.equal OrdoRecommendation.RequireDesignReview review.Recommendation
              Assert.equal OrdoExceptionCoverage.NoCoverage review.Coverage
              let full = ordo "full-coverage"
              Assert.equal OrdoRecommendation.ConsiderSplitAlong full.Recommendation
              Assert.equal (Some OrdoRecommendation.RequireDesignReview) full.DowngradedFrom
              Assert.equal OrdoExceptionCoverage.Full full.Coverage
              Assert.equal OrdoExceptionCoverage.Partial (ordo "partial-coverage").Coverage
              Assert.equal OrdoRiskLevel.Low (ordo "low-no-action").Risk)

          t "real reports drive the expected decisions" (fun () ->
              let required = policy EvidenceRequirement.Required EvidenceRequirement.Required
              let decide item dok ord = assessItem required (item, "task") [] (supplied dok) (supplied ord) |> CompletionReadiness.isReady
              Assert.isTrue (decide "WI-QUALITY" (dokimos "pass") (ordo "low-no-action")) "pass + no-action"
              Assert.isTrue (decide "PRX-CORRELATION-ID" (dokimos "excepted-pass") (ordo "full-coverage")) "excepted pass + approved"
              Assert.isTrue (not (decide "WI-QUALITY" (dokimos "regression") (ordo "low-no-action"))) "regression"
              Assert.isTrue (not (decide "WI-QUALITY" (dokimos "invalid-exceptions") (ordo "low-no-action"))) "invalid exceptions"
              Assert.isTrue (not (decide "WI-QUALITY" (dokimos "unavailable") (ordo "low-no-action"))) "unavailable"
              Assert.isTrue (not (decide "PRX-USAGE-AUTOPAUSE" (dokimos "pass") (ordo "require-design-review"))) "design review"
              Assert.isTrue (not (decide "PRX-CORRELATION-ID" (dokimos "pass") (ordo "partial-coverage"))) "partial coverage")

          t "an unknown contract or schema version is unsupported, never parsed" (fun () ->
              let mutate (name: string) (change: JsonObject -> unit) =
                  let node = JsonNode.Parse(fixture name).AsObject()
                  change node
                  node.ToJsonString()

              let unsupported reading =
                  match reading with
                  | EvidenceReading.Unsupported _ -> ()
                  | other -> failwith $"expected unsupported, got {other}"

              unsupported (QualityEvidenceJson.decodeDokimos (mutate "dokimos/pass.json" (fun node -> node.["Contract"] <- JsonValue.Create "dokimos.other")))
              unsupported (QualityEvidenceJson.decodeDokimos (mutate "dokimos/pass.json" (fun node -> node.["SchemaVersion"] <- JsonValue.Create "2.0.0")))
              unsupported (QualityEvidenceJson.decodeOrdo (mutate "ordo/low-no-action.json" (fun node -> node.["schema"] <- JsonValue.Create "ordo.boundary-amplification/2")))
              ())

          t "malformed documents are malformed, never parsed" (fun () ->
              let mutate (name: string) (change: JsonObject -> unit) =
                  let node = JsonNode.Parse(fixture name).AsObject()
                  change node
                  node.ToJsonString()

              let malformed reading =
                  match reading with
                  | EvidenceReading.Malformed _ -> ()
                  | other -> failwith $"expected malformed, got {other}"

              malformed (QualityEvidenceJson.decodeDokimos "{ not json")
              malformed (QualityEvidenceJson.decodeDokimos "[]")
              malformed (QualityEvidenceJson.decodeDokimos (mutate "dokimos/pass.json" (fun node -> node.Remove "Verdict" |> ignore)))
              malformed (QualityEvidenceJson.decodeDokimos (mutate "dokimos/pass.json" (fun node -> node.["Verdict"] <- JsonValue.Create "fine")))
              malformed (QualityEvidenceJson.decodeDokimos (mutate "dokimos/pass.json" (fun node -> node.["Summary"] <- JsonValue.Create 1)))
              malformed (QualityEvidenceJson.decodeOrdo "")
              malformed (QualityEvidenceJson.decodeOrdo (fixture "dokimos/pass.json"))
              malformed (QualityEvidenceJson.decodeOrdo (mutate "ordo/low-no-action.json" (fun node -> node.Remove "risk" |> ignore)))
              malformed (QualityEvidenceJson.decodeOrdo (mutate "ordo/low-no-action.json" (fun node -> node.["recommendation"].["kind"] <- JsonValue.Create "split")))
              malformed (QualityEvidenceJson.decodeOrdo (mutate "ordo/low-no-action.json" (fun node -> node.["exception"].["coverage"] <- JsonValue.Create "most"))))

          t "the readiness record is a versioned, self-describing contract" (fun () ->
              let readiness =
                  assess (policy EvidenceRequirement.Required EvidenceRequirement.Optional) (supplied (dokimos "regression")) SourceObservation.NotSupplied

              let node = QualityEvidenceJson.readinessNode readiness
              Assert.equal "praxis.completion-readiness/1" (str (node.["schema"]))
              Assert.equal "refused" (str (node.["decision"]))
              Assert.equal "not-satisfied" (str (node.["facets"].["architectureVerified"].["status"]))
              Assert.equal true (flag (node.["facets"].["architectureVerified"].["blocking"]))
              Assert.equal "not-required" (str (node.["facets"].["releaseReady"].["status"]))
              Assert.equal "regression" (str (node.["sources"].["dokimos"].["verdict"]))
              Assert.equal "not-supplied" (str (node.["sources"].["ordoBoundary"].["observation"]))
              Assert.equal "required" (str (node.["policy"].["dokimos"]))) ]

    // ------------------------------------------------------------- CLI

    let private work root (arguments: string list) = CliHarness.ros root ("work" :: arguments)

    let private setPolicy root (policy: string) =
        CliGolden.updateJson root "ros.json" (fun config -> config.["workProtocol"].["qualityEvidence"] <- JsonNode.Parse policy)

    let private withRepository test =
        CliGolden.withRepository "ros-quality-evidence" "Quality Evidence" CliGolden.noPreparation true test

    let private start root id =
        work root [ "start"; "--id"; id; "--occurred-at"; CliGolden.at (); "--type"; "task" ] |> CliGolden.expectExit 0
        CliHarness.write root "IMPLEMENTATION-NOTES.md" "Implemented.\n"
        CliHarness.write root "TESTS-NOTES.md" "Tested.\n"

    let private complete root id (extra: (string * string) list) =
        let evidence =
            [ "implementation", "IMPLEMENTATION-NOTES.md"; "tests", "TESTS-NOTES.md" ] @ extra
            |> List.collect (fun (kind, path) -> [ "--evidence"; $"{kind}={path}" ])

        work root ([ "complete"; "--id"; id; "--occurred-at"; CliGolden.at () ] @ evidence)

    let private copyFixture root name =
        let relative = $"quality/{Path.GetFileName(name: string)}"
        CliHarness.write root relative (fixture name)
        relative

    let private completionEvent root id =
        CliGolden.events root
        |> List.filter (fun event -> CliGolden.text event "workItem" = Some id && CliGolden.text event "type" = Some "work.completed")
        |> List.tryHead

    let private cliTests =
        [ t "cli: without a policy, completion is unchanged and records no readiness" (fun () ->
              withRepository (fun root ->
                  start root "WI-LEGACY"
                  complete root "WI-LEGACY" [] |> CliGolden.expectExit 0
                  let event = completionEvent root "WI-LEGACY" |> Option.get
                  Assert.isTrue (isNull event.["completionReadiness"]) "no readiness on a legacy completion"
                  Assert.isTrue (isNull (CliGolden.contextItem root "WI-LEGACY").["completionReadiness"]) "no readiness on the item"))

          t "cli: a required dokimos regression refuses completion (exit 3); a pass report completes and is recorded" (fun () ->
              withRepository (fun root ->
                  setPolicy root """{ "version": "1.0.0", "dokimos": "required" }"""
                  start root "WI-QUALITY"

                  let context = work root [ "context"; "WI-QUALITY" ]
                  CliGolden.expectExit 0 context
                  CliGolden.contains "qualityEvidenceForCompletion" context.Out
                  CliGolden.contains "dokimos-ratchet" context.Out

                  let regression = copyFixture root "dokimos/regression.json"
                  let refused = complete root "WI-QUALITY" [ "dokimos-ratchet", regression ]
                  CliGolden.expectExit 3 refused
                  CliGolden.contains "architecture-verified not satisfied" refused.Err
                  let document = JsonNode.Parse refused.Out
                  Assert.equal "refused" (str (document.["outcome"]))
                  Assert.equal "regression" (str (document.["completionReadiness"].[0].["sources"].["dokimos"].["verdict"]))
                  Assert.equal (Some "active") (CliGolden.text (CliGolden.contextItem root "WI-QUALITY") "semanticState")
                  Assert.isTrue (completionEvent root "WI-QUALITY" |> Option.isNone) "a refusal writes no event"

                  let missing = complete root "WI-QUALITY" []
                  CliGolden.expectExit 3 missing
                  CliGolden.contains "no dokimos-ratchet evidence was supplied" missing.Err

                  let pass = copyFixture root "dokimos/pass.json"
                  let completed = complete root "WI-QUALITY" [ "dokimos-ratchet", pass ]
                  CliGolden.expectExit 0 completed
                  CliGolden.contains "\"completionReadiness\"" completed.Out

                  let event = completionEvent root "WI-QUALITY" |> Option.get
                  let record = event.["completionReadiness"]
                  Assert.equal "praxis.completion-readiness/1" (str (record.["schema"]))
                  Assert.equal "ready" (str (record.["decision"]))
                  Assert.equal "satisfied" (str (record.["facets"].["architectureVerified"].["status"]))
                  let item = CliGolden.contextItem root "WI-QUALITY"
                  Assert.equal (Some "complete") (CliGolden.text item "semanticState")
                  Assert.equal "ready" (str (item.["completionReadiness"].["decision"]))

                  CliHarness.ros root [ "validate" ] |> CliGolden.expectExit 0))

          t "cli: unavailable and invalid-exceptions reports refuse completion" (fun () ->
              withRepository (fun root ->
                  setPolicy root """{ "dokimos": "required" }"""
                  start root "WI-QUALITY"

                  for name, fragment in [ "dokimos/unavailable.json", "unavailable"; "dokimos/invalid-exceptions.json", "invalid-exceptions" ] do
                      let refused = complete root "WI-QUALITY" [ "dokimos-ratchet", copyFixture root name ]
                      CliGolden.expectExit 3 refused
                      CliGolden.contains fragment refused.Err

                  CliHarness.write root "quality/broken.json" "{ not json"
                  let malformed = complete root "WI-QUALITY" [ "dokimos-ratchet", "quality/broken.json" ]
                  CliGolden.expectExit 3 malformed
                  CliGolden.contains "malformed" malformed.Err))

          t "cli: a required ordo design review without full coverage refuses completion" (fun () ->
              withRepository (fun root ->
                  setPolicy root """{ "dokimos": "optional", "ordoBoundary": "required" }"""
                  start root "PRX-USAGE-AUTOPAUSE"
                  let refused = complete root "PRX-USAGE-AUTOPAUSE" [ "ordo-boundary", copyFixture root "ordo/require-design-review.json" ]
                  CliGolden.expectExit 3 refused
                  CliGolden.contains "require-design-review" refused.Err))

          t "cli: an invalid policy fails closed" (fun () ->
              withRepository (fun root ->
                  setPolicy root """{ "dokimos": "requird" }"""
                  start root "WI-QUALITY"
                  let refused = complete root "WI-QUALITY" []
                  CliGolden.expectExit 3 refused
                  CliGolden.contains "workProtocol.qualityEvidence" refused.Err
                  Assert.equal (Some "active") (CliGolden.text (CliGolden.contextItem root "WI-QUALITY") "semanticState"))) ]

    let tests = judgementTests @ combinationTests @ policyTests @ contractTests @ cliTests
