module Ros.Cli.RemoteCommands

open System
open System.Globalization
open System.IO
open System.Reflection
open Ros.Contracts.Remote
open Ros.Domain.Remote
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Remote

/// `praxis remote execute` (PRAXIS-REMOTE-03, `DF-ROS-2026-A041`): the
/// transport-independent boundary that runs one `praxis.remote` request.
///
/// The boundary decides (`RequestDecision`), then carries an accepted
/// request out with the *same* command implementation the local CLI runs:
/// this binary, invoked with a typed argument list in a child process whose
/// environment carries only the requester's asserted identity and the
/// executor's observed facts (`RemoteIdentity.childEnvironment`). There is
/// no second rule set. Around a mutation it adds only what remote
/// execution needs and local execution does not: a clean-tree and
/// expected-commit binding, a refusal to leave the repository less valid
/// than it found it, a restore of anything a refused mutation wrote, and a
/// journal entry that makes the outcome recoverable from the repository.
/// Transport concerns (dispatch, checkout, commit, push) stay in the
/// adapter.

let usage =
    "remote execute --request FILE [--grant read|mutate|complete|reconcile]* [--output FILE] [--timeout-seconds N] | remote classify --request FILE | remote describe"

[<Literal>]
let private MaxRequestBytes = 262144L

[<Literal>]
let private MaxMessageLength = 2000

let private optionValue name (arguments: string list) =
    arguments |> List.pairwise |> List.tryPick (fun (flag, value) -> if flag = name then Some value else None)

let private optionValues name (arguments: string list) =
    arguments |> List.pairwise |> List.choose (fun (flag, value) -> if flag = name then Some value else None)

let private now () =
    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

