namespace Praxis.Cli

open System
open System.Globalization
open Praxis.Application.Work
open Praxis.Infrastructure.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work

/// Offline, read-only proposal inspection. No host-policy/approval file is
/// accepted, and no execution, Git, telemetry or network port is accessed.
[<RequireQualifiedAccess>]
module LocalHandoffCommands =
    let usage = "handoff explain --packet FILE [--packet FILE]* [--as-of UTC-TIMESTAMP] [--json]"

    let private parseTime (value: string) =
        let formats = [| "yyyy-MM-dd'T'HH:mm:ss'Z'"; "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"; "yyyy-MM-dd'T'HH:mm:sszzz"; "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz" |]
        match DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, time when time.Offset = TimeSpan.Zero -> Ok time
        | _ -> Error "--as-of requires an explicit ISO-8601 UTC timestamp"
    let private renderText (report: LocalHandoffExplanation) =
        printfn "Local handoff proposals as of %s" (report.AsOf.ToString("O"))
        for limitation in report.Limitations do printfn "  %s" limitation
        for assignment in report.Assignments do
            let p = assignment.Packet
            printfn "\n%s / %s: %s (%s)" p.DispatchId p.AttemptId p.WorkerId (Praxis.Domain.Execution.ExecutionRole.toWire p.Role)
            printfn "  Lineage: %s -> %s; group %s; cohort %s" p.ParentExecutionId p.ChildExecutionId p.GroupId p.CohortId
            printfn "  Repository: %s; source %s" p.RepositoryIdentity p.SourceCommit
            printfn "  Manifest: %s; blueprint %s" p.SourceManifestDigest p.BlueprintDigest
            printfn "  Packet: %s" (assignment.PacketDigest |> Option.defaultValue "invalid")
            printfn "  Authority: %s; receipt %s; valid %s to %s" p.AuthorityRevision p.ReceiptDigest (p.IssuedAt.ToString("O")) (p.ExpiresAt.ToString("O"))
            printfn "  Budget: timeout %ds; output %d bytes" p.TimeoutSeconds p.MaxOutputBytes
            printfn "  Allowed paths: %s" (String.concat ", " p.AllowedPaths)
            printfn "  Decisions: %s" (String.concat ", " p.DecisionIds)
            for memberItem in p.Members do printfn "  Member %s [%s]: %s" memberItem.WorkItemId memberItem.ExecutionId (String.concat ", " memberItem.RequirementKeys)
            for obligation in p.Acceptance do printfn "  Acceptance %s for %s: %s [%s]" obligation.ObligationId obligation.WorkItemId obligation.ValidatorId obligation.InputDigest
            for dependency in assignment.Dependencies do
                printfn "  Dependency %s [%s]; proposed producers: %s" dependency.WorkItemId dependency.EvidenceDigest (if dependency.ProposedProducers.IsEmpty then "outside supplied packets" else String.concat ", " dependency.ProposedProducers)
            for reason in assignment.BlockingReasons do printfn "  BLOCK %s" reason
        for conflict in report.WriteConflicts do printfn "\nSerialize %s / %s: %s" conflict.LeftDispatchId conflict.RightDispatchId (String.concat ", " conflict.Paths)

    let run root arguments =
        let rec parse (packets: string list) (asOf: string option) json (remaining: string list) =
            match remaining with
            | [] when packets.IsEmpty -> Error "handoff explain requires --packet FILE"
            | [] -> Ok(List.rev packets, asOf, json)
            | "--packet" :: path :: rest when not (path.StartsWith("--", StringComparison.Ordinal)) && packets.Length < LocalHandoffExplanation.MaxPackets -> parse (path :: packets) asOf json rest
            | "--as-of" :: value :: rest when asOf.IsNone -> parse packets (Some value) json rest
            | "--json" :: rest when not json -> parse packets asOf true rest
            | _ -> Error("unexpected, missing or excessive handoff arguments; " + usage)
        let fail error = eprintfn "ERROR %s" error; 2
        match arguments with
        | "explain" :: rest ->
            match parse [] None false rest with
            | Error error -> fail error
            | Ok(paths, timestamp, json) ->
                let observedTime = timestamp |> Option.map (parseTime >> Result.map Some) |> Option.defaultValue (Ok None)
                match observedTime with
                | Error error -> fail error
                | Ok asOf ->
                    match LocalHandoffInspection.explain (FileLocalHandoffInspection.create root) asOf paths with
                    | Error error -> fail error
                    | Ok report ->
                        if json then printfn "%s" (LocalHandoffExplanationJson.render report) else renderText report
                        // Successful inspection is not dispatch eligibility.
                        0
        | _ -> fail usage
