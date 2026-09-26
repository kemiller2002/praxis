namespace Ros.Domain.Provenance

open System
open System.Text.RegularExpressions
open Ros.Domain.Artifacts

[<RequireQualifiedAccess>]
type FindingSeverity =
    | Error
    | Warning
    | Info

[<RequireQualifiedAccess>]
module FindingSeverity =
    let code severity =
        match severity with
        | FindingSeverity.Error -> "error"
        | FindingSeverity.Warning -> "warning"
        | FindingSeverity.Info -> "info"

type ProvenanceFinding =
    { Severity: FindingSeverity
      Path: string
      Field: string
      Message: string }

/// Repository provenance policy (`ros.json` `provenance`). When absent,
/// provenance is not required anywhere -- a repository that predates this
/// capability keeps validating exactly as before -- but any provenance a
/// record does carry is still checked structurally. When enforced, every
/// canonical artifact created on or after `RequiredFrom` must record its
/// contributions; artifacts created earlier are legacy and are never
/// required to acquire invented history.
type ProvenancePolicy =
    { Enforced: bool
      RequiredFrom: string option
      RequireOriginator: Set<string> }

[<RequireQualifiedAccess>]
module ProvenancePolicy =
    let defaultRequireOriginator = Set.ofList [ "RQ" ]

    let notConfigured =
        { Enforced = false
          RequiredFrom = None
          RequireOriginator = defaultRequireOriginator }

    let private datePattern = Regex("^[0-9]{4}-[0-9]{2}-[0-9]{2}", RegexOptions.CultureInvariant)

    /// The `yyyy-MM-dd` prefix of a date or timestamp, or `None` when the
    /// value cannot be compared honestly.
    let dateOf (value: string) =
        let trimmed = value.Trim()
        if datePattern.IsMatch trimmed then Some trimmed[..9] else None

/// A read-only view of one execution record, projected to its actor.
type ExecutionActorView = { ExecutionId: string; Actor: Actor }

type ProvenanceValidationRequest =
    { Policy: ProvenancePolicy
      Documents: ArtifactDocument list
      Executions: Map<string, Actor>
      KnownIdentifiers: Set<string> }

/// Author fields that predate structured provenance. They are self-declared
/// free text, so they are reported as "legacy-declared" and never converted
/// into structured provenance (which would manufacture certainty).
[<RequireQualifiedAccess>]
module LegacyAttribution =
    let fields = [ "author_agent"; "created_by_agent"; "owner_agent"; "source_author" ]

    let declared (document: ArtifactDocument) =
        fields
        |> List.choose (fun field ->
            match ArtifactDocument.tryMetadata field document with
            | Some(ArtifactValue.Text value) when value.Trim().Length > 0 && value <> "replace-me" -> Some(field, value)
            | _ -> None)

