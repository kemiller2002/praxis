namespace Ros.Cli

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Application.Artifacts
open Ros.Application.Git
open Ros.Contracts.Provenance
open Ros.Domain.Artifacts
open Ros.Domain.Git
open Ros.Domain.Provenance
open Ros.Domain.Telemetry
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Git
open Ros.Infrastructure.Provenance
open Ros.Infrastructure.Work

/// The `provenance` command family and the identity arguments shared by
/// every command that records who acted (`DF-ROS-2026-A036`). Extracted from
/// the composition root as `DF-ROS-2026-A035` requires for a new command
/// family: this module parses, delegates to Domain/Infrastructure, and
/// renders; provenance rules live in `Ros.Domain.Provenance`.
[<RequireQualifiedAccess>]
module ProvenanceCommands =
    let private optionValue name (arguments: string list) =
        arguments
        |> List.tryFindIndex ((=) name)
        |> Option.bind (fun index -> arguments |> List.tryItem (index + 1))

    let private optionValues name (arguments: string list) =
        arguments
        |> List.mapi (fun index value -> index, value)
        |> List.choose (fun (index, value) -> if value = name then arguments |> List.tryItem (index + 1) else None)

    let rec private positionalArgs (flagsWithValues: Set<string>) (arguments: string list) =
        match arguments with
        | flag :: _ :: rest when flagsWithValues.Contains flag -> positionalArgs flagsWithValues rest
        | token :: rest -> token :: positionalArgs flagsWithValues rest
        | [] -> []

    let private dependencyFailureMessage (failure: DependencyFailure) : string =
        let location = failure.Path |> Option.map (fun path -> $" '{path}'") |> Option.defaultValue ""

        let outcome =
            match failure.Outcome with
            | DependencyOutcome.Failed -> "failed"
            | DependencyOutcome.Indeterminate -> "indeterminate"

        $"{failure.Operation}{location} {outcome}: {failure.Message}"

    /// Identity a command may declare explicitly for the execution it creates
    /// and the records it writes. `--agent` names the stable agent identity;
    /// `--actor` is accepted as its alias so the one flag that already existed
    /// on every transition also reaches the execution record. Anything not
    /// declared falls back to the whitelisted environment (`ROS_ACTOR`,
    /// `ROS_ACTOR_KIND`, `ROS_TELEMETRY_*`, and known agent runtimes).
    let identityOverridesFrom (arguments: string list) : IdentityInputs =
        { IdentityInputs.empty with
            Provider = optionValue "--provider" arguments
            Model = optionValue "--model" arguments
            ModelVersion = optionValue "--model-version" arguments
            Runtime = optionValue "--runtime" arguments
            RuntimeVersion = optionValue "--runtime-version" arguments
            SessionId = optionValue "--session" arguments
            ConversationId = optionValue "--conversation" arguments
            RunId = optionValue "--run" arguments
            AgentId = optionValue "--agent" arguments |> Option.orElse (optionValue "--actor" arguments)
            SubagentId = optionValue "--subagent" arguments
            ActorKind = optionValue "--actor-kind" arguments }

    /// Resolves the acting identity once, before any mutation, so every record
    /// a command writes carries the same actor; an invalid explicit
    /// `--actor-kind` is an argument error (exit 2), never silently coerced.
    let withResolvedActor (arguments: string list) (run: Actor -> int) =
        match FileTelemetryExecutionRepository.resolveActor (identityOverridesFrom arguments) with
        | Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok actor -> run actor

    let private identityFlags =
        [ "--provider"; "--model"; "--model-version"; "--runtime"; "--runtime-version"; "--session"; "--conversation"; "--run"; "--agent"; "--actor"; "--subagent"; "--actor-kind" ]

    let toArtifactFinding (finding: ProvenanceFinding) : ArtifactFinding =
        { Path = finding.Path; Field = finding.Field; Message = finding.Message }

    /// Canonical artifacts changed relative to the base revision: the working
    /// tree against `HEAD`, plus everything committed since `ROS_BASE_REF`
    /// when CI supplies it, with each artifact's front matter at that base.
    /// Git being unavailable yields no changes here; work attribution already
    /// reports that condition.
    let private artifactChanges root (documents: ArtifactDocument list) : ArtifactChange list =
        let baseRef =
            match Environment.GetEnvironmentVariable "ROS_BASE_REF" with
            | null
            | "" -> None
            | value -> Some value

        let workingTree =
            match GitOperations.observe (ProcessGitRepository.create root) with
            | GitStatusObservation.Changed changes -> changes |> List.map _.Path
            | _ -> []

        let committed, revision =
            match GitOperations.compareBase (ProcessGitRepository.createBaseComparison root) baseRef with
            | GitBaseComparisonOutcome.Committed paths -> paths, baseRef |> Option.defaultValue "HEAD"
            | _ -> [], "HEAD"

        let changed = workingTree @ committed |> Set.ofList

        documents
        |> List.filter (fun document -> changed.Contains document.RelativePath)
        |> List.map (fun document ->
            { Document = document
              Before =
                ProcessGitRepository.readFileAtRevision root revision document.RelativePath
                |> Option.bind (fun text ->
                    match FrontMatter.parse document.RelativePath text with
                    | Ok before -> Some before.Metadata
                    | Error _ -> None) })

    /// Every provenance finding (all severities) for the repository: artifact
    /// provenance against the `ros.json` policy, cross-checked with execution
    /// records, plus event actor attribution. A malformed policy is itself an
    /// error and the repository is then evaluated as not configured.
    let private computeProvenanceFindings root : Result<ProvenanceFinding list, string> =
        let policyFindings, policy =
            match FileProvenanceRepository.readPolicy root with
            | Ok policy -> [], policy
            | Error message ->
                [ { Severity = FindingSeverity.Error; Path = "ros.json"; Field = "provenance"; Message = message } ],
                ProvenancePolicy.notConfigured

        match (FileArtifactRepository.create root).Load() with
        | Error failure -> Error(dependencyFailureMessage failure)
        | Ok loaded ->
            let request: ProvenanceValidationRequest =
                { Policy = policy
                  Documents = loaded.Documents
                  Executions = FileProvenanceRepository.readExecutionActors root
                  KnownIdentifiers = loaded.Documents |> List.map ArtifactDocument.identifier |> Set.ofList }

            Ok(
                policyFindings
                @ ProvenanceValidation.findings request
                @ ProvenanceChanges.findings policy (artifactChanges root loaded.Documents)
                @ EventProvenance.findings (FileProvenanceRepository.readEventViews root)
            )

    let findingsOf severity root =
        computeProvenanceFindings root
        |> Result.map (List.filter (fun (finding: ProvenanceFinding) -> finding.Severity = severity))

    /// `provenance identity [--json] [identity flags]`: who this process will be
    /// recorded as, how that was determined, and which executions are active.
    let private runProvenanceIdentity root (arguments: string list) =
        match FileTelemetryExecutionRepository.resolveIdentity (identityOverridesFrom arguments) with
        | Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok(actor, identity, source) ->
            let kindSource =
                if (optionValue "--actor-kind" arguments).IsSome then "flag"
                elif not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "ROS_ACTOR_KIND")) then "environment"
                else "discovery-mechanism"

            let active = FileProvenanceRepository.readExecutions root |> List.filter (fun view -> view.Status = "active")

            if arguments |> List.contains "--json" then
                let node = JsonObject()
                node["actor"] <- ActorJson.node actor
                let identityNode = JsonObject()

                [ "provider", Some identity.Provider
                  "model", identity.Model
                  "modelVersion", identity.ModelVersion
                  "runtime", Some identity.Runtime
                  "runtimeVersion", identity.RuntimeVersion
                  "sessionId", identity.SessionId
                  "conversationId", identity.ConversationId
                  "runId", identity.RunId
                  "agentId", identity.AgentId
                  "subagentId", identity.SubagentId ]
                |> List.iter (fun (name, value) -> identityNode[name] <- ProvenanceReportJson.optional value)

                node["identity"] <- identityNode
                let discovery = JsonObject()
                discovery["mechanism"] <- JsonValue.Create source.Mechanism
                discovery["actorKindSource"] <- JsonValue.Create kindSource
                node["discovery"] <- discovery

                node["activeExecutions"] <-
                    active
                    |> List.map (fun view ->
                        let item = JsonObject()
                        item["executionId"] <- JsonValue.Create view.ExecutionId
                        item["workItemId"] <- JsonValue.Create view.WorkItemId
                        item["startedAt"] <- JsonValue.Create view.StartedAt
                        item["actor"] <- ActorJson.node view.Actor
                        item :> JsonNode)
                    |> ProvenanceReportJson.nodes

                node["assurance"] <- JsonValue.Create "self-reported"
                printf "%s" (ProvenanceReportJson.render node)
            else
                printfn "actor: %s" (Actor.describe actor)
                printfn "discovered via: %s (actor kind from %s)" source.Mechanism kindSource

                match active with
                | [] -> printfn "active executions: none (run './ros work begin --id ID --occurred-at TIMESTAMP' to start one)"
                | views ->
                    printfn "active executions:"

                    for view in views do
                        printfn "  %s (%s): %s" view.ExecutionId view.WorkItemId (Actor.describe view.Actor)

                printfn "assurance: self-reported provenance, not authentication"

            0

    let private nowTimestamp () =
        DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Globalization.CultureInfo.InvariantCulture)

    /// `provenance record (--path PATH | --id ID | TARGET) --operation OP
    /// [--reason TEXT] [--evidence REF]* [--derived-from REF]* [--execution EXE]
    /// [--occurred-at TIMESTAMP] [--json]`: attributes a contribution to the
    /// active execution's actor, which was established once at `work begin`.
    let private runProvenanceRecord root (arguments: string list) =
        let positional =
            positionalArgs
                (Set.ofList ([ "--path"; "--id"; "--operation"; "--reason"; "--evidence"; "--derived-from"; "--execution"; "--occurred-at" ] @ identityFlags))
                arguments
            |> List.filter (fun value -> value <> "--json")

        let target =
            optionValue "--path" arguments
            |> Option.orElse (optionValue "--id" arguments)
            |> Option.orElse (List.tryHead positional)

        let operation = optionValue "--operation" arguments
        let evidence = optionValues "--evidence" arguments
        let derivedFrom = optionValues "--derived-from" arguments
        let occurredAt = optionValue "--occurred-at" arguments |> Option.defaultWith nowTimestamp
        let unsafeReferences = evidence @ derivedFrom |> List.filter (ProvenanceFrontMatter.isListSafe >> not)

        match target, operation |> Option.bind ContributionOperation.tryParse with
        | None, _ ->
            eprintfn "ERROR provenance record requires --path PATH or --id ARTIFACT-ID"
            2
        | _, None ->
            eprintfn "ERROR provenance record requires --operation {created|modified|reviewed|approved|superseded|migrated|x-...}"
            2
        | _ when not (Contribution.isTimestamp occurredAt) ->
            eprintfn "ERROR --occurred-at must be an ISO-8601 UTC timestamp (yyyy-MM-ddTHH:mm:ss[.fff]Z)"
            2
        | _ when not unsafeReferences.IsEmpty ->
            eprintfn "ERROR references must not contain whitespace, commas, brackets, or quotes: %s" (String.concat ", " unsafeReferences)
            2
        | Some targetValue, Some parsedOperation ->
            let request: ContributionRecordRequest =
                { Target = targetValue
                  Operation = parsedOperation
                  Reason = optionValue "--reason" arguments
                  Evidence = evidence
                  DerivedFrom = derivedFrom
                  ExecutionId = optionValue "--execution" arguments
                  OccurredAt = occurredAt
                  IdentityOverrides = identityOverridesFrom arguments }

            match FileProvenanceRepository.record root request with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok outcome ->
                if arguments |> List.contains "--json" then
                    let node = JsonObject()
                    node["path"] <- JsonValue.Create outcome.Path
                    node["artifactId"] <- ProvenanceReportJson.optional outcome.ArtifactId
                    node["changed"] <- JsonValue.Create outcome.Changed
                    node["contribution"] <- ProvenanceReportJson.contribution outcome.Contribution
                    node["eventId"] <- ProvenanceReportJson.optional outcome.EventId
                    node["previousSha256"] <- JsonValue.Create outcome.BeforeSha256
                    node["currentSha256"] <- JsonValue.Create outcome.AfterSha256
                    printf "%s" (ProvenanceReportJson.render node)
                elif outcome.Changed then
                    printfn
                        "recorded %s on %s by %s (%s)"
                        (ContributionOperation.code parsedOperation)
                        outcome.Path
                        (Actor.describe outcome.Contribution.Actor)
                        outcome.Contribution.Key
                else
                    printfn "already recorded: %s on %s (%s)" (ContributionOperation.code parsedOperation) outcome.Path outcome.Contribution.Key

                0

    let private loadDocumentForShow root (target: string) : Result<ArtifactDocument * ArtifactDocument list, string> =
        match (FileArtifactRepository.create root).Load() with
        | Error failure -> Error(dependencyFailureMessage failure)
        | Ok loaded ->
            let normalized = target.Replace('\\', '/')

            match
                loaded.Documents
                |> List.tryFind (fun document -> ArtifactDocument.identifier document = target || document.RelativePath = normalized)
            with
            | Some document -> Ok(document, loaded.Documents)
            | None ->
                let file = Path.Combine(root, normalized)

                if File.Exists file then
                    FrontMatter.parse normalized (File.ReadAllText file)
                    |> Result.map (fun document -> document, loaded.Documents)
                    |> Result.mapError (fun message -> $"{normalized}: front matter: {message}")
                else
                    Error $"no artifact or file matches '{target}'"

    /// `provenance show TARGET [--json]`: contributors, involvement, lineage,
    /// and legacy-declared attribution of one artifact.
    let private runProvenanceShow root (arguments: string list) =
        match arguments |> List.filter (fun value -> value <> "--json") with
        | [ target ] ->
            match loadDocumentForShow root target with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(document, documents) ->
                let parsed = ArtifactProvenance.parse document.Metadata
                let identifier = ArtifactDocument.identifier document
                let sources = Lineage.sources document
                let derivatives = if identifier.Length > 0 then Lineage.derivatives identifier documents else []

                let originOf (reference: string) =
                    documents
                    |> List.tryFind (fun candidate -> ArtifactDocument.identifier candidate = reference)
                    |> Option.map (fun candidate ->
                        match ArtifactProvenance.parse candidate.Metadata with
                        | Ok(Some provenance) -> (Involvement.describe provenance).Label
                        | Ok None -> "unattributed"
                        | Error _ -> "malformed")

                let events =
                    FileProvenanceRepository.readContributionEvents root
                    |> List.filter (fun node ->
                        match node["artifact"] with
                        | :? JsonObject as artifact ->
                            match artifact["path"] with
                            | :? JsonValue as path -> path.GetValue<string>() = document.RelativePath
                            | _ -> false
                        | _ -> false)
                    |> List.choose (fun node ->
                        match node["eventId"] with
                        | :? JsonValue as value -> Some(value.GetValue<string>())
                        | _ -> None)

                let legacy = LegacyAttribution.declared document

                if arguments |> List.contains "--json" then
                    let node = JsonObject()
                    node["path"] <- JsonValue.Create document.RelativePath
                    node["artifactId"] <- ProvenanceReportJson.optional (if identifier.Length > 0 then Some identifier else None)

                    match parsed with
                    | Ok(Some provenance) ->
                        node["status"] <- JsonValue.Create "recorded"

                        node["contributions"] <-
                            provenance.Contributions
                            |> List.map (fun item -> ProvenanceReportJson.contribution item :> JsonNode)
                            |> ProvenanceReportJson.nodes

                        node["involvement"] <- ProvenanceReportJson.involvement (Involvement.describe provenance)
                    | Ok None ->
                        node["status"] <- JsonValue.Create "unattributed"
                        node["contributions"] <- JsonArray()
                        node["involvement"] <- null
                    | Error problems ->
                        node["status"] <- JsonValue.Create "malformed"

                        node["problems"] <-
                            problems |> List.map (fun problem -> $"{problem.Field}: {problem.Message}") |> ProvenanceReportJson.strings

                    let lineage = JsonObject()

                    lineage["derivedFrom"] <-
                        sources
                        |> List.map (fun reference ->
                            let item = JsonObject()
                            item["reference"] <- JsonValue.Create reference
                            item["origin"] <- ProvenanceReportJson.optional (originOf reference)
                            item :> JsonNode)
                        |> ProvenanceReportJson.nodes

                    lineage["derivatives"] <- ProvenanceReportJson.strings derivatives
                    node["lineage"] <- lineage

                    node["legacyDeclared"] <-
                        legacy
                        |> List.map (fun (field, value) ->
                            let item = JsonObject()
                            item["field"] <- JsonValue.Create field
                            item["value"] <- JsonValue.Create value
                            item["assurance"] <- JsonValue.Create "self-declared, unverified"
                            item :> JsonNode)
                        |> ProvenanceReportJson.nodes

                    node["events"] <- ProvenanceReportJson.strings events
                    printf "%s" (ProvenanceReportJson.render node)
                else
                    printfn "%s%s" document.RelativePath (if identifier.Length > 0 then $" ({identifier})" else "")

                    match parsed with
                    | Ok(Some provenance) ->
                        let involvement = Involvement.describe provenance
                        printfn "involvement: %s" involvement.Label

                        for item in provenance.Contributions do
                            let operations = item.Operations |> List.map ContributionOperation.code |> String.concat "+"
                            printfn "  %s %s by %s [%s]" item.At operations (Actor.describe item.Actor) item.Key
                            item.Reason |> Option.iter (printfn "    reason: %s")

                            if not item.Evidence.IsEmpty then
                                printfn "    evidence: %s" (String.concat ", " item.Evidence)
                    | Ok None -> printfn "provenance: none recorded (legacy or unattributed; never inferred)"
                    | Error problems ->
                        for problem in problems do
                            printfn "  MALFORMED %s: %s" problem.Field problem.Message

                    for (field, value) in legacy do
                        printfn "legacy-declared %s: %s (self-declared, unverified)" field value

                    for reference in sources do
                        printfn "derived from: %s%s" reference (originOf reference |> Option.map (fun label -> $" [{label}]") |> Option.defaultValue "")

                    for derivative in derivatives do
                        printfn "derived into: %s" derivative

                    if not events.IsEmpty then
                        printfn "contribution events: %s" (String.concat ", " events)

                0
        | _ ->
            eprintfn "ERROR provenance show requires exactly one artifact ID or path"
            2

    /// `provenance audit [--json]`: repository-wide provenance coverage, every
    /// finding (errors, warnings, and informational legacy notes), the
    /// flattened contribution facts behind provenance metrics, and per-actor
    /// contribution summaries.
    let private runProvenanceAudit root (arguments: string list) =
        match computeProvenanceFindings root, (FileArtifactRepository.create root).Load() with
        | Error message, _ ->
            eprintfn "ERROR %s" message
            1
        | _, Error failure ->
            eprintfn "ERROR %s" (dependencyFailureMessage failure)
            1
        | Ok findings, Ok loaded ->
            let facts = ProvenanceIndex.facts loaded.Documents
            let byActor = ProvenanceIndex.byActor facts
            let attributed = facts |> List.map _.Path |> List.distinct |> List.length
            let events = FileProvenanceRepository.readEventViews root

            let eventsWithActor =
                events |> List.filter (fun event -> match event.Actor with Ok(Some _) -> true | _ -> false) |> List.length

            let executions = FileProvenanceRepository.readExecutions root
            let backlogTotal, backlogWithActor = FileProvenanceRepository.readBacklogActorCoverage root
            let count severity = findings |> List.filter (fun finding -> finding.Severity = severity) |> List.length

            let policyText =
                match FileProvenanceRepository.readPolicy root with
                | Ok policy when policy.Enforced ->
                    let date = policy.RequiredFrom |> Option.defaultValue "?"
                    $"enforced for artifacts created on or after {date}"
                | Ok _ -> "not enforced (no ros.json provenance policy)"
                | Error message -> $"invalid: {message}"

            if arguments |> List.contains "--json" then
                let node = JsonObject()
                node["policy"] <- JsonValue.Create policyText
                let summary = JsonObject()
                summary["artifacts"] <- JsonValue.Create loaded.Documents.Length
                summary["attributedArtifacts"] <- JsonValue.Create attributed
                summary["unattributedArtifacts"] <- JsonValue.Create(loaded.Documents.Length - attributed)
                summary["contributions"] <- JsonValue.Create facts.Length
                summary["events"] <- JsonValue.Create events.Length
                summary["eventsWithActor"] <- JsonValue.Create eventsWithActor
                summary["executions"] <- JsonValue.Create executions.Length

                let byKind = JsonObject()

                executions
                |> List.countBy (fun view -> ActorKind.code view.Actor.Kind)
                |> List.sortBy fst
                |> List.iter (fun (kind, total) -> byKind[kind] <- JsonValue.Create total)

                summary["executionsByActorKind"] <- byKind
                summary["backlogItems"] <- JsonValue.Create backlogTotal
                summary["backlogItemsWithActor"] <- JsonValue.Create backlogWithActor
                summary["errors"] <- JsonValue.Create(count FindingSeverity.Error)
                summary["warnings"] <- JsonValue.Create(count FindingSeverity.Warning)
                summary["info"] <- JsonValue.Create(count FindingSeverity.Info)
                node["summary"] <- summary
                node["byActor"] <- byActor |> List.map (fun item -> ProvenanceReportJson.actorSummary item :> JsonNode) |> ProvenanceReportJson.nodes
                node["contributions"] <- facts |> List.map (fun item -> ProvenanceReportJson.fact item :> JsonNode) |> ProvenanceReportJson.nodes
                node["findings"] <- findings |> List.map (fun item -> ProvenanceReportJson.finding item :> JsonNode) |> ProvenanceReportJson.nodes
                printf "%s" (ProvenanceReportJson.render node)
            else
                printfn "policy: %s" policyText
                printfn "artifacts: %d (%d attributed, %d without recorded provenance)" loaded.Documents.Length attributed (loaded.Documents.Length - attributed)
                printfn "events with actor: %d of %d" eventsWithActor events.Length
                printfn "backlog items with actor: %d of %d" backlogWithActor backlogTotal

                for item in byActor do
                    printfn
                        "  %s: %d artifact(s); created %d, modified %d, reviewed/approved %d; %d execution(s)"
                        (Actor.describe item.Actor)
                        item.Artifacts
                        item.Created
                        item.Modified
                        item.Reviewed
                        item.Executions

                for finding in findings do
                    let location = if finding.Field.Length = 0 then finding.Path else $"{finding.Path}:{finding.Field}"
                    printfn "%s %s: %s" ((FindingSeverity.code finding.Severity).ToUpperInvariant()) location finding.Message

                printfn "%d error(s), %d warning(s), %d info" (count FindingSeverity.Error) (count FindingSeverity.Warning) (count FindingSeverity.Info)

            if count FindingSeverity.Error = 0 then 0 else 1


    let usage =
        "provenance identity [--json] | provenance record (--path PATH|--id ID) --operation OP [--reason TEXT] [--evidence REF]* [--derived-from REF]* [--execution EXE-ID] [--occurred-at TIMESTAMP] [--json] | provenance show ID|PATH [--json] | provenance audit [--json]"

    let run root (arguments: string list) =
        match arguments with
        | "identity" :: rest -> runProvenanceIdentity root rest
        | "record" :: rest -> runProvenanceRecord root rest
        | "show" :: rest -> runProvenanceShow root rest
        | "audit" :: rest when rest |> List.forall ((=) "--json") -> runProvenanceAudit root rest
        | _ ->
            eprintfn "ERROR usage: %s" usage
            2
