namespace Praxis.Cli

open System
open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// `praxis work claim|renew|release|recover|expire|claims|complete-verified`
/// (PRX-REL-001..011): parse, call the claim services, render one JSON
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
        + $"work expire --id ID {store} | work claims [ID] {store} | "
        + $"work complete-verified {fence} --commit SHA --verification-revision SHA --authority FILE [--ordo-contract FILE [--expected-ordo-digest sha256:HEX]] "
        + "[--ordo-contract-version V] [--ordo-contract-digest sha256:HEX] [--requirement ID]* [--decision ID]* "
        + "[--conflict-resolution ID]* [--validation NAME=passed|failed|unavailable[:EVIDENCE-ID,...]]* "
        + $"[--evidence ID=PATH[@sha256:HEX]]* [--telemetry-execution EXE-ID]* [--timeout-seconds N] {store}"

    let verbs = set [ "claim"; "recover"; "renew"; "release"; "expire"; "claims"; "complete-verified" ]

    let private optionValue (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.tryPick (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private optionValues (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.choose (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private required name arguments =
        optionValue name arguments |> Option.map Ok |> Option.defaultValue (Error $"{name} is required")

    let private number name (parse: string -> bool * 'value) arguments =
        match optionValue name arguments with
        | None -> Ok None
        | Some text ->
            match parse text with
            | true, value -> Ok(Some value)
            | _ -> Error $"{name} must be a number"

    let private lease arguments =
        number "--lease-seconds" Double.TryParse arguments
        |> Result.bind (fun seconds ->
            match seconds with
            | Some value when value <= 0.0 -> Error "--lease-seconds must be positive"
            | value -> Ok(TimeSpan.FromSeconds(value |> Option.defaultValue 1800.0)))

    let private storeChoice arguments =
        match optionValue "--store" arguments, optionValue "--remote" arguments with
        | (None | Some "local"), None -> Ok ClaimStoreChoice.Local
        | Some "git", remote -> Ok(ClaimStoreChoice.GitRef(remote |> Option.defaultValue "origin"))
        | (None | Some "local"), Some _ -> Error "--remote requires --store git"
        | Some other, _ -> Error $"unknown --store '{other}' (expected local or git)"

    let private claimant (actor: Actor) arguments = optionValue "--claimant" arguments |> Option.defaultValue actor.Id

    let private fenceOf actor arguments =
        required "--token" arguments
        |> Result.bind (fun token ->
            number "--generation" Int64.TryParse arguments
            |> Result.bind (fun generation ->
                match generation with
                | None -> Error "--generation is required"
                | Some generation -> Ok { Claimant = claimant actor arguments; ClaimToken = token; Generation = generation }))

    let private validation (text: string) =
        let nameAndRest = text.Split('=', 2)

        if nameAndRest.Length <> 2 then
            Error $"--validation '{text}' must be NAME=STATUS[:EVIDENCE-ID,...]"
        else
            let statusAndEvidence = nameAndRest[1].Split(':', 2)

            match CheckStatus.tryParse statusAndEvidence[0] with
            | None -> Error $"--validation '{text}' has an unknown status (passed, failed or unavailable)"
            | Some status ->
                let evidence =
                    if statusAndEvidence.Length = 2 then
                        statusAndEvidence[1].Split(',', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
                    else
                        []

                Ok { Name = nameAndRest[0]; Status = status; EvidenceIds = evidence }

    let private evidence (text: string) =
        match text.Split('=', 2) with
        | [| id; rest |] when id <> "" && rest <> "" ->
            match rest.LastIndexOf "@sha256:" with
            | -1 -> Ok { Id = id; Path = rest; ClaimedDigest = None }
            | at -> Ok { Id = id; Path = rest.Substring(0, at); ClaimedDigest = Some(rest.Substring(at + 1)) }
        | _ -> Error $"--evidence '{text}' must be ID=PATH[@sha256:HEX]"

    let private collect parse values =
        values
        |> List.fold (fun state value -> state |> Result.bind (fun items -> parse value |> Result.map (fun item -> items @ [ item ]))) (Ok [])

    let private print (text: string, code: int) =
        printfn "%s" text
        code

    let private invalid (message: string) =
        eprintfn "ERROR %s" message
        eprintfn "usage: %s" usage
        2

    let private acquisition actor recoverOnly ports arguments =
        required "--id" arguments
        |> Result.bind (fun id ->
            lease arguments
            |> Result.bind (fun lease ->
                number "--expected-revision" Int64.TryParse arguments
                |> Result.map (fun expected ->
                    { WorkItemId = id
                      Claimant = claimant actor arguments
                      ClaimToken = optionValue "--token" arguments |> Option.defaultWith FileWorkClaims.newToken
                      LeaseDuration = lease
                      ExpectedRevision = expected
                      RecoverOnly = recoverOnly })))
        |> Result.map (WorkClaims.acquire ports)

    let private completion actor arguments =
        let lists name = optionValues name arguments

        required "--id" arguments
        |> Result.bind (fun id ->
            fenceOf actor arguments
            |> Result.bind (fun fence ->
                required "--commit" arguments
                |> Result.bind (fun commit ->
                    required "--verification-revision" arguments
                    |> Result.bind (fun revision ->
                        collect validation (lists "--validation")
                        |> Result.bind (fun validations ->
                            collect evidence (lists "--evidence")
                            |> Result.map (fun evidence ->
                                { WorkItemId = id
                                  Fence = fence
                                  Commit = commit
                                  VerificationRevision = revision
                                  OrdoContractVersion = optionValue "--ordo-contract-version" arguments
                                  OrdoContractDigest = optionValue "--ordo-contract-digest" arguments
                                  RequirementIds = lists "--requirement"
                                  DecisionIds = lists "--decision"
                                  ConflictResolutionIds = lists "--conflict-resolution"
                                  Validations = validations
                                  Evidence = evidence
                                  TelemetryExecutions = lists "--telemetry-execution" }))))))

    let private completeVerified root actor choice arguments =
        let timeout =
            number "--timeout-seconds" Double.TryParse arguments
            |> Result.map (fun value -> TimeSpan.FromSeconds(value |> Option.defaultValue 120.0))

        match completion actor arguments, timeout, required "--authority" arguments with
        | Error message, _, _
        | _, Error message, _
        | _, _, Error message -> invalid message
        | Ok request, Ok timeout, Ok authorityPath ->
            let ports = FileWorkClaims.completionPorts root choice (Some actor) (optionValue "--execution" arguments) timeout

            let bound =
                FileWorkClaims.readAuthorityInput root authorityPath
                |> Result.mapError Choice1Of2
                |> Result.bind (FileWorkClaims.bindOrdoContract root (optionValue "--ordo-contract" arguments) (optionValue "--expected-ordo-digest" arguments) >> Result.mapError Choice2Of2)

            match bound with
            | Error(Choice1Of2 message) -> invalid message
            | Error(Choice2Of2 findings) ->
                CompletionOutcome.Rejected(findings, ports.Claims.Store.Read request.WorkItemId |> Result.defaultValue None)
                |> WorkClaimOutcomeJson.completionOutcome (ports.Claims.Now())
                |> print
            | Ok input ->
                WorkClaims.completeVerified ports input request
                |> WorkClaimOutcomeJson.completionOutcome (ports.Claims.Now())
                |> print

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
        | Ok outcome -> WorkClaimOutcomeJson.claimOutcome (ports.Now()) outcome |> print

    let private inspect root choice (arguments: string list) =
        let ports = FileWorkClaims.ports root choice None None

        let id =
            match arguments with
            | first :: _ when not (first.StartsWith "--") -> Some first
            | _ -> optionValue "--id" arguments

        match WorkClaims.inspect ports id with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok claims -> print (WorkClaimOutcomeJson.claims (ports.Now()) claims, 0)

    /// Wraps the legacy `work complete`: an item under claim-based ownership
    /// (or any item when `workProtocol.requireVerifiedCompletion` is set) is
    /// refused with exit 3 unless `complete-verified` already recorded a
    /// Dokimos-accepted completion. Unreadable policy or claims fail closed.
    let guardPlainComplete root (arguments: string list) (inner: Actor -> int) (actor: Actor) =
        let decision = storeChoice arguments |> Result.bind (fun choice -> FileWorkClaims.plainCompletion root choice (optionValues "--id" arguments))

        match decision with
        | Ok PlainCompletion.Permitted -> inner actor
        | Ok(PlainCompletion.Refused finding) ->
            eprintfn "ERROR %s: %s" finding.Code finding.Detail
            3
        | Error message ->
            eprintfn "ERROR %s: %s" PlainCompletion.RequiredCode message
            3

    /// Routed from the root dispatcher for every verb in `verbs`.
    let run root (withActor: string list -> (Actor -> int) -> int) verb arguments =
        match storeChoice arguments with
        | Error message -> invalid message
        | Ok choice ->
            match verb with
            | "claims" -> inspect root choice arguments
            | "complete-verified" -> withActor arguments (fun actor -> completeVerified root actor choice arguments)
            | _ -> withActor arguments (fun actor -> mutate root actor choice verb arguments)