[<RequireQualifiedAccess>]
module ProvenanceValidation =
    let private finding severity path field message =
        { Severity = severity
          Path = path
          Field = field
          Message = message }

    let private textField name (document: ArtifactDocument) =
        match ArtifactDocument.tryMetadata name document with
        | Some(ArtifactValue.Text value) -> Some value
        | _ -> None

    let private recordHint (document: ArtifactDocument) operation =
        $"run './ros provenance record --path {document.RelativePath} --operation {operation}' inside the responsible work execution"

    let private crossCheck request (document: ArtifactDocument) (provenance: ArtifactProvenance) =
        provenance.Contributions
        |> List.collect (fun contribution ->
            let field = $"provenance.contributions.{contribution.Key}"

            let executionFindings =
                match Contribution.execution contribution with
                | None ->
                    // A foreign (EXT-...) execution belongs to another
                    // Echelon system: carried verbatim, never cross-checkable
                    // here, and never an error in itself (RQ-ROS-2026-A013).
                    match Contribution.foreignExecution contribution with
                    | Some foreignId ->
                        let system = Contribution.foreignSystem foreignId |> Option.defaultValue "unknown"

                        [ finding
                              FindingSeverity.Info
                              document.RelativePath
                              field
                              $"execution '{foreignId}' belongs to Echelon system '{system}'; its self-reported identity is carried verbatim and cannot be cross-checked in this repository" ]
                    | None -> []
                | Some executionId ->
                    match request.Executions |> Map.tryFind executionId with
                    | None ->
                        [ finding
                              FindingSeverity.Warning
                              document.RelativePath
                              field
                              $"execution '{executionId}' has no record in this repository (imported, pruned, or mistyped); its identity cannot be cross-checked" ]
                    | Some executionActor when not (Actor.agrees executionActor contribution.Actor) ->
                        [ finding
                              FindingSeverity.Error
                              document.RelativePath
                              $"{field}.actor"
                              $"recorded actor {Actor.describe contribution.Actor} contradicts execution '{executionId}' identity {Actor.describe executionActor}" ]
                    | Some _ -> []

            let vocabularyFindings =
                contribution.Operations
                |> List.filter (ContributionOperation.isKnown >> not)
                |> List.map (fun operation ->
                    finding
                        FindingSeverity.Warning
                        document.RelativePath
                        $"{field}.operations"
                        $"operation '{ContributionOperation.code operation}' is not known to this Praxis version; it is preserved verbatim (upgrade Praxis to interpret it)")

            let evidenceFindings =
                contribution.Evidence
                |> List.filter (fun reference ->
                    ArtifactPolicy.isValidIdentifier reference && not (request.KnownIdentifiers.Contains reference))
                |> List.map (fun reference ->
                    finding FindingSeverity.Error document.RelativePath $"{field}.evidence" $"broken reference '{reference}'")

            executionFindings @ vocabularyFindings @ evidenceFindings)

    let private policyFindings request (document: ArtifactDocument) (provenance: ArtifactProvenance option) =
        match request.Policy.Enforced, request.Policy.RequiredFrom with
        | true, Some requiredFrom ->
            let created = textField "created" document |> Option.bind ProvenancePolicy.dateOf
            let updated = textField "updated" document |> Option.bind ProvenancePolicy.dateOf
            let isNew = created |> Option.exists (fun date -> date >= requiredFrom)
            let prefix = ArtifactKinds.identifierPrefix (ArtifactDocument.identifier document)
            let contributions = provenance |> Option.map _.Contributions |> Option.defaultValue []

            let createHint = recordHint document "created"
            let modifyHint = recordHint document "modified"

            let latestDate =
                contributions |> List.map Contribution.date |> List.sort |> List.tryLast

            let createdText = created |> Option.defaultValue requiredFrom

            let missing =
                if isNew && contributions.IsEmpty then
                    [ finding
                          FindingSeverity.Error
                          document.RelativePath
                          ArtifactProvenance.FieldName
                          $"created {createdText}, on or after the provenance policy date {requiredFrom}, but records no provenance; {createHint}" ]
                else
                    []

            let originator =
                match provenance |> Option.bind ArtifactProvenance.originator with
                | Some _ -> []
                | None when isNew && not contributions.IsEmpty ->
                    let severity =
                        if request.Policy.RequireOriginator.Contains prefix then FindingSeverity.Error else FindingSeverity.Warning

                    [ finding
                          severity
                          document.RelativePath
                          "provenance.contributions"
                          $"no contribution records the artifact's creation ('created'); its originator is unknown" ]
                | None -> []

            // `updated` is a local calendar date while contributions are
            // UTC timestamps, so a date one day ahead of the latest
            // contribution is within time-zone tolerance, not evidence of an
            // unattributed change. (Git-based change detection,
            // `ProvenanceChanges`, is the precise check.)
            let afterTolerance (updatedDate: string) (contributionDate: string) =
                match
                    DateOnly.TryParseExact(updatedDate, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None),
                    DateOnly.TryParseExact(contributionDate, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None)
                with
                | (true, updatedDay), (true, contributionDay) -> updatedDay > contributionDay.AddDays 1
                | _ -> false

            let staleModification =
                match updated, latestDate with
                | Some updatedDate, Some contributionDate when afterTolerance updatedDate contributionDate ->
                    [ finding
                          (if isNew then FindingSeverity.Error else FindingSeverity.Warning)
                          document.RelativePath
                          "updated"
                          $"updated {updatedDate}, after its latest recorded contribution ({contributionDate}); the modification is unattributed; {modifyHint}" ]
                | Some updatedDate, None when not isNew && updatedDate >= requiredFrom ->
                    [ finding
                          FindingSeverity.Warning
                          document.RelativePath
                          "updated"
                          $"legacy artifact updated {updatedDate}, on or after the provenance policy date {requiredFrom}, without a recorded contribution; {modifyHint}" ]
                | _ -> []

            let legacy =
                if not isNew && contributions.IsEmpty then
                    let declared =
                        match LegacyAttribution.declared document with
                        | [] -> "no author is declared"
                        | values ->
                            "self-declared, unverified "
                            + (values |> List.map (fun (field, value) -> $"{field}={value}") |> String.concat ", ")

                    [ finding
                          FindingSeverity.Info
                          document.RelativePath
                          ArtifactProvenance.FieldName
                          (match created with
                           | Some date -> $"legacy artifact created {date}, before {requiredFrom}: provenance unknown ({declared})"
                           | None -> $"creation date unknown; provenance requirement not evaluated ({declared})") ]
                else
                    []

            missing @ originator @ staleModification @ legacy
        | _ -> []

    /// Every provenance finding for the repository's canonical artifacts.
    /// Errors fail validation; warnings are reported without failing it;
    /// informational findings are surfaced only by `provenance audit`.
    let findings (request: ProvenanceValidationRequest) : ProvenanceFinding list =
        request.Documents
        |> List.collect (fun document ->
            match ArtifactProvenance.parse document.Metadata with
            | Error problems ->
                problems
                |> List.map (fun problem -> finding FindingSeverity.Error document.RelativePath problem.Field problem.Message)
            | Ok provenance ->
                let structural =
                    provenance
                    |> Option.map ArtifactProvenance.problems
                    |> Option.defaultValue []
                    |> List.map (fun problem -> finding FindingSeverity.Error document.RelativePath problem.Field problem.Message)

                let crossChecks =
                    provenance |> Option.map (crossCheck request document) |> Option.defaultValue []

                structural @ crossChecks @ policyFindings request document provenance)
        |> List.sortBy (fun item -> item.Path, item.Field, item.Message)

