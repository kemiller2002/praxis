namespace Praxis.Infrastructure.Identity

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Contracts.Identity
open Praxis.Domain.Identity
open Praxis.Infrastructure.Git

/// Observes the repository Praxis runs in (PRX-REMOTE-046). In GitHub
/// Actions the provider's stable repository ID and locator are observed
/// from `GITHUB_REPOSITORY_ID` and `GITHUB_REPOSITORY`; elsewhere only the
/// `origin` remote's locator is, and its provider only when the remote is
/// on a known host. Nothing is fetched from the network.
[<RequireQualifiedAccess>]
module RepositoryObserver =
    let private knownHosts = [ "github.com", RepositoryProvider.github ]

    let private providerOfRemote (url: string) =
        let lowered = url.ToLowerInvariant()

        knownHosts
        |> List.tryPick (fun (host, provider) ->
            if lowered.Contains("://" + host + "/") || lowered.Contains("@" + host + ":") || lowered.Contains("@" + host + "/") then
                Some provider
            else
                None)

    let environmentVariable (name: string) =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> None
        | value -> Some value

    let observeWith (variable: string -> string option) (root: string) : RepositoryObservation =
        match variable "GITHUB_ACTIONS", variable "GITHUB_REPOSITORY_ID" with
        | Some "true", Some providerId when RepositoryIdentity.isValidProviderId providerId ->
            { Provider = Some RepositoryProvider.github
              ProviderId = Some providerId
              Locator = variable "GITHUB_REPOSITORY" |> Option.bind RepositoryLocator.tryCreate
              Source = "github-actions" }
        | _ ->
            match ProcessGitRepository.readLines root [ "remote"; "get-url"; "origin" ] with
            | Ok(url :: _) ->
                { Provider = providerOfRemote url
                  ProviderId = None
                  Locator = RepositoryLocator.ofRemoteUrl url
                  Source = "git-remote" }
            | Ok []
            | Error _ -> RepositoryObservation.none

    let observe root = observeWith environmentVariable root

/// `ros.json` `repository.identity` (PRX-REMOTE-046/048). The legacy
/// `repository.id` name is left untouched: it stays a name.
[<RequireQualifiedAccess>]
module FileRepositoryIdentityRepository =
    let private configPath root = Path.Combine(root, "ros.json")

    let private readConfig (root: string) : Result<JsonObject option, string> =
        let path = configPath root

        if not (File.Exists path) then
            Ok None
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as config -> Ok(Some config)
                | _ -> Error "ros.json must be a JSON object"
            with
            | :? JsonException as error -> Error $"ros.json is not valid JSON: {error.Message}"
            | :? IOException as error -> Error $"ros.json cannot be read: {error.Message}"

    let private repositoryNode (config: JsonObject) =
        match config["repository"] with
        | :? JsonObject as repository -> Some repository
        | _ -> None

    /// The configured identity, `Ok None` when none is configured.
    let readConfigured (root: string) : Result<RepositoryIdentity option, string> =
        readConfig root
        |> Result.bind (fun config ->
            match config |> Option.bind repositoryNode |> Option.bind (fun repository -> Option.ofObj repository["identity"]) with
            | None -> Ok None
            | Some node -> IdentityJson.parseRepository None node |> Result.map Some |> Result.mapError (fun message -> $"ros.json repository.identity: {message}"))

    /// The legacy `repository.id` name, when present.
    let readLegacyName (root: string) =
        match readConfig root with
        | Ok(Some config) ->
            match config |> repositoryNode |> Option.bind (fun repository -> Option.ofObj repository["id"]) with
            | Some(:? JsonValue as value) when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
            | _ -> None
        | _ -> None

    let status (variable: string -> string option) (root: string) : Result<RepositoryIdentityStatus, string> =
        readConfigured root |> Result.map (fun configured -> RepositoryIdentityStatus.assess configured (RepositoryObserver.observeWith variable root))

    /// The repository identity canonical work-item identities derive from
    /// right now, or `None` (not established, unreadable, or contradicted).
    let current (variable: string -> string option) (root: string) =
        match status variable root with
        | Ok status -> RepositoryIdentityStatus.current status
        | Error _ -> None

    /// Records `repository.identity`, keeping every other setting.
    let writeConfigured (root: string) (identity: RepositoryIdentity) : Result<unit, string> =
        readConfig root
        |> Result.bind (fun config ->
            match config with
            | None -> Error "ros.json does not exist; run 'praxis init' first"
            | Some config ->
                let repository =
                    match repositoryNode config with
                    | Some repository -> repository
                    | None ->
                        let created = JsonObject()
                        config["repository"] <- created
                        created

                repository["identity"] <- IdentityJson.renderRepository identity

                try
                    let path = configPath root
                    let temporary = path + ".identity.tmp"
                    File.WriteAllText(temporary, config.ToJsonString IdentityJson.options + "\n")
                    File.Move(temporary, path, true)
                    Ok()
                with :? IOException as error ->
                    Error $"ros.json cannot be written: {error.Message}")

