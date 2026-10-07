namespace Ros.Domain.Installation

open System
open System.Security.Cryptography
open System.Text

// Praxis is the registration *client* of Project Administration's
// installation inventory (`installation.register` / `installation.query` /
// `installation.remove`, Echelon Registry `spec/installation-protocol.md`).
// Project Administration owns the mutation and its persistence; Praxis only
// builds typed requests, resolves the configured transport, and interprets
// typed results. Nothing here knows Project Administration's file layout.

/// Where Praxis sends installation requests.
[<RequireQualifiedAccess>]
type Transport =
    /// Run the receiving system's executable against a store it owns.
    | LocalExecutable of command: string list * store: string
    /// Dispatch the receiving system's GitHub workflow with the request.
    | GitHubWorkflow of repository: string * workflow: string * reference: string

/// `.echelon/administration.json` (schema `echelon.administration/v1`).
type AdministrationConfig =
    { Provider: string
      /// When true, failure to register fails the calling command.
      Required: bool
      /// The explicitly configured logical environment ID, if any. Never
      /// derived from the host.
      EnvironmentId: string option
      /// An Echelon Registry `registry/systems.json` used to refuse unknown
      /// systems. Optional; its absence never blocks registration.
      Catalog: string option
      Transport: Transport }

[<RequireQualifiedAccess>]
type TargetKind =
    | Repository
    | Environment

type Target = { Kind: TargetKind; Id: string }

[<RequireQualifiedAccess>]
module Target =
    let kindToWire kind =
        match kind with
        | TargetKind.Repository -> "repository"
        | TargetKind.Environment -> "environment"

    let key (t: Target) = kindToWire t.Kind + ":" + t.Id

    let private isLowerAlnum (c: char) = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')

    let validRepository (id: string) =
        match id.Split('/') with
        | [| owner; name |] ->
            owner.Length > 0
            && name.Length > 0
            && owner |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-')
            && name |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_' || c = '.')
        | _ -> false

    let validEnvironment (id: string) =
        id.Length > 0 && id.Length <= 64 && isLowerAlnum id[0] && id |> Seq.forall (fun c -> isLowerAlnum c || c = '.' || c = '_' || c = '-')

    /// Stable `owner/repository` identity from a Git remote URL. Only the
    /// remote's repository path is used; no local path is ever part of it.
    let repositoryFromRemote (remoteUrl: string) =
        let trimmed = remoteUrl.Trim()

        let path =
            if trimmed.StartsWith("git@", StringComparison.Ordinal) then
                match trimmed.IndexOf ':' with
                | -1 -> None
                | i -> Some(trimmed.Substring(i + 1))
            else
                match Uri.TryCreate(trimmed, UriKind.Absolute) with
                | true, uri when uri.Scheme = "https" || uri.Scheme = "http" || uri.Scheme = "ssh" -> Some(uri.AbsolutePath.TrimStart('/'))
                | _ -> None

        path
        |> Option.map (fun p -> if p.EndsWith(".git", StringComparison.Ordinal) then p.Substring(0, p.Length - 4) else p)
        |> Option.map (fun p ->
            // A proxied remote such as http://host/git/owner/repo keeps the
            // last two segments.
            let parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries)
            if parts.Length >= 2 then parts[parts.Length - 2] + "/" + parts[parts.Length - 1] else p)
        |> Option.filter validRepository

    /// Resolve the target. A repository target may be inferred from the Git
    /// remote; an environment target must be explicit (flag or configured
    /// logical ID) and is never inferred from a hostname.
    let resolve (kind: string option) (id: string option) (configuredEnvironment: string option) (remote: string option) : Result<Target, string> =
        match kind |> Option.map (fun k -> k.Trim().ToLowerInvariant()) with
        | Some "environment" ->
            match id |> Option.orElse configuredEnvironment with
            | Some env when validEnvironment env -> Ok { Kind = TargetKind.Environment; Id = env }
            | Some env -> Error $"environment target '{env}' is not a valid logical id ([a-z0-9][a-z0-9._-]{{0,63}})"
            | None ->
                Error "an environment target needs an explicit logical id (--target-id or .echelon/administration.json environment.id); Praxis never infers one from the hostname"
        | None
        | Some "repository" ->
            match id with
            | Some repo when validRepository repo -> Ok { Kind = TargetKind.Repository; Id = repo }
            | Some repo -> Error $"repository target '{repo}' must be owner/repository"
            | None ->
                match remote |> Option.bind repositoryFromRemote with
                | Some repo -> Ok { Kind = TargetKind.Repository; Id = repo }
                | None -> Error "could not infer the repository identity from the Git remote; pass --target-id owner/repository"
        | Some other -> Error $"unknown target kind '{other}'; expected repository or environment"