/// One canonical artifact whose content changed relative to the base
/// revision (the working tree against `HEAD`, or everything since
/// `ROS_BASE_REF` in CI). `Before` is the artifact's front matter at the
/// base revision, or `None` when it did not exist there.
type ArtifactChange =
    { Document: ArtifactDocument
      Before: Map<string, ArtifactValue> option }

[<RequireQualifiedAccess>]
module ProvenanceChanges =
    let private contributionsOf metadata =
        match ArtifactProvenance.parse metadata with
        | Ok(Some provenance) -> Some provenance.Contributions
        | _ -> None

    /// Detects a modification that was not attributed, independently of
    /// date fields: an artifact that existed at the base revision and
    /// changed, while its recorded contributions did not change at all. Under
    /// an enforced policy this is an error for artifacts subject to the
    /// policy and a warning for legacy artifacts; without a policy nothing is
    /// required.
    let findings (policy: ProvenancePolicy) (changes: ArtifactChange list) : ProvenanceFinding list =
        match policy.Enforced, policy.RequiredFrom with
        | true, Some requiredFrom ->
            changes
            |> List.choose (fun change ->
                match change.Before with
                | None -> None
                | Some before when contributionsOf before <> contributionsOf change.Document.Metadata -> None
                | Some _ ->
                    let created =
                        match ArtifactDocument.tryMetadata "created" change.Document with
                        | Some(ArtifactValue.Text value) -> ProvenancePolicy.dateOf value
                        | _ -> None

                    let isNew = created |> Option.exists (fun date -> date >= requiredFrom)
                    let path = change.Document.RelativePath

                    Some
                        { Severity = if isNew then FindingSeverity.Error else FindingSeverity.Warning
                          Path = path
                          Field = ArtifactProvenance.FieldName
                          Message =
                            $"changed since the base revision without a recorded contribution; run './ros provenance record --path {path} --operation modified' inside the responsible work execution" })
        | _ -> []

