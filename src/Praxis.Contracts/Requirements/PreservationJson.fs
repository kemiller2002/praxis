namespace Praxis.Contracts.Requirements

open System
open System.Text.Json
open Praxis.Contracts
open Praxis.Domain.Requirements

/// Strict decoders for the requirements-preservation input documents
/// (`praxis.requirements-baseline/1`, `praxis.requirements-disposition/1`,
/// `praxis.requirements-approvals/1`) and the renderer of the
/// `praxis.requirements-preservation/1` report (REQ-PRES-001). Decoding is
/// pure: text in, typed value or a reason out. A document naming another
/// schema is refused, never guessed at.
[<RequireQualifiedAccess>]
module PreservationJson =
    let baselineSchema = "praxis.requirements-baseline/1"
    let dispositionSchema = "praxis.requirements-disposition/1"
    let approvalsSchema = "praxis.requirements-approvals/1"
    let reportSchema = "praxis.requirements-preservation/1"
    let sectionsSchema = "praxis.requirements-sections/1"

    type private ResultBuilder() =
        member _.Bind(value, next) = Result.bind next value
        member _.Return value = Ok value
        member _.ReturnFrom(value: Result<'T, string>) = value

    let private result = ResultBuilder()

    let private property (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Ok value
        | _ -> Error $"missing required member '{name}'"

    let private str name element =
        property name element
        |> Result.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then Ok(value.GetString())
            else Error $"member '{name}' must be a string")

    let private nonEmpty name element =
        str name element
        |> Result.bind (fun value -> if String.IsNullOrWhiteSpace value then Error $"member '{name}' must not be empty" else Ok value)

    let private optionalStr (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
        | true, value when value.ValueKind = JsonValueKind.String -> Ok(Some(value.GetString()))
        | _ -> Error $"member '{name}' must be a string or null"

    let private sequence (decode: JsonElement -> Result<'T, string>) (items: JsonElement seq) =
        items
        |> Seq.mapi (fun index item -> index, item)
        |> Seq.fold
            (fun acc (index, item) ->
                acc
                |> Result.bind (fun decoded ->
                    decode item
                    |> Result.map (fun value -> value :: decoded)
                    |> Result.mapError (fun reason -> $"[{index}]: {reason}")))
            (Ok [])
        |> Result.map List.rev

    let private array name decode (element: JsonElement) =
        property name element
        |> Result.bind (fun value ->
            if value.ValueKind <> JsonValueKind.Array then Error $"member '{name}' must be an array"
            else value.EnumerateArray() |> sequence decode |> Result.mapError (fun reason -> $"member '{name}'{reason}"))

    let private optionalArray (name: string) decode (element: JsonElement) =
        match element.TryGetProperty name with
        | false, _ -> Ok []
        | true, _ -> array name decode element

    let private stringItem (element: JsonElement) =
        if element.ValueKind = JsonValueKind.String then Ok(element.GetString())
        else Error "expected a string"

    let private obj name element =
        property name element
        |> Result.bind (fun value ->
            if value.ValueKind = JsonValueKind.Object then Ok value
            else Error $"member '{name}' must be an object")

    let private parse (text: string) (schema: string) (decode: JsonElement -> Result<'T, string>) =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error "the document must be a JSON object"
            else
                match str "schema" root with
                | Ok found when found = schema -> decode root
                | Ok found -> Error $"schema is '{found}'; expected '{schema}'"
                | Error reason -> Error reason
        with :? JsonException as error ->
            Error $"not valid JSON: {error.Message}"

    let private kind element =
        str "kind" element
        |> Result.bind (fun code ->
            match code with
            | "value" -> Ok ObligationKind.Value
            | "acceptance" -> Ok ObligationKind.Acceptance
            | "constraint" -> Ok ObligationKind.Constraint
            | other -> Error $"member 'kind' has unknown value '{other}' (expected value, acceptance or constraint)")

    let private obligation (element: JsonElement) : Result<Obligation, string> =
        result {
            let! id = nonEmpty "id" element
            let! kind = kind element
            let! text = nonEmpty "text" element
            let! tokens = optionalArray "tokens" stringItem element
            return { Id = id; Kind = kind; Text = text; Tokens = tokens }
        }
        |> Result.mapError (fun reason -> $"obligation: {reason}")

    let private requirement (element: JsonElement) : Result<SourceRequirement, string> =
        result {
            let! id = nonEmpty "id" element
            let! title = nonEmpty "title" element
            let! priority = nonEmpty "priority" element
            let! document = nonEmpty "document" element
            let! heading = nonEmpty "heading" element
            let! sha = nonEmpty "sectionSha256" element
            let! obligations = array "obligations" obligation element

            return
                { Id = id
                  Title = title
                  Priority = priority
                  Document = document
                  Heading = heading
                  SectionSha256 = sha.ToLowerInvariant()
                  Obligations = obligations }
        }
        |> Result.mapError (fun reason -> $"requirement: {reason}")

    let private document (element: JsonElement) : Result<SourceDocument, string> =
        result {
            let! path = nonEmpty "path" element
            let! sha = nonEmpty "sha256" element
            let! upstream = optionalStr "upstreamPath" element
            let! pattern = optionalStr "requirementPattern" element

            return
                { Path = path
                  Sha256 = sha.ToLowerInvariant()
                  UpstreamPath = upstream
                  RequirementPattern = pattern }
        }

    let private conflict (element: JsonElement) : Result<DeclaredConflict, string> =
        result {
            let! id = nonEmpty "id" element
            let! requirements = array "requirements" stringItem element
            let! description = nonEmpty "description" element
            let! decision = optionalStr "decision" element

            return
                { Id = id
                  Requirements = requirements
                  Description = description
                  Decision = decision }
        }

    let decodeBaseline (text: string) : Result<Baseline, string> =
        parse text baselineSchema (fun root ->
            result {
                let! id = nonEmpty "id" root
                let! source = obj "source" root
                let! repository = nonEmpty "repository" source
                let! commit = nonEmpty "commit" source
                let! documents = array "documents" document source
                let! requirements = array "requirements" requirement root
                let! conflicts = optionalArray "conflicts" conflict root

                return
                    { Id = id
                      Repository = repository
                      Commit = commit
                      Documents = documents
                      Requirements = requirements
                      Conflicts = conflicts }
            })

    let private references name element =
        array name stringItem element
        |> Result.bind (fun items ->
            items
            |> List.map CriterionRef.parse
            |> List.fold (fun acc r -> acc |> Result.bind (fun xs -> r |> Result.map (fun x -> x :: xs))) (Ok [])
            |> Result.map List.rev)

    let private review (element: JsonElement) : Result<EquivalenceReview option, string> =
        match element.TryGetProperty "review" with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Object ->
            result {
                let! reviewer = str "reviewer" value
                let! date = str "date" value
                let! basis = str "basis" value
                return Some { Reviewer = reviewer; Date = date; Basis = basis }
            }
        | _ -> Error "member 'review' must be an object"

    let private mapping (element: JsonElement) : Result<ObligationMapping, string> =
        result {
            let! obligation = nonEmpty "obligation" element

            let! coverage =
                match element.TryGetProperty "exclusion", element.TryGetProperty "criteria" with
                | (true, exclusion), (false, _) when exclusion.ValueKind = JsonValueKind.Object ->
                    result {
                        let! approval = nonEmpty "approval" exclusion
                        let! rationale = nonEmpty "rationale" exclusion
                        return ObligationCoverage.Excluded(approval, rationale)
                    }
                | (false, _), (true, _) ->
                    result {
                        let! criteria = references "criteria" element
                        let! review = review element
                        return ObligationCoverage.Mapped(criteria, review)
                    }
                | _ -> Error "a mapping has exactly one of 'criteria' (array) or 'exclusion' (object)"

            return { Obligation = obligation; Coverage = coverage }
        }
        |> Result.mapError (fun reason -> $"mapping: {reason}")

    let private entry (element: JsonElement) : Result<RequirementDisposition, string> =
        result {
            let! requirement = nonEmpty "requirement" element
            let! code = str "disposition" element
            let text name = optionalStr name element |> Result.map (Option.defaultValue "")

            let! disposition =
                match code with
                | "preserved" -> optionalArray "mappings" mapping element |> Result.map Disposition.Preserved
                | "strengthened" ->
                    result {
                        let! mappings = optionalArray "mappings" mapping element
                        let! rationale = text "rationale"
                        return Disposition.Strengthened(mappings, rationale)
                    }
                | "excluded-with-approval" ->
                    result {
                        let! approval = nonEmpty "approval" element
                        let! rationale = text "rationale"
                        return Disposition.ExcludedWithApproval(approval, rationale)
                    }
                | "superseded" ->
                    result {
                        let! replacement = references "replacement" element
                        let! approval = nonEmpty "approval" element
                        return Disposition.Superseded(replacement, approval)
                    }
                | "not-applicable" ->
                    result {
                        let! justification = text "justification"
                        let! approval = optionalStr "approval" element
                        return Disposition.NotApplicable(justification, approval)
                    }
                | other ->
                    Error
                        $"member 'disposition' has unknown value '{other}' (expected preserved, strengthened, excluded-with-approval, superseded or not-applicable)"

            return { Requirement = requirement; Disposition = disposition }
        }
        |> Result.mapError (fun reason -> $"disposition: {reason}")

    let decodeDisposition (text: string) : Result<DispositionSet, string> =
        parse text dispositionSchema (fun root ->
            result {
                let! baseline = obj "baseline" root
                let! id = nonEmpty "id" baseline
                let! commit = nonEmpty "commit" baseline
                let! baselineSha256 = optionalStr "sha256" baseline
                let! package = nonEmpty "package" root
                let! entries = array "dispositions" entry root
                let! conflicts = optionalArray "conflicts" conflict root

                return
                    { BaselineId = id
                      BaselineCommit = commit
                      BaselineSha256 = baselineSha256
                      Package = package
                      Entries = entries
                      Conflicts = conflicts }
            })

    let private actorKind (text: string) =
        match text with
        | "human" -> Ok ActorKind.Human
        | "agent" -> Ok ActorKind.Agent
        | "automation" -> Ok ActorKind.Automation
        | other -> Error $"approvedBy.kind '{other}' is not human, agent or automation"

    let private actorKindCode kind =
        match kind with
        | ActorKind.Human -> "human"
        | ActorKind.Agent -> "agent"
        | ActorKind.Automation -> "automation"

    /// `approvedBy`: who signed the approval off (REQ-PRES-001-R2).
    let private approver (element: JsonElement) : Result<Approver option, string> =
        match element.TryGetProperty "approvedBy" with
        | false, _ -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
        | true, value when value.ValueKind = JsonValueKind.Object ->
            result {
                let! id = nonEmpty "id" value
                let! kindText = nonEmpty "kind" value
                let! kind = actorKind kindText
                return Some { Id = id; Kind = kind }
            }
        | _ -> Error "member 'approvedBy' must be an object"

    let private approval (element: JsonElement) : Result<ApprovalRecord, string> =
        result {
            let! decision = nonEmpty "decisionId" element
            let! authority = str "authority" element
            let! scope = str "scope" element
            let! covers = array "covers" stringItem element
            let! date = str "date" element
            let! ordo = optionalStr "ordoDecision" element
            let! approvedBy = approver element

            return
                { DecisionId = decision
                  Authority = authority
                  Scope = scope
                  Covers = covers
                  Date = date
                  OrdoDecision = ordo
                  ApprovedBy = approvedBy }
        }
        |> Result.mapError (fun reason -> $"approval: {reason}")

    let decodeApprovals (text: string) : Result<ApprovalRecord list, string> =
        parse text approvalsSchema (array "approvals" approval)

    /// Provenance of one consumed input file, recorded in the report.
    type InputFile = { Path: string; Sha256: string }

    type DerivedFile =
        { File: InputFile
          Requirement: string option
          Criteria: int }

    type ReportInputs =
        { Baseline: InputFile
          BaselineValue: Baseline
          Disposition: InputFile
          Package: string
          Approvals: InputFile option
          /// What the caller supplied out of band (REQ-PRES-001 R1, R2).
          Trust: InputTrust
          /// The baseline digest the disposition recorded, if any.
          RecordedBaselineSha256: string option
          DerivedRoot: string
          DerivedFiles: DerivedFile list
          SourceRoot: string }

    let verdictCode verdict =
        match verdict with
        | Verdict.Pass -> "pass"
        | Verdict.Fail -> "fail"
        | Verdict.Blocked -> "blocked"

    let statusCode status =
        match status with
        | FindingStatus.Fail -> "fail"
        | FindingStatus.Drift -> "drift"
        | FindingStatus.Blocked -> "blocked"
        | FindingStatus.NeedsReview -> "needs-review"
        | FindingStatus.Info -> "info"

    let private kindCode kind =
        match kind with
        | ObligationKind.Value -> "value"
        | ObligationKind.Acceptance -> "acceptance"
        | ObligationKind.Constraint -> "constraint"

    let private optional (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some v -> writer.WriteString(name, v)
        | None -> writer.WriteNull name

    let private inputFile (writer: Utf8JsonWriter) (name: string) (file: InputFile) =
        writer.WriteStartObject name
        writer.WriteString("path", file.Path)
        writer.WriteString("sha256", file.Sha256)
        writer.WriteEndObject()

    let private approvalObject (writer: Utf8JsonWriter) (name: string) (approval: ApprovalRecord) =
        writer.WriteStartObject name
        writer.WriteString("decisionId", approval.DecisionId)
        writer.WriteString("authority", approval.Authority)
        writer.WriteString("scope", approval.Scope)
        writer.WriteString("date", approval.Date)
        writer.WriteStartArray "covers"
        approval.Covers |> List.iter writer.WriteStringValue
        writer.WriteEndArray()
        optional writer "ordoDecision" approval.OrdoDecision

        match approval.ApprovedBy with
        | Some approver ->
            writer.WriteStartObject "approvedBy"
            writer.WriteString("id", approver.Id)
            writer.WriteString("kind", actorKindCode approver.Kind)
            writer.WriteEndObject()
        | None -> writer.WriteNull "approvedBy"

        writer.WriteEndObject()

    let exitCodeOf verdict =
        match verdict with
        | Verdict.Pass -> 0
        | Verdict.Fail
        | Verdict.Blocked -> 3

    let renderReport (inputs: ReportInputs) (report: PreservationReport) =
        let count status = report.Findings |> List.filter (fun f -> f.Status = status) |> List.length

        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", reportSchema)
            writer.WriteString("verdict", verdictCode report.Verdict)
            writer.WriteNumber("exitCode", exitCodeOf report.Verdict)

            writer.WriteStartObject "baseline"
            writer.WriteString("path", inputs.Baseline.Path)
            writer.WriteString("sha256", inputs.Baseline.Sha256)
            writer.WriteString("id", inputs.BaselineValue.Id)
            writer.WriteString("repository", inputs.BaselineValue.Repository)
            writer.WriteString("commit", inputs.BaselineValue.Commit)
            optional writer "recordedSha256" inputs.RecordedBaselineSha256
            writer.WriteEndObject()

            writer.WriteStartObject "disposition"
            writer.WriteString("path", inputs.Disposition.Path)
            writer.WriteString("sha256", inputs.Disposition.Sha256)
            writer.WriteString("package", inputs.Package)
            writer.WriteEndObject()

            match inputs.Approvals with
            | Some file -> inputFile writer "approvals" file
            | None -> writer.WriteNull "approvals"

            writer.WriteStartObject "trust"
            optional writer "approvalsPinnedSha256" inputs.Trust.PinnedApprovalsSha256
            writer.WriteStartArray "trustedApprovers"
            inputs.Trust.TrustedApprovers |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WriteEndObject()

            writer.WriteStartObject "derived"
            writer.WriteString("root", inputs.DerivedRoot)
            writer.WriteStartArray "files"

            for file in inputs.DerivedFiles do
                writer.WriteStartObject()
                writer.WriteString("path", file.File.Path)
                writer.WriteString("sha256", file.File.Sha256)
                optional writer "requirement" file.Requirement
                writer.WriteNumber("criteria", file.Criteria)
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject()

            writer.WriteStartObject "source"
            writer.WriteString("root", inputs.SourceRoot)
            writer.WriteStartArray "documents"

            for document in report.Documents do
                writer.WriteStartObject()
                writer.WriteString("path", document.Document.Path)
                optional writer "upstreamPath" document.Document.UpstreamPath
                writer.WriteString("pinnedSha256", document.Document.Sha256)
                optional writer "observedSha256" document.ObservedSha256

                writer.WriteString(
                    "status",
                    match document.ObservedSha256 with
                    | None -> "missing"
                    | Some sha when sha = document.Document.Sha256 -> "unchanged"
                    | Some _ -> "changed"
                )

                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject()

            writer.WriteStartArray "requirements"

            for outcome in report.Requirements do
                writer.WriteStartObject()
                writer.WriteString("id", outcome.Requirement.Id)
                writer.WriteString("title", outcome.Requirement.Title)
                writer.WriteString("priority", outcome.Requirement.Priority)
                optional writer "disposition" (outcome.Disposition |> Option.map Disposition.code)
                writer.WriteString("outcome", verdictCode outcome.Verdict)
                writer.WriteStartArray "obligations"

                for o in outcome.Obligations do
                    writer.WriteStartObject()
                    writer.WriteString("id", o.Obligation.Id)
                    writer.WriteString("kind", kindCode o.Obligation.Kind)
                    optional writer "outcome" (o.Status |> Option.map statusCode)
                    writer.WriteStartArray "criteria"
                    o.Criteria |> List.iter (CriterionRef.render >> writer.WriteStringValue)
                    writer.WriteEndArray()
                    writer.WriteStartArray "tokens"

                    for t in o.Tokens do
                        writer.WriteStartObject()
                        writer.WriteString("token", t.Token)
                        writer.WriteBoolean("found", t.Found)
                        writer.WriteEndObject()

                    writer.WriteEndArray()

                    match o.Decision with
                    | Decision.Tokens -> writer.WriteString("decidedBy", "tokens")
                    | Decision.Verbatim -> writer.WriteString("decidedBy", "verbatim")
                    | Decision.Review review ->
                        writer.WriteString("decidedBy", "review")
                        writer.WriteStartObject "review"
                        writer.WriteString("reviewer", review.Reviewer)
                        writer.WriteString("date", review.Date)
                        writer.WriteString("basis", review.Basis)
                        writer.WriteEndObject()
                    | Decision.Exclusion approval ->
                        writer.WriteString("decidedBy", "exclusion")
                        writer.WriteString("approval", approval)
                    | Decision.Undecided -> writer.WriteString("decidedBy", "undecided")

                    writer.WriteEndObject()

                writer.WriteEndArray()
                writer.WriteEndObject()

            writer.WriteEndArray()

            writer.WriteStartArray "exclusions"

            for exclusion in report.Exclusions do
                writer.WriteStartObject()
                writer.WriteString("requirement", exclusion.Requirement)
                optional writer "obligation" exclusion.Obligation
                approvalObject writer "approval" exclusion.Approval
                writer.WriteEndObject()

            writer.WriteEndArray()

            writer.WriteStartArray "conflicts"

            for c in report.Conflicts do
                writer.WriteStartObject()
                writer.WriteString("id", c.Conflict.Id)
                writer.WriteStartArray "requirements"
                c.Conflict.Requirements |> List.iter writer.WriteStringValue
                writer.WriteEndArray()
                writer.WriteString("description", c.Conflict.Description)
                writer.WriteString("status", (if c.Resolved then "resolved" else "unresolved"))
                optional writer "decision" c.Conflict.Decision
                writer.WriteEndObject()

            writer.WriteEndArray()

            writer.WriteStartArray "findings"

            for f in report.Findings do
                writer.WriteStartObject()
                writer.WriteString("code", FindingCode.render f.Code)
                writer.WriteString("status", statusCode f.Status)
                optional writer "requirement" f.Requirement
                optional writer "obligation" f.Obligation
                writer.WriteString("message", f.Message)
                writer.WriteEndObject()

            writer.WriteEndArray()

            writer.WriteStartObject "summary"
            writer.WriteNumber("requirements", report.Requirements.Length)
            writer.WriteNumber("obligations", report.Requirements |> List.sumBy (fun r -> r.Requirement.Obligations.Length))
            writer.WriteNumber("exclusions", report.Exclusions.Length)
            writer.WriteNumber("fail", count FindingStatus.Fail)
            writer.WriteNumber("drift", count FindingStatus.Drift)
            writer.WriteNumber("blocked", count FindingStatus.Blocked)
            writer.WriteNumber("needsReview", count FindingStatus.NeedsReview)
            writer.WriteNumber("info", count FindingStatus.Info)
            writer.WriteEndObject()

            writer.WriteEndObject())

    /// The human summary: verdict, counts, every non-info finding, and every
    /// approved exclusion (so an exclusion can never pass unseen).
    let renderText (inputs: ReportInputs) (report: PreservationReport) =
        let lines =
            [ yield $"requirements preservation: {(verdictCode report.Verdict).ToUpperInvariant()}"
              yield $"baseline {inputs.BaselineValue.Id} ({inputs.BaselineValue.Repository} @ {inputs.BaselineValue.Commit}); package {inputs.Package}"

              for r in report.Requirements do
                  let disposition = r.Disposition |> Option.map Disposition.code |> Option.defaultValue "none"
                  yield $"  {(verdictCode r.Verdict).ToUpperInvariant(),-7} {r.Requirement.Id} [{r.Requirement.Priority}] {disposition}: {r.Requirement.Title}"

              for e in report.Exclusions do
                  let target = e.Obligation |> Option.defaultValue e.Requirement
                  yield $"  EXCLUDED {target} under {e.Approval.DecisionId} ({e.Approval.Authority}, {e.Approval.Date}): {e.Approval.Scope}"

              // Reviews, decisions and supersessions stay visible too; approved
              // exclusions are already listed above.
              for f in report.Findings |> List.filter (fun f -> f.Code <> FindingCode.ExclusionApproved) do
                  yield $"  {(statusCode f.Status).ToUpperInvariant()} {FindingCode.render f.Code}: {f.Message}" ]

        String.concat "\n" lines

    let renderSections (document: string) (entries: Preservation.SectionEntry list) (documentSha256: string) =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", sectionsSchema)
            writer.WriteString("document", document)
            writer.WriteString("sha256", documentSha256)
            writer.WriteStartArray "sections"

            for entry in entries do
                writer.WriteStartObject()
                optional writer "id" entry.Id
                writer.WriteString("heading", entry.Heading)
                writer.WriteString("sectionSha256", entry.Sha256)
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject())