let private environmentVariable (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | value -> Some value

let private parentEnvironment () =
    Environment.GetEnvironmentVariables()
    |> Seq.cast<Collections.DictionaryEntry>
    |> Seq.map (fun entry -> string entry.Key, string entry.Value)
    |> Map.ofSeq

/// This very binary: a single-file executable is its own process; under
/// `dotnet ros-fs.dll` the entry assembly must be named explicitly.
let private self () =
    let processPath = Environment.ProcessPath

    match Path.GetFileNameWithoutExtension processPath with
    | "dotnet" -> processPath, [ Assembly.GetEntryAssembly().Location ]
    | _ -> processPath, []

/// A command's diagnostics, bounded, and never carrying credential
/// material even if a command were to print some.
let private diagnostic (text: string) =
    let trimmed = text.Trim()
    let bounded = if trimmed.Length > MaxMessageLength then trimmed.Substring(0, MaxMessageLength) + "..." else trimmed

    if SecretMaterial.looksLikeSecret bounded then "[diagnostics withheld: they looked like credential material]"
    else bounded

type private Context =
    { Root: string
      Version: string
      Executor: ExecutorFacts
      ObservedRef: string option
      ObservedSha: string option
      Grants: Set<Capability>
      Timeout: TimeSpan }

let private runAs (context: Context) (actor: RequestActor option) (arguments: string list) =
    let program, prefix = self ()
    let environment = RemoteIdentity.childEnvironment (parentEnvironment ()) actor context.Executor
    FileRemoteRepository.run context.Root program (prefix @ [ "--root"; context.Root ] @ arguments) environment context.Timeout

let private runCommand (context: Context) (request: Request) (arguments: string list) =
    runAs context request.Actor arguments

let private validationFindings (context: Context) (request: Request) =
    let outcome = runCommand context request [ "validate"; "--json" ]
    RemoteJson.parseValidationFindings outcome.Stdout

let private failure code message problems = RemoteFailure.create code message problems

let private withExecutor (context: Context) (response: Response) =
    { response with Executor = Some context.Executor }

let private rejectedFor (context: Context) (request: Request) (value: RemoteFailure) =
    Response.rejected
        request.ProtocolVersion
        context.Version
        (Some request.RequestId)
        (Some(Operation.code request.Operation))
        request.Repository
        context.ObservedSha
        value
    |> withExecutor context

[<Literal>]
let private GitHubWorkflow = ".github/workflows/praxis-remote.yml"

[<Literal>]
let private AgentContract = "docs/remote-agent-contract.md"

/// The `praxis.describe` discovery document (PRAXIS-REMOTE-07,
/// `PRX-REMOTE-033`): everything an agent without a local runtime needs to
/// decide what it can do -- versions, operations and their arguments, the
/// authority available, the repository's current work, and where results
/// are kept. Work state comes from the same `status` command the local CLI
/// runs; nothing here re-derives it.
let private describe (context: Context) =
    let status = runAs context None [ "status"; "--json" ]

    let openWork =
        try
            use document = System.Text.Json.JsonDocument.Parse status.Stdout

            match document.RootElement.TryGetProperty "workItems" with
            | true, items when items.ValueKind = System.Text.Json.JsonValueKind.Array ->
                items.EnumerateArray()
                |> Seq.filter (fun item ->
                    match item.TryGetProperty "semanticState" with
                    | true, state -> state.GetString() <> "complete"
                    | _ -> true)
                |> Seq.map (fun item -> item.GetRawText())
                |> Seq.toList
                |> Some
            | _ -> None
        with _ ->
            None

    let readyWork =
        let ready = runAs context None [ "work"; "ready" ]

        try
            use document = System.Text.Json.JsonDocument.Parse ready.Stdout

            if document.RootElement.ValueKind = System.Text.Json.JsonValueKind.Array then
                document.RootElement.EnumerateArray()
                |> Seq.map (fun item ->
                    let text (name: string) =
                        match item.TryGetProperty name with
                        | true, value when value.ValueKind = System.Text.Json.JsonValueKind.String -> Some(value.GetString())
                        | _ -> None

                    text "id", text "title", text "priority")
                |> Seq.toList
                |> Some
            else
                None
        with _ ->
            None

    Ros.Contracts.JsonRendering.renderIndented (fun writer ->
        writer.WriteStartObject()
        writer.WriteString("schema", "praxis.describe")
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteBoolean("available", true)
        writer.WriteString("protocol", ProtocolVersion.Protocol)
        writer.WriteStartArray("protocolVersions")
        [ 0 .. ProtocolVersion.current.Minor ]
        |> List.iter (fun minor -> writer.WriteStringValue(ProtocolVersion.code { ProtocolVersion.current with Minor = minor }))
        writer.WriteEndArray()
        writer.WriteString("praxisVersion", context.Version)
        writer.WriteString("contract", AgentContract)
        writer.WriteStartObject("repository")

        match context.ObservedRef with
        | Some reference -> writer.WriteString("ref", reference)
        | None -> writer.WriteNull("ref")

        match context.ObservedSha with
        | Some sha -> writer.WriteString("sha", sha)
        | None -> writer.WriteNull("sha")

        writer.WriteStartArray("capabilities")
        FileRemoteRepository.readRepositoryCapabilities context.Root |> Set.toList |> List.map Capability.code |> List.iter writer.WriteStringValue
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.WriteStartArray("grants")
        context.Grants |> Set.toList |> List.map Capability.code |> List.iter writer.WriteStringValue
        writer.WriteEndArray()
        writer.WriteStartArray("operations")

        Operation.all
        |> List.iter (fun operation ->
            let required, optional = Operation.arguments operation
            writer.WriteStartObject()
            writer.WriteString("operation", Operation.code operation)
            writer.WriteString("capability", Capability.code (Operation.capability operation))
            writer.WriteBoolean("mutating", Operation.isMutating operation)
            writer.WriteBoolean("requiresExpectedSha", Operation.isMutating operation)
            writer.WriteStartArray("requiredArguments")
            required |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WriteStartArray("optionalArguments")
            optional |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WriteEndObject())

        writer.WriteEndArray()

        match openWork with
        | Some items ->
            writer.WriteStartArray("openWork")
            items |> List.iter (fun raw -> writer.WriteRawValue raw)
            writer.WriteEndArray()
        | None -> writer.WriteNull("openWork")

        match readyWork with
        | Some items ->
            writer.WriteStartArray("readyWork")

            items
            |> List.iter (fun (id, title, priority) ->
                writer.WriteStartObject()
                [ "id", id; "title", title; "priority", priority ]
                |> List.iter (fun (name, value) ->
                    match value with
                    | Some text -> writer.WriteString(name, text)
                    | None -> writer.WriteNull(name))
                writer.WriteEndObject())

            writer.WriteEndArray()
        | None -> writer.WriteNull("readyWork")

        writer.WriteStartArray("transports")

        if File.Exists(Path.Combine(context.Root, GitHubWorkflow)) then
            writer.WriteStartObject()
            writer.WriteString("kind", "github-actions")
            writer.WriteString("workflow", GitHubWorkflow)
            writer.WriteString("dispatch", "workflow_dispatch with inputs request (JSON) and request_id, on the branch the request targets")
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteStartObject("results")
        writer.WriteString("journal", $"{RemotePersistence.JournalDirectory}/<requestId>.json")
        writer.WriteString("statusOperation", Operation.code Operation.RequestStatus)
        writer.WriteString("retry", "reuse the same requestId; a recorded mutation replays instead of running again")
        writer.WriteEndObject()
        writer.WriteEndObject())

let private requestStatus (context: Context) (requestId: string) =
    match FileRemoteRepository.lookup context.Root requestId with
    | Error message -> Error message
    | Ok(_, recorded) ->
        Ok(
            Ros.Contracts.JsonRendering.renderIndented (fun writer ->
                writer.WriteStartObject()
                writer.WriteString("requestId", requestId)
                writer.WriteBoolean("recorded", recorded.IsSome)

                match recorded with
                | Some response ->
                    writer.WritePropertyName("response")
                    writer.WriteRawValue(response)
                | None -> writer.WriteNull("response")

                writer.WriteEndObject())
        )

/// Runs a read operation's command and reports it verbatim.
let private executeRead (context: Context) (request: Request) (arguments: string list) =
    let outcome = runCommand context request arguments
    let result = Some(RemoteJson.commandResult outcome.Stdout)

    if outcome.TimedOut then
        rejectedFor context request (failure FailureCode.Timeout "the command did not finish in time" [])
    else
        match CommandOutcome.classify request.Operation outcome.ExitCode with
        | None -> Response.succeeded context.Version request context.ObservedSha result |> withExecutor context
        | Some code -> { rejectedFor context request (failure code (diagnostic outcome.Stderr) []) with Result = result }

/// Undoes whatever a refused mutation wrote, then reports the refusal. If
/// the undo itself fails the outcome is honestly `unknown`.
let private refuse (context: Context) (request: Request) (written: string list) (value: RemoteFailure) (result: string option) =
    match FileRemoteRepository.restore context.Root written with
    | Ok() -> { rejectedFor context request value with Result = result }
    | Error message ->
        { rejectedFor
              context
              request
              (failure
                  FailureCode.RepositoryWriteFailed
                  $"the mutation was refused ({FailureCode.code value.Code}: {value.Message}) but restoring the working tree failed: {diagnostic message}"
                  value.Problems) with
            Result = result }

let private executeMutation (context: Context) (request: Request) (arguments: string list) =
    let changed () =
        FileRemoteRepository.changedPaths context.Root |> Result.map RemotePersistence.partition

    match changed () with
    | Error message -> rejectedFor context request (failure FailureCode.Internal $"cannot observe the working tree: {diagnostic message}" [])
    | Ok(owned, other) when not (owned.IsEmpty && other.IsEmpty) ->
        // Uncommitted changes mean the executor's state is not the commit
        // the request was formed against.
        rejectedFor
            context
            request
            (failure
                FailureCode.StaleRef
                "the working tree has uncommitted changes, so it is not the commit the request names"
                [ { Field = "repository.expectedSha"; Message = "the executor's working tree differs from it" } ])
    | Ok _ ->
        let before = validationFindings context request |> Option.defaultValue []
        let outcome = runCommand context request arguments
        let result = Some(RemoteJson.commandResult outcome.Stdout)

        match changed () with
        | Error message ->
            rejectedFor context request (failure FailureCode.RepositoryWriteFailed $"cannot observe what the command wrote: {diagnostic message}" [])
        | Ok(owned, other) ->
            let written = owned @ other

            if outcome.TimedOut then
                refuse context request written (failure FailureCode.Timeout "the command did not finish in time; anything it wrote was undone" []) result
            else
                match CommandOutcome.classify request.Operation outcome.ExitCode with
                | Some code -> refuse context request written (failure code (diagnostic outcome.Stderr) []) result
                | None when not other.IsEmpty ->
                    refuse
                        context
                        request
                        written
                        (failure
                            FailureCode.Internal
                            "the command wrote outside Praxis-owned state; nothing was kept"
                            (other |> List.map (fun path -> { Field = "persistence"; Message = $"not Praxis-owned: {path}" })))
                        result
                | None ->
                    let after = validationFindings context request |> Option.defaultValue []

                    match ValidationRegression.introduced before after with
                    | (_ :: _) as introduced ->
                        refuse
                            context
                            request
                            written
                            (failure
                                FailureCode.ValidationFailed
                                "the mutation would leave the repository less valid than it was; it was undone"
                                (introduced |> List.map (fun finding -> { Field = finding.Path; Message = finding.Message })))
                            result
                    | [] ->
                        let journal = RemotePersistence.journalPath request.RequestId

                        let response =
                            { Response.succeeded context.Version request context.ObservedSha result with
                                Executor = Some context.Executor
                                Persistence = (owned @ [ journal ]) |> List.distinct |> List.sort }

                        let entry: RemoteJournal.Entry =
                            { Request = request
                              Fingerprint = RequestFingerprint.compute request
                              RecordedAt = now ()
                              Principal = context.Executor.Principal
                              Response = RemoteJson.renderResponse response }

                        match FileRemoteRepository.write context.Root entry with
                        | Ok _ -> response
                        | Error message ->
                            refuse
                                context
                                request
                                owned
                                (failure FailureCode.RepositoryWriteFailed $"the journal entry could not be written; the mutation was undone: {diagnostic message}" [])
                                result

/// Decides and executes one parsed request, returning the rendered response.
let private handle (context: Context) (request: Request) : Response * string option =
    let decideAndRun lookup recorded =
        let trusted: TrustedContext =
            { Principal = context.Executor.Principal |> Option.defaultValue "unknown"
              Grants = context.Grants
              ObservedRef = context.ObservedRef
              ObservedSha = context.ObservedSha }

        match RequestDecision.decide trusted lookup request with
        | Decision.Reject value -> rejectedFor context request value, None
        | Decision.Replay ->
            // The recorded response is returned as it was, flagged as a
            // replay; nothing executes again.
            Response.succeeded context.Version request context.ObservedSha None, recorded |> Option.map RemoteJournal.replayedResponse
        | Decision.Execute ->
            match ExecutionPlan.forRequest (now ()) request with
            | ExecutionPlan.Describe -> Response.succeeded context.Version request context.ObservedSha (Some(describe context)) |> withExecutor context, None
            | ExecutionPlan.RequestStatus requestId ->
                match requestStatus context requestId with
                | Ok result -> Response.succeeded context.Version request context.ObservedSha (Some result) |> withExecutor context, None
                | Error message -> rejectedFor context request (failure FailureCode.Internal (diagnostic message) []), None
            | ExecutionPlan.Command arguments when Operation.isMutating request.Operation -> executeMutation context request arguments, None
            | ExecutionPlan.Command arguments -> executeRead context request arguments, None

    if not (Operation.isMutating request.Operation) then
        decideAndRun JournalLookup.NotRecorded None
    else
        // One remote mutation at a time per working tree: the journal check,
        // the command, and the journal write form one critical section.
        match RegistryLock.acquire context.Root "remote-requests" RegistryLock.defaultSettings with
        | Error lockFailure -> rejectedFor context request (failure FailureCode.ConcurrencyConflict lockFailure.Message []), None
        | Ok lease ->
            try
                match FileRemoteRepository.lookup context.Root request.RequestId with
                | Error message -> rejectedFor context request (failure FailureCode.Internal (diagnostic message) []), None
                | Ok(lookup, recorded) -> decideAndRun lookup recorded
            finally
                lease.Release() |> ignore

let private emit (output: string option) (rendered: string) (succeeded: bool) =
    printf "%s" rendered
    output |> Option.iter (fun file -> File.WriteAllText(file, rendered))
    if succeeded then 0 else 1

let run (root: string) (version: string) (arguments: string list) : int =
    let grants = optionValues "--grant" arguments |> List.map (fun value -> value, Capability.tryParse value)
    let timeoutSeconds = optionValue "--timeout-seconds" arguments

    match optionValue "--request" arguments, grants |> List.tryFind (snd >> Option.isNone), timeoutSeconds |> Option.map Int32.TryParse with
    | None, _, _ ->
        eprintfn "ERROR usage: %s" usage
        2
    | _, Some(bad, _), _ ->
        eprintfn "ERROR unknown capability '%s'; expected read, mutate, complete, reconcile, or admin" bad
        2
    | _, _, Some(false, _) ->
        eprintfn "ERROR --timeout-seconds must be a whole number"
        2
    | Some requestFile, None, parsedTimeout ->
        let fullRoot = Path.GetFullPath root
        let observedRef, observedSha = FileRemoteRepository.observeHead fullRoot

        let executor =
            GitHubActionsExecutor.observe environmentVariable version
            |> Option.defaultValue
                { Kind = "local"
                  RunId = None
                  RunAttempt = None
                  WorkflowRef = None
                  Repository = None
                  Host = None
                  Principal = None
                  PraxisVersion = version }

        let context =
            { Root = fullRoot
              Version = version
              Executor = executor
              ObservedRef = observedRef
              ObservedSha = observedSha
              Grants =
                RemotePolicy.effectiveGrants
                    (FileRemoteRepository.readRepositoryCapabilities fullRoot)
                    (grants |> List.choose snd |> Set.ofList)
              Timeout =
                parsedTimeout
                |> Option.map (snd >> float >> TimeSpan.FromSeconds)
                |> Option.defaultValue (TimeSpan.FromMinutes 10.0) }

        let output = optionValue "--output" arguments

        let text =
            try
                let info = FileInfo requestFile

                if info.Length > MaxRequestBytes then Error $"the request exceeds {MaxRequestBytes} bytes"
                else Ok(File.ReadAllText requestFile)
            with error ->
                Error $"the request file cannot be read: {error.Message}"

        let respond (response: Response) =
            emit output (RemoteJson.renderResponse response) (response.Outcome = Outcome.Succeeded)

        match text with
        | Error message ->
            Response.rejected ProtocolVersion.current version None None { Ref = None; ExpectedSha = None } observedSha (failure FailureCode.InvalidRequest message [])
            |> withExecutor context
            |> respond
        | Ok document ->
            match RemoteJson.parseRequest ProtocolVersion.current document with
            | Error parseFailure -> RemoteJson.rejection ProtocolVersion.current version observedSha parseFailure |> withExecutor context |> respond
            | Ok request ->
                match handle context request with
                | _, Some replayed -> emit output replayed true
                | response, None -> respond response

/// `praxis remote classify`: the capability a request needs, decided by the
/// protocol's own catalog, so an adapter can choose least-privilege
/// credentials (a read-only job for reads) without re-implementing any of
/// it. Only the document's shape and operation are examined; nothing is
/// authorized or executed.
let classify (version: string) (arguments: string list) : int =
    match optionValue "--request" arguments with
    | None ->
        eprintfn "ERROR usage: remote classify --request FILE"
        2
    | Some requestFile ->
        let text =
            try
                let info = FileInfo requestFile
                if info.Length > MaxRequestBytes then Error $"the request exceeds {MaxRequestBytes} bytes" else Ok(File.ReadAllText requestFile)
            with error ->
                Error $"the request file cannot be read: {error.Message}"

        let rejection (response: Response) =
            printf "%s" (RemoteJson.renderResponse response)
            1

        match text with
        | Error message ->
            Response.rejected ProtocolVersion.current version None None { Ref = None; ExpectedSha = None } None (failure FailureCode.InvalidRequest message [])
            |> rejection
        | Ok document ->
            match RemoteJson.parseRequest ProtocolVersion.current document with
            | Error parseFailure -> RemoteJson.rejection ProtocolVersion.current version None parseFailure |> rejection
            | Ok request ->
                printf
                    "%s"
                    (Ros.Contracts.JsonRendering.renderIndented (fun writer ->
                        writer.WriteStartObject()
                        writer.WriteString("requestId", request.RequestId)
                        writer.WriteString("operation", Operation.code request.Operation)
                        writer.WriteString("capability", Capability.code (Operation.capability request.Operation))
                        writer.WriteBoolean("mutating", Operation.isMutating request.Operation)
                        writer.WriteEndObject()))

                0

/// `praxis remote describe`: the same discovery document, produced locally
/// (for documentation, diagnosis, or an agent that does have a runtime).
/// Grants are the repository's own opt-in; nothing is executed remotely.
let describeLocal (root: string) (version: string) : int =
    let fullRoot = Path.GetFullPath root
    let observedRef, observedSha = FileRemoteRepository.observeHead fullRoot

    let context =
        { Root = fullRoot
          Version = version
          Executor =
            { Kind = "local"
              RunId = None
              RunAttempt = None
              WorkflowRef = None
              Repository = None
              Host = None
              Principal = None
              PraxisVersion = version }
          ObservedRef = observedRef
          ObservedSha = observedSha
          Grants = FileRemoteRepository.readRepositoryCapabilities fullRoot
          Timeout = TimeSpan.FromMinutes 2.0 }

    printf "%s" (describe context)
    0