/// A read-only view of one work-protocol event's actor attribution.
type EventActorView =
    { EventId: string
      EventType: string
      OccurredAt: string
      TelemetryExecutionIds: string list
      Actor: Result<Actor option, string> }

[<RequireQualifiedAccess>]
module EventProvenance =
    let findings (events: EventActorView list) : ProvenanceFinding list =
        events
        |> List.collect (fun event ->
            let path = $".ros/events/events.jsonl#{event.EventId}"

            match event.Actor with
            | Error message -> [ { Severity = FindingSeverity.Error; Path = path; Field = "actor"; Message = message } ]
            | Ok(Some actor) ->
                Actor.problems actor
                |> List.map (fun (field, message) ->
                    { Severity = FindingSeverity.Error
                      Path = path
                      Field = $"actor.{field}"
                      Message = message })
            | Ok None when not event.TelemetryExecutionIds.IsEmpty ->
                [ { Severity = FindingSeverity.Info
                    Path = path
                    Field = "actor"
                    Message = $"legacy {event.EventType} event records executions but no actor (written before actor attribution existed)" } ]
            | Ok None -> [])

/// One flattened (artifact, contribution) fact -- the queryable unit behind
/// provenance metrics: requirements created/modified by agent, findings by
/// agent, human corrections of agent work, agent-to-agent revisions, and
/// modification hotspots all aggregate over these rows.
type ContributionFact =
    { ArtifactId: string
      Path: string
      Kind: string
      Contribution: Contribution
      IsOrigin: bool }

type ActorContributionSummary =
    { Actor: Actor
      Artifacts: int
      Created: int
      Modified: int
      Reviewed: int
      Executions: int }

[<RequireQualifiedAccess>]
module ProvenanceIndex =
    let facts (documents: ArtifactDocument list) : ContributionFact list =
        documents
        |> List.collect (fun document ->
            match ArtifactProvenance.parse document.Metadata with
            | Ok(Some provenance) ->
                let origin = ArtifactProvenance.originator provenance |> Option.map _.Key
                let identifier = ArtifactDocument.identifier document

                provenance.Contributions
                |> List.map (fun contribution ->
                    { ArtifactId = identifier
                      Path = document.RelativePath
                      Kind = ArtifactKinds.identifierPrefix identifier
                      Contribution = contribution
                      IsOrigin = Some contribution.Key = origin })
            | _ -> [])

    let private counts (predicate: Contribution -> bool) (rows: ContributionFact list) =
        rows |> List.filter (fun row -> predicate row.Contribution) |> List.map _.Path |> List.distinct |> List.length

    /// Contributions grouped by stable actor identity (kind, id, provider,
    /// model, runtime), most active first.
    let byActor (facts: ContributionFact list) : ActorContributionSummary list =
        facts
        |> List.groupBy (fun row -> row.Contribution.Actor)
        |> List.map (fun (actor, rows) ->
            { Actor = actor
              Artifacts = rows |> List.map _.Path |> List.distinct |> List.length
              Created = counts Contribution.isCreation rows
              Modified =
                counts
                    (fun contribution ->
                        Contribution.has ContributionOperation.Modified contribution
                        || Contribution.has ContributionOperation.Superseded contribution
                        || Contribution.has ContributionOperation.Migrated contribution)
                    rows
              Reviewed =
                counts
                    (fun contribution ->
                        Contribution.has ContributionOperation.Reviewed contribution
                        || Contribution.has ContributionOperation.Approved contribution)
                    rows
              Executions = rows |> List.choose (fun row -> Contribution.anyExecution row.Contribution) |> List.distinct |> List.length })
        |> List.sortBy (fun summary -> -summary.Artifacts, Actor.describe summary.Actor)
