namespace Praxis.Tests

open System
open System.IO
open Praxis.Cli
open Praxis.Domain.Foundations
open Praxis.Infrastructure.Foundations

/// The report-only reliability applicability declaration check
/// (RQ-ROS-2026-A038 PRX-REL-001..010, DF-ROS-2026-A076).
[<RequireQualifiedAccess>]
module ReliabilityDeclarationTests =
    let private today = DateOnly(2026, 11, 26)

    /// The pure evaluator over a declaration text and a set of existing paths.
    let private assessWith (paths: string list) (json: string option) =
        json
        |> ReliabilityDeclarationReader.parse
        |> ReliabilityDeclaration.assess (fun path -> List.contains path paths) today

    let private assess json = assessWith [] (Some json)
    let private suffixes (assessment: ReliabilityAssessment) = assessment.Findings |> List.map _.Suffix
    let private requirements (assessment: ReliabilityAssessment) = assessment.Findings |> List.map _.Requirement |> List.distinct

    let private notApplicable =
        """{
  "schema": "echelon.reliability/v1",
  "systemClass": "static-website",
  "tenets": {
    "continuousAvailability": { "applicability": "not-applicable", "reason": "Static content; no accepted work." },
    "cacheReadiness": { "applicability": "not-applicable", "reason": "No server-side queries." },
    "reliabilityByConstruction": { "applicability": "not-applicable", "reason": "No runtime." }
  }
}"""

    /// A transactional application with the given families, exceptions and extra top-level members.
    let private transactional (families: string) (extra: string) =
        $"""{{
  "schema": "echelon.reliability/v1",
  "systemClass": "transactional-application",
  "tenets": {{
    "continuousAvailability": {{ "applicability": "applicable" }},
    "cacheReadiness": {{ "applicability": "applicable" }},
    "reliabilityByConstruction": {{ "applicability": "applicable" }}
  }},
  "families": [ {families} ]{extra}
}}"""

    let private design =
        """, "designEvidence": { "decision": "docs/adr/topology.md", "serviceLevels": "docs/slo.md", "failureModes": "docs/failure-modes.md" }"""

    let private withTemp action =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-reliability-{Guid.NewGuid():N}")
        Directory.CreateDirectory(root) |> ignore

        try
            action root
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)

    let private write (root: string) (relativePath: string) (content: string) =
        let path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    let tests =
        [ { Name = "reliability declaration: an absent file is reported, never a failure (PRX-REL-001)"
            Run =
              fun () ->
                  let assessment = assessWith [] None
                  Assert.equal ReliabilityStatus.Absent assessment.Status
                  Assert.equal [ "001" ] (suffixes assessment)
                  Assert.equal [ "PRX-REL-001" ] (requirements assessment) }

          { Name = "reliability declaration: text that is not a JSON object is invalid (PRX-REL-001)"
            Run =
              fun () ->
                  Assert.equal ReliabilityStatus.Invalid (assess "{ not json").Status
                  Assert.equal [ "002" ] (suffixes (assess "[]")) }

          { Name = "reliability declaration: a valid not-applicable declaration is present with no findings"
            Run =
              fun () ->
                  let assessment = assess notApplicable
                  Assert.equal ReliabilityStatus.Present assessment.Status
                  Assert.empty assessment.Findings
                  Assert.equal (Some "static-website") assessment.SystemClass
                  Assert.equal
                      [ "continuousAvailability", Some "not-applicable"
                        "cacheReadiness", Some "not-applicable"
                        "reliabilityByConstruction", Some "not-applicable" ]
                      assessment.Tenets }

          { Name = "reliability declaration: a tenet other than applicable without a reason is invalid (PRX-REL-003)"
            Run =
              fun () ->
                  let assessment = assess (notApplicable.Replace(", \"reason\": \"No runtime.\"", ""))
                  Assert.equal ReliabilityStatus.Invalid assessment.Status
                  Assert.equal [ "005" ] (suffixes assessment)
                  Assert.equal [ "PRX-REL-003" ] (requirements assessment)
                  let blank = assess (notApplicable.Replace("\"No runtime.\"", "\"  \""))
                  Assert.equal [ "005" ] (suffixes blank) }

          { Name = "reliability declaration: system class, schema, applicability and unknown properties are checked (PRX-REL-001..003)"
            Run =
              fun () ->
                  Assert.equal [ "003" ] (suffixes (assess (notApplicable.Replace("static-website", "mainframe"))))
                  Assert.equal [ "002" ] (suffixes (assess (notApplicable.Replace("echelon.reliability/v1", "echelon.reliability/v2"))))
                  Assert.equal [ "004" ] (suffixes (assess (notApplicable.Replace("\"applicability\": \"not-applicable\", \"reason\": \"No runtime.\"", "\"applicability\": \"sometimes\", \"reason\": \"No runtime.\""))))
                  Assert.equal [ "002"; "004" ] (suffixes (assess (notApplicable.Replace("\"cacheReadiness\"", "\"cacheReadyness\""))))
                  let extra = assess (notApplicable.Replace("\"systemClass\"", "\"owner\": \"me\", \"systemClass\""))
                  Assert.equal [ "002" ] (suffixes extra)
                  Assert.equal ReliabilityStatus.Invalid extra.Status }

          { Name = "reliability declaration: compliant without evidence is reported as unverified (PRX-REL-004)"
            Run =
              fun () ->
                  let assessment = assess (transactional """{ "family": "EAR-CMD", "state": "compliant" }""" (design + ", \"migrationPlan\": \"WI-1\""))
                  let family = Assert.single assessment.Families
                  Assert.equal "compliant" family.Declared
                  Assert.equal "unverified" family.Effective
                  Assert.equal [ "006" ] (suffixes assessment)
                  Assert.isTrue (assessment.Findings |> List.forall (fun item -> item.Requirement = "PRX-REL-004")) $"{assessment.Findings}" }

          { Name = "reliability declaration: evidence naming a nonexistent repository path is reported as unverified (PRX-REL-004)"
            Run =
              fun () ->
                  let families = """{ "family": "EAR-API", "state": "partially-compliant", "evidence": [ { "class": "integration-test", "ref": "tests/Api/IdempotencyTests.fs" } ] }"""
                  let text = transactional families (design + ", \"migrationPlan\": \"WI-1\"")
                  let missing = assess text
                  Assert.equal "unverified" (Assert.single missing.Families).Effective
                  Assert.equal [ "007" ] (suffixes missing)
                  let present = assessWith [ "tests/Api/IdempotencyTests.fs" ] (Some text)
                  Assert.equal "partially-compliant" (Assert.single present.Families).Effective
                  Assert.empty present.Findings }

          { Name = "reliability declaration: escaping, absolute and mutable external references never resolve; immutable ones do"
            Run =
              fun () ->
                  let exists _ = true
                  let unresolved reference =
                      match ReliabilityDeclaration.resolveEvidence exists reference with
                      | ReliabilityDeclaration.Unresolved _ -> true
                      | _ -> false

                  Assert.isTrue (unresolved "../other/evidence.md") "a path escaping the repository"
                  Assert.isTrue (unresolved "/etc/passwd") "an absolute path"
                  Assert.isTrue (unresolved "https://example.test/main/report.html") "a mutable URL"
                  Assert.isTrue (not (unresolved "https://example.test/blob/0123456789abcdef0123456789abcdef01234567/report.html")) "a commit URL"
                  Assert.isTrue (not (unresolved "docs/evidence.md#results")) "a repository path with a fragment" }

          { Name = "reliability declaration: a downgraded family is never rendered as compliant"
            Run =
              fun () ->
                  let report = Foundations.reliabilityReport (assess (transactional """{ "family": "EAR-CMD", "state": "compliant" }""" design))
                  let family = Assert.single report.Assessment.Families
                  Assert.isTrue (family.Effective <> "compliant") "a claim without evidence must not read compliant"
                  Assert.isTrue (report.Findings |> List.forall (fun item -> item.Severity = "info")) "every reliability finding is report-only" }

          { Name = "reliability declaration: an expired or incomplete exception is reported (PRX-REL-006/008)"
            Run =
              fun () ->
                  let exceptionWith expires =
                      $""", "migrationPlan": "WI-1", "exceptions": [ {{ "requirement": "EAR-CMD-004", "rationale": "Pilot.", "compensatingControls": "Manual reconciliation.", "approver": "owner", "decision": "DF-X-1", "expires": "{expires}" }} ]"""

                  let compliant = """{ "family": "EAR-CMD", "state": "noncompliant" }"""
                  Assert.equal [ "010" ] (suffixes (assess (transactional compliant (design + exceptionWith "2026-11-25"))))
                  Assert.empty (assess (transactional compliant (design + exceptionWith "2026-11-26"))).Findings
                  Assert.equal [ "009" ] (suffixes (assess (transactional compliant (design + exceptionWith "next year"))))
                  let incomplete = assess (transactional compliant (design + """, "migrationPlan": "WI-1", "exceptions": [ { "requirement": "EAR-CMD-4", "expires": "2027-01-01" } ]"""))
                  Assert.equal [ "PRX-REL-006"; "PRX-REL-008" ] (requirements incomplete) }

          { Name = "reliability declaration: applicable continuous availability without design evidence is reported (PRX-REL-007)"
            Run =
              fun () ->
                  let assessment = assess (transactional """{ "family": "EAR-OBS", "state": "noncompliant" }""" ", \"migrationPlan\": \"WI-1\", \"designEvidence\": { \"decision\": \"docs/adr/topology.md\" }")
                  Assert.equal [ "011"; "011" ] (suffixes assessment)
                  Assert.isTrue (assessment.Findings |> List.exists (fun item -> item.Message.Contains "serviceLevels")) "serviceLevels is named"
                  Assert.isTrue (assessment.Findings |> List.exists (fun item -> item.Message.Contains "failureModes")) "failureModes is named" }

          { Name = "reliability declaration: applicable and not all-compliant without a migration plan is reported (PRX-REL-010)"
            Run =
              fun () ->
                  Assert.equal [ "012" ] (suffixes (assess (transactional """{ "family": "EAR-OBS", "state": "noncompliant" }""" design)))
                  // A claim downgraded to unverified is not compliant, so it needs a plan too.
                  Assert.equal [ "006"; "012" ] (suffixes (assess (transactional """{ "family": "EAR-OBS", "state": "compliant" }""" design)))
                  let compliant = """{ "family": "EAR-OBS", "state": "compliant", "evidence": [ { "class": "operational", "ref": "docs/drill.md" } ] }"""
                  Assert.empty (assessWith [ "docs/drill.md" ] (Some(transactional compliant design))).Findings }

          { Name = "reliability declaration: a dependency must carry a pin (PRX-REL-009)"
            Run =
              fun () ->
                  let text = notApplicable.TrimEnd().TrimEnd('}') + """, "dependencies": [ { "capability": "execution", "pin": "" } ] }"""
                  Assert.equal [ "013" ] (suffixes (assess text)) }

          { Name = "reliability declaration: report-only findings never fail foundations verify"
            Run =
              fun () ->
                  withTemp (fun root ->
                      write root ".echelon/foundations.json" """{ "schemaVersion": 1, "application": "Example", "capabilities": {} }"""

                      let verified () =
                          match Foundations.verifyOn today root with
                          | Ok report -> report
                          | Error message -> failwith message

                      let absent = verified ()
                      Assert.equal ReliabilityStatus.Absent absent.Reliability.Assessment.Status
                      Assert.equal [ "ECHELON-FND-RELIABILITY-001" ] (absent.Reliability.Findings |> List.map _.Code)
                      Assert.isTrue absent.Passed "an absent declaration never fails verification"
                      Assert.empty absent.Findings

                      write root ".echelon/reliability.json" (transactional """{ "family": "EAR-CMD", "state": "compliant", "evidence": [ { "class": "automated", "ref": "missing.md" } ] }""" """, "exceptions": [ { "requirement": "EAR-CMD-001", "expires": "2020-01-01" } ]""")
                      let noisy = verified ()
                      Assert.equal ReliabilityStatus.Invalid noisy.Reliability.Assessment.Status
                      Assert.isTrue (noisy.Reliability.Findings.Length >= 5) $"{noisy.Reliability.Findings}"
                      Assert.isTrue noisy.Passed "reliability findings are report-only"
                      Assert.equal "unverified" (Assert.single noisy.Reliability.Assessment.Families).Effective
                      let json = Foundations.renderJson noisy
                      Assert.isTrue (json.Contains "\"mode\": \"report-only\"") json
                      Assert.isTrue (json.Contains "\"effective\": \"unverified\"") json
                      Assert.equal 0 (Foundations.run root true)) } ]
