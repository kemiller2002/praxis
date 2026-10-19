namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Artifacts

/// I2: runs `dokimos completion verify --packet P --authority A` as a
/// separate process. The executable is `PRAXIS_DOKIMOS_EXECUTABLE`, else
/// `dokimos` on PATH; a `.dll` is run through `dotnet`. Absence, a crash,
/// a timeout or an unparsable receipt all fail closed in the gate.
[<RequireQualifiedAccess>]
module DokimosCompletionVerifier =
    let EnvironmentVariable = "PRAXIS_DOKIMOS_EXECUTABLE"

    let resolve (environment: string -> string option) =
        match environment EnvironmentVariable |> Option.filter (String.IsNullOrWhiteSpace >> not) with
        | Some path when File.Exists path -> Ok path
        | Some path -> Error $"{EnvironmentVariable} names '{path}', which does not exist"
        | None ->
            let names = if OperatingSystem.IsWindows() then [ "dokimos.exe"; "dokimos.cmd" ] else [ "dokimos" ]

            environment "PATH"
            |> Option.defaultValue ""
            |> fun path -> path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            |> Seq.collect (fun directory -> names |> Seq.map (fun name -> Path.Combine(directory, name)))
            |> Seq.tryFind File.Exists
            |> Option.map Ok
            |> Option.defaultValue (Error $"no Dokimos executable: set {EnvironmentVariable} or put 'dokimos' on PATH")

    let private write (file: string) (node: JsonNode) =
        File.WriteAllText(file, node.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n", UTF8Encoding(false))

    let create (executable: Result<string, string>) (timeout: TimeSpan) : CompletionVerifier =
        fun packet authority ->
            match executable with
            | Error reason -> VerifierRun.Unavailable reason
            | Ok executable ->
                let scratch = Path.Combine(Path.GetTempPath(), $"praxis-completion-{Guid.NewGuid():N}")

                try
                    Directory.CreateDirectory scratch |> ignore
                    let packetPath = Path.Combine(scratch, "packet.json")
                    let authorityPath = Path.Combine(scratch, "authority.json")
                    write packetPath (WorkClaimJson.packetNode packet)
                    write authorityPath (WorkClaimJson.authorityNode authority)
                    let arguments = [ "completion"; "verify"; "--packet"; packetPath; "--authority"; authorityPath ]

                    let program, arguments =
                        if executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then "dotnet", executable :: arguments
                        else executable, arguments

                    match ClaimProcess.run program scratch [] None timeout arguments with
                    | Error(ClaimProcessFailure.NotStarted reason) -> VerifierRun.Unavailable $"Dokimos could not start: {reason}"
                    | Error(ClaimProcessFailure.TimedOut after) -> VerifierRun.TimedOut after
                    | Ok result ->
                        let digest = CompletionGate.sha256 (Encoding.UTF8.GetBytes result.Stdout)
                        let detail = (result.Stderr + " " + result.Stdout).Trim()
                        let detail = if detail.Length > 600 then detail.Substring(0, 600) else detail
                        VerifierRun.Exited(result.ExitCode, WorkClaimJson.parseReceipt result.Stdout, digest, detail)
                finally
                    try
                        if Directory.Exists scratch then Directory.Delete(scratch, true)
                    with _ ->
                        ()

/// Appends I1 events to `.ros/events/events.jsonl` under the shared
/// `work-protocol` lock and `work-state` journal, exactly like other work
/// events. Idempotent: an event whose `eventId` is already present is not
/// appended again.
[<RequireQualifiedAccess>]
module FileWorkClaimEvents =
    let private eventsRelativePath = ".ros/events/events.jsonl"
    let private contextRelativePath = ".ros/context/current.json"

    let private present (text: string) (eventId: string) =
        text.Split('\n') |> Array.exists (fun line -> line.Contains($"\"eventId\":\"{eventId}\"", StringComparison.Ordinal))

    let private append (root: string) (line: string) (eventId: string) =
        let eventsPath = Path.Combine(root, eventsRelativePath)
        let contextPath = Path.Combine(root, contextRelativePath)
        let current = if File.Exists eventsPath then File.ReadAllText eventsPath else ""

        if present current eventId then
            Ok()
        else
            let separator = if current.Length > 0 && not (current.EndsWith "\n") then "\n" else ""
            let content = $"{current}{separator}{line}\n"

            if File.Exists contextPath then
                let writes: WorkStateWrite list =
                    [ { Path = eventsRelativePath; Content = content }
                      { Path = contextRelativePath; Content = File.ReadAllText contextPath } ]

                WorkStateTransaction.prepare root writes
                |> Result.bind (fun () -> WorkStateTransaction.recover root)
                |> Result.mapError _.Message
            else
                Directory.CreateDirectory(Path.GetDirectoryName eventsPath) |> ignore
                let temporary = $"{eventsPath}.{Guid.NewGuid():N}.tmp"
                File.WriteAllText(temporary, content, UTF8Encoding(false))
                File.Move(temporary, eventsPath, true)
                Ok()

    let create (root: string) (actor: Actor option) (execution: string option) : ClaimEvent -> Result<unit, string> =
        fun event ->
            let repository = FileWorkConfigRepository.readRepositoryId root
            let protocolVersion = FileWorkConfigRepository.readProtocolVersion root
            let node = WorkClaimJson.eventNode repository protocolVersion execution actor event
            let eventId = node["eventId"].GetValue<string>()

            match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
            | Error failure -> Error failure.Message
            | Ok lease ->
                let result =
                    try
                        WorkStateTransaction.recover root
                        |> Result.mapError _.Message
                        |> Result.bind (fun () -> append root (Praxis.Infrastructure.Json.CanonicalJson.serializeCompact node) eventId)
                    with error ->
                        Error error.Message

                lease.Release() |> ignore
                result

[<RequireQualifiedAccess>]
type ClaimStoreChoice =
    | Local
    | GitRef of remote: string

/// Composition of the claim ports for one repository root: the real
/// clock, Guid claim tokens, the chosen store, queue-backed claimability,
/// Praxis's own evidence digests and Git HEAD.
[<RequireQualifiedAccess>]
module FileWorkClaims =
    let newToken () = Guid.NewGuid().ToString "N"

    let gitTimeout = TimeSpan.FromSeconds 60.0

    let store (root: string) (choice: ClaimStoreChoice) =
        match choice with
        | ClaimStoreChoice.Local -> FileWorkClaimStore.create root
        | ClaimStoreChoice.GitRef remote -> GitRefWorkClaimStore.create root remote gitTimeout

    /// Claimable when the queue or the event log knows the item and the
    /// queue does not record it as complete or abandoned.
    let claimability (root: string) (id: string) =
        try
            match FileBacklogQueueRepository.readItems root |> List.tryFind (fun item -> item.Id = id) with
            | Some item when item.Status = "complete" || item.Status = "abandoned" ->
                Claimability.NotClaimable $"work item '{id}' is {item.Status} in the queue"
            | Some _ -> Claimability.Claimable
            | None when (FileEventLogRepository.readWorkItemIds root).Contains id -> Claimability.Claimable
            | None -> Claimability.NotClaimable $"work item '{id}' is unknown; capture it with 'praxis add' first"
        with error ->
            Claimability.NotClaimable $"the work queue cannot be read: {error.Message}"

    let observeEvidence (root: string) (submission: EvidenceSubmission) =
        let path = if Path.IsPathRooted submission.Path then submission.Path else Path.Combine(root, submission.Path)

        try
            if File.Exists path then
                EvidenceObservation.Observed(CompletionGate.sha256 (File.ReadAllBytes path))
            else
                EvidenceObservation.Missing
        with error ->
            EvidenceObservation.Unreadable error.Message

    let observeHead (root: string) () =
        match ClaimProcess.run "git" root [] None gitTimeout [ "rev-parse"; "--verify"; "HEAD^{commit}" ] with
        | Ok result when result.ExitCode = 0 -> Ok(result.Stdout.Trim())
        | Ok result -> Error $"cannot observe HEAD: {result.Stderr.Trim()}"
        | Error _ -> Error "cannot observe HEAD: git is unavailable"

    let ports (root: string) (choice: ClaimStoreChoice) (actor: Actor option) (execution: string option) : WorkClaimPorts =
        { Store = store root choice
          Now = fun () -> DateTimeOffset.UtcNow
          Claimability = claimability root
          Emit = FileWorkClaimEvents.create root actor execution }

    let completionPorts (root: string) (choice: ClaimStoreChoice) (actor: Actor option) (execution: string option) (timeout: TimeSpan) =
        let environment name = Environment.GetEnvironmentVariable name |> Option.ofObj

        { Claims = ports root choice actor execution
          ObserveEvidence = observeEvidence root
          ObserveHead = observeHead root
          Verifier = DokimosCompletionVerifier.create (DokimosCompletionVerifier.resolve environment) timeout }

    let readAuthorityInput (root: string) (path: string) =
        let file = if Path.IsPathRooted path then path else Path.Combine(root, path)

        try
            if File.Exists file then WorkClaimJson.parseAuthorityInput (File.ReadAllText file)
            else Error $"completion authority input '{path}' does not exist"
        with error ->
            Error error.Message
