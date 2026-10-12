namespace Praxis.Infrastructure.Planning

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Praxis.Application.Work
open Praxis.Domain.Execution
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// A host-selected command template. `{candidate}` and `{trx}` (validators)
/// or `{worktree}` (workers) are replaced by fresh host-provisioned paths.
type LocalCommandTemplate =
    { Executable: string; ExecutableDigest: string; Arguments: string list
      Environment: Map<string, string>; Input: string
      TimeoutMilliseconds: int; MaxOutputBytes: int
      /// Absolute tool-closure files (assemblies, scripts, configuration).
      Dependencies: Map<string, string> }

type LocalValidatorTemplate = { Id: string; Tests: bool; Command: LocalCommandTemplate }

type LocalWorkerTemplate =
    { AdapterId: string; WorkerId: string
      Roles: Set<ExecutionRole>; OperatingSystems: Set<LocalWorkerOperatingSystem>
      Command: LocalCommandTemplate
      /// The worker launches only inside a containment profile qualified on
      /// this host by passing probes; otherwise every launch refuses.
      RequiresContainment: bool }

/// Host containment profile selection (`praxis.local-host/1` `containment`).
/// The evidence file is written only by `praxis local containment qualify`.
type LocalContainmentConfiguration =
    { Evidence: string
      Settings: LinuxContainmentSettings
      MaxEvidenceAge: TimeSpan }

/// Operator-owned controller configuration. It lives outside the repository
/// and is never read from repository configuration or worker output. It
/// selects mechanisms only; execution authority still comes from the signed
/// host policy and the signed delegation of each exact packet.
type LocalHostConfiguration =
    { Path: string; Digest: string
      RepositoryRoot: string; RepositoryIdentity: string
      FileAccess: LocalHostFileAccess
      PolicyRoot: string; JournalRoot: string; SpoolRoot: string; SubmissionRoot: string
      IntegrationRoot: string; EvidenceRoot: string; CheckoutRoot: string
      LockTimeout: TimeSpan
      Git: LocalGitTool; Committer: LocalIntegrationCommitter
      ValidatorPolicyRevision: string
      SystemValidators: Map<WorkerCheckKind, LocalValidatorTemplate>
      AcceptanceValidators: Map<string, LocalValidatorTemplate>
      Workers: LocalWorkerTemplate list
      CompletionEvidence: (string * LocalEvidenceSource) list
      Containment: LocalContainmentConfiguration option }