/// `.praxis/instance.json`, the locally authoritative Praxis instance
/// identity (DER-16, 17). Reads report typed failures; nothing is
/// swallowed. Writes never replace a record unless the domain decided a
/// reinitialization.
[<RequireQualifiedAccess>]
module FileInstanceIdentityStore =
    [<Literal>]
    let RelativePath = ".praxis/instance.json"

    let private path (root: string) = Path.Combine(root, ".praxis", "instance.json")

    let readRecord (root: string) : Result<InstanceRecord option, string> =
        let file = path root

        if not (File.Exists file) then
            Ok None
        else
            try
                IdentityJson.parseInstance (File.ReadAllText file) |> Result.map Some
            with :? IOException as error ->
                Error $"cannot be read: {error.Message}"

    /// The repository identity an instance is bound to and checked against:
    /// what the environment observes first (it cannot be copied with the
    /// files), then the configured identity.
    let currentRepository (variable: string -> string option) (root: string) =
        let observation = RepositoryObserver.observeWith variable root

        match RepositoryObservation.identity observation with
        | Some observed when observed.ProviderId.IsSome -> Some observed
        | observed ->
            match FileRepositoryIdentityRepository.readConfigured root with
            | Ok(Some configured) ->
                match observed |> Option.bind _.Locator with
                | Some locator when configured.Locator <> Some locator -> observed
                | _ -> Some configured
            | Ok None
            | Error _ -> observed

    let localWith (variable: string -> string option) (root: string) : LocalInstance =
        match readRecord root with
        | Ok None -> LocalInstance.Missing
        | Error reason -> LocalInstance.Unreadable reason
        | Ok(Some record) -> LocalInstance.Present(record, InstanceBinding.assess record (currentRepository variable root))

    let local root = localWith RepositoryObserver.environmentVariable root

    /// The instance ID new records may carry (never a foreign one).
    let authoritativeId (root: string) =
        local root |> LocalInstance.authoritativeId |> Option.map InstanceId.value

    let private write (root: string) (record: InstanceRecord) (replace: bool) : Result<unit, string> =
        try
            let file = path root
            Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
            let temporary = file + ".tmp"
            File.WriteAllText(temporary, IdentityJson.renderInstance record)
            File.Move(temporary, file, replace)
            Ok()
        with :? IOException as error ->
            Error $"{RelativePath} cannot be written: {error.Message}"

    /// Creates the identity when missing (or replaces it on an explicit
    /// reinitialization). Returns the decision that was applied.
    let ensureWith
        (variable: string -> string option)
        (root: string)
        (newId: InstanceId)
        (now: DateTimeOffset)
        (version: string)
        (reinitialize: string option)
        : Result<InstanceInitDecision, string> =
        let existing = localWith variable root
        let decision = InstanceInit.decide existing newId now version (currentRepository variable root) reinitialize

        match decision with
        | InstanceInitDecision.Create record ->
            let replacing =
                match existing with
                | LocalInstance.Present _ -> true
                | _ -> false

            write root record replacing |> Result.map (fun () -> decision)
        | InstanceInitDecision.Keep _ -> Ok decision
        | InstanceInitDecision.Refuse reason -> Error reason

    /// `init`/`upgrade`/`instance init` with a fresh random identity.
    let ensure (root: string) (version: string) (reinitialize: string option) =
        ensureWith RepositoryObserver.environmentVariable root (InstanceId.ofGuid (Guid.NewGuid())) DateTimeOffset.UtcNow version reinitialize

/// The lifecycle port: `init` and `upgrade` ensure the instance identity
/// and report what they did in one line.
[<RequireQualifiedAccess>]
module InstanceIdentityLifecycle =
    let describe (decision: InstanceInitDecision) =
        match decision with
        | InstanceInitDecision.Create record when not record.Predecessors.IsEmpty ->
            $"reinitialized as {InstanceId.value record.InstanceId} (replaces {InstanceId.value record.Predecessors.Head.InstanceId})"
        | InstanceInitDecision.Create record -> $"created {InstanceId.value record.InstanceId} in {FileInstanceIdentityStore.RelativePath}"
        | InstanceInitDecision.Keep record -> $"kept {InstanceId.value record.InstanceId}"
        | InstanceInitDecision.Refuse reason -> $"refused: {reason}"

    let ensure (root: string) (version: string) : Result<string, string> =
        FileInstanceIdentityStore.ensure root version None |> Result.map describe

