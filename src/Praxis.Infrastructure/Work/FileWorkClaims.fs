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

    /// The first release providing `completion.verify-attested/1`.
    let AttestedSince = Version(0, 7, 0)

    let private command (executable: string) (arguments: string list) =
        if executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then "dotnet", executable :: arguments
        else executable, arguments

    /// Attested mode fails closed unless `dokimos version` reports a release
    /// that provides `completion.verify-attested/1` (0.7.0 or later): an older
    /// Dokimos would refuse the authority as malformed, or ignore attestations.
    let supportsAttestation (executable: string) (timeout: TimeSpan) =
        let program, arguments = command executable [ "version" ]

        match ClaimProcess.run program (Path.GetTempPath()) [] None timeout arguments with
        | Ok result when result.ExitCode = 0 ->
            try
                use document = JsonDocument.Parse result.Stdout
                let release = document.RootElement.GetProperty("ReleaseVersion").GetString()

                match Version.TryParse((release.Split('+', '-')[0])) with
                | true, version when version >= AttestedSince -> Ok()
                | _ -> Error $"Dokimos {release} does not provide completion.verify-attested/1 (needs {AttestedSince} or later)"
            with error ->
                Error $"the Dokimos version report is unreadable: {error.Message}"
        | Ok result -> Error $"'dokimos version' exited {result.ExitCode}"
        | Error _ -> Error "'dokimos version' did not run"

    let create (executable: Result<string, string>) (timeout: TimeSpan) : CompletionVerifier =
        fun packet authority ->
            match executable with
            | Error reason -> VerifierRun.Unavailable reason
            | Ok executable ->
                match (if authority.TrustedVerifierKeys.IsEmpty then Ok() else supportsAttestation executable timeout) with
                | Error reason -> VerifierRun.AttestationUnsupported reason
                | Ok() ->
                    let scratch = Path.Combine(Path.GetTempPath(), $"praxis-completion-{Guid.NewGuid():N}")

                    try
                        Directory.CreateDirectory scratch |> ignore
                        let packetPath = Path.Combine(scratch, "packet.json")
                        let authorityPath = Path.Combine(scratch, "authority.json")
                        write packetPath (WorkClaimJson.packetNode packet)
                        write authorityPath (WorkClaimJson.authorityNode authority)
                        let arguments = [ "completion"; "verify"; "--packet"; packetPath; "--authority"; authorityPath ]

                        let program, arguments = command executable arguments

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

