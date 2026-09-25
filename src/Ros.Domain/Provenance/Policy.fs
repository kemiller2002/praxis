namespace Ros.Domain.Provenance

open System
open Ros.Domain.Artifacts

[<RequireQualifiedAccess>]
type Severity =
    | Error
    | Warning
    | Info

[<RequireQualifiedAccess>]
module Severity =
    let code severity =
        match severity with
        | Severity.Error -> "error"
        | Severity.Warning -> "warning"
        | Severity.Info -> "info"

type ProvenanceFinding =
    { Severity: Severity
      Path: string
      Field: string
      Code: string
      Message: string }

/// Repository provenance policy (`ros.json` `provenance`). Without a policy
/// the repository is in legacy mode: malformed provenance is still an
/// error, but absent provenance is only informational. With `Enforce`, a
/// record created at or after `RequiredSince` (every record, when unset)
/// must carry provenance; older records remain readable legacy records.
type ProvenancePolicy =
    { Enforce: bool
      RequiredSince: DateTimeOffset option }

[<RequireQualifiedAccess>]
type RecordKind =
    | Event
    | BacklogItem
    | Artifact

[<RequireQualifiedAccess>]
module RecordKind =
    let code kind =
        match kind with
        | RecordKind.Event -> "event"
        | RecordKind.BacklogItem -> "backlog-item"
        | RecordKind.Artifact -> "artifact"

/// What a record declares: an events-style single `actor`, or an
/// accumulating `provenance` block (backlog items, canonical artifacts).
[<RequireQualifiedAccess>]
type Declared =
    | ActorRecord of ArtifactValue
    | ProvenanceRecord of ArtifactValue
    | Absent

/// One record subject to the provenance invariant.
type ProvenanceSubject =
    { Kind: RecordKind
      RecordId: string
      Path: string
      Field: string
      /// When the record came into existence (`created`, `createdAt`,
      /// `occurredAt`). Decides legacy versus new under the policy.
      CreatedAt: string option
      Declared: Declared
      /// A pre-provenance free-form author string (`author_agent`,
      /// `createdBy`). Reported, never converted into structured provenance.
      LegacyAuthor: string option }

[<RequireQualifiedAccess>]
module ProvenancePolicy =
    let legacy =
        { Enforce = false
          RequiredSince = None }

    /// A record is new -- and so held to the invariant -- when the policy is
    /// enforced and the record was created at or after the cutoff. A record
    /// whose creation time cannot be read is new only when there is no
    /// cutoff (a repository that requires provenance everywhere).
    let isNew (policy: ProvenancePolicy) (createdAt: string option) =
        policy.Enforce
        && match policy.RequiredSince with
           | None -> true
           | Some cutoff ->
               createdAt
               |> Option.bind Timestamp.tryParse
               |> Option.map (fun created ->
                   // A date-only value (`2026-09-25`) counts as that whole day.
                   let isDateOnly = createdAt |> Option.exists (fun text -> text.Trim().Length = 10)
                   if isDateOnly then created.AddDays 1.0 > cutoff else created >= cutoff)
               |> Option.defaultValue false

