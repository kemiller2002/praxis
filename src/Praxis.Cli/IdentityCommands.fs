namespace Praxis.Cli

open System.Text.Json.Nodes
open Praxis.Contracts.Identity
open Praxis.Domain.Artifacts
open Praxis.Domain.Identity
open Praxis.Domain.Provenance
open Praxis.Infrastructure.Identity

/// `praxis repository identity` and `praxis instance ...` (PRX-REMOTE-046,
/// 048, 049; DER-16..26). Parses arguments and renders; every decision is
/// in `Praxis.Domain.Identity` and every effect in
/// `Praxis.Infrastructure.Identity`.
[<RequireQualifiedAccess>]
module IdentityCommands =
    let usage =
        "repository identity [--json] | repository identity set (--provider NAME --provider-id ID [--locator OWNER/REPO] | --from-environment) [--json] | instance [show] [--json] | instance init [--reinitialize --reason TEXT] [--json] | instance projection [--json] | instance register [--config PATH] [--catalog FILE] [--require] [--json] [IDENTITY]"

    let private optionValue (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.tryPick (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private hasFlag name (arguments: string list) = List.contains name arguments

    let private fail (message: string) =
        eprintfn "ERROR %s" message
        2

    let private emit json (node: JsonNode) (text: string) =
        if json then printfn "%s" (node.ToJsonString IdentityJson.options) else printfn "%s" text

    let private describeRepository status =
        match RepositoryIdentityStatus.current status with
        | Some identity ->
            let id = RepositoryIdentity.repositoryId identity |> Option.defaultValue "no stable ID (legacy)"
            $"repository identity: {RepositoryIdentity.display identity} ({id}); {RepositoryIdentityStatus.code status}"
        | None -> $"repository identity: none; {RepositoryIdentityStatus.code status}"

    let private showRepository root arguments =
        match FileRepositoryIdentityRepository.status RepositoryObserver.environmentVariable root with
        | Error message -> fail message
        | Ok status ->
            emit (hasFlag "--json" arguments) (IdentityJson.renderRepositoryStatus status) (describeRepository status)

            for _, message in RepositoryIdentityStatus.findings status do
                eprintfn "NOTE %s" message

            match status with
            | RepositoryIdentityStatus.Contradicted _ -> 1
            | _ -> 0

    let private requested root arguments : Result<RepositoryIdentity, string> =
        let observation = RepositoryObserver.observe root

        if hasFlag "--from-environment" arguments then
            match observation.Source, RepositoryObservation.identity observation with
            | "github-actions", Some identity -> Ok identity
            | _ -> Error "--from-environment needs a hosting environment that exposes the stable repository ID (GITHUB_REPOSITORY_ID in GitHub Actions)"
        else
            match optionValue "--provider" arguments |> Option.map (fun value -> value, RepositoryProvider.tryCreate value) with
            | None -> Error "--provider NAME is required (for example github)"
            | Some(value, None) -> Error $"'{value}' is not a provider name"
            | Some(_, Some provider) ->
                let locator =
                    match optionValue "--locator" arguments with
                    | Some value ->
                        match RepositoryLocator.tryCreate value with
                        | Some parsed -> Ok(Some parsed)
                        | None -> Error $"'{value}' is not an owner/repo locator"
                    | None -> Ok observation.Locator

                locator |> Result.bind (RepositoryIdentity.create provider (optionValue "--provider-id" arguments))

    let private setRepository root arguments =
        match requested root arguments |> Result.bind (fun identity -> FileRepositoryIdentityRepository.writeConfigured root identity |> Result.map (fun () -> identity)) with
        | Error message -> fail message
        | Ok identity ->
            emit (hasFlag "--json" arguments) (IdentityJson.renderRepository identity) $"recorded repository.identity {RepositoryIdentity.display identity} in ros.json; commit it"
            0

    let private describeInstance local =
        match local with
        | LocalInstance.Missing -> "instance identity: none (run 'praxis instance init')"
        | LocalInstance.Unreadable reason -> $"instance identity: unreadable ({reason})"
        | LocalInstance.Present(record, binding) -> $"instance identity: {InstanceId.value record.InstanceId}; {InstanceBinding.code binding}"

    let private showInstance root arguments =
        let local = FileInstanceIdentityStore.local root
        emit (hasFlag "--json" arguments) (IdentityJson.renderInstanceStatus local) (describeInstance local)

        for isError, message in LocalInstance.findings local do
            eprintfn "%s %s" (if isError then "ERROR" else "NOTE") message

        if LocalInstance.findings local |> List.exists fst then 1 else 0

    let private initInstance root version arguments =
        let reinitialize =
            if hasFlag "--reinitialize" arguments then Some(optionValue "--reason" arguments |> Option.defaultValue "") else None

        match FileInstanceIdentityStore.ensure root version reinitialize with
        | Error message -> fail message
        | Ok decision ->
            let local = FileInstanceIdentityStore.local root
            emit (hasFlag "--json" arguments) (IdentityJson.renderInstanceStatus local) ("instance identity " + InstanceIdentityLifecycle.describe decision)
            0

    let private projection root version arguments =
        match FileInstanceProjection.build root version with
        | Error message -> fail message
        | Ok projection ->
            let node = IdentityJson.renderProjection projection
            emit (hasFlag "--json" arguments) node (node.ToJsonString IdentityJson.options)
            0

    /// Registration through the existing installation client: the projection
    /// travels as evidence, the operation ID is the projection's digest, so a
    /// retry is idempotent; an absent or unreachable registry is
    /// `unavailable` and changes nothing local (DER-18, 19).
    let private register root version (actor: Actor) arguments =
        match FileInstanceProjection.build root version with
        | Error message -> fail message
        | Ok projection ->
            let evidence =
                [ yield "praxis-instance", InstanceId.value projection.InstanceId
                  yield "praxis-reconciliation-protocol", projection.ReconciliationProtocolVersion
                  yield "praxis-remote-protocol", projection.RemoteProtocolVersion
                  yield! projection.Repository |> Option.bind RepositoryIdentity.repositoryId |> Option.map (fun id -> "praxis-repository-id", id) |> Option.toList
                  for capability in projection.Capabilities do
                      yield "praxis-capability", capability
                  for name, availability in projection.Integrations do
                      yield "praxis-integration", $"{name}:{IntegrationAvailability.code availability}" ]
                |> List.collect (fun (kind, reference) -> [ "--evidence"; $"{kind}={reference}" ])

            let target =
                match projection.Repository |> Option.bind _.Locator with
                | Some locator -> [ "--target-kind"; "repository"; "--target-id"; RepositoryLocator.value locator ]
                | None -> []

            let passThrough =
                [ "--config"; "--catalog" ]
                |> List.collect (fun flag -> optionValue flag arguments |> Option.map (fun value -> [ flag; value ]) |> Option.defaultValue [])

            let switches = [ "--require"; "--json" ] |> List.filter (fun flag -> hasFlag flag arguments)

            InstallationCommands.mutate
                root
                "installation.register"
                actor
                ([ "--system"; "praxis"; "--version"; version; "--distribution"; "praxis-instance"; "--operation-id"; InstanceProjection.operationId projection ]
                 @ target
                 @ evidence
                 @ passThrough
                 @ switches)

    let run (root: string) (version: string) (withActor: (Actor -> int) -> int) (family: string) (arguments: string list) =
        match family, arguments with
        | "repository", "identity" :: "set" :: rest -> setRepository root rest
        | "repository", "identity" :: rest -> showRepository root rest
        | "instance", ([] | "show" :: _ | "--json" :: _) -> showInstance root arguments
        | "instance", "init" :: rest -> initInstance root version rest
        | "instance", "projection" :: rest -> projection root version rest
        | "instance", "register" :: rest -> withActor (fun resolved -> register root version resolved rest)
        | _ ->
            eprintfn "Usage: praxis %s" usage
            2

    /// After `work capture`: the canonical identity of the new item, or why
    /// it is incomplete (PRX-REMOTE-049). Informational, on standard error.
    let reportCanonical (root: string) (localId: string) =
        match FileRepositoryIdentityRepository.current RepositoryObserver.environmentVariable root, LocalWorkItemId.tryCreate localId with
        | Some repository, Some id ->
            let identity = WorkItemIdentity.create repository id

            match WorkItemIdentity.key identity with
            | Some key -> eprintfn "canonical identity: %s (%s)" (WorkItemIdentity.display identity) key
            | None -> eprintfn "canonical identity: %s (legacy: incomplete until the repository's stable ID is recorded)" (WorkItemIdentity.display identity)
        | _ ->
            eprintfn "canonical identity: incomplete; the repository identity is not established (run 'praxis repository identity set')"

        0

    let private convert (isError, path, field, message) = isError, ({ Path = path; Field = field; Message = message }: ArtifactFinding)

    /// The identity contributors to `validate`, split into errors and warnings.
    let validation root observedPaths meaningful workItems : ArtifactFinding list * ArtifactFinding list =
        let findings = FileIdentityValidation.findings root observedPaths meaningful workItems |> List.map convert
        findings |> List.filter fst |> List.map snd, findings |> List.filter (fst >> not) |> List.map snd
