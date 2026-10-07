namespace Praxis.Cli

open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Artifacts
open Praxis.Infrastructure.Provenance
open Praxis.Infrastructure.Work

/// `praxis inbox ...`: the input-document lifecycle (DER-08..10). Parses
/// arguments, calls `InputInboxOperations`, renders, picks the exit code.
[<RequireQualifiedAccess>]
module InputInboxCommands =
    let usage =
        "inbox list [--json] | inbox show CLAIM-ID [--json] | inbox claim PATH [--json] [IDENTITY] | inbox derive CLAIM-ID --kind {requirement|decision|constraint|evidence|risk|question|reference} (--id ARTIFACT-ID|--path PATH) --summary TEXT [--locator TEXT] [--json] [IDENTITY] | inbox complete CLAIM-ID [--no-derivations REASON] [--json] | inbox release CLAIM-ID --reason TEXT [--json] | inbox reject PATH|CLAIM-ID --reason TEXT [--json] [IDENTITY] | inbox recover [--json]"

    let private valued =
        set [ "--kind"; "--id"; "--path"; "--summary"; "--locator"; "--no-derivations"; "--reason"; "--provider"; "--model"; "--model-version"; "--runtime"; "--runtime-version"; "--session"; "--conversation"; "--run"; "--agent"; "--actor"; "--subagent"; "--actor-kind" ]

    let private option name (arguments: string list) =
        arguments |> List.pairwise |> List.tryPick (fun (flag, value) -> if flag = name then Some value else None)

    let rec private positional (arguments: string list) =
        match arguments with
        | flag :: _ :: rest when valued.Contains flag -> positional rest
        | flag :: rest when flag.StartsWith "--" -> positional rest
        | value :: rest -> value :: positional rest
        | [] -> []

    let private json (arguments: string list) = arguments |> List.contains "--json"

    let private fail code (message: string) =
        eprintfn "ERROR %s" message
        code

    let private refuse (error: InputInboxError) =
        eprintfn "ERROR %s: %s" (InputInboxError.code error) (InputInboxError.message error)
        InputInboxError.exitCode error

    let private printClaim (asJson: bool) (claim: InputClaim) (headline: string) =
        if asJson then printfn "%s" (InputInboxJson.render claim |> fun text -> text.TrimEnd())
        else
            printfn "%s" headline
            printfn "  source: %s (sha256 %s, %d bytes)" claim.Source.Path claim.Source.Sha256 claim.Source.Size
            printfn "  claimant: %s%s" (Praxis.Domain.Provenance.Actor.describe claim.Claimant) (claim.ExecutionId |> Option.map (sprintf " (%s)") |> Option.defaultValue "")

            for derivation in claim.Derivations do
                printfn "  derived %s %s: %s" (DerivationKind.code derivation.Kind) (DerivationTarget.value derivation.Target) derivation.Summary

    let private withLock root (run: unit -> int) =
        match RegistryLock.acquire root "input-inbox" RegistryLock.defaultSettings with
        | Error failure -> fail 1 failure.Message
        | Ok lease ->
            let result = run ()

            match lease.Release() with
            | Error failure when result = 0 -> fail 1 failure.Message
            | _ -> result

    let private withProducer root (arguments: string list) (run: Praxis.Domain.Provenance.Actor -> string option -> int) =
        match FileProvenanceRepository.currentProducer root (ProvenanceCommands.identityOverridesFrom arguments) with
        | Error message -> fail 2 message
        | Ok(actor, execution) -> run actor execution

    let private list root (arguments: string list) =
        match InputInboxOperations.list (FileInputInbox.create root) with
        | Error error -> refuse error
        | Ok listing when json arguments ->
            let node = JsonObject()
            let pending = JsonArray()

            for source in listing.Pending do
                let item = JsonObject()
                item["path"] <- JsonValue.Create source.Path
                item["sha256"] <- JsonValue.Create source.Sha256
                item["size"] <- JsonValue.Create source.Size
                pending.Add item

            let claims = JsonArray()

            for location, claim in listing.Claims do
                let item = InputInboxJson.node claim
                item["location"] <- JsonValue.Create(ClaimLocation.code location)
                claims.Add item

            node["schema"] <- JsonValue.Create "praxis.inbox-list/1"
            node["pending"] <- pending
            node["claims"] <- claims
            printfn "%s" (node.ToJsonString())
            0
        | Ok listing ->
            for source in listing.Pending do
                printfn "%s\t%s" (System.IO.Path.GetDirectoryName(source.Path).Replace('\\', '/')) source.Path

            if listing.Pending.IsEmpty then printfn "No pending input documents."

            for location, claim in listing.Claims do
                printfn "%s\t%s\t%s\t%s\t%d derivation(s)" (ClaimLocation.code location) (InputClaimState.code claim.State) claim.ClaimId claim.Source.Path claim.Derivations.Length

            0

    let private derive root claimId (arguments: string list) =
        let target =
            match option "--id" arguments, option "--path" arguments with
            | Some id, None -> Ok(DerivationTarget.Artifact id)
            | None, Some path -> Ok(DerivationTarget.RepositoryPath(path.Replace('\\', '/')))
            | _ -> Error "inbox derive needs exactly one of --id ARTIFACT-ID or --path PATH"

        match option "--kind" arguments |> Option.bind DerivationKind.tryParse, target, option "--summary" arguments with
        | None, _, _ -> fail 2 "inbox derive needs --kind {requirement|decision|constraint|evidence|risk|question|reference}"
        | _, Error message, _ -> fail 2 message
        | _, _, None -> fail 2 "inbox derive needs --summary TEXT"
        | Some kind, Ok target, Some summary ->
            withProducer root arguments (fun actor execution ->
                match InputInboxOperations.derive (FileInputInbox.create root) claimId kind target summary (option "--locator" arguments) actor execution with
                | Error error -> refuse error
                | Ok(claim, changed) ->
                    printClaim (json arguments) claim (if changed then $"derived {DerivationKind.code kind} {DerivationTarget.value target} from {claimId}" else $"already recorded on {claimId}")
                    0)

    let private mutate root (arguments: string list) =
        let port = FileInputInbox.create root

        match positional arguments with
        | [ "claim"; path ] ->
            withProducer root arguments (fun actor execution ->
                match InputInboxOperations.claim port path actor execution with
                | Error error -> refuse error
                | Ok(claim, created) ->
                    printClaim (json arguments) claim (if created then $"claimed {claim.ClaimId}" else $"already claimed: {claim.ClaimId}")
                    0)
        | [ "derive"; claimId ] -> derive root claimId arguments
        | [ "complete"; claimId ] ->
            match InputInboxOperations.complete port claimId (option "--no-derivations" arguments) with
            | Error error -> refuse error
            | Ok claim ->
                printClaim (json arguments) claim $"completed {claim.ClaimId}: moved to .praxis/processed/{claim.ClaimId}"
                0
        | [ "release"; claimId ] ->
            match InputInboxOperations.release port claimId (option "--reason" arguments |> Option.defaultValue "") with
            | Error error -> refuse error
            | Ok claim ->
                printClaim (json arguments) claim $"released {claim.ClaimId}: {claim.Source.Path} is pending again"
                0
        | [ "reject"; target ] ->
            withProducer root arguments (fun actor execution ->
                match InputInboxOperations.reject port target (option "--reason" arguments |> Option.defaultValue "") actor execution with
                | Error error -> refuse error
                | Ok claim ->
                    printClaim (json arguments) claim $"rejected {claim.ClaimId}: moved to .praxis/rejected/documents/{claim.ClaimId}"
                    0)
        | [ "recover" ] ->
            match InputInboxOperations.recover port with
            | Error error -> refuse error
            | Ok [] ->
                printfn "No interrupted inbox operation."
                0
            | Ok recovered ->
                for entry in recovered do
                    printfn "recovered %s: %s" entry.ClaimId entry.Action

                0
        | _ -> fail 2 $"usage: praxis {usage}"

    let run root (arguments: string list) =
        match positional arguments with
        | [ "list" ] -> list root arguments
        | [ "show"; claimId ] ->
            match InputInboxOperations.show (FileInputInbox.create root) claimId with
            | Error error -> refuse error
            | Ok(location, claim) ->
                printClaim (json arguments) claim $"{claim.ClaimId} {InputClaimState.code claim.State} ({ClaimLocation.code location})"
                0
        | _ -> withLock root (fun () -> mutate root arguments)
