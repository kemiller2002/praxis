namespace Praxis.Infrastructure.Execution

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Execution
open Praxis.Contracts.Execution
open Praxis.Domain.Execution
open Praxis.Domain.Remote

/// The file-system, Git and process implementation of `ExecutionPorts`
/// for one repository root.
[<RequireQualifiedAccess>]
module FileExecutionPorts =
    /// The environment variable a host or launcher sets to the path of its
    /// `praxis.containment-evidence/1` report (PRX-SEC-001/013).
    [<Literal>]
    let ContainmentEvidenceVariable = "PRAXIS_CONTAINMENT_EVIDENCE"

    let private variable (name: string) =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> None
        | value -> Some value

    /// Run `command` under `/bin/sh -c` in `cwd`; the host observes only the
    /// exit code. A process that cannot be started is an unknown outcome,
    /// never a failure or a success.
    let runShell (cwd: string) (command: string) (environment: (string * string) list) =
        let info = ProcessStartInfo("/bin/sh", [ "-c"; command ])
        info.WorkingDirectory <- cwd
        info.UseShellExecute <- false

        for name, value in environment do
            info.Environment[name] <- value

        try
            use p = Process.Start info
            p.WaitForExit()
            ObservedFact.CommandExited(command, p.ExitCode)
        with
        | :? Win32Exception as ex -> ObservedFact.CommandOutcomeUnknown(command, ex.Message)
        | :? InvalidOperationException as ex -> ObservedFact.CommandOutcomeUnknown(command, ex.Message)

    let private parseFile (path: string) : Result<JsonNode, string> =
        if not (File.Exists path) then
            Error $"{path} does not exist"
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | null -> Error $"{path} is empty"
                | node -> Ok node
            with :? JsonException as ex ->
                Error $"{path} is not JSON: {ex.Message}"

    /// `ros.json` `execution`; the empty policy when the file or section is
    /// absent.
    let readPolicy (root: string) : Result<ExecutionPolicy, string> =
        let path = Path.Combine(root, "ros.json")

        if not (File.Exists path) then
            Ok ExecutionPolicy.empty
        else
            parseFile path
            |> Result.bind (fun node ->
                match node with
                | :? JsonObject as o ->
                    let mutable section: JsonNode = null
                    ExecutionJson.readPolicy (if o.TryGetPropertyValue("execution", &section) then Option.ofObj section else None)
                | _ -> Error "ros.json is not an object")

    /// The host-reported containment profile: an explicit file, else the
    /// host's environment variable; nothing reported is `Ok None`.
    let readContainmentEvidence (explicitPath: string option) : Result<ContainmentProfile option, string> =
        match explicitPath |> Option.orElse (variable ContainmentEvidenceVariable) with
        | None -> Ok None
        | Some path -> parseFile path |> Result.bind ExecutionJson.readContainmentEvidence |> Result.map Some

    let remoteExecutor () =
        RemoteIdentity.readExecutor variable
        |> Option.map (fun facts -> facts.Kind + (facts.RunId |> Option.map (fun r -> ":" + r) |> Option.defaultValue ""))

    /// The active telemetry execution a work transition created or kept for
    /// a work item and actor: the envelope it binds shares this ID.
    let activeTelemetryExecution (root: string) (workItemId: string) (actorId: string) =
        Praxis.Infrastructure.Provenance.FileProvenanceRepository.readExecutions root
        |> List.filter (fun view -> view.WorkItemId = workItemId && view.Status = "active" && view.Actor.Id = actorId)
        |> List.sortBy _.StartedAt
        |> List.tryLast
        |> Option.map _.ExecutionId

    let create (root: string) : ExecutionPorts =
        let absolute (relative: string) = Path.GetFullPath(Path.Combine(root, relative))

        { Store =
            { Load = ExecutionStore.loadEnvelope root
              Save = ExecutionStore.saveEnvelope root
              Exists = ExecutionStore.exists root
              ReadEntries = ExecutionStore.readEntries root
              AppendEntry = ExecutionStore.appendEntry root
              AppendRecord = fun id kind fields -> ExecutionStore.appendRecord root id kind fields DateTimeOffset.UtcNow
              AppendVerification = ExecutionStore.appendVerification root
              ReadVerification = ExecutionStore.readVerification root
              Attributions = ExecutionStore.attributions root
              ResolvedResources = ExecutionStore.resolvedResources root
              List = fun () -> ExecutionStore.list root
              NewId = fun () -> ExecutionStore.newId DateTimeOffset.UtcNow
              EnvelopePath = ExecutionStore.envelopeFile root }
          Workspace =
            { Root = root
              Head = GitWorkspace.head
              Branch = GitWorkspace.branch
              Resolve = GitWorkspace.resolve
              ChangedPaths = GitWorkspace.changedPaths
              Uncommitted = GitWorkspace.hasUncommittedChanges
              IsAncestor = GitWorkspace.isAncestor
              DirectoryExists = Directory.Exists
              Commits = GitWorkspace.commits
              Digest = GitWorkspace.fileDigest
              Repository = fun () -> GitWorkspace.remoteUrl root |> Option.bind Praxis.Domain.Installation.Target.repositoryFromRemote
              CreateWorktree = fun branch path baseline -> GitWorkspace.createWorktree root branch path baseline |> Result.map ignore
              RemoveWorktree = fun path -> GitWorkspace.removeWorktree root path |> Result.map ignore
              Absolute = absolute
              Relative = GitWorkspace.relativeTo root }
          Host =
            { Now = fun () -> DateTimeOffset.UtcNow
              Run = runShell
              Policy = fun () -> readPolicy root
              ContainmentEvidence = readContainmentEvidence
              RemoteExecutor = remoteExecutor } }