[<RequireQualifiedAccess>]
module ProvenanceValidation =
    let private finding severity (subject: ProvenanceSubject) (field: string) code message =
        { Severity = severity
          Path = subject.Path
          Field = if field.Length = 0 then subject.Field else $"{subject.Field}.{field}"
          Code = code
          Message = $"{RecordKind.code subject.Kind} {subject.RecordId}: {message}" }

    let private structural subject (issues: ProvenanceIssue list) =
        issues |> List.map (fun issue -> finding Severity.Error subject issue.Field issue.Code issue.Message)

    /// Semantic grading of one actor: honest `unknown` values are allowed,
    /// but on a new record they are surfaced so a missing self-identification
    /// is visible rather than silent.
    let private actorFindings subject isNew (prefix: string) (actor: Actor) =
        let field name = if prefix.Length = 0 then name else $"{prefix}.{name}"
        let newOrInfo = if isNew then Severity.Warning else Severity.Info

        [ if actor.Kind = ActorKind.Unknown then
              yield finding newOrInfo subject (field "kind") "unknown-actor-kind" "actor kind is unknown; set ROS_ACTOR_KIND (agent, human, or automation)"
          if actor.Kind <> ActorKind.Unknown && not (Attribute.isKnown actor.Id) then
              yield finding newOrInfo subject (field "id") "unknown-actor-id" $"{ActorKind.code actor.Kind} actor did not identify itself; set ROS_ACTOR"
          // An agent is expected to work inside a Praxis execution; tooling
          // automation (an installer, a CI step) often has none to bind.
          if actor.Kind = ActorKind.Agent && not (Attribute.isKnown actor.ExecutionId) then
              yield finding newOrInfo subject (field "executionId") "unbound-execution" "no Praxis execution is bound; run 'work begin' or set ROS_EXECUTION_ID"
          if actor.Kind = ActorKind.Automation && not (Attribute.isKnown actor.ExecutionId) then
              yield finding Severity.Info subject (field "executionId") "unbound-execution" "automation recorded without a Praxis execution"
          match actor.Assurance with
          | Assurance.Declared level ->
              yield finding Severity.Info subject (field "assurance") "unverified-assurance" $"assurance '{level}' is preserved but not verified by this Praxis version"
          | Assurance.SelfReported -> () ]

    let private contributionFindings subject isNew (provenance: Provenance) =
        let created =
            provenance.Contributions
            |> List.filter (fun contribution -> contribution.Operation = Operation.Created)

        let times =
            provenance.Contributions |> List.choose (fun contribution -> Timestamp.tryParse contribution.At)

        [ if created.Length > 1 then
              yield finding Severity.Error subject "contributions" "duplicate-authorship" "more than one 'created' contribution; later contributors record 'modified'"
          match provenance.Contributions with
          | first :: _ when created.Length = 1 && first.Operation <> Operation.Created ->
              yield finding Severity.Error subject "contributions" "authorship-not-first" "'created' must be the first contribution"
          | _ -> ()
          if isNew && created.IsEmpty && not provenance.Contributions.IsEmpty then
              yield finding Severity.Error subject "contributions" "missing-creator" "a new record must record its 'created' contribution"
          if times.Length = provenance.Contributions.Length
             && times |> List.pairwise |> List.exists (fun (earlier, later) -> later < earlier) then
              yield finding Severity.Warning subject "contributions" "non-chronological" "contributions are not in chronological order"
          yield!
              provenance.Contributions
              |> List.mapi (fun index contribution -> actorFindings subject isNew $"contributions[{index}].actor" contribution.Actor)
              |> List.concat ]

    let validateSubject (policy: ProvenancePolicy) (subject: ProvenanceSubject) : ProvenanceFinding list =
        let isNew = ProvenancePolicy.isNew policy subject.CreatedAt

        match subject.Declared with
        | Declared.ActorRecord value ->
            let actor, issues = ProvenanceCodec.readActor "" value
            structural subject issues @ actorFindings subject isNew "" actor
        | Declared.ProvenanceRecord value ->
            let provenance, issues = ProvenanceCodec.readProvenance value
            structural subject issues @ contributionFindings subject isNew provenance
        | Declared.Absent when isNew ->
            [ finding Severity.Error subject "" "missing-provenance" "record requires machine-readable provenance under the repository provenance policy" ]
        | Declared.Absent ->
            let legacyNote =
                subject.LegacyAuthor
                |> Option.map (fun author -> $"; legacy declared author '{author}' is unstructured, unverified, and not converted")
                |> Option.defaultValue ""

            [ finding Severity.Info subject "" "legacy-unattributed" $"legacy record without structured provenance{legacyNote}" ]

    let private compareFindings (left: ProvenanceFinding) (right: ProvenanceFinding) =
        compare
            (left.Path, left.Field, left.Code, left.Message)
            (right.Path, right.Field, right.Code, right.Message)

    let validate (policy: ProvenancePolicy) (subjects: ProvenanceSubject list) =
        subjects |> List.collect (validateSubject policy) |> List.sortWith compareFindings

    /// A rewrite check for a record whose earlier state is known (the
    /// committed version of a file being edited): every earlier contribution
    /// must survive, in order, as a prefix.
    let preservation (path: string) (field: string) (before: Provenance) (after: Provenance) =
        if Provenance.preserves before after then
            []
        else
            [ { Severity = Severity.Error
                Path = path
                Field = field
                Code = "provenance-rewritten"
                Message = "existing contributions were removed or altered; provenance is append-only" } ]

    /// A record whose content changed relative to its committed version
    /// without a new contribution was modified without attribution. Only a
    /// local working-tree check (CI sees committed content), and a warning:
    /// Praxis cannot judge whether an edit was material.
    let unrecordedModification (policy: ProvenancePolicy) (path: string) (field: string) (before: Provenance) (after: Provenance) =
        if policy.Enforce && after.Contributions.Length <= before.Contributions.Length then
            [ { Severity = Severity.Warning
                Path = path
                Field = field
                Code = "unrecorded-modification"
                Message = "changed since its committed version without a new contribution; run 'provenance record' for a material change" } ]
        else
            []
