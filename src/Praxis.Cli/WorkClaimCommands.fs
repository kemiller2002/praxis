namespace Praxis.Cli

open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// `praxis work claim|renew|release|recover|expire|claims|validate-run`
/// (PRX-REL-001..011), and the router for `work complete-verified`, `work finish`,
/// `work completion-identity`, `work verify`, `work claims migrate` and
/// `verifier serve|run`: parse, call the claim services, render one JSON
/// document. Exit codes: 0 done or replayed, 2 invalid arguments, 3 refused
/// (structured `PRX.CLAIM.*` / `PRX.COMPLETION.*` / `DOK.COMPLETION.*`
/// codes), 1 storage or event failure.
[<RequireQualifiedAccess>]
module WorkClaimCommands =
    let private store = "[--store local|git] [--remote NAME]"
    let private fence = "--id ID [--claimant NAME] --token TOKEN --generation N"

    let usage =
        $"work claim --id ID [--claimant NAME] [--token TOKEN] [--lease-seconds N] [--expected-revision N] {store} | "
        + $"work recover --id ID [--claimant NAME] [--token TOKEN] [--lease-seconds N] {store} | "
        + $"work renew {fence} [--lease-seconds N] [--running] {store} | work release {fence} [--failed] {store} | "
        + $"work expire --id ID {store} | work claims [ID] {store} | {WorkCompletionCommands.usage} | {WorkFinishCommands.usage} | {WorkValidationCommands.usage} | {VerifierCommands.usage}"

    let verbs =
        set [ "claim"; "recover"; "renew"; "release"; "expire"; "claims"; "complete-verified"; "validate-run"; "verify"; "completion-identity"; "finish" ]

    /// `work <verb> ...` for a claim verb, or `verifier <verb> ...`.
    let (|Routed|_|) (arguments: string list) =
        match arguments with
        | "work" :: verb :: rest when verbs.Contains verb -> Some(verb, rest)
        | "verifier" :: verb :: rest -> Some("verifier:" + verb, rest)
        | [ "verifier" ] -> Some("verifier:", [])
        | _ -> None

    let private optionValue = ClaimArguments.optionValue
    let private required = ClaimArguments.required
    let private number = ClaimArguments.number
    let private invalid = ClaimArguments.invalid usage

    let private lease arguments = ClaimArguments.seconds "--lease-seconds" 1800.0 arguments

    let private claimant (actor: Actor) arguments = optionValue "--claimant" arguments |> Option.defaultValue actor.Id

    let private fenceOf actor arguments =
        required "--token" arguments
        |> Result.bind (fun token ->
            number "--generation" System.Int64.TryParse arguments
            |> Result.bind (fun generation ->
                match generation with
                | None -> Error "--generation is required"
                | Some generation -> Ok { Claimant = claimant actor arguments; ClaimToken = token; Generation = generation }))

    let private acquisition actor recoverOnly ports arguments =
        required "--id" arguments
        |> Result.bind (fun id ->
            lease arguments
            |> Result.bind (fun lease ->
                number "--expected-revision" System.Int64.TryParse arguments
                |> Result.map (fun expected ->
                    { WorkItemId = id
                      Claimant = claimant actor arguments
                      ClaimToken = optionValue "--token" arguments |> Option.defaultWith FileWorkClaims.newToken
                      LeaseDuration = lease
                      ExpectedRevision = expected
                      RecoverOnly = recoverOnly })))
        |> Result.map (WorkClaims.acquire ports)

    let private mutate root (actor: Actor) choice verb arguments =
        let ports = FileWorkClaims.ports root choice (Some actor) (optionValue "--execution" arguments)

        let outcome =
            match verb with
            | "claim" -> acquisition actor false ports arguments
            | "recover" -> acquisition actor true ports arguments
            | "renew" ->
                required "--id" arguments
                |> Result.bind (fun id ->
                    fenceOf actor arguments
                    |> Result.bind (fun fence ->
                        lease arguments |> Result.map (fun lease -> WorkClaims.renew ports id fence lease (List.contains "--running" arguments))))
            | "release" ->
                required "--id" arguments
                |> Result.bind (fun id ->
                    fenceOf actor arguments |> Result.map (fun fence -> WorkClaims.release ports id fence (List.contains "--failed" arguments)))
            | _ -> required "--id" arguments |> Result.map (WorkClaims.expire ports)

        match outcome with
        | Error message -> invalid message
        | Ok outcome -> WorkClaimOutcomeJson.claimOutcome (ports.Now()) outcome |> ClaimArguments.print

    let private inspect root choice (arguments: string list) =
        let ports = FileWorkClaims.ports root choice None None

        let id =
            match arguments with
            | first :: _ when not (first.StartsWith "--") -> Some first
            | _ -> optionValue "--id" arguments

        match WorkClaims.inspect ports id with
        | Error message when message.StartsWith WorkClaimOutcomeJson.LegacyStoreCode -> WorkClaimOutcomeJson.legacy message |> ClaimArguments.print
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok claims -> ClaimArguments.print (WorkClaimOutcomeJson.claims (ports.Now()) claims, 0)

    /// Wraps the legacy `work complete`: an item under claim-based ownership
    /// (or any item when `workProtocol.requireVerifiedCompletion` is set) is
    /// refused with exit 3 unless `complete-verified` already recorded a
    /// Dokimos-accepted completion. Unreadable policy or claims fail closed.
    let guardPlainComplete root (arguments: string list) (inner: Actor -> int) (actor: Actor) =
        let decision =
            ClaimArguments.storeChoice arguments
            |> Result.bind (fun choice -> FileWorkClaims.plainCompletion root choice (ClaimArguments.optionValues "--id" arguments))

        match decision with
        | Ok PlainCompletion.Permitted -> inner actor
        | Ok(PlainCompletion.Refused finding) ->
            eprintfn "ERROR %s: %s" finding.Code finding.Detail
            3
        | Error message ->
            eprintfn "ERROR %s: %s" PlainCompletion.RequiredCode message
            3

    /// Routed from the root dispatcher for every match of `Routed`.
    let run root (withActor: string list -> (Actor -> int) -> int) (verb: string) arguments =
        let options, command = if verb = "validate-run" then WorkValidationCommands.split arguments else arguments, []

        match verb, ClaimArguments.storeChoice options with
        | verifier, _ when verifier.StartsWith "verifier:" -> VerifierCommands.run (verifier.Substring 9) arguments
        | "verify", _ -> VerifierCommands.verify root arguments
        | "claims", _ when List.tryHead arguments = Some "migrate" -> VerifierCommands.migrate root arguments
        | _, Error message -> invalid message
        | "claims", Ok choice -> inspect root choice arguments
        | "complete-verified", Ok choice -> withActor arguments (fun actor -> WorkCompletionCommands.completeVerified root actor choice arguments)
        | "finish", Ok choice -> withActor arguments (fun actor -> WorkFinishCommands.run root actor choice arguments)
        | "completion-identity", Ok choice -> WorkCompletionCommands.completionIdentity root choice arguments
        | "validate-run", Ok choice -> withActor options (fun actor -> WorkValidationCommands.run root actor choice options command)
        | _, Ok choice -> withActor arguments (fun actor -> mutate root actor choice verb arguments)