/// `ros.json` `workProtocol.branchPolicy` and the branch work runs on
/// (DER-01).
[<RequireQualifiedAccess>]
module FileBranchPolicyRepository =
    /// The configured policy; an unrecognised value is an error, never
    /// silently `none`.
    let readPolicy (root: string) : Result<BranchPolicy, string> =
        let path = Path.Combine(root, "ros.json")

        if not (File.Exists path) then
            Ok BranchPolicy.Unrestricted
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as config ->
                    match config["workProtocol"] with
                    | :? JsonObject as protocol ->
                        match protocol["branchPolicy"] with
                        | null -> Ok BranchPolicy.Unrestricted
                        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String ->
                            let text = value.GetValue<string>()

                            BranchPolicy.tryParse text
                            |> Option.map Ok
                            |> Option.defaultValue (Error $"'{text}' is not a branch policy (none or work-item-id)")
                        | _ -> Error "must be the string 'none' or 'work-item-id'"
                    | _ -> Ok BranchPolicy.Unrestricted
                | _ -> Ok BranchPolicy.Unrestricted
            with
            | :? JsonException as error -> Error $"ros.json is not valid JSON: {error.Message}"
            | :? IOException as error -> Error $"ros.json cannot be read: {error.Message}"

    /// The branch under validation: the pull request's head branch in
    /// GitHub Actions (whose checkout is a detached merge commit), else the
    /// checked-out branch; `None` for a detached HEAD.
    let currentBranchWith (variable: string -> string option) (root: string) =
        match variable "GITHUB_ACTIONS", variable "GITHUB_HEAD_REF" with
        | Some "true", Some head -> Some head
        | _ -> ProcessGitRepository.readBranchAndCommit root |> fst

    let currentBranch root = currentBranchWith RepositoryObserver.environmentVariable root

/// The identity contributors to `validate`: repository identity
/// (PRX-REMOTE-046/048/049), instance identity (DER-16, 17, 24) and the
/// branch policy (DER-01). Each finding is `(isError, path, field, message)`.
/// A missing instance identity is a warning only for an installation that
/// has the `.praxis/` layout (one that predates instance identity); a
/// repository identity that was never adopted is not a validate finding.
[<RequireQualifiedAccess>]
module FileIdentityValidation =
    let findingsWith
        (variable: string -> string option)
        (root: string)
        (observedPaths: unit -> Result<string list, string>)
        (meaningful: string list -> string list)
        (workItems: unit -> string list)
        : (bool * string * string * string) list =
        let installed = Directory.Exists(Path.Combine(root, ".praxis"))

        let repository =
            match FileRepositoryIdentityRepository.status variable root with
            | Error message -> [ true, "ros.json", "repository.identity", message ]
            | Ok status ->
                let adopted =
                    match status with
                    | RepositoryIdentityStatus.NotEstablished _ -> false
                    | _ -> true

                // An identity never adopted is reported by `repository
                // identity` and `work capture`, not as a validate warning.
                RepositoryIdentityStatus.findings status
                |> List.filter (fun (isError, _) -> isError || adopted)
                |> List.map (fun (isError, message) -> isError, "ros.json", "repository.identity", message)

        let instance =
            LocalInstance.findings (FileInstanceIdentityStore.localWith variable root)
            |> List.filter (fun (isError, _) -> isError || installed)
            |> List.map (fun (isError, message) -> isError, FileInstanceIdentityStore.RelativePath, "instanceId", message)

        let branch =
            match FileBranchPolicyRepository.readPolicy root with
            | Error message -> [ true, "ros.json", "workProtocol.branchPolicy", message ]
            | Ok BranchPolicy.Unrestricted -> []
            | Ok policy ->
                match observedPaths () with
                | Error message -> [ true, ".git", "branch", $"cannot apply workProtocol.branchPolicy: {message}" ]
                | Ok paths ->
                    BranchPolicy.check policy (FileBranchPolicyRepository.currentBranchWith variable root) (meaningful paths) (workItems ())
                    |> Option.toList
                    |> List.map (fun message -> true, ".git", "branch", message)

        repository @ instance @ branch

    let findings root observedPaths meaningful workItems =
        findingsWith RepositoryObserver.environmentVariable root observedPaths meaningful workItems
