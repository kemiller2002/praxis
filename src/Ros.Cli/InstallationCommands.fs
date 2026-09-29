namespace Ros.Cli

open System
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Installation
open Ros.Domain.Installation
open Ros.Domain.Provenance
open Ros.Infrastructure.Execution
open Ros.Infrastructure.Installation
open Ros.Infrastructure.Work
open Ros.Domain.Telemetry

/// `praxis installation register|remove|reconcile|verify|list|status` — the
/// registration client of Project Administration's installation inventory.
/// Registration is an optional integration: when nothing is configured or
/// the provider is unreachable the outcome is `unavailable` and the caller
/// keeps working, unless `required` policy (config or `--require`) says
/// otherwise.
[<RequireQualifiedAccess>]
module InstallationCommands =
    let usage =
        "installation register --system ID --version SEMVER [--target-kind repository|environment] [--target-id ID] [--source-repository OWNER/REPO] [--distribution CHANNEL] [--release TAG] [--artifact ID] [--digest DIGEST] [--evidence KIND=REF]* [--occurred-at TIMESTAMP] [--execution EXE-ID] [--work-item ID] [--operation-id ID] [--config PATH] [--catalog FILE] [--require] [--json] [IDENTITY] | installation remove|verify (same options) | installation reconcile --state installed|removed|indeterminate (same options) | installation list|status [--target KIND:ID] [--system ID] [--version V] [--older-than V] [--state S] [--config PATH] [--json] | installation history --target KIND:ID [--system ID] [--config PATH] [--json]"

    let private optionValue (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.tryPick (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private optionValues (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.choose (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private hasFlag name (arguments: string list) = List.contains name arguments

    let private emit (json: bool) (node: JsonNode) (text: string) =
        if json then printfn "%s" (node.ToJsonString InstallationJson.options) else printfn "%s" text

    let private describe outcome =
        match outcome with
        | RegistrationOutcome.Recorded(eventId, operation) -> $"recorded {operation} ({eventId})"
        | RegistrationOutcome.Replayed eventId -> $"replayed: already recorded as {eventId}"
        | RegistrationOutcome.Unchanged -> "unchanged: the inventory already records this installation"
        | RegistrationOutcome.Submitted detail -> $"submitted: {detail}"
        | RegistrationOutcome.Refused(code, message) -> $"refused ({code}): {message}"
        | RegistrationOutcome.Invalid(code, message) -> $"invalid ({code}): {message}"
        | RegistrationOutcome.Unavailable reason -> $"unavailable: {reason} (optional integration; core behavior is unaffected)"
        | RegistrationOutcome.Misconfigured reason -> $"misconfigured: {reason}"

    let private finish json required request outcome provider =
        let code = RegistrationOutcome.exitCode required outcome
        emit json (InstallationJson.outcome request outcome required provider) ("installation " + describe outcome)
        code

    /// Build and send one request. `actor` is the resolved Praxis identity.
    let private mutate (root: string) (capability: string) (actor: Actor) (arguments: string list) =
        let json = hasFlag "--json" arguments
        let explicitRequire = hasFlag "--require" arguments

        match AdministrationClient.loadConfig root (optionValue "--config" arguments) with
        | Error reason -> finish json true None (RegistrationOutcome.Misconfigured reason) null
        | Ok config ->
            let required = explicitRequire || (config |> Option.exists _.Required)
            let invalid code message = finish json required None (RegistrationOutcome.Invalid(code, message)) null

            let occurredAt =
                match optionValue "--occurred-at" arguments with
                | None -> Ok DateTimeOffset.UtcNow
                | Some raw ->
                    match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
                    | true, t -> Ok t
                    | _ -> Error $"--occurred-at '{raw}' is not a timestamp"

            let observedState =
                match capability, optionValue "--state" arguments with
                | "installation.reconcile", Some s when List.contains s [ "installed"; "removed"; "indeterminate" ] -> Ok(Some s)
                | "installation.reconcile", _ -> Error "reconcile needs --state installed|removed|indeterminate"
                | _ -> Ok None

            match optionValue "--system" arguments, optionValue "--version" arguments, occurredAt, observedState with
            | None, _, _, _ -> invalid "missing-system" "--system ID is required"
            | _, None, _, _ -> invalid "missing-version" "--version SEMVER is required"
            | Some system, _, _, _ when not (Validation.systemId system) -> invalid "invalid-system" $"'{system}' is not a system id (^[a-z][a-z0-9-]*$)"
            | _, Some version, _, _ when not (Validation.version version) -> invalid "invalid-version" $"'{version}' is not a semantic version"
            | _, _, Error message, _
            | _, _, _, Error message -> invalid "invalid-arguments" message
            | Some system, Some version, Ok at, Ok state ->
                let remote = GitWorkspace.remoteUrl root

                match Target.resolve (optionValue "--target-kind" arguments) (optionValue "--target-id" arguments) (config |> Option.bind _.EnvironmentId) remote with
                | Error message -> invalid "invalid-target" message
                | Ok target ->
                    let evidence =
                        optionValues "--evidence" arguments
                        |> List.map (fun e -> match e.IndexOf '=' with -1 -> "reference", e | i -> e.Substring(0, i), e.Substring(i + 1))

                    let known v = v |> Option.filter (fun s -> s <> Actor.UnknownValue)

                    let draft =
                        { Capability = capability
                          OperationId = ""
                          OccurredAt = at
                          SystemId = system
                          SystemVersion = version
                          ObservedState = state
                          Target = target
                          SourceRepository = optionValue "--source-repository" arguments
                          Distribution = optionValue "--distribution" arguments
                          Release = optionValue "--release" arguments
                          Artifact = optionValue "--artifact" arguments
                          Digest = optionValue "--digest" arguments
                          Evidence = evidence
                          ActorKind = Some(ActorKind.code actor.Kind)
                          ActorId = Some actor.Id |> known
                          Provider = actor.Provider |> known
                          Model = actor.Model |> known
                          Runtime = actor.Runtime |> known
                          ExecutionId = optionValue "--execution" arguments
                          WorkItem = optionValue "--work-item" arguments
                          ExecutionRepository = (if target.Kind = TargetKind.Repository then Some target.Id else None) }

                    let request =
                        { draft with
                            OperationId = optionValue "--operation-id" arguments |> Option.defaultWith (fun () -> RegistrationRequest.operationIdFor draft) }

                    match config with
                    | None ->
                        finish
                            json
                            required
                            (Some request)
                            (RegistrationOutcome.Unavailable "no Project Administration integration is configured (.echelon/administration.json)")
                            null
                    | Some cfg ->
                        let outcome, provider = AdministrationClient.send cfg (optionValue "--catalog" arguments) request
                        finish json required (Some request) outcome provider

    let private queryArguments (arguments: string list) =
        [ "--target"; "--system"; "--version"; "--older-than"; "--state"; "--stale-after-days" ]
        |> List.collect (fun flag -> optionValue flag arguments |> Option.map (fun v -> [ flag; v ]) |> Option.defaultValue [])

    let private query (root: string) (verb: string) (arguments: string list) =
        let json = hasFlag "--json" arguments

        match AdministrationClient.loadConfig root (optionValue "--config" arguments) with
        | Error reason ->
            eprintfn "ERROR administration configuration: %s" reason
            2
        | Ok None ->
            eprintfn "installation %s: unavailable: no Project Administration integration is configured (.echelon/administration.json)" verb
            0
        | Ok(Some config) ->
            let args = verb :: queryArguments arguments @ (if json then [ "--json" ] else [])

            match AdministrationClient.query config args with
            | Ok output ->
                printf "%s" output
                0
            | Error reason ->
                eprintfn "installation %s: unavailable: %s" verb reason
                if config.Required then 6 else 0

    /// `installation status` defaults to this repository's target.
    let private status (root: string) (arguments: string list) =
        match optionValue "--target" arguments with
        | Some _ -> query root "query" arguments
        | None ->
            match GitWorkspace.remoteUrl root |> Option.bind Target.repositoryFromRemote with
            | Some repo -> query root "query" ("--target" :: ("repository:" + repo) :: arguments)
            | None -> query root "query" arguments

    let run (root: string) (actor: Actor) (arguments: string list) =
        match arguments with
        | "register" :: rest -> mutate root "installation.register" actor rest
        | "remove" :: rest -> mutate root "installation.remove" actor rest
        | "verify" :: rest -> mutate root "installation.verify" actor rest
        | "reconcile" :: rest -> mutate root "installation.reconcile" actor rest
        | _ ->
            eprintfn "Usage: praxis %s" usage
            2

    let runQuery (root: string) (arguments: string list) =
        match arguments with
        | "list" :: rest -> query root "query" rest
        | "status" :: rest -> status root rest
        | "history" :: rest -> query root "history" rest
        | _ ->
            eprintfn "Usage: praxis %s" usage
            2

    /// Self-registration after a successful `init`/`upgrade`: records this
    /// Praxis version against this repository when an administration
    /// integration is configured. Silent when none is; never fails the
    /// lifecycle command unless policy requires registration.
    let selfRegister (root: string) (version: string) =
        match AdministrationClient.loadConfig root None with
        | Ok None -> 0
        | Error reason ->
            eprintfn "installation registration misconfigured: %s" reason
            0
        | Ok(Some config) ->
            match FileTelemetryExecutionRepository.resolveActor IdentityInputs.empty with
            | Error _ -> 0
            | Ok actor ->
                let arguments =
                    [ "--system"; "praxis"; "--version"; version; "--source-repository"; "kemiller2002/praxis"; "--distribution"; "praxis-cli" ]

                let code =
                    let previous = Console.Out
                    use sink = new StringWriter()
                    Console.SetOut sink

                    try
                        mutate root "installation.register" actor arguments
                    finally
                        Console.SetOut previous
                        eprintfn "praxis self-registration: %s" (sink.ToString().Trim())

                if config.Required then code else 0
