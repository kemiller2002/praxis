namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Praxis.Contracts.Requirements
open Praxis.Domain.Requirements

/// REQ-PRES-001 (DC-008): the deterministic requirements-preservation gate.
/// Scenarios A-L run the real CLI against the fixtures in
/// tests/fixtures/requirements-preservation (see its README for the
/// provenance of the copied Indy-init and Rehearsal 3 files); the remaining
/// cases exercise the pure gate on the same decoded fixtures.
[<RequireQualifiedAccess>]
module RequirementsPreservationTests =
    let private t name run = { Name = $"requirements preservation: {name}"; Run = run }

    let rec private repositoryRoot (directory: DirectoryInfo) =
        if Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "requirements-preservation")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate tests/fixtures/requirements-preservation"
        else
            repositoryRoot directory.Parent

    let private fixtures =
        lazy (Path.Combine(repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory())), "tests", "fixtures", "requirements-preservation"))

    let private fixture (relative: string) = Path.Combine(fixtures.Value, relative)

    let private str (node: JsonNode) = node.GetValue<string>()

    /// `at "a.0.b" node`: a dotted path, numeric segments indexing arrays.
    let private at (path: string) (node: JsonNode) =
        path.Split('.')
        |> Array.fold
            (fun (n: JsonNode) segment ->
                match Int32.TryParse segment with
                | true, index -> n.[index]
                | _ -> n.[segment])
            node

    let private fixtureOperator = "fixture-operator"
    let private fixtureReviewer = "fixture-reviewer (human, test fixture)"

    let private sha256File (path: string) =
        File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    type private Run =
        { Exit: int
          Out: string
          Document: JsonNode option }

    /// `requirements preservation verify` on a fixture directory (`causa` or
    /// `r3`), with `--json` unless `text` is set.
    let private verifyIn (set: string) (disposition: string) (derived: string) (source: string) (approvals: string option) (text: bool) =
        let arguments =
            [ CliHarness.cli
              "requirements"
              "preservation"
              "verify"
              "--baseline"
              fixture $"{set}/baseline.json"
              "--disposition"
              fixture $"{set}/{disposition}"
              "--derived"
              fixture $"{set}/{derived}"
              "--source"
              fixture $"{set}/{source}" ]
            @ (approvals
               |> Option.map (fun a -> [ "--approvals"; fixture $"{set}/{a}"; "--approvals-sha256"; sha256File (fixture $"{set}/{a}") ])
               |> Option.defaultValue [])
            @ [ "--trusted-approver"; fixtureOperator; "--trusted-approver"; fixtureReviewer ]
            @ (if text then [] else [ "--json" ])

        let result = CliHarness.run "dotnet" arguments []

        { Exit = result.Exit
          Out = result.Out
          Document = (if text || result.Exit = 2 then None else Some(JsonNode.Parse result.Out)) }

    let private causa disposition derived approvals = verifyIn "causa" disposition derived "source" approvals false

    let private document (run: Run) =
        run.Document |> Option.defaultWith (fun () -> failwith $"no JSON document (exit {run.Exit}): {run.Out}")

    let private verdict run = str ((document run).["verdict"])

    let private findings (run: Run) =
        (document run).["findings"].AsArray()
        |> Seq.map (fun f ->
            str f["code"],
            str f["status"],
            (f["requirement"] |> Option.ofObj |> Option.map str),
            (f["obligation"] |> Option.ofObj |> Option.map str))
        |> Seq.toList

    let private hasFinding code obligation run =
        findings run |> List.exists (fun (c, _, _, o) -> c = code && (obligation = None || o = obligation))

    let private blocking run =
        findings run |> List.filter (fun (_, status, _, _) -> status <> "info")

    let private expect exitCode expectedVerdict run =
        Assert.equal exitCode run.Exit
        Assert.equal expectedVerdict (verdict run)

    let private obligationOutcome (run: Run) (requirement: string) (obligation: string) =
        (document run).["requirements"].AsArray()
        |> Seq.find (fun r -> str r["id"] = requirement)
        |> fun r -> r["obligations"].AsArray() |> Seq.find (fun o -> str o["id"] = obligation)

    /// Git's blob id, so fixture copies can be checked against the source
    /// repository's recorded blob ids without a checkout of it.
    let private gitBlob (path: string) =
        let bytes = File.ReadAllBytes path
        let header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\000")
        SHA1.HashData(Array.append header bytes) |> Convert.ToHexString |> _.ToLowerInvariant()

    // Pure-gate helpers over the decoded fixtures.
    let private decoded () =
        let read relative = File.ReadAllText(fixture relative)

        let ok =
            function
            | Ok value -> value
            | Error reason -> failwith reason

        let baseline = ok (PreservationJson.decodeBaseline (read "causa/baseline.json"))
        let disposition = ok (PreservationJson.decodeDisposition (read "causa/A-complete/disposition.json"))

        let observations =
            [ ({ Path = "CAUSA-UI-EXCERPT.md"
                 Text = Some(read "causa/source/CAUSA-UI-EXCERPT.md") }: SourceObservation) ]

        let criteria =
            Directory.GetFiles(fixture "causa/A-complete/derived", "*.md")
            |> Array.sort
            |> Array.toList
            |> List.collect (fun path -> Preservation.parseDerived (Path.GetFileName path) (File.ReadAllText path))

        baseline, disposition, observations, criteria

    let private replaceEntry (requirement: string) (disposition: Disposition) (set: DispositionSet) =
        { set with
            Entries =
                set.Entries
                |> List.map (fun e -> if e.Requirement = requirement then { e with Disposition = disposition } else e) }

    let private approval id covers : ApprovalRecord =
        { DecisionId = id
          Authority = "operator (test)"
          Scope = "test scope"
          Covers = covers
          Date = "2026-10-24"
          OrdoDecision = None
          ApprovedBy = Some { Id = fixtureOperator; Kind = ActorKind.Human } }

    /// The pure gate under the trust the fixtures establish: the baseline
    /// digest the disposition records, an approvals digest that matches its
    /// pin, and the fixture operator and reviewer as trusted identities.
    let private evaluate baseline observations (disposition: DispositionSet) approvals criteria =
        let trust: InputTrust =
            { BaselineSha256 = disposition.BaselineSha256 |> Option.defaultValue ""
              ApprovalsSha256 = Some "pinned"
              PinnedApprovalsSha256 = Some "pinned"
              TrustedApprovers = [ fixtureOperator; fixtureReviewer ] }

        Preservation.evaluate trust baseline observations disposition approvals criteria

    let private codes (report: PreservationReport) = report.Findings |> List.map (fun f -> f.Code, f.Status)

    // REQ-PRES-004 scenarios run the real CLI on variants written to a
    // scratch directory, so every variant is an ordinary input file.
    let private verifyArgs (arguments: string list) =
        let result = CliHarness.run "dotnet" ([ CliHarness.cli; "requirements"; "preservation"; "verify" ] @ arguments @ [ "--json" ]) []

        { Exit = result.Exit
          Out = result.Out
          Document = (if result.Exit = 2 then None else Some(JsonNode.Parse result.Out)) }

    let private causaArgs (baseline: string) (disposition: string) (derived: string) =
        [ "--baseline"; baseline; "--disposition"; disposition; "--derived"; derived; "--source"; fixture "causa/source" ]

    let private trusted = [ "--trusted-approver"; fixtureOperator; "--trusted-approver"; fixtureReviewer ]

    let private inScratch (body: string -> unit) =
        let directory = Path.Combine(Path.GetTempPath(), $"praxis-preservation-{Guid.NewGuid():N}")
        Directory.CreateDirectory directory |> ignore

        try
            body directory
        finally
            Directory.Delete(directory, true)

    /// `relative` fixture text with `edit` applied, written under `directory`.
    let private variant (directory: string) (relative: string) (edit: string -> string) =
        let original = File.ReadAllText(fixture relative)
        let edited = edit original
        Assert.isTrue (edited <> original) $"the edit of {relative} changed nothing"
        let path = Path.Combine(directory, relative.Replace('/', '_'))
        File.WriteAllText(path, edited)
        path

    let tests =
        [ t "A complete preservation passes, and the one token-less obligation is accepted only by its recorded review" (fun () ->
              let run = causa "A-complete/disposition.json" "A-complete/derived" None
              expect 0 "pass" run
              Assert.empty (blocking run)
              Assert.equal "review" (str ((obligationOutcome run "UI-016" "UI-016-O1").["decidedBy"]))
              Assert.equal "tokens" (str ((obligationOutcome run "UI-016" "UI-016-O2").["decidedBy"]))
              Assert.isTrue (hasFinding "equivalence-reviewed" (Some "UI-016-O1") run) "the review stays visible in the report"
              Assert.equal "praxis.requirements-preservation/1" (str ((document run).["schema"]))
              Assert.equal "unchanged" (document run |> at "source.documents.0.status" |> str))

          t "B UI-016 removed from the derived package fails with a missing disposition" (fun () ->
              let run = causa "B-ui016-removed/disposition.json" "B-ui016-removed/derived" None
              expect 3 "fail" run
              Assert.equal [ "disposition-missing", "fail", Some "UI-016", None ] (blocking run))

          t "C palette replaced with 'use appropriate colors' fails as a weakened value obligation" (fun () ->
              let run = causa "C-palette-weakened/disposition.json" "C-palette-weakened/derived" None
              expect 3 "fail" run
              Assert.equal [ "value-token-missing", "fail", Some "UI-016", Some "UI-016-O2" ] (blocking run)

              let tokens = (obligationOutcome run "UI-016" "UI-016-O2").["tokens"].AsArray()
              Assert.isTrue (tokens |> Seq.forall (fun t -> not (t["found"].GetValue<bool>()))) "no palette hex value is found")

          t "D palette kept but the deployed-screenshot obligation dropped fails, and remapping it elsewhere fails too" (fun () ->
              let dropped = causa "D-screenshots-removed/disposition.json" "D-screenshots-removed/derived" None
              expect 3 "fail" dropped
              Assert.equal [ "obligation-unmapped", "fail", Some "UI-016", Some "UI-016-O7" ] (blocking dropped)

              let remapped = causa "D-screenshots-removed/disposition-remapped.json" "D-screenshots-removed/derived" None
              expect 3 "fail" remapped
              Assert.equal [ "token-missing", "fail", Some "UI-016", Some "UI-016-O7" ] (blocking remapped))

          t "E an approved exclusion passes and is visible in the JSON report and the human summary" (fun () ->
              let run = causa "E-approved-exclusion/disposition.json" "E-approved-exclusion/derived" (Some "E-approved-exclusion/approvals.json")
              expect 0 "pass" run
              let exclusion = Assert.single ((document run).["exclusions"].AsArray() |> Seq.toList)
              Assert.equal "UI-004" (str exclusion["requirement"])
              Assert.equal "APR-TEST-E-001" (exclusion |> at "approval.decisionId" |> str)
              Assert.equal "operator (test fixture)" (exclusion |> at "approval.authority" |> str)
              Assert.equal "2026-10-24" (exclusion |> at "approval.date" |> str)

              let text = verifyIn "causa" "E-approved-exclusion/disposition.json" "E-approved-exclusion/derived" "source" (Some "E-approved-exclusion/approvals.json") true
              Assert.equal 0 text.Exit
              Assert.isTrue (text.Out.Contains "EXCLUDED UI-004 under APR-TEST-E-001") text.Out)

          t "F an exclusion without an approval record, or with one that does not cover it, fails" (fun () ->
              let missing = causa "F-unapproved-exclusion/disposition.json" "F-unapproved-exclusion/derived" None
              expect 3 "fail" missing
              Assert.equal [ "approval-missing", "fail", Some "UI-004", None ] (blocking missing)
              Assert.empty ((document missing).["exclusions"].AsArray() |> Seq.toList)

              let wrongScope =
                  causa "F-unapproved-exclusion/disposition.json" "F-unapproved-exclusion/derived" (Some "F-unapproved-exclusion/approvals-wrong-scope.json")

              expect 3 "fail" wrongScope
              Assert.equal [ "approval-invalid", "fail", Some "UI-004", None ] (blocking wrongScope))

          t "G a changed source baseline is detected as drift: document hash, UI-016 section and an uninventoried requirement" (fun () ->
              let run = verifyIn "causa" "A-complete/disposition.json" "A-complete/derived" "G-drift/source" None false
              expect 3 "fail" run

              Assert.equal
                  [ "source-document-changed", "drift", None, None
                    "uninventoried-requirement", "drift", Some "UI-017", None
                    "source-section-changed", "drift", Some "UI-016", None ]
                  (blocking run)

              Assert.equal "changed" (document run |> at "source.documents.0.status" |> str)
              Assert.equal 3 ((document run |> at "summary.drift").GetValue<int>()))

          t "H a declared conflict between mandatory requirements is BLOCKED until an approval decides it" (fun () ->
              let run = causa "H-conflict/disposition.json" "A-complete/derived" None
              expect 3 "blocked" run
              Assert.equal [ "conflict-unresolved", "blocked", None, None ] (blocking run)
              Assert.equal "unresolved" (document run |> at "conflicts.0.status" |> str)

              let decided = causa "H-conflict/disposition-decided.json" "A-complete/derived" (Some "H-conflict/approvals.json")
              expect 0 "pass" decided
              Assert.equal "resolved" (document decided |> at "conflicts.0.status" |> str))

          t "I one source requirement mapped to many criteria passes only when every obligation is covered" (fun () ->
              let whole = causa "I-one-to-many/disposition.json" "I-one-to-many/derived" None
              expect 0 "pass" whole
              Assert.equal 3 ((obligationOutcome whole "UI-016" "UI-016-O2").["criteria"].AsArray().Count)

              let partial = causa "I-one-to-many/disposition-partial.json" "I-one-to-many/derived" None
              expect 3 "fail" partial

              Assert.equal
                  [ "value-token-missing", "fail", Some "UI-016", Some "UI-016-O2"
                    "token-missing", "fail", Some "UI-016", Some "UI-016-O7" ]
                  (blocking partial))

          t "J a visual-identity requirement mapped to a generic accessibility criterion fails, with the token-less obligation needing review" (fun () ->
              let run = causa "J-generic-accessibility/disposition.json" "J-generic-accessibility/derived" None
              expect 3 "fail" run
              Assert.isTrue (hasFinding "value-token-missing" (Some "UI-016-O2") run) "palette false equivalence"
              Assert.isTrue (hasFinding "value-token-missing" (Some "UI-016-O4") run) "contrast thresholds false equivalence"
              Assert.isTrue (hasFinding "equivalence-unverified" (Some "UI-016-O1") run) "never silently accepted"

              Assert.isTrue
                  (findings run |> List.exists (fun (c, s, _, o) -> c = "equivalence-unverified" && s = "needs-review" && o = Some "UI-016-O1"))
                  "needs-review status")

          t "K a read-only scope's approved exclusion of write workflows passes while UI, security and accessibility stay preserved" (fun () ->
              let run = causa "K-read-only-scope/disposition.json" "K-read-only-scope/derived" (Some "K-read-only-scope/approvals.json")
              expect 0 "pass" run

              let excluded =
                  (document run).["exclusions"].AsArray()
                  |> Seq.map (fun e -> str e["requirement"], e["obligation"] |> Option.ofObj |> Option.map str)
                  |> Seq.toList

              Assert.equal [ "UI-004", None; "UI-006", Some "UI-006-O1"; "UI-006", Some "UI-006-O2" ] excluded
              Assert.equal "tokens" (str ((obligationOutcome run "UI-006" "UI-006-O3").["decidedBy"]))

              for requirement in [ "UI-005"; "UI-011"; "UI-016" ] do
                  let r = (document run).["requirements"].AsArray() |> Seq.find (fun r -> str r["id"] = requirement)
                  Assert.equal "preserved" (str r["disposition"])
                  Assert.equal "pass" (str r["outcome"]))

          t "L Rehearsal 3 regression: the original package is detected, a claimed mapping to its closest criteria fails, a corrected package passes" (fun () ->
              let original = verifyIn "r3" "disposition-original-absent.json" "derived-original" "source" None false
              expect 3 "fail" original
              Assert.equal [ "disposition-missing", "fail", Some "UI-016", None ] (blocking original)

              let files = (document original |> at "derived.files").AsArray() |> Seq.toList
              Assert.equal 8 files.Length
              // The real package: 8 requirements carrying 56 criteria.
              Assert.equal 56 (files |> List.sumBy (fun f -> f["criteria"].GetValue<int>()))

              let claimed = verifyIn "r3" "disposition-original-claimed.json" "derived-original" "source" None false
              expect 3 "fail" claimed
              Assert.isTrue (hasFinding "value-token-missing" (Some "UI-016-O2") claimed) "RQ-0008 UI-01 carries no palette"
              Assert.isTrue (hasFinding "token-missing" (Some "UI-016-O5") claimed) "RQ-0008 UI-03 is a generic colour-alone criterion"
              Assert.isTrue (hasFinding "token-missing" (Some "UI-016-O7") claimed) "RQ-0008 DEP-02 carries no screenshots"

              let corrected = verifyIn "r3" "disposition-corrected.json" "derived-corrected" "source" None false
              expect 0 "pass" corrected
              Assert.empty (blocking corrected))

          t "L fixture copies are byte-identical to the recorded Rehearsal 3 blobs and the UI-016 section of Indy-init" (fun () ->
              let blobs =
                  [ "RQ-CAUSA-2026-0001--investigation-discovery.md", "357bbc4aaef5a408bea842a6ab8d0d2aaaf6db1b"
                    "RQ-CAUSA-2026-0002--integrity-and-reconstruction.md", "f0ced569ceb517c719cef6807c25d27880efafbd"
                    "RQ-CAUSA-2026-0003--catalogue-and-summary.md", "bd927660655d5097be2e982b03dd502f15987949"
                    "RQ-CAUSA-2026-0004--evidence-and-provenance.md", "b1a01cd754d4b355ffc075cd3280e58147ab6d67"
                    "RQ-CAUSA-2026-0005--hypotheses-and-causality.md", "fed20b4be0c4ae32bb9749affdb1c1af87765633"
                    "RQ-CAUSA-2026-0006--uncertainty-actions-lifecycle.md", "b129e5a6e99d4007d81e8b038785eddbd92db309"
                    "RQ-CAUSA-2026-0007--investigation-workflows.md", "47938885fffdbe88ccf7835edf593f44bcb18457"
                    "RQ-CAUSA-2026-0008--interface-security-architecture-deployment.md", "23ed0ab80fb261838d70d5d5afd0ab1ac011e2e0" ]

              for name, blob in blobs do
                  Assert.equal blob (gitBlob (fixture $"r3/derived-original/{name}"))
                  Assert.equal blob (gitBlob (fixture $"r3/derived-corrected/{name}"))

              Assert.equal "67fe4d39f4c86a99a18a757e6ea6f543d4b190bb" (gitBlob (fixture "r3/derived-corrected/RQ-CAUSA-2026-0009--rehearsal-003-remediation.md"))

              // The section hash recorded in both baselines is the hash of
              // UI-016 in Indy-init at fc5df1c9 (checked when the fixture was made).
              let section = Preservation.section None "## UI-016: Causa semantic color system [P0]" (File.ReadAllText(fixture "r3/source/CAUSA-UI-016-section.md"))
              Assert.equal (Some "53e5178f38722dcbcbabb33bf6fbbacba8e91e4db0330bbb9aaf80d4600f5685") (section |> Option.map Preservation.sha256Text))

          t "the gate is deterministic: identical inputs give byte-identical evidence, and --output writes the same document" (fun () ->
              let first = causa "J-generic-accessibility/disposition.json" "J-generic-accessibility/derived" None
              let second = causa "J-generic-accessibility/disposition.json" "J-generic-accessibility/derived" None
              Assert.equal first.Out second.Out

              let directory = CliHarness.temporaryDirectory "praxis-preservation"
              let output = Path.Combine(directory, "evidence", "preservation.json")

              let written =
                  CliHarness.run
                      "dotnet"
                      ([ CliHarness.cli; "requirements"; "preservation"; "verify"
                         "--baseline"; fixture "causa/baseline.json"
                         "--disposition"; fixture "causa/J-generic-accessibility/disposition.json"
                         "--derived"; fixture "causa/J-generic-accessibility/derived"
                         "--source"; fixture "causa/source"
                         "--output"; output ]
                       @ trusted)
                      []

              Assert.equal 3 written.Exit
              Assert.equal first.Out (File.ReadAllText output))

          t "the gate never writes approvals, reviews or fixture files" (fun () ->
              let snapshot () =
                  Directory.GetFiles(fixtures.Value, "*", SearchOption.AllDirectories)
                  |> Array.sort
                  |> Array.map (fun path -> path, File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString)

              let before = snapshot ()
              causa "F-unapproved-exclusion/disposition.json" "F-unapproved-exclusion/derived" None |> ignore
              causa "A-complete/disposition.json" "A-complete/derived" None |> ignore
              Assert.equal before (snapshot ()))

          t "invalid input is refused with exit 2 and no verdict" (fun () ->
              let directory = CliHarness.temporaryDirectory "praxis-preservation-invalid"
              let wrong = Path.Combine(directory, "disposition.json")
              File.WriteAllText(wrong, """{"schema":"praxis.requirements-baseline/1"}""")

              let run =
                  CliHarness.run
                      "dotnet"
                      [ CliHarness.cli; "requirements"; "preservation"; "verify"
                        "--baseline"; fixture "causa/baseline.json"
                        "--disposition"; wrong
                        "--derived"; fixture "causa/A-complete/derived"
                        "--source"; fixture "causa/source" ]
                      []

              Assert.equal 2 run.Exit
              Assert.isTrue (run.Err.Contains "expected 'praxis.requirements-disposition/1'") run.Err

              let missingArgument = CliHarness.run "dotnet" [ CliHarness.cli; "requirements"; "preservation"; "verify" ] []
              Assert.equal 2 missingArgument.Exit)

          t "sections lists the heading sections and hashes a baseline pins" (fun () ->
              let run =
                  CliHarness.run
                      "dotnet"
                      [ CliHarness.cli; "requirements"; "preservation"; "sections"
                        "--source"; fixture "causa/source"
                        "--document"; "CAUSA-UI-EXCERPT.md"
                        "--pattern"; "^## (UI-\\d{3}):"
                        "--json" ]
                      []

              Assert.equal 0 run.Exit
              let node = JsonNode.Parse run.Out
              Assert.equal "praxis.requirements-sections/1" (str node["schema"])
              Assert.equal "336903badda1d4b1ba5298cd9b7f1806552dc5439d7aa394ed010514189f5c8e" (str node["sha256"])

              let ids =
                  node["sections"].AsArray()
                  |> Seq.choose (fun s -> s["id"] |> Option.ofObj |> Option.map str)
                  |> Seq.toList

              Assert.equal [ "UI-004"; "UI-005"; "UI-006"; "UI-011"; "UI-016" ] ids)

          t "not applicable needs a justification, and without an approval it needs review rather than passing" (fun () ->
              let baseline, disposition, observations, criteria = decoded ()

              let unjustified =
                  evaluate baseline observations (replaceEntry "UI-004" (Disposition.NotApplicable("", None)) disposition) [] criteria

              Assert.equal Verdict.Fail unjustified.Verdict
              Assert.isTrue (codes unjustified |> List.contains (FindingCode.NotApplicableUnjustified, FindingStatus.Fail)) "justification required"

              let unapproved =
                  evaluate baseline observations (replaceEntry "UI-004" (Disposition.NotApplicable("no commands in this build", None)) disposition) [] criteria

              Assert.equal Verdict.Blocked unapproved.Verdict
              Assert.isTrue (codes unapproved |> List.contains (FindingCode.NotApplicableUnapproved, FindingStatus.NeedsReview)) "needs review"

              let approved =
                  evaluate
                      baseline
                      observations
                      (replaceEntry "UI-004" (Disposition.NotApplicable("no commands in this build", Some "APR-NA")) disposition)
                      [ approval "APR-NA" [ "UI-004" ] ]
                      criteria

              Assert.equal Verdict.Pass approved.Verdict
              Assert.equal [ "UI-004" ] (approved.Exclusions |> List.map _.Requirement))

          t "superseded requires an approval and an existing replacement criterion" (fun () ->
              let baseline, disposition, observations, criteria = decoded ()
              let replacement = { Requirement = "RQ-TEST-0001"; Criterion = "CMD-01" }
              let superseded = replaceEntry "UI-004" (Disposition.Superseded([ replacement ], "APR-SUP")) disposition

              Assert.equal Verdict.Fail (evaluate baseline observations superseded [] criteria).Verdict
              Assert.equal Verdict.Pass (evaluate baseline observations superseded [ approval "APR-SUP" [ "UI-004" ] ] criteria).Verdict

              let dangling =
                  replaceEntry "UI-004" (Disposition.Superseded([ { replacement with Criterion = "CMD-99" } ], "APR-SUP")) disposition

              let report = evaluate baseline observations dangling [ approval "APR-SUP" [ "UI-004" ] ] criteria
              Assert.isTrue (codes report |> List.contains (FindingCode.CriterionNotFound, FindingStatus.Fail)) "replacement must exist")

          t "an approval record must name an authority, a scope and an ISO date" (fun () ->
              let baseline, disposition, observations, criteria = decoded ()
              let excluded = replaceEntry "UI-004" (Disposition.ExcludedWithApproval("APR-X", "out of scope")) disposition

              for broken in
                  [ { approval "APR-X" [ "UI-004" ] with Authority = "" }
                    { approval "APR-X" [ "UI-004" ] with Scope = " " }
                    { approval "APR-X" [ "UI-004" ] with Date = "24/10/2026" } ] do
                  let report = evaluate baseline observations excluded [ broken ] criteria
                  Assert.equal Verdict.Fail report.Verdict
                  Assert.isTrue (codes report |> List.contains (FindingCode.ApprovalInvalid, FindingStatus.Fail)) $"{broken}")

          t "an acceptance obligation mapped only to a non-acceptance criterion fails" (fun () ->
              let baseline, disposition, observations, criteria = decoded ()

              let statements =
                  criteria
                  |> List.map (fun c -> if c.Id = "VIS-01" then { c with Kind = CriterionKind.Statement } else c)

              let report = evaluate baseline observations disposition [] statements
              Assert.equal Verdict.Fail report.Verdict

              Assert.isTrue
                  (report.Findings |> List.exists (fun f -> f.Code = FindingCode.AcceptanceCriterionMissing && f.Obligation = Some "UI-016-O7"))
                  "acceptance obligations need acceptance criteria")

          t "a baseline whose obligation text is not verbatim, or whose tokens are not in it, is invalid" (fun () ->
              let baseline, disposition, observations, criteria = decoded ()

              let paraphrase =
                  { baseline with
                      Requirements =
                          baseline.Requirements
                          |> List.map (fun r ->
                              if r.Id <> "UI-016" then r
                              else
                                  { r with
                                      Obligations =
                                          r.Obligations
                                          |> List.map (fun o ->
                                              if o.Id = "UI-016-O6" then { o with Text = "Print output should be readable in grayscale." }
                                              elif o.Id = "UI-016-O9" then { o with Tokens = [ "monochrome" ] }
                                              else o) }) }

              let report = evaluate paraphrase observations disposition [] criteria
              Assert.equal Verdict.Fail report.Verdict

              let invalid =
                  report.Findings |> List.filter (fun f -> f.Code = FindingCode.BaselineInvalid) |> List.choose _.Obligation

              Assert.equal [ "UI-016-O6"; "UI-016-O9" ] (List.sort invalid))

          t "a disposition reconciled against another baseline, or naming unknown ids, fails" (fun () ->
              let baseline, disposition, observations, criteria = decoded ()
              let other = evaluate baseline observations { disposition with BaselineCommit = "0000000" } [] criteria
              Assert.isTrue (codes other |> List.contains (FindingCode.BaselineMismatch, FindingStatus.Fail)) "baseline identity"

              let extra =
                  { disposition with
                      Entries =
                          disposition.Entries
                          @ [ { Requirement = "UI-099"
                                Disposition = Disposition.Preserved [] } ] }

              let report = evaluate baseline observations extra [] criteria
              Assert.isTrue (codes report |> List.contains (FindingCode.DispositionUnknownRequirement, FindingStatus.Fail)) "unknown requirement") ]

    /// The published schemas name the contracts the decoders and renderer
    /// implement, and a real report carries every member the report schema
    /// requires, with finding codes drawn from its enumeration.
    let schemaTests =
        [ t "schemas match the decoder and report contracts" (fun () ->
              let schemaDirectory = Path.Combine(repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory())), "schemas")
              let schema name = JsonNode.Parse(File.ReadAllText(Path.Combine(schemaDirectory, name)))

              for file, title in
                  [ "praxis-requirements-baseline.schema.json", PreservationJson.baselineSchema
                    "praxis-requirements-disposition.schema.json", PreservationJson.dispositionSchema
                    "praxis-requirements-approvals.schema.json", PreservationJson.approvalsSchema
                    "praxis-requirements-preservation.schema.json", PreservationJson.reportSchema ] do
                  Assert.equal title (str (schema file).["title"])

              let report = schema "praxis-requirements-preservation.schema.json"
              let run = causa "J-generic-accessibility/disposition.json" "J-generic-accessibility/derived" None
              let produced = (document run).AsObject() |> Seq.map _.Key |> Set.ofSeq
              let required = report.["required"].AsArray() |> Seq.map str |> Set.ofSeq
              Assert.equal required produced

              let codes =
                  (report |> at "properties.findings.items.properties.code.enum").AsArray() |> Seq.map str |> Set.ofSeq

              for code, _, _, _ in findings run do
                  Assert.isTrue (codes.Contains code) $"finding code '{code}' is not in the report schema")

          t "every finding code the gate can emit is in the report schema" (fun () ->
              let schemaDirectory = Path.Combine(repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory())), "schemas")
              let report = JsonNode.Parse(File.ReadAllText(Path.Combine(schemaDirectory, "praxis-requirements-preservation.schema.json")))

              let schemaCodes =
                  (report |> at "properties.findings.items.properties.code.enum").AsArray() |> Seq.map str |> Set.ofSeq

              let emitted =
                  Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<FindingCode>)
                  |> Array.map (fun case -> (Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, [||]) :?> FindingCode) |> FindingCode.render)
                  |> Set.ofArray

              Assert.equal schemaCodes emitted) ]

    /// REQ-PRES-003: requirements opened by a bold paragraph or a list item
    /// (Indy-init PF-001..021, EXT-DATA-001..012) are sections bounded by
    /// the next requirement or heading, and criteria tables honour `\|`.
    let anchorTests =
        [ t "a paragraph-anchored requirement runs to the next requirement line or heading" (fun () ->
              let text =
                  "## Architectural contract\n\n**PF-001 [P0] One.** Define a port.\n\n**PF-002 [P0] Two.** Levels:\n1. Domain.\n2. UI.\n\nEach level must have a command.\n\n## Fixture catalog\n\n**PF-003 [P0] Three.** Last.\n"

              let pattern = Some "^\\*\\*(PF-\\d{3}) "
              Assert.equal (Some "**PF-001 [P0] One.** Define a port.") (Preservation.section pattern "**PF-001 [P0] One.** Define a port." text)

              Assert.equal
                  (Some "**PF-002 [P0] Two.** Levels:\n1. Domain.\n2. UI.\n\nEach level must have a command.")
                  (Preservation.section pattern "**PF-002 [P0] Two.** Levels:" text)

              Assert.equal (Some "**PF-003 [P0] Three.** Last.") (Preservation.section pattern "**PF-003 [P0] Three.** Last." text)

              let ids = Preservation.sections pattern text |> List.choose _.Id
              Assert.equal [ "PF-001"; "PF-002"; "PF-003" ] ids)

          t "a list-item requirement runs to the next requirement item or heading" (fun () ->
              let text = "## Requirements\n- EXT-DATA-001 [P0] First MUST hold.\n- EXT-DATA-002 [P0] Second MUST hold.\n\n## Acceptance\nA clean build.\n"
              let pattern = Some "^- (EXT-DATA-\\d{3}) "
              Assert.equal (Some "- EXT-DATA-001 [P0] First MUST hold.") (Preservation.section pattern "- EXT-DATA-001 [P0] First MUST hold." text)
              Assert.equal (Some "- EXT-DATA-002 [P0] Second MUST hold.") (Preservation.section pattern "- EXT-DATA-002 [P0] Second MUST hold." text))

          t "criteria cells keep escaped pipes as literal text" (fun () ->
              let text =
                  "| ID | Acceptance criterion |\n|---|---|\n| UIX-016 | Palette \\| Deep navy \\| `#14283F` \\| Teal `#087F8C` |\n"

              let criterion = Assert.single (Preservation.parseDerived "RQ-X-0001--x.md" text)
              Assert.equal "UIX-016" criterion.Id
              Assert.equal "Palette | Deep navy | `#14283F` | Teal `#087F8C`" criterion.Text
              Assert.equal CriterionKind.Acceptance criterion.Kind) ]

    /// REQ-PRES-003: a criterion that carries the obligation verbatim
    /// decides it deterministically; anything less still needs a review.
    let verbatimTests =
        [ t "an obligation carried verbatim (whitespace collapsed) needs no review" (fun () ->
              let baseline, disposition, observations, criteria = decoded ()
              let o1 = baseline.Requirements |> List.find (fun r -> r.Id = "UI-016") |> _.Obligations |> List.find (fun o -> o.Id = "UI-016-O1")

              let unreviewed =
                  { disposition with
                      Entries =
                          disposition.Entries
                          |> List.map (fun e ->
                              match e.Disposition with
                              | Disposition.Preserved mappings when e.Requirement = "UI-016" ->
                                  { e with
                                      Disposition =
                                          Disposition.Preserved(
                                              mappings
                                              |> List.map (fun m ->
                                                  match m.Coverage with
                                                  | ObligationCoverage.Mapped(c, _) -> { m with Coverage = ObligationCoverage.Mapped(c, None) }
                                                  | other -> m)
                                          ) }
                              | _ -> e) }

              let paraphrased = evaluate baseline observations unreviewed [] criteria
              Assert.equal Verdict.Blocked paraphrased.Verdict

              let verbatimCriteria =
                  criteria
                  |> List.map (fun c -> if c.Id = "COL-01" then { c with Text = "Carried:   " + o1.Text.Replace(" ", "\n  ") } else c)

              let verbatim = evaluate baseline observations unreviewed [] verbatimCriteria
              Assert.equal Verdict.Pass verbatim.Verdict

              let outcome =
                  verbatim.Requirements |> List.find (fun r -> r.Requirement.Id = "UI-016") |> _.Obligations |> List.find (fun o -> o.Obligation.Id = "UI-016-O1")

              Assert.equal Decision.Verbatim outcome.Decision)

          // REQ-PRES-004 (REQ-PRES-001 R1): the baseline the package claims is the one supplied.
          t "a disposition without the baseline digest is blocked, never passed" (fun () ->
              inScratch (fun scratch ->
                  let disposition = variant scratch "causa/A-complete/disposition.json" (fun t -> Text.RegularExpressions.Regex.Replace(t, ",\\s*\"sha256\": \"[0-9a-f]{64}\"", ""))
                  let run = verifyArgs (causaArgs (fixture "causa/baseline.json") disposition (fixture "causa/A-complete/derived") @ trusted)
                  expect 3 "blocked" run
                  Assert.isTrue (hasFinding "baseline-digest-missing" None run) "the missing digest is named"))

          t "an altered baseline fails on its digest even when it still parses" (fun () ->
              inScratch (fun scratch ->
                  let baseline = variant scratch "causa/baseline.json" (fun t -> t.Replace("\"priority\": \"P0\"", "\"priority\": \"P1\""))
                  let run = verifyArgs (causaArgs baseline (fixture "causa/A-complete/disposition.json") (fixture "causa/A-complete/derived") @ trusted)
                  expect 3 "fail" run
                  Assert.isTrue (hasFinding "baseline-digest-mismatch" None run) "the digest mismatch is named"))

          // REQ-PRES-004 (REQ-PRES-001 R2): only authenticated, trusted, non-agent approvals count.
          t "approvals without their published digest are blocked, and with a different digest they fail" (fun () ->
              let args approvalsArgs =
                  causaArgs (fixture "causa/baseline.json") (fixture "causa/E-approved-exclusion/disposition.json") (fixture "causa/E-approved-exclusion/derived")
                  @ [ "--approvals"; fixture "causa/E-approved-exclusion/approvals.json" ]
                  @ approvalsArgs
                  @ trusted

              let unpinned = verifyArgs (args [])
              expect 3 "blocked" unpinned
              Assert.isTrue (hasFinding "approvals-unauthenticated" None unpinned) "unauthenticated approvals are named"

              let wrong = verifyArgs (args [ "--approvals-sha256"; String('0', 64) ])
              expect 3 "fail" wrong
              Assert.isTrue (hasFinding "approvals-digest-mismatch" None wrong) "the approvals digest mismatch is named")

          t "an agent-authored or untrusted approval does not authorize an exclusion" (fun () ->
              inScratch (fun scratch ->
                  let run approvals extraTrust =
                      verifyArgs (
                          causaArgs (fixture "causa/baseline.json") (fixture "causa/E-approved-exclusion/disposition.json") (fixture "causa/E-approved-exclusion/derived")
                          @ [ "--approvals"; approvals; "--approvals-sha256"; sha256File approvals ]
                          @ extraTrust
                      )

                  let byAgent =
                      variant scratch "causa/E-approved-exclusion/approvals.json" (fun t ->
                          t.Replace("{ \"id\": \"fixture-operator\", \"kind\": \"human\" }", "{ \"id\": \"agent:anthropic/claude-code\", \"kind\": \"agent\" }"))

                  let agent = run byAgent ([ "--trusted-approver"; "agent:anthropic/claude-code" ] @ trusted)
                  expect 3 "fail" agent
                  Assert.isTrue (hasFinding "approval-agent-authored" None agent) "an agent approval is refused with its own code, even when its id is trusted"

                  let untrusted = run (fixture "causa/E-approved-exclusion/approvals.json") [ "--trusted-approver"; fixtureReviewer ]
                  expect 3 "fail" untrusted
                  Assert.isTrue (hasFinding "approval-untrusted" None untrusted) "an approver the caller does not trust is refused"))

          t "a review by an identity the caller does not trust leaves the obligation needing review" (fun () ->
              let run =
                  verifyArgs (
                      causaArgs (fixture "causa/baseline.json") (fixture "causa/A-complete/disposition.json") (fixture "causa/A-complete/derived")
                      @ [ "--trusted-approver"; fixtureOperator ]
                  )

              expect 3 "blocked" run
              Assert.isTrue (hasFinding "review-untrusted" (Some "UI-016-O1") run) "the untrusted review is named")

          // REQ-PRES-004: no silent priority downgrade.
          t "a P0 requirement carried by a lower-priority derived requirement fails" (fun () ->
              inScratch (fun scratch ->
                  let derived = Path.Combine(scratch, "derived")
                  Directory.CreateDirectory derived |> ignore

                  for path in Directory.GetFiles(fixture "causa/A-complete/derived", "*.md") do
                      let text = File.ReadAllText path
                      let lowered = if text.Contains "id: RQ-TEST-0002" then text.Replace("version: 1.0.0", "version: 1.0.0\npriority: low") else text
                      File.WriteAllText(Path.Combine(derived, Path.GetFileName path), lowered)

                  let run = verifyArgs (causaArgs (fixture "causa/baseline.json") (fixture "causa/A-complete/disposition.json") derived @ trusted)
                  expect 3 "fail" run
                  Assert.isTrue (hasFinding "priority-downgraded" None run) "the downgrade is named")) ]