[<RequireQualifiedAccess>]
module Validation =
    let systemId (raw: string) =
        raw.Length > 0 && raw.Length <= 64 && Char.IsAsciiLetterLower raw[0] && raw |> Seq.forall (fun c -> Char.IsAsciiLetterLower c || Char.IsAsciiDigit c || c = '-')

    /// SemVer 2.0 core, pre-release and build syntax.
    let version (raw: string) =
        let numeric (s: string) = s.Length > 0 && s |> Seq.forall Char.IsAsciiDigit && (s = "0" || s[0] <> '0')
        let ids (s: string) = s.Split('.') |> Array.forall (fun p -> p.Length > 0 && p |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-'))
        let core, build = match raw.IndexOf '+' with -1 -> raw, None | i -> raw.Substring(0, i), Some(raw.Substring(i + 1))
        let release, pre = match core.IndexOf '-' with -1 -> core, None | i -> core.Substring(0, i), Some(core.Substring(i + 1))

        match release.Split('.') with
        | [| a; b; c |] -> numeric a && numeric b && numeric c && pre |> Option.forall ids && build |> Option.forall ids
        | _ -> false

/// The registration request Praxis builds; rendered to
/// `echelon.installation.request/v1` by Ros.Contracts.
type RegistrationRequest =
    { Capability: string
      OperationId: string
      OccurredAt: DateTimeOffset
      SystemId: string
      SystemVersion: string
      ObservedState: string option
      Target: Target
      SourceRepository: string option
      Distribution: string option
      Release: string option
      Artifact: string option
      Digest: string option
      Evidence: (string * string) list
      ActorKind: string option
      ActorId: string option
      Provider: string option
      Model: string option
      Runtime: string option
      ExecutionId: string option
      WorkItem: string option
      ExecutionRepository: string option }

[<RequireQualifiedAccess>]
module RegistrationRequest =
    /// A deterministic idempotency key: the same logical request at the same
    /// instant (a retry) maps to the same operation.
    let operationIdFor (r: RegistrationRequest) =
        let material =
            String.concat
                "|"
                [ r.Capability
                  r.SystemId
                  r.SystemVersion
                  Target.key r.Target
                  defaultArg r.Artifact ""
                  defaultArg r.Digest ""
                  defaultArg r.ExecutionId ""
                  defaultArg r.ObservedState ""
                  r.OccurredAt.ToUniversalTime().ToString("O") ]

        "praxis-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes material)).ToLowerInvariant().Substring(0, 32)

/// What happened, from the caller's point of view.
[<RequireQualifiedAccess>]
type RegistrationOutcome =
    | Recorded of eventId: string * operation: string
    | Replayed of eventId: string
    | Unchanged
    /// Accepted by an asynchronous transport; not yet recorded.
    | Submitted of detail: string
    | Refused of code: string * message: string
    | Invalid of code: string * message: string
    /// No administration integration is configured, or the provider cannot
    /// be reached. A normal, diagnosable optional-integration outcome.
    | Unavailable of reason: string
    | Misconfigured of reason: string

[<RequireQualifiedAccess>]
module RegistrationOutcome =
    let toWire outcome =
        match outcome with
        | RegistrationOutcome.Recorded _ -> "recorded"
        | RegistrationOutcome.Replayed _ -> "replayed"
        | RegistrationOutcome.Unchanged -> "unchanged"
        | RegistrationOutcome.Submitted _ -> "submitted"
        | RegistrationOutcome.Refused _ -> "refused"
        | RegistrationOutcome.Invalid _ -> "invalid"
        | RegistrationOutcome.Unavailable _ -> "unavailable"
        | RegistrationOutcome.Misconfigured _ -> "misconfigured"

    /// Exit code under policy. Unavailable/misconfigured integration never
    /// breaks the caller unless policy requires registration.
    let exitCode (required: bool) outcome =
        match outcome with
        | RegistrationOutcome.Recorded _
        | RegistrationOutcome.Replayed _
        | RegistrationOutcome.Unchanged
        | RegistrationOutcome.Submitted _ -> 0
        | RegistrationOutcome.Invalid _ -> 2
        | RegistrationOutcome.Refused _ -> 3
        | RegistrationOutcome.Unavailable _
        | RegistrationOutcome.Misconfigured _ -> if required then 6 else 0

    /// Interpret a provider `echelon.installation.result/v1` status.
    let fromResult (status: string) (eventId: string option) (operation: string option) (code: string option) (message: string option) =
        match status with
        | "recorded" -> RegistrationOutcome.Recorded(defaultArg eventId "", defaultArg operation "")
        | "replayed" -> RegistrationOutcome.Replayed(defaultArg eventId "")
        | "unchanged" -> RegistrationOutcome.Unchanged
        | "refused" -> RegistrationOutcome.Refused(defaultArg code "refused", defaultArg message "")
        | "invalid" -> RegistrationOutcome.Invalid(defaultArg code "invalid", defaultArg message "")
        | other -> RegistrationOutcome.Unavailable $"provider returned '{other}': {defaultArg message String.Empty}"