[<RequireQualifiedAccess>]
module LocalHostConfiguration =
    [<Literal>]
    let Schema = "praxis.local-host/1"

    let private maxBytes = 262144

    let private clean (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length <= 1024 && value = value.Trim()
        && not (value |> Seq.exists Char.IsControl)

    let private fail message : 'a = raise (InvalidDataException message)

    let private exactly (element: JsonElement) (required: string list) (optional: string list) =
        if element.ValueKind <> JsonValueKind.Object then fail "expected a JSON object"
        let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        if names.Length <> (Set.ofList names).Count then fail "duplicate configuration field"
        let allowed = Set.ofList (required @ optional)
        match names |> List.tryFind (allowed.Contains >> not) with
        | Some name -> fail ("unknown configuration field: " + name)
        | None -> ()
        match required |> List.tryFind (fun name -> not (List.contains name names)) with
        | Some name -> fail ("missing configuration field: " + name)
        | None -> ()

    let private property (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value -> Some value
        | _ -> None

    let private text (element: JsonElement) name =
        match property element name with
        | Some value when value.ValueKind = JsonValueKind.String && clean (value.GetString()) -> value.GetString()
        | _ -> fail ("invalid text field: " + name)

    let private textOr element name fallback = if (property element name).IsSome then text element name else fallback

    let private absolute element name =
        let value = text element name
        if not (Path.IsPathFullyQualified value) then fail (name + " must be an absolute path")
        Path.TrimEndingDirectorySeparator(Path.GetFullPath value)

    let private number (element: JsonElement) name low high =
        match property element name with
        | Some value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, parsed when parsed >= low && parsed <= high -> parsed
            | _ -> fail ("out-of-range number: " + name)
        | _ -> fail ("invalid number field: " + name)

    let private boolean (element: JsonElement) name =
        match property element name with
        | Some value when value.ValueKind = JsonValueKind.True -> true
        | Some value when value.ValueKind = JsonValueKind.False -> false
        | None -> false
        | _ -> fail ("invalid boolean field: " + name)

    let private strings (element: JsonElement) name =
        match property element name with
        | None -> []
        | Some value when value.ValueKind = JsonValueKind.Array && value.GetArrayLength() <= 64 ->
            value.EnumerateArray() |> Seq.map (fun item ->
                if item.ValueKind <> JsonValueKind.String || isNull (item.GetString()) || item.GetString().Length > 4096
                   || item.GetString().Contains '\u0000' then fail ("invalid string array: " + name)
                item.GetString()) |> Seq.toList
        | _ -> fail ("invalid string array: " + name)

    let private textMap (element: JsonElement) name =
        match property element name with
        | None -> Map.empty
        | Some value when value.ValueKind = JsonValueKind.Object ->
            let entries = value.EnumerateObject() |> Seq.toList
            if entries.Length > 256 || entries.Length <> (entries |> List.map _.Name |> Set.ofList).Count then fail ("invalid map: " + name)
            entries |> List.map (fun entry ->
                if entry.Value.ValueKind <> JsonValueKind.String || isNull (entry.Value.GetString()) then fail ("invalid map value: " + name)
                entry.Name, entry.Value.GetString()) |> Map.ofList
        | _ -> fail ("invalid map: " + name)

    let private digest value =
        if not (LocalAgentHandoff.isDigest value) then fail "pins must be lowercase sha256: digests"
        value

    let private command (element: JsonElement) =
        exactly element [ "executable"; "sha256"; "arguments"; "timeoutMilliseconds"; "maxOutputBytes" ] [ "environment"; "input"; "dependencies" ]
        let dependencies = textMap element "dependencies"
        if dependencies |> Map.exists (fun path value -> not (Path.IsPathFullyQualified path) || not (LocalAgentHandoff.isDigest value)) then
            fail "dependency pins need absolute paths and sha256: digests"
        { Executable = absolute element "executable"; ExecutableDigest = digest (text element "sha256")
          Arguments = strings element "arguments"; Environment = textMap element "environment"
          Input = (match property element "input" with Some value when value.ValueKind = JsonValueKind.String -> value.GetString() | None -> "" | _ -> fail "invalid input")
          TimeoutMilliseconds = number element "timeoutMilliseconds" 1 86400000
          MaxOutputBytes = number element "maxOutputBytes" 1 1048576
          Dependencies = dependencies }

    /// Only the named role tokens may appear; any other brace refuses, so a
    /// literal value can never impersonate a host-provisioned location.
    let private onlyTokens (allowed: string list) (template: LocalCommandTemplate) =
        let values = template.Input :: template.Arguments @ (template.Environment |> Map.values |> Seq.toList)
        let stripped = values |> List.map (fun value -> allowed |> List.fold (fun (rest: string) token -> rest.Replace(token, "")) value)
        if stripped |> List.exists (fun value -> value.Contains '{' || value.Contains '}') then
            fail ("command templates may use only these placeholders: " + String.concat ", " allowed)
        values |> List.exists (fun value -> value.Contains "{trx}")

    let private validator (element: JsonElement) =
        exactly element [ "id"; "command" ] [ "tests" ]
        let tests = boolean element "tests"
        let template = command (element.GetProperty "command")
        let mentionsTrx = onlyTokens (if tests then [ "{candidate}"; "{trx}" ] else [ "{candidate}" ]) template
        if tests && not mentionsTrx then fail "a tests validator must pass {trx} to its runner"
        { Id = text element "id"; Tests = tests; Command = template }

    let private role value =
        match ExecutionRole.tryParse value with
        | Some role -> role
        | None -> fail ("unknown worker role: " + value)

    let private operatingSystem = function
        | "linux" -> LocalWorkerOperatingSystem.Linux
        | "macos" -> LocalWorkerOperatingSystem.MacOS
        | "windows" -> LocalWorkerOperatingSystem.Windows
        | other -> fail ("unknown operating system: " + other)

    let private worker (element: JsonElement) =
        exactly element [ "adapterId"; "workerId"; "roles"; "operatingSystems"; "command" ] [ "containment" ]
        let contained =
            match (property element "containment") |> Option.map (fun _ -> text element "containment") with
            | None | Some "none" -> false
            | Some "required" -> true
            | Some other -> fail ("unknown worker containment requirement: " + other)
        let roles = strings element "roles" |> List.map role |> Set.ofList
        let systems = strings element "operatingSystems" |> List.map operatingSystem |> Set.ofList
        let template = command (element.GetProperty "command")
        onlyTokens [ "{worktree}" ] template |> ignore
        if roles.IsEmpty || systems.IsEmpty then fail "invalid worker template"
        // The archived output spool bounds combined stdout/stderr at 64 KiB.
        if template.MaxOutputBytes > 65536 then fail "worker maxOutputBytes must be at most 65536"
        { AdapterId = text element "adapterId"; WorkerId = text element "workerId"; Roles = roles; OperatingSystems = systems; Command = template
          RequiresContainment = contained }

    /// Defaults are the merged-/usr toolchain and a minimal /etc subset; an
    /// operator adds pinned toolchains (for example a dotnet root) explicitly.
    let defaultReadOnlyPaths =
        [ "/usr"; "/bin"; "/sbin"; "/lib"; "/lib64"; "/lib32"; "/etc/passwd"; "/etc/group"; "/etc/nsswitch.conf"
          "/etc/hosts"; "/etc/ld.so.cache"; "/etc/localtime"; "/etc/alternatives"; "/etc/ssl/certs" ]

    let private containment repository (element: JsonElement) =
        exactly element [ "evidence" ] [ "readOnlyPaths"; "environmentAllowlist"; "maxProcesses"; "maxOpenFiles"; "maxFileBytes"; "scratchMegabytes"; "maxEvidenceAgeMinutes" ]
        let evidence = absolute element "evidence"
        LocalHostJournalFiles.rootOutside repository (Path.GetDirectoryName evidence) |> ignore
        let optionalNumber name low high fallback = if (property element name).IsSome then number element name low high else fallback
        let fileBytes =
            match property element "maxFileBytes" with
            | None -> 1073741824L
            | Some value when value.ValueKind = JsonValueKind.Number ->
                match value.TryGetInt64() with
                | true, parsed when parsed >= 4096L && parsed <= 68719476736L -> parsed
                | _ -> fail "out-of-range number: maxFileBytes"
            | _ -> fail "invalid number field: maxFileBytes"
        let settings : LinuxContainmentSettings =
            { ReadOnlyPaths = (if (property element "readOnlyPaths").IsSome then strings element "readOnlyPaths" else defaultReadOnlyPaths)
              EnvironmentAllowlist = (if (property element "environmentAllowlist").IsSome then strings element "environmentAllowlist" else [ "PATH"; "LANG"; "HOME"; "TMPDIR" ]) |> Set.ofList
              MaxProcesses = optionalNumber "maxProcesses" 8 65536 256
              MaxOpenFiles = optionalNumber "maxOpenFiles" 16 1048576 1024
              MaxFileBytes = fileBytes
              ScratchMegabytes = optionalNumber "scratchMegabytes" 1 65536 256 }
        match LinuxContainment.checkSettings settings with
        | Error reason -> fail ("containment settings refused: " + reason)
        | Ok() ->
            { Evidence = evidence; Settings = settings
              MaxEvidenceAge = TimeSpan.FromMinutes(float (optionalNumber "maxEvidenceAgeMinutes" 1 43200 1440)) }

    let private evidenceSource = function
        | "member-changes" -> LocalEvidenceSource.MemberChanges
        | "acceptance-artifacts" -> LocalEvidenceSource.AcceptanceArtifacts
        | other -> fail ("unknown completion evidence source: " + other)

    let private fileAccess = function
        | "unix-private" -> LocalHostFileAccess.unixPrivate
        | "portable-development" -> LocalHostFileAccess.portableDevelopment
        | other -> fail ("unknown file access profile: " + other)

    let parse (path: string) (content: byte array) =
        try
            if content.Length > maxBytes then fail "host configuration exceeds 256 KiB"
            let textContent = UTF8Encoding(false, true).GetString content
            use document = JsonDocument.Parse(textContent, JsonDocumentOptions(MaxDepth = 8))
            let root = document.RootElement
            exactly root [ "schemaVersion"; "repositoryRoot"; "repositoryIdentity"; "fileAccess"; "roots"; "git"; "committer"
                           "validatorPolicyRevision"; "systemValidators"; "acceptanceValidators"; "workers"; "completionEvidence" ]
                         [ "lockTimeoutMilliseconds"; "containment" ]
            if text root "schemaVersion" <> Schema then fail "unsupported host configuration schema"
            let repository = absolute root "repositoryRoot"
            let roots = root.GetProperty "roots"
            let names = [ "policy"; "journal"; "spool"; "submissions"; "integration"; "evidence"; "checkouts" ]
            exactly roots names []
            let located = names |> List.map (fun name -> name, LocalHostJournalFiles.rootOutside repository (absolute roots name)) |> Map.ofList
            if (located |> Map.values |> Set.ofSeq).Count <> names.Length then fail "host roots must be distinct directories"
            let git = root.GetProperty "git"
            exactly git [ "executable"; "sha256" ] [ "timeoutMilliseconds" ]
            let committer = root.GetProperty "committer"
            exactly committer [ "name"; "email" ] []
            let system = root.GetProperty "systemValidators"
            exactly system [ "build"; "tests"; "architecture"; "scope" ] []
            let kinds = [ "build", WorkerCheckKind.Build; "tests", WorkerCheckKind.Tests; "architecture", WorkerCheckKind.Architecture; "scope", WorkerCheckKind.Scope ]
            let systemValidators = kinds |> List.map (fun (name, kind) -> kind, validator (system.GetProperty name)) |> Map.ofList
            if not systemValidators[WorkerCheckKind.Tests].Tests then fail "the tests system validator must produce TRX (\"tests\": true)"
            let acceptance = root.GetProperty "acceptanceValidators"
            if acceptance.ValueKind <> JsonValueKind.Array || acceptance.GetArrayLength() > 256 then fail "acceptanceValidators must be an array"
            let acceptanceValidators = acceptance.EnumerateArray() |> Seq.map validator |> Seq.toList
            let ids = (systemValidators |> Map.values |> Seq.map _.Id |> Seq.toList) @ (acceptanceValidators |> List.map _.Id)
            if ids.Length <> (Set.ofList ids).Count then fail "validator ids must be unique"
            let workers = root.GetProperty "workers"
            if workers.ValueKind <> JsonValueKind.Array || workers.GetArrayLength() = 0 || workers.GetArrayLength() > 256 then fail "workers must be a non-empty array"
            let evidence = root.GetProperty "completionEvidence"
            if evidence.ValueKind <> JsonValueKind.Object then fail "completionEvidence must be an object"
            let completion = evidence.EnumerateObject() |> Seq.map (fun entry ->
                if entry.Value.ValueKind <> JsonValueKind.String || not (clean entry.Name) then fail "invalid completion evidence entry"
                entry.Name, evidenceSource (entry.Value.GetString())) |> Seq.toList
            if completion.IsEmpty then fail "completionEvidence must name at least one native evidence type"
            let parsedWorkers = workers.EnumerateArray() |> Seq.map worker |> Seq.toList
            let containmentSettings = property root "containment" |> Option.map (containment repository)
            if containmentSettings.IsNone && parsedWorkers |> List.exists _.RequiresContainment then
                fail "a worker requires containment but the host configuration has no containment profile"
            Ok { Path = Path.GetFullPath path
                 Digest = "sha256:" + (SHA256.HashData content |> Convert.ToHexString).ToLowerInvariant()
                 RepositoryRoot = repository; RepositoryIdentity = text root "repositoryIdentity"
                 FileAccess = fileAccess (text root "fileAccess")
                 PolicyRoot = located["policy"]; JournalRoot = located["journal"]; SpoolRoot = located["spool"]
                 SubmissionRoot = located["submissions"]; IntegrationRoot = located["integration"]
                 EvidenceRoot = located["evidence"]; CheckoutRoot = located["checkouts"]
                 LockTimeout = TimeSpan.FromMilliseconds(float (if (property root "lockTimeoutMilliseconds").IsSome then number root "lockTimeoutMilliseconds" 0 30000 else 5000))
                 Git = { Executable = absolute git "executable"; ExecutableDigest = digest (text git "sha256")
                         TimeoutMilliseconds = if (property git "timeoutMilliseconds").IsSome then number git "timeoutMilliseconds" 1000 600000 else 60000 }
                 Committer = { Name = text committer "name"; Email = text committer "email" }
                 ValidatorPolicyRevision = text root "validatorPolicyRevision"
                 SystemValidators = systemValidators
                 AcceptanceValidators = acceptanceValidators |> List.map (fun item -> item.Id, item) |> Map.ofList
                 Workers = parsedWorkers
                 CompletionEvidence = completion
                 Containment = containmentSettings }
        with
        | :? InvalidDataException as error -> Error("host configuration refused: " + error.Message)
        | :? JsonException | :? DecoderFallbackException | :? InvalidOperationException | :? ArgumentException as error ->
            Error("host configuration refused: " + error.GetType().Name + ": " + error.Message)

    /// The file must itself be outside the repository and unlinked.
    let load (path: string) =
        try
            let full = Path.GetFullPath path
            if not (LocalHostJournalFiles.noLinks full) then Error "host configuration path is linked"
            else
                use input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read)
                if input.Length > int64 maxBytes then Error "host configuration exceeds 256 KiB"
                else
                    let bytes = Array.zeroCreate<byte> (int input.Length)
                    input.ReadExactly bytes
                    parse full bytes |> Result.bind (fun configuration ->
                        let directory = Path.GetDirectoryName full
                        LocalHostJournalFiles.rootOutside configuration.RepositoryRoot directory |> ignore
                        Ok configuration)
        with error -> Error("host configuration unavailable: " + error.Message)

    /// Fresh re-read; any byte change during a run refuses further use.
    let unchanged (configuration: LocalHostConfiguration) =
        load configuration.Path |> Result.bind (fun current ->
            if current.Digest <> configuration.Digest then Error "host configuration changed during the run" else Ok())