/// I1 claim-protocol events. They go to the out-of-tree log
/// `<store>/events.jsonl` (I8): claiming, validating and verifying never
/// touch the tracked tree. Idempotent: an event whose `eventId` is already
/// present is not appended again. Only the canonical completion event is
/// tracked (`appendTracked`, `.ros/events/events.jsonl`, written by
/// `complete-verified` into its metadata-only commit).
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

    /// Whether the tracked log already holds `eventId`.
    let trackedContains (root: string) (eventId: string) =
        let path = Path.Combine(root, eventsRelativePath)
        File.Exists path && present (File.ReadAllText path) eventId

    /// Appends one event node to the tracked `.ros/events/events.jsonl` under
    /// the shared `work-protocol` lock and `work-state` journal.
    let appendTracked (root: string) (node: JsonNode) =
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

    let create (root: string) (actor: Actor option) (execution: string option) : ClaimEvent -> Result<unit, string> =
        fun event ->
            let repository = FileWorkConfigRepository.readRepositoryId root
            let protocolVersion = FileWorkConfigRepository.readProtocolVersion root
            let node = WorkClaimJson.eventNode repository protocolVersion execution actor event
            let eventId = node["eventId"].GetValue<string>()
            TransientStore.appendEvent root eventId (Praxis.Infrastructure.Json.CanonicalJson.serializeCompact node)

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

    /// Evidence is a `praxis.validation-result/1` document (PRAXIS-REL-005):
    /// Praxis hashes the exact bytes and parses the result; any other file is
    /// `Unrecognized` and refused by the gate. Existence alone proves nothing.
    let observeEvidence (root: string) (submission: EvidenceSubmission) =
        let path = if Path.IsPathRooted submission.Path then submission.Path else Path.Combine(root, submission.Path)

        try
            if File.Exists path then
                ValidationResultJson.observe (File.ReadAllBytes path)
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
          RecordedValidations = FileValidationEvents.read root
          ObserveHead = observeHead root
          Verifier = DokimosCompletionVerifier.create (DokimosCompletionVerifier.resolve environment) timeout }

    let readAuthorityInput (root: string) (path: string) =
        let file = if Path.IsPathRooted path then path else Path.Combine(root, path)

        try
            if File.Exists file then WorkClaimJson.parseAuthorityInput (File.ReadAllText file)
            else Error $"completion authority input '{path}' does not exist"
        with error ->
            Error error.Message

    /// A boolean `workProtocol.<name>` in ros.json. Absent: false. Present but
    /// not a boolean, or ros.json unreadable: an error, so callers fail closed.
    let readProtocolFlag (name: string) (root: string) : Result<bool, string> =
        let path = Path.Combine(root, "ros.json")

        if not (File.Exists path) then
            Ok false
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)

                match document.RootElement.TryGetProperty "workProtocol" with
                | true, protocol when protocol.ValueKind = JsonValueKind.Object ->
                    match protocol.TryGetProperty name with
                    | true, value when value.ValueKind = JsonValueKind.True -> Ok true
                    | true, value when value.ValueKind = JsonValueKind.False -> Ok false
                    | true, _ -> Error $"ros.json workProtocol.{name} must be a boolean"
                    | _ -> Ok false
                | _ -> Ok false
            with error ->
                Error $"ros.json could not be read: {error.Message}"

    /// `workProtocol.requireVerifiedCompletion` in ros.json (legacy items stay
    /// completable when absent).
    let readRequireVerifiedCompletion (root: string) = readProtocolFlag "requireVerifiedCompletion" root

    /// `workProtocol.requireAttestedEvidence` in ros.json (PRAXIS-REL-006 rollout
    /// gate): when set, a host authority that trusts no verifier key is refused,
    /// so repository policy, not the per-run authority file, decides whether
    /// unattested evidence can ever complete an item.
    let readRequireAttestedEvidence (root: string) = readProtocolFlag "requireAttestedEvidence" root

    /// `workProtocol.allowUnattestedEvidence` in ros.json (REL-GATE-003): the
    /// explicit, reviewed opt-out that lets `requireVerifiedCompletion` accept
    /// unattested evidence. It never overrides `requireAttestedEvidence`.
    let readAllowUnattestedEvidence (root: string) = readProtocolFlag "allowUnattestedEvidence" root

    /// Why repository policy requires attested evidence, if it does
    /// (REL-GATE-003): `requireAttestedEvidence`, or `requireVerifiedCompletion`
    /// without the `allowUnattestedEvidence` opt-out.
    let attestationRequirement (requireAttested: bool) (requireVerified: bool) (allowUnattested: bool) : string option =
        ProtectedCompletionPolicy.attestationRequirement requireAttested requireVerified allowUnattested

    /// The effective attestation requirement read from the worktree ros.json.
    /// Any malformed flag, the opt-out included, is an error, so callers fail
    /// closed. Completion decides against the protected policy instead
    /// (`effectivePolicy`, REL-GATE-004).
    let readAttestationRequirement (root: string) : Result<string option, string> =
        readRequireAttestedEvidence root
        |> Result.bind (fun requireAttested ->
            readRequireVerifiedCompletion root
            |> Result.bind (fun requireVerified ->
                readAllowUnattestedEvidence root
                |> Result.map (attestationRequirement requireAttested requireVerified)))

    /// The completion-policy members of one ros.json document (REL-GATE-004).
    /// A malformed flag or key list is an error, so callers fail closed; the
    /// upstream locator falls back to `workProtocol.upstreamSync`'s defaults
    /// exactly as `praxis sync` reads it.
    let parseCompletionPolicy (json: string) : Result<CompletionPolicy, string> =
        let keyId = Text.RegularExpressions.Regex("^sha256:[0-9a-f]{64}$")

        try
            use document = JsonDocument.Parse json

            match document.RootElement.TryGetProperty "workProtocol" with
            | true, protocol when protocol.ValueKind = JsonValueKind.Object ->
                let flag (name: string) =
                    match protocol.TryGetProperty name with
                    | true, value when value.ValueKind = JsonValueKind.True -> Ok true
                    | true, value when value.ValueKind = JsonValueKind.False -> Ok false
                    | true, _ -> Error $"ros.json workProtocol.{name} must be a boolean"
                    | _ -> Ok false

                let keys =
                    match protocol.TryGetProperty "trustedVerifierKeyIds" with
                    | false, _ -> Ok None
                    | true, value when value.ValueKind = JsonValueKind.Array ->
                        let entries = value.EnumerateArray() |> Seq.toList

                        if entries |> List.forall (fun entry -> entry.ValueKind = JsonValueKind.String && keyId.IsMatch(entry.GetString())) then
                            Ok(Some(entries |> List.map _.GetString() |> List.distinct))
                        else
                            Error "ros.json workProtocol.trustedVerifierKeyIds must be an array of 'sha256:<64 lowercase hex>' verifier key IDs"
                    | true, _ -> Error "ros.json workProtocol.trustedVerifierKeyIds must be an array of 'sha256:<64 lowercase hex>' verifier key IDs"

                let locator =
                    let text (sync: JsonElement) (name: string) fallback =
                        match sync.TryGetProperty name with
                        | true, field when field.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(field.GetString())) -> field.GetString()
                        | _ -> fallback

                    match protocol.TryGetProperty "upstreamSync" with
                    | true, sync when sync.ValueKind = JsonValueKind.Object ->
                        text sync "remote" (fst ProtectedCompletionPolicy.empty.Upstream), text sync "branch" (snd ProtectedCompletionPolicy.empty.Upstream)
                    | _ -> ProtectedCompletionPolicy.empty.Upstream

                flag "requireVerifiedCompletion"
                |> Result.bind (fun verified ->
                    flag "requireAttestedEvidence"
                    |> Result.bind (fun attested ->
                        flag "allowUnattestedEvidence"
                        |> Result.bind (fun allow ->
                            keys
                            |> Result.map (fun pinned ->
                                { RequireVerifiedCompletion = verified
                                  RequireAttestedEvidence = attested
                                  AllowUnattestedEvidence = allow
                                  TrustedVerifierKeyIds = pinned
                                  Upstream = locator }))))
            | _ -> Ok ProtectedCompletionPolicy.empty
        with error ->
            Error $"ros.json could not be read: {error.Message}"

    /// The completion policy in the worktree's ros.json (absent: empty).
    let readWorktreePolicy (root: string) : Result<CompletionPolicy, string> =
        let path = Path.Combine(root, "ros.json")

        try
            if File.Exists path then parseCompletionPolicy (File.ReadAllText path) else Ok ProtectedCompletionPolicy.empty
        with error ->
            Error $"ros.json could not be read: {error.Message}"

    let private gitText (root: string) (arguments: string list) =
        match ClaimProcess.run "git" root [] None gitTimeout arguments with
        | Ok result when result.ExitCode = 0 -> Ok result.Stdout
        | Ok result -> Error(result.Stderr.Trim())
        | Error _ -> Error "git is unavailable"

    /// ros.json committed at `commit`: absent is the empty policy.
    let private committedPolicy (root: string) (commit: string) : Result<CompletionPolicy, string> =
        gitText root [ "ls-tree"; "--name-only"; commit; "--"; "ros.json" ]
        |> Result.bind (fun listed ->
            if String.IsNullOrWhiteSpace listed then Ok ProtectedCompletionPolicy.empty
            else gitText root [ "show"; $"{commit}:ros.json" ] |> Result.bind parseCompletionPolicy)

    /// The merge base of HEAD and `refs/remotes/<remote>/<branch>`: `None`
    /// when that ref does not exist, an error when they share no history.
    let private mergeBase (root: string) (remote: string, branch: string) : Result<string, string> option =
        match gitText root [ "rev-parse"; "--verify"; "--quiet"; $"refs/remotes/{remote}/{branch}^{{commit}}" ] with
        | Error _ -> None
        | Ok upstream ->
            gitText root [ "merge-base"; "HEAD"; upstream.Trim() ]
            |> Result.map _.Trim()
            |> Result.mapError (fun reason -> $"HEAD shares no history with {remote}/{branch}: {reason}")
            |> Some

    /// The protected source the completion policy is compared with
    /// (REL-GATE-004). The base is located with a fixed bootstrap locator
    /// (`refs/remotes/origin/HEAD`'s target, else origin/main), never with
    /// the worktree's or candidate's `upstreamSync`. When the protected
    /// policy found there configures another upstream, the base is
    /// re-located there exactly once; if that ref is absent the bootstrap
    /// base stands. With no bootstrap ref, every readable committed version
    /// of ros.json in HEAD's history binds.
    let protectedPolicySource (root: string) : ProtectedPolicySource =
        match gitText root [ "rev-parse"; "--verify"; "--quiet"; "HEAD^{commit}" ] with
        | Error reason -> ProtectedPolicySource.Unavailable $"HEAD does not resolve: {reason}"
        | Ok _ ->
            let bootstrap =
                gitText root [ "symbolic-ref"; "--quiet"; "refs/remotes/origin/HEAD" ]
                |> Result.toOption
                |> Option.bind ProtectedCompletionPolicy.parseOriginHead
                |> ProtectedCompletionPolicy.bootstrapLocator

            let based commit = ProtectedPolicySource.Base(commit, committedPolicy root commit)

            match mergeBase root bootstrap with
            | Some(Error reason) -> ProtectedPolicySource.Unavailable reason
            | Some(Ok commit) ->
                match based commit with
                | ProtectedPolicySource.Base(_, Ok protectedPolicy) as source ->
                    match ProtectedCompletionPolicy.relocation bootstrap protectedPolicy |> Option.bind (mergeBase root) with
                    | None -> source
                    | Some(Error reason) -> ProtectedPolicySource.Unavailable reason
                    | Some(Ok relocated) -> based relocated
                | source -> source
            | None ->
                match gitText root [ "log"; "--format=%H"; "HEAD"; "--"; "ros.json" ] with
                | Error reason -> ProtectedPolicySource.Unavailable $"the history of ros.json could not be read: {reason}"
                | Ok listed ->
                    listed.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                    |> Array.toList
                    |> List.choose (fun commit -> committedPolicy root commit |> Result.toOption |> Option.map (fun policy -> commit, policy))
                    |> ProtectedPolicySource.History

    /// The effective completion policy (REL-GATE-004): the worktree's, once it
    /// is shown to be no weaker than the protected source.
    let effectivePolicy (root: string) : Result<CompletionPolicy, CompletionFinding> =
        ProtectedCompletionPolicy.decide (readWorktreePolicy root) (protectedPolicySource root)

    /// `path` with every symbolic link along it resolved.
    let rec private canonical (path: string) : string =
        let full = Path.GetFullPath path

        match Path.GetDirectoryName full with
        | null -> full
        | parent ->
            let candidate = Path.Combine(canonical parent, Path.GetFileName full)
            let info : FileSystemInfo = if Directory.Exists candidate then DirectoryInfo candidate else FileInfo candidate

            match info.LinkTarget with
            | null -> candidate
            | _ ->
                match info.ResolveLinkTarget true with
                | null -> candidate
                | target -> canonical target.FullName

    /// The repository location (worktree or Git directory) that the completion
    /// authority at `path` resolves inside, following symbolic links, if any
    /// (REL-GATE-004): a file there is writable by the completing process.
    let authorityInside (root: string) (path: string) : string option =
        let file = canonical (if Path.IsPathRooted path then path else Path.Combine(root, path))

        let locations =
            [ Some root
              gitText root [ "rev-parse"; "--show-toplevel" ] |> Result.toOption
              gitText root [ "rev-parse"; "--path-format=absolute"; "--git-common-dir" ] |> Result.toOption
              gitText root [ "rev-parse"; "--path-format=absolute"; "--git-dir" ] |> Result.toOption ]
            |> List.choose id
            |> List.map (fun location -> canonical (location.Trim()))
            |> List.distinct

        let separator = string Path.DirectorySeparatorChar

        locations
        |> List.tryFind (fun location -> file = location || file.StartsWith(location.TrimEnd(Path.DirectorySeparatorChar) + separator, StringComparison.Ordinal))

    /// The Ordo contract bytes at `path` (interface I3).
    let private readOrdoContract (root: string) (path: string) =
        let file = if Path.IsPathRooted path then path else Path.Combine(root, path)

        try
            if File.Exists file then Ok(File.ReadAllText file)
            else Error [ { Code = "PRX.COMPLETION.AUTHORITY_UNAVAILABLE"; Detail = $"Ordo contract '{path}' does not exist" } ]
        with error ->
            Error [ { Code = "PRX.COMPLETION.AUTHORITY_UNAVAILABLE"; Detail = $"Ordo contract '{path}' could not be read: {error.Message}" } ]

    /// Whether plain `work complete` may close `ids`: reads the policy and each
    /// item's claim from the selected store (fails closed on any read error).
    let plainCompletion (root: string) (choice: ClaimStoreChoice) (ids: string list) : Result<PlainCompletion, string> =
        let claims = store root choice
        let worktree = readWorktreePolicy root

        // REL-GATE-004: requireVerifiedCompletion is decided against the
        // protected policy too, so a worktree that drops it cannot fall back
        // to plain completion. Outside Git there is nothing to compare with.
        let source =
            worktree
            |> Result.map (fun _ ->
                match protectedPolicySource root with
                | ProtectedPolicySource.Unavailable _ -> ProtectedPolicySource.History []
                | source -> source)

        match worktree, source with
        | Error message, _
        | _, Error message -> Error message
        | Ok _, Ok source ->
            match ProtectedCompletionPolicy.decide worktree source with
            | Error finding -> Ok(PlainCompletion.Refused finding)
            | Ok policy ->
                ids
                |> List.fold (fun acc id -> acc |> Result.bind (fun read -> claims.Read id |> Result.map (fun claim -> read @ [ id, claim ]))) (Ok [])
                |> Result.map (PlainCompletion.decideAll policy.RequireVerifiedCompletion)

    /// The authority input bound to the ready Ordo contract at `contract`
    /// (PRX-REL-008, REL-GATE-002). Without a contract there is no ready
    /// scope, so verified completion is refused rather than trusting a
    /// declared identity.
    let bindOrdoContract (root: string) (contract: string option) (expectedDigest: string option) (input: CompletionAuthorityInput) =
        match contract with
        | None -> Error(OrdoBoundAuthority.missingContract ())
        | Some path -> readOrdoContract root path |> Result.bind (fun json -> OrdoBoundAuthority.bind expectedDigest json input)
