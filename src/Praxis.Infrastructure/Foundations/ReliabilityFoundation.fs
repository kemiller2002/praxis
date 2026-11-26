namespace Praxis.Infrastructure.Foundations

open System
open System.Text.Json.Nodes
open Praxis.Domain.Foundations

/// One report-only reliability finding as `foundations verify` shows it.
/// Its severity is always `info`: it never fails verification.
type ReliabilityReportFinding =
    { Code: string
      Severity: string
      Message: string
      Remediation: string }

/// The report-only reliability section of `foundations verify`
/// (RQ-ROS-2026-A038, DF-ROS-2026-A076). It never changes `passed`.
type ReliabilitySection =
    { Configuration: string
      Assessment: ReliabilityAssessment
      Findings: ReliabilityReportFinding list }

[<RequireQualifiedAccess>]
module ReliabilityFoundation =
    /// Pure: the assessment as a report section; every finding is `info`.
    let section (assessment: ReliabilityAssessment) : ReliabilitySection =
        { Configuration = ReliabilityDeclaration.RelativePath
          Assessment = assessment
          Findings =
            assessment.Findings
            |> List.map (fun item ->
                { Code = $"ECHELON-FND-RELIABILITY-{item.Suffix}"
                  Severity = "info"
                  Message = $"{item.Requirement}: {item.Message}"
                  Remediation = item.Remediation }) }

    /// The I/O edge: read the declaration and answer path existence under `root`.
    let verify (today: DateOnly) (root: string) : ReliabilitySection =
        ReliabilityDeclarationReader.read root
        |> ReliabilityDeclarationReader.parse
        |> ReliabilityDeclaration.assess (ReliabilityDeclarationReader.fileExists root) today
        |> section

    /// Today's UTC date, for exception expiry.
    let today () = DateOnly.FromDateTime DateTime.UtcNow

    let statusText =
        function
        | ReliabilityStatus.Absent -> "absent"
        | ReliabilityStatus.Present -> "present"
        | ReliabilityStatus.Invalid -> "invalid"

    /// Pure: the text-report lines. A family shows its effective state; a
    /// downgraded claim reads `unverified` with the declared state and reasons.
    let textLines (report: ReliabilitySection) =
        let assessment = report.Assessment

        [ yield ""
          yield $"Reliability declaration (report-only, RQ-ROS-2026-A038): {statusText assessment.Status} ({report.Configuration})"
          match assessment.SystemClass with
          | Some value -> yield $"  system class: {value}"
          | None -> ()
          for key, value in assessment.Tenets do
              let shown = value |> Option.defaultValue "(undeclared)"
              yield $"  tenet {key}: {shown}"
          for family in assessment.Families do
              if family.Reasons.IsEmpty then
                  yield $"  family {family.Family}: {family.Effective}"
              else
                  let reasons = String.Join("; ", family.Reasons)
                  yield $"  family {family.Family}: {family.Effective} (declared {family.Declared}; {reasons})"
          for finding in report.Findings do
              yield $"  NOTE {finding.Code} {finding.Message}"
              yield $"    REPAIR {finding.Remediation}"
          if not report.Findings.IsEmpty then
              yield $"  {report.Findings.Length} reliability note(s); report-only, never a verification failure" ]

    let private optional (value: string option) =
        match value with
        | Some text -> JsonValue.Create(text) :> JsonNode
        | None -> null

    let private strings (values: string list) =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value))
        array

    /// Pure: the `reliability` member of the `--json` report.
    let json (report: ReliabilitySection) : JsonObject =
        let assessment = report.Assessment
        let node = JsonObject()
        node["configuration"] <- JsonValue.Create report.Configuration
        node["mode"] <- JsonValue.Create "report-only"
        node["status"] <- JsonValue.Create(statusText assessment.Status)
        node["systemClass"] <- optional assessment.SystemClass
        let tenets = JsonObject()
        assessment.Tenets |> List.iter (fun (key, value) -> tenets[key] <- optional value)
        node["tenets"] <- tenets
        let families = JsonArray()

        for family in assessment.Families do
            let item = JsonObject()
            item["family"] <- JsonValue.Create family.Family
            item["declared"] <- JsonValue.Create family.Declared
            item["effective"] <- JsonValue.Create family.Effective
            item["reasons"] <- strings family.Reasons
            families.Add(item)

        node["families"] <- families
        let findings = JsonArray()

        for finding in report.Findings do
            let item = JsonObject()
            item["code"] <- JsonValue.Create finding.Code
            item["capability"] <- JsonValue.Create "reliability"
            item["severity"] <- JsonValue.Create finding.Severity
            item["message"] <- JsonValue.Create finding.Message
            item["remediation"] <- JsonValue.Create finding.Remediation
            findings.Add(item)

        node["findings"] <- findings
        node
