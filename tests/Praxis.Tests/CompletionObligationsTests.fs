namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Cli
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// PRX-QUAL-020..023 (DF-ROS-2026-A052 mechanism): work-item risk metadata,
/// the completion obligations derived from it, the design-debt and
/// verification-matrix facets, digests of every consumed evidence file and
/// the release-readiness contract. Pure decisions first, then the CLI.
[<RequireQualifiedAccess>]
module CompletionObligationsTests =
    let private str (node: JsonNode) = node.GetValue<string>()
    let private t name run = { Name = $"completion obligations: {name}"; Run = run }

    let private risk classes level =
        { ChangeClasses = classes
          Level = level
          PersistentStateImpact = false
          ExternalProtocolImpact = false
          SecurityImpact = false
          FailurePosture = None
          Tiers = []
          LiveProof = None }

    let private dims codes =
        codes |> List.map (fun code -> (WorkRisk.tryParseDimension code).Value) |> Set.ofList

    let private noObligationEvidence: ObligationObservations =
        { DesignDebt = SourceObservation.NotSupplied
          VerificationMatrix = SourceObservation.NotSupplied
          ReleaseReadiness = SourceObservation.NotSupplied }

    // ------------------------------------------------- PRX-QUAL-020 risk

    let private riskTests =
        [ t "risk metadata needs a change class and, with impact, a failure posture" (fun () ->
              match WorkRisk.validate (risk [] RiskLevel.Low) with
              | Error reason -> Assert.isTrue (reason.Contains "change class") reason
              | Ok _ -> failwith "an empty declaration must be refused"

              match WorkRisk.validate { risk [ ChangeClass.Persistence ] RiskLevel.High with PersistentStateImpact = true } with
              | Error reason -> Assert.isTrue (reason.Contains "failure posture") reason
              | Ok _ -> failwith "impact without a failure posture must be refused"

              match WorkRisk.validate { risk [ ChangeClass.Feature; ChangeClass.Feature ] RiskLevel.Low with Tiers = [ Tier.Domain; Tier.Domain ] } with
              | Ok declared -> Assert.equal ([ ChangeClass.Feature ], [ Tier.Domain ]) (declared.ChangeClasses, declared.Tiers)
              | Error reason -> failwith reason)

          t "work without risk metadata has no obligations" (fun () ->
              let none = WorkRisk.obligations None
              Assert.isTrue (not (WorkRisk.hasObligations none)) "legacy behaviour is unchanged"
              Assert.isTrue (not (WorkRisk.hasObligations (WorkRisk.obligations (Some(risk [ ChangeClass.Documentation ] RiskLevel.Low))))) "low-risk docs owe nothing")

          t "high-risk work owes a design-debt declaration" (fun () ->
              let obligations = WorkRisk.obligations (Some(risk [ ChangeClass.Feature ] RiskLevel.High))
              Assert.isTrue obligations.DesignDebtDeclaration "high risk declares its debt"
              Assert.equal Set.empty obligations.VerificationDimensions)

          t "persistence work owes a matrix with concurrency but no live effect" (fun () ->
              let obligations = WorkRisk.obligations (Some(risk [ ChangeClass.Persistence ] RiskLevel.Medium))
              Assert.equal (dims [ "happy-path"; "negative"; "corruption"; "compatibility"; "recovery"; "concurrency" ]) obligations.VerificationDimensions
              Assert.isTrue (not obligations.DesignDebtDeclaration) "medium risk owes no debt declaration")

          t "remote-execution work and a declared live proof owe a live effect" (fun () ->
              Assert.isTrue ((WorkRisk.obligations (Some(risk [ ChangeClass.RemoteExecution ] RiskLevel.Low))).VerificationDimensions.Contains VerificationDimension.LiveEffect) "remote"

              let declared = { risk [ ChangeClass.Feature ] RiskLevel.Low with SecurityImpact = true; FailurePosture = Some FailurePosture.FailClosed; LiveProof = Some "hook denies a real call" }
              let obligations = WorkRisk.obligations (Some declared)
              Assert.isTrue (obligations.VerificationDimensions.Contains VerificationDimension.LiveEffect) "live proof"
              Assert.isTrue (not (obligations.VerificationDimensions.Contains VerificationDimension.Concurrency)) "no shared state, no concurrency")

          t "the work update risk options parse into a validated declaration" (fun () ->
              Assert.equal WorkRiskInput.NotGiven (WorkRiskArguments.parse [ "--id"; "X"; "--title"; "T" ])

              match WorkRiskArguments.parse [ "--change-class"; "persistence"; "--change-class"; "security"; "--risk-level"; "critical"; "--state-impact"; "--failure-posture"; "fail-closed"; "--tier"; "domain"; "--tier"; "infrastructure"; "--live-proof"; "restart keeps the hold" ] with
              | WorkRiskInput.Given declared ->
                  Assert.equal [ ChangeClass.Persistence; ChangeClass.Security ] declared.ChangeClasses
                  Assert.equal RiskLevel.Critical declared.Level
                  Assert.isTrue declared.PersistentStateImpact "state impact"
                  Assert.equal (Some FailurePosture.FailClosed) declared.FailurePosture
                  Assert.equal [ Tier.Domain; Tier.Infrastructure ] declared.Tiers
                  Assert.equal (Some "restart keeps the hold") declared.LiveProof
              | other -> failwith $"{other}"

              for arguments, fragment in
                  [ [ "--change-class"; "rocket"; "--risk-level"; "low" ], "rocket"
                    [ "--change-class"; "feature" ], "--risk-level is required"
                    [ "--change-class"; "feature"; "--risk-level"; "low"; "--tier"; "ui" ], "ui"
                    [ "--change-class"; "feature"; "--risk-level"; "low"; "--security-impact" ], "failure posture" ] do
                  match WorkRiskArguments.parse arguments with
                  | WorkRiskInput.Invalid reason -> Assert.isTrue (reason.Contains fragment) reason
                  | other -> failwith $"expected Invalid for {arguments}, got {other}")

          t "an invalid risk declaration rejects the update; a valid one is planned" (fun () ->
              let request risk: WorkUpdateRequest =
                  { Id = "WI-0001"
                    QueueContainsId = true
                    ContextContainsId = false
                    Title = None
                    Description = None
                    Tags = None
                    Priority = None
                    Risk = risk
                    OccurredAt = "2026-10-06T00:00:00.000Z" }

              Assert.equal (WorkUpdateOutcome.Rejected(WorkUpdateRejection.InvalidRisk "bad")) (WorkUpdate.plan (request (WorkRiskInput.Invalid "bad")))
              let declared = risk [ ChangeClass.Feature ] RiskLevel.Low

              match WorkUpdate.plan (request (WorkRiskInput.Given declared)) with
              | WorkUpdateOutcome.Planned plan -> Assert.equal (WorkRiskChange.Set declared) plan.Risk
              | other -> failwith $"{other}")

          t "praxis.work-risk/1 round-trips and refuses unknown values" (fun () ->
              let declared =
                  { risk [ ChangeClass.RemoteExecution ] RiskLevel.High with
                      ExternalProtocolImpact = true
                      FailurePosture = Some FailurePosture.Indeterminate
                      Tiers = [ Tier.Application ]
                      LiveProof = Some "remote run" }

              use document = JsonDocument.Parse((QualityEvidenceJson.riskNode declared).ToJsonString())
              Assert.equal (Ok declared) (QualityEvidenceJson.decodeRisk document.RootElement)

              use bad = JsonDocument.Parse """{"schema":"praxis.work-risk/1","changeClasses":["feature"],"level":"extreme"}"""

              match QualityEvidenceJson.decodeRisk bad.RootElement with
              | Error reason -> Assert.isTrue (reason.Contains "extreme") reason
              | Ok _ -> failwith "an unknown level must be refused") ]

    // ------------------------------------------ PRX-QUAL-021 design debt

    let private debt prototype entries =
        SourceObservation.Supplied(
            "quality/debt.json",
            EvidenceReading.Parsed
                { WorkItem = "WI-1"
                  Prototype = prototype
                  Entries = entries }
        )

    let private entry id =
        { WorkItem = id
          Rationale = "slice collapsed the store into the CLI"
          Risk = "state writes bypass the journal"
          CompromisedBoundary = "Infrastructure/Work" }

    let private judgeDebt observation =
        CompletionReadiness.judgeDesignDebt "WI-1" (set [ "WI-DEBT" ]) observation

    let private passed judgement =
        match judgement with
        | SourceJudgement.Passed _ -> ()
        | other -> failwith $"expected Passed, got {other}"

    let private failedWith (fragment: string) judgement =
        match judgement with
        | SourceJudgement.Failed reasons -> Assert.isTrue (reasons |> List.exists (fun reason -> reason.Contains fragment)) $"{reasons}"
        | other -> failwith $"expected Failed mentioning '{fragment}', got {other}"

    let private unavailableWith (fragment: string) judgement =
        match judgement with
        | SourceJudgement.Unavailable reasons -> Assert.isTrue (reasons |> List.exists (fun reason -> reason.Contains fragment)) $"{reasons}"
        | other -> failwith $"expected Unavailable mentioning '{fragment}', got {other}"

    let private debtTests =
        [ t "a declaration of no known debt passes" (fun () -> judgeDebt (debt false []) |> passed)

          t "debt tracked by a recorded open item passes" (fun () -> judgeDebt (debt false [ entry "WI-DEBT" ]) |> passed)

          t "untracked debt fails: the item itself or an unrecorded or closed item" (fun () ->
              judgeDebt (debt false [ entry "WI-1" ]) |> failedWith "separate work item"
              judgeDebt (debt false [ entry "WI-GONE" ]) |> failedWith "not a recorded open work item")

          t "a prototype cannot become canonical without a debt entry" (fun () ->
              judgeDebt (debt true []) |> failedWith "prototype"
              judgeDebt (debt true [ entry "WI-DEBT" ]) |> passed)

          t "a declaration for another item, or none at all, is unavailable" (fun () ->
              CompletionReadiness.judgeDesignDebt "WI-2" Set.empty (debt false []) |> unavailableWith "not 'WI-2'"
              judgeDebt SourceObservation.NotSupplied |> unavailableWith "no design-debt evidence")

          t "praxis.design-debt/1 decoding is strict" (fun () ->
              match QualityEvidenceJson.decodeDesignDebt """{"schema":"praxis.design-debt/1","workItem":"WI-1","knownDebt":"none","entries":[]}""" with
              | EvidenceReading.Parsed declaration -> Assert.equal [] declaration.Entries
              | other -> failwith $"{other}"

              for document, expected in
                  [ """{"schema":"praxis.design-debt/1","workItem":"WI-1","knownDebt":"declared","entries":[]}""", "malformed"
                    """{"schema":"praxis.design-debt/1","workItem":"WI-1","knownDebt":"none","entries":[{"workItem":"WI-2","rationale":"r","risk":"r","compromisedBoundary":"b"}]}""", "malformed"
                    """{"schema":"praxis.design-debt/1","workItem":"WI-1","knownDebt":"declared","entries":[{"workItem":"WI-2","rationale":" ","risk":"r","compromisedBoundary":"b"}]}""", "malformed"
                    """{"schema":"praxis.design-debt/2","workItem":"WI-1"}""", "unsupported" ] do
                  match QualityEvidenceJson.decodeDesignDebt document, expected with
                  | EvidenceReading.Malformed _, "malformed"
                  | EvidenceReading.Unsupported _, "unsupported" -> ()
                  | other, _ -> failwith $"expected {expected} for {document}, got {other}") ]

    // ------------------------------------ PRX-QUAL-022 verification matrix

    let private row code status evidence =
        { Dimension = (WorkRisk.tryParseDimension code).Value
          Status = status
          Evidence = evidence }

    let private ev kind reference =
        { Kind = kind
          Reference = reference
          Result = "passed" }

    let private integration = [ ev VerificationEvidenceKind.IntegrationTest "PersistenceTests" ]

    let private matrix rows =
        SourceObservation.Supplied("quality/matrix.json", EvidenceReading.Parsed { WorkItem = "WI-1"; Rows = rows })

    let private persistenceDims = dims [ "happy-path"; "negative"; "corruption"; "compatibility"; "recovery"; "concurrency" ]

    let private fullRows =
        [ for code in [ "happy-path"; "negative"; "corruption"; "compatibility"; "recovery"; "concurrency" ] -> row code MatrixRowStatus.Met integration ]
        @ [ row "live-effect" (MatrixRowStatus.NotApplicable "no live boundary") [] ]

    let private judgeMatrix required rows =
        CompletionReadiness.judgeVerificationMatrix "WI-1" required (fun reference -> reference.StartsWith "src/") (matrix rows)

    let private matrixTests =
        [ t "a complete matrix with real evidence passes" (fun () -> judgeMatrix persistenceDims fullRows |> passed)

          t "a missing required dimension, a not-met row and a not-applicable required row fail" (fun () ->
              judgeMatrix persistenceDims (fullRows |> List.filter (fun r -> r.Dimension <> VerificationDimension.Recovery)) |> failedWith "recovery has no row"

              judgeMatrix persistenceDims (fullRows |> List.map (fun r -> if r.Dimension = VerificationDimension.Negative then { r with Status = MatrixRowStatus.NotMet } else r))
              |> failedWith "negative is not met"

              judgeMatrix persistenceDims (fullRows |> List.map (fun r -> if r.Dimension = VerificationDimension.Concurrency then { r with Status = MatrixRowStatus.NotApplicable "single writer"; Evidence = [] } else r))
              |> failedWith "cannot be not-applicable")

          t "compilation or unit tests alone never satisfy the matrix" (fun () ->
              judgeMatrix persistenceDims (fullRows |> List.map (fun r -> if r.Dimension = VerificationDimension.HappyPath then { r with Evidence = [ ev VerificationEvidenceKind.Build "dotnet build" ] } else r))
              |> failedWith "compilation alone"

              judgeMatrix persistenceDims (fullRows |> List.map (fun r -> { r with Evidence = if r.Status = MatrixRowStatus.Met then [ ev VerificationEvidenceKind.UnitTest "UnitTests" ] else [] }))
              |> failedWith "unit tests and compilation alone")

          t "a met row needs evidence, a live effect needs live evidence, and locations must exist" (fun () ->
              judgeMatrix persistenceDims (fullRows |> List.map (fun r -> if r.Dimension = VerificationDimension.HappyPath then { r with Evidence = [] } else r))
              |> failedWith "without evidence"

              let liveRequired = Set.add VerificationDimension.LiveEffect persistenceDims
              let withLive evidence = fullRows |> List.map (fun r -> if r.Dimension = VerificationDimension.LiveEffect then { r with Status = MatrixRowStatus.Met; Evidence = evidence } else r)
              judgeMatrix liveRequired (withLive integration) |> failedWith "live evidence"
              judgeMatrix liveRequired (withLive [ ev VerificationEvidenceKind.Live "remote run 42" ]) |> passed

              judgeMatrix persistenceDims (fullRows |> List.map (fun r -> if r.Dimension = VerificationDimension.Recovery then { r with Evidence = [ ev VerificationEvidenceKind.Location "docs/missing.md:3" ] } else r))
              |> failedWith "does not exist")

          t "praxis.verification-matrix/1 decoding is strict" (fun () ->
              let document rows = $$"""{"schema":"praxis.verification-matrix/1","workItem":"WI-1","rows":[{{rows}}]}"""
              let good = """{"dimension":"happy-path","status":"met","evidence":[{"kind":"integration-test","reference":"T","result":"passed"}]}"""

              match QualityEvidenceJson.decodeVerificationMatrix (document good) with
              | EvidenceReading.Parsed parsed -> Assert.equal 1 parsed.Rows.Length
              | other -> failwith $"{other}"

              for rows in
                  [ good + "," + good
                    """{"dimension":"happy-path","status":"not-applicable","evidence":[]}"""
                    """{"dimension":"smoke","status":"met","evidence":[]}"""
                    """{"dimension":"happy-path","status":"met","evidence":[{"kind":"vibes","reference":"T","result":"ok"}]}""" ] do
                  match QualityEvidenceJson.decodeVerificationMatrix (document rows) with
                  | EvidenceReading.Malformed _ -> ()
                  | other -> failwith $"expected Malformed for {rows}, got {other}") ]

    // ------------------------------ PRX-QUAL-023 release readiness, digests

    let private release verdict checks =
        SourceObservation.Supplied(
            "quality/release.json",
            EvidenceReading.Parsed
                { ReleaseName = "praxis"
                  Version = "3.8.0"
                  Commit = "abc123"
                  CheckedAt = "2026-10-06T00:00:00Z"
                  Verdict = verdict
                  Checks = checks }
        )

    let private check name status = { Name = name; Status = status; Evidence = "ci run 1" }

    let private releaseTests =
        [ t "a ready release whose checks all passed is release-ready" (fun () ->
              CompletionReadiness.judgeRelease (release ReleaseVerdict.Ready [ check "suite" ReleaseCheckStatus.Passed; check "attest" ReleaseCheckStatus.Passed ])
              |> passed)

          t "a ready verdict over a failed or skipped check is inconsistent, never a pass" (fun () ->
              CompletionReadiness.judgeRelease (release ReleaseVerdict.Ready [ check "suite" ReleaseCheckStatus.Skipped ]) |> unavailableWith "inconsistent"
              CompletionReadiness.judgeRelease (release ReleaseVerdict.Ready []) |> unavailableWith "no checks")

          t "a not-ready release fails release-ready with its failing checks" (fun () ->
              CompletionReadiness.judgeRelease (release ReleaseVerdict.NotReady [ check "attest" ReleaseCheckStatus.Failed ]) |> failedWith "attest failed")

          t "a required release-ready facet is unavailable without evidence and satisfied with it" (fun () ->
              let policy = { QualityEvidencePolicies.legacyDefault with RequiredFacets = set [ CompletionFacet.ReleaseReady ] }
              let facetOf (readiness: ItemReadiness) = readiness.Facets |> List.find (fun facet -> facet.Facet = CompletionFacet.ReleaseReady)

              let without = CompletionReadinessOperations.assess policy CompletionFacts.none (fun _ -> true) ("WI-1", "task") [] SourceObservation.NotSupplied SourceObservation.NotSupplied noObligationEvidence []
              Assert.isTrue (facetOf without).Blocking "absent release evidence blocks a required facet"

              let observations = { noObligationEvidence with ReleaseReadiness = release ReleaseVerdict.Ready [ check "suite" ReleaseCheckStatus.Passed ] }
              let withEvidence = CompletionReadinessOperations.assess policy CompletionFacts.none (fun _ -> true) ("WI-1", "task") [] SourceObservation.NotSupplied SourceObservation.NotSupplied observations []

              match (facetOf withEvidence).Status with
              | FacetStatus.Satisfied _ -> ()
              | other -> failwith $"{other}")

          t "praxis.release-readiness/1 decoding is strict" (fun () ->
              let good = """{"schema":"praxis.release-readiness/1","release":{"name":"praxis","version":"3.8.0","commit":"abc"},"checkedAt":"2026-10-06T00:00:00Z","verdict":"ready","checks":[{"name":"suite","status":"passed","evidence":"ci"}]}"""

              match QualityEvidenceJson.decodeReleaseReadiness good with
              | EvidenceReading.Parsed parsed -> Assert.equal "3.8.0" parsed.Version
              | other -> failwith $"{other}"

              match QualityEvidenceJson.decodeReleaseReadiness (good.Replace("\"ready\"", "\"maybe\"")) with
              | EvidenceReading.Malformed _ -> ()
              | other -> failwith $"{other}")

          t "the digest of a consumed evidence file is the sha256 of its bytes" (fun () ->
              let root = Path.Combine(Path.GetTempPath(), "praxis-digest-" + Guid.NewGuid().ToString("N"))
              Directory.CreateDirectory(Path.Combine(root, "quality")) |> ignore

              try
                  File.WriteAllText(Path.Combine(root, "quality", "a.json"), "{}")
                  let expected = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes "{}")).ToLowerInvariant()
                  Assert.equal (Some expected) (FileCompletionReadiness.digest root "quality/a.json")
                  Assert.equal None (FileCompletionReadiness.digest root "quality/missing.json")
                  Assert.isTrue (FileCompletionReadiness.locationExists root "quality/a.json:1") "an existing line"
                  Assert.isTrue (not (FileCompletionReadiness.locationExists root "quality/a.json:2")) "beyond the last line"
                  Assert.isTrue (not (FileCompletionReadiness.locationExists root "../outside.json")) "outside the repository"
              finally
                  Directory.Delete(root, true))

          t "the gate records every consumed evidence file with its digest" (fun () ->
              let sources: QualityEvidenceSources =
                  { ReadDokimos = fun _ -> failwith "unused"
                    ReadOrdo = fun _ -> failwith "unused"
                    ReadDesignDebt = fun _ -> EvidenceReading.Parsed { WorkItem = "WI-1"; Prototype = false; Entries = [] }
                    ReadVerificationMatrix = fun _ -> failwith "unused"
                    ReadReleaseReadiness = fun _ -> failwith "unused"
                    Digest = fun path -> Some $"sha256:{path.Length}"
                    LocationExists = fun _ -> true }

              let facts = { CompletionFacts.none with Risk = fun _ -> Some(risk [ ChangeClass.Feature ] RiskLevel.High) }
              let provided = [ { Type = "design-debt"; Path = "quality/debt.json" }; { Type = "implementation"; Path = "src/a.fs" } ]

              match CompletionReadinessOperations.gate sources facts (Ok QualityEvidencePolicies.legacyDefault) [ "WI-1", "task" ] provided with
              | CompletionGateOutcome.Ready [ readiness ] ->
                  Assert.equal [ { Type = "design-debt"; Path = "quality/debt.json"; Sha256 = Some "sha256:17" } ] readiness.ConsumedEvidence
                  let node = QualityEvidenceJson.readinessNode readiness
                  Assert.equal "sha256:17" (str (node.["consumedEvidence"].[0].["sha256"]))
                  Assert.equal true (node.["obligations"].["designDebtDeclaration"].GetValue<bool>())
                  Assert.equal "high" (str (node.["risk"].["level"]))
                  Assert.equal "satisfied" (str (node.["facets"].["designDebtDeclared"].["status"]))
              | other -> failwith $"{other}") ]

    // ---------------------------------------------------------------- CLI

    let private work root (arguments: string list) = CliHarness.ros root ("work" :: arguments)

    let private withRepository test =
        CliGolden.withRepository "ros-completion-obligations" "Completion Obligations" CliGolden.noPreparation true test

    let private start root id =
        work root [ "start"; "--id"; id; "--occurred-at"; CliGolden.at (); "--type"; "task" ] |> CliGolden.expectExit 0
        CliHarness.write root "IMPLEMENTATION-NOTES.md" "Implemented.\n"
        CliHarness.write root "TESTS-NOTES.md" "Tested.\n"

    let private complete root id (extra: (string * string) list) =
        let evidence =
            [ "implementation", "IMPLEMENTATION-NOTES.md"; "tests", "TESTS-NOTES.md" ] @ extra
            |> List.collect (fun (kind, path) -> [ "--evidence"; $"{kind}={path}" ])

        work root ([ "complete"; "--id"; id; "--occurred-at"; CliGolden.at () ] @ evidence)

    let private matrixDocument id liveRow =
        let rows =
            [ for code in [ "happy-path"; "negative"; "corruption"; "compatibility"; "recovery"; "concurrency" ] ->
                  $$"""{"dimension":"{{code}}","status":"met","evidence":[{"kind":"integration-test","reference":"PersistenceTests","result":"passed"}]}""" ]
            @ [ liveRow ]
            |> String.concat ","

        $$"""{"schema":"praxis.verification-matrix/1","workItem":"{{id}}","rows":[{{rows}}]}"""

    let private cliTests =
        [ t "cli: risk metadata on a high-risk persistence item makes design debt and a matrix completion obligations" (fun () ->
              withRepository (fun root ->
                  start root "WI-RISKY"

                  work root [ "update"; "--id"; "WI-RISKY"; "--occurred-at"; CliGolden.at (); "--change-class"; "persistence"; "--risk-level"; "high"; "--state-impact"; "--failure-posture"; "fail-closed"; "--tier"; "infrastructure" ]
                  |> CliGolden.expectExit 0

                  let invalid = work root [ "update"; "--id"; "WI-RISKY"; "--occurred-at"; CliGolden.at (); "--change-class"; "persistence"; "--risk-level"; "severe" ]
                  CliGolden.expectExit 1 invalid
                  CliGolden.contains "invalid risk metadata" invalid.Err

                  let context = work root [ "context"; "WI-RISKY" ]
                  CliGolden.expectExit 0 context
                  CliGolden.contains "\"designDebtDeclaration\": true" context.Out
                  CliGolden.contains "verification-matrix" context.Out

                  let refused = complete root "WI-RISKY" []
                  CliGolden.expectExit 3 refused
                  CliGolden.contains "design-debt-declared unavailable" refused.Err
                  CliGolden.contains "verification-matrix-satisfied unavailable" refused.Err
                  Assert.equal (Some "active") (CliGolden.text (CliGolden.contextItem root "WI-RISKY") "semanticState")

                  CliHarness.write root "quality/debt.json" """{"schema":"praxis.design-debt/1","workItem":"WI-RISKY","knownDebt":"declared","entries":[{"workItem":"WI-UNKNOWN","rationale":"r","risk":"r","compromisedBoundary":"Infrastructure"}]}"""
                  CliHarness.write root "quality/matrix.json" (matrixDocument "WI-RISKY" """{"dimension":"live-effect","status":"not-applicable","reason":"no live boundary","evidence":[]}""")
                  let untracked = complete root "WI-RISKY" [ "design-debt", "quality/debt.json"; "verification-matrix", "quality/matrix.json" ]
                  CliGolden.expectExit 3 untracked
                  CliGolden.contains "not a recorded open work item" untracked.Err

                  CliHarness.write root "quality/debt.json" """{"schema":"praxis.design-debt/1","workItem":"WI-RISKY","knownDebt":"none","entries":[]}"""
                  let completed = complete root "WI-RISKY" [ "design-debt", "quality/debt.json"; "verification-matrix", "quality/matrix.json" ]
                  CliGolden.expectExit 0 completed

                  let item = CliGolden.contextItem root "WI-RISKY"
                  let record = item.["completionReadiness"]
                  Assert.equal "ready" (str (record.["decision"]))
                  Assert.equal "satisfied" (str (record.["facets"].["verificationMatrixSatisfied"].["status"]))
                  let digests = record.["consumedEvidence"].AsArray() |> Seq.map (fun entry -> str entry.["type"], (str entry.["sha256"]).StartsWith "sha256:") |> Seq.toList
                  Assert.equal [ "design-debt", true; "verification-matrix", true ] digests
                  CliHarness.ros root [ "validate" ] |> CliGolden.expectExit 0))

          t "cli: an item without risk metadata completes exactly as before" (fun () ->
              withRepository (fun root ->
                  start root "WI-PLAIN"
                  complete root "WI-PLAIN" [] |> CliGolden.expectExit 0
                  Assert.isTrue (isNull (CliGolden.contextItem root "WI-PLAIN").["completionReadiness"]) "no readiness record")) ]

    let tests = riskTests @ debtTests @ matrixTests @ releaseTests @ cliTests
