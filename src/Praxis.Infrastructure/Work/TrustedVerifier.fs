namespace Praxis.Infrastructure.Work

open System
open System.Diagnostics
open System.IO
open System.Net.Sockets
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Work

/// The verifier's ECDSA P-256 signing key. It lives only in this process's
/// memory: generated at startup, or imported from a PEM file (CI, where the
/// key comes from a secret). It is never written anywhere by Praxis.
type VerifierKey =
    private
        { Key: ECDsa
          SubjectPublicKeyInfo: byte array }

    member this.KeyId = VerificationReceipt.keyId this.SubjectPublicKeyInfo
    member this.PublicKey = Convert.ToBase64String this.SubjectPublicKeyInfo

    /// The I6 `TrustedVerifierKeys` entry a host pins for this key.
    member this.Trusted: TrustedVerifierKey =
        { KeyId = this.KeyId; Algorithm = VerificationReceipt.Algorithm; PublicKey = this.PublicKey }

[<RequireQualifiedAccess>]
module VerifierKey =
    let private wrap (key: ECDsa) =
        { Key = key; SubjectPublicKeyInfo = key.ExportSubjectPublicKeyInfo() }

    let generate () = wrap (ECDsa.Create(ECCurve.NamedCurves.nistP256))

    /// Imports a PEM (PKCS#8 or SEC1 EC) P-256 private key.
    let fromPem (pem: string) =
        try
            let key = ECDsa.Create()
            key.ImportFromPem(pem.AsSpan())

            if key.KeySize <> 256 then
                key.Dispose()
                Error "the verifier key must be an ECDSA P-256 key"
            else
                Ok(wrap key)
        with error ->
            Error $"the verifier key cannot be read: {error.Message}"

    let fromPemFile (path: string) =
        try
            if File.Exists path then fromPem (File.ReadAllText path) else Error $"key file '{path}' does not exist"
        with error ->
            Error error.Message

    /// PKCS#8 PEM of the private key (tests and key provisioning only).
    let exportPem (key: VerifierKey) = key.Key.ExportPkcs8PrivateKeyPem()

    /// One DSSE envelope over the exact payload bytes.
    let sign (key: VerifierKey) (payload: byte array) : ReceiptEnvelope =
        let signature =
            key.Key.SignData(
                VerificationReceipt.pae VerificationReceipt.PayloadType payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation
            )

        { PayloadType = VerificationReceipt.PayloadType
          Payload = payload
          Signatures = [ { KeyId = key.KeyId; Signature = signature } ] }

/// I5 `echelon.validation-policy/1` intake.
[<RequireQualifiedAccess>]
module ValidationPolicyJson =
    let SchemaVersion = "echelon.validation-policy/1"

    let parse (bytes: byte array) : Result<ValidationPolicy, string> =
        try
            use document = JsonDocument.Parse(ReadOnlyMemory bytes)
            let root = document.RootElement

            let text (element: JsonElement) (name: string) =
                match element.TryGetProperty name with
                | true, value when value.ValueKind = JsonValueKind.String && value.GetString() <> "" -> value.GetString()
                | _ -> failwith $"'{name}' must be a non-empty string"

            let array (element: JsonElement) (name: string) =
                match element.TryGetProperty name with
                | true, value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
                | true, _ -> failwith $"'{name}' must be an array"
                | _ -> []

            if text root "schemaVersion" <> SchemaVersion then failwith $"schemaVersion must be {SchemaVersion}"

            let validations =
                array root "validations"
                |> List.map (fun item ->
                    let command = array item "command" |> List.map (fun part -> if part.ValueKind = JsonValueKind.String then part.GetString() else failwith "'command' must hold strings")
                    if command.IsEmpty then failwith "a validation 'command' must not be empty"
                    let name = text item "name"
                    let evidenceId = text item "evidenceId"
                    if not (ValidationEvidence.isSafeName name && ValidationEvidence.isSafeName evidenceId) then failwith $"validation '{name}' has an unsafe name or evidenceId"

                    let timeout =
                        match item.TryGetProperty "timeoutSeconds" with
                        | true, value when value.ValueKind = JsonValueKind.Number && value.GetDouble() > 0.0 -> TimeSpan.FromSeconds(value.GetDouble())
                        | true, _ -> failwith "'timeoutSeconds' must be a positive number"
                        | _ -> TimeSpan.FromMinutes 30.0

                    { Name = name; EvidenceId = evidenceId; Command = command; Timeout = timeout })

            if validations.IsEmpty then failwith "the policy names no validations"

            Ok
                { PolicyId = text root "policyId"
                  Digest = CompletionGate.sha256 bytes
                  Validations = validations
                  ProtectedPaths = array root "protectedPaths" |> List.map (fun item -> { Path = text item "path"; Sha256 = (text item "sha256").ToLowerInvariant() }) }
        with error ->
            Error $"the validation policy is malformed: {error.Message}"

    let readFile (path: string) =
        try
            if File.Exists path then
                let bytes = File.ReadAllBytes path
                parse bytes
            else
                Error $"policy '{path}' does not exist"
        with error ->
            Error error.Message

/// Newline-delimited JSON between `praxis work verify` and the verifier.
[<RequireQualifiedAccess>]
module VerifierWire =
    let private compact (node: JsonNode) = node.ToJsonString(JsonSerializerOptions(WriteIndented = false))

    let requestLine (request: VerifyRequest) =
        let node = JsonObject()
        node["repoPath"] <- JsonValue.Create request.RepoPath
        node["workItemId"] <- JsonValue.Create request.WorkItemId
        node["claimant"] <- JsonValue.Create request.Claimant
        node["fencingGeneration"] <- JsonValue.Create request.FencingGeneration
        node["commit"] <- JsonValue.Create request.Commit
        compact node

    let parseRequest (line: string) : Result<VerifyRequest, string> =
        try
            use document = JsonDocument.Parse line
            let root = document.RootElement
            let text (name: string) = root.GetProperty(name).GetString() |> Option.ofObj |> Option.defaultWith (fun () -> failwith $"'{name}' is required")

            Ok
                { RepoPath = text "repoPath"
                  WorkItemId = text "workItemId"
                  Claimant = text "claimant"
                  FencingGeneration = root.GetProperty("fencingGeneration").GetInt64()
                  Commit = text "commit" }
        with error ->
            Error $"malformed verify request: {error.Message}"

    let responseLine (response: VerifierResponse) =
        let node = JsonObject()

        match response with
        | VerifierResponse.Refused(code, detail) ->
            node["outcome"] <- JsonValue.Create "refused"
            node["code"] <- JsonValue.Create code
            node["detail"] <- JsonValue.Create detail
        | VerifierResponse.Signed(receipts, diagnostics) ->
            node["outcome"] <- JsonValue.Create "signed"
            let array = JsonArray()

            for receipt in receipts do
                let entry = JsonObject()
                entry["envelope"] <- VerificationReceipt.envelopeNode receipt.Envelope
                entry["output"] <- JsonValue.Create receipt.Output
                array.Add(entry :> JsonNode)

            node["receipts"] <- array
            let notes = JsonArray()
            diagnostics |> List.iter (fun note -> notes.Add(JsonValue.Create note))
            node["diagnostics"] <- notes

        compact node

    /// The response, with every payload parsed (not verified: the
    /// completion gate verifies signatures against the host's trusted keys).
    let parseResponse (line: string) : Result<VerifierResponse, string> =
        try
            use document = JsonDocument.Parse line
            let root = document.RootElement

            match root.GetProperty("outcome").GetString() with
            | "refused" -> Ok(VerifierResponse.Refused(root.GetProperty("code").GetString(), root.GetProperty("detail").GetString()))
            | "signed" ->
                let receipts =
                    root.GetProperty("receipts").EnumerateArray()
                    |> Seq.map (fun entry ->
                        VerificationReceipt.parseEnvelope (entry.GetProperty("envelope").GetRawText())
                        |> Result.bind (fun envelope ->
                            VerificationReceipt.parsePayload envelope.Payload
                            |> Result.map (fun payload -> { Envelope = envelope; Payload = payload; Output = entry.GetProperty("output").GetString() })))
                    |> Seq.toList

                match receipts |> List.tryPick (function Error message -> Some message | Ok _ -> None) with
                | Some message -> Error $"the verifier returned an unreadable receipt: {message}"
                | None ->
                    let diagnostics = root.GetProperty("diagnostics").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
                    Ok(VerifierResponse.Signed(receipts |> List.choose (function Ok receipt -> Some receipt | Error _ -> None), diagnostics))
            | other -> Error $"unknown verifier outcome '{other}'"
        with error ->
            Error $"malformed verifier response: {error.Message}"

/// F1 verifier engine: re-read the claim, clone the commit into a private
/// temporary directory, enforce protected paths, run each policy validation
/// (argv, no shell, timeout) and sign one receipt per validation. `serve`
/// and `run` share it.
[<RequireQualifiedAccess>]
module TrustedVerifierEngine =
    let VerifierId = "praxis-verifier"

    let version =
        typeof<VerifierKey>.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        |> Option.ofObj
        |> Option.map _.InformationalVersion
        |> Option.defaultValue "unknown"

    let private gitTimeout = TimeSpan.FromMinutes 10.0

    let private git (directory: string) (arguments: string list) =
        match ClaimProcess.run "git" directory [ "GIT_TERMINAL_PROMPT", "0" ] None gitTimeout arguments with
        | Ok result when result.ExitCode = 0 -> Ok(result.Stdout.Trim())
        | Ok result -> Error $"""git {arguments |> List.truncate 2 |> String.concat " "} failed: {result.Stderr.Trim()}"""
        | Error(ClaimProcessFailure.NotStarted reason) -> Error $"git is unavailable: {reason}"
        | Error(ClaimProcessFailure.TimedOut after) -> Error $"git timed out after {after.TotalSeconds:F0}s"

    /// `git clone --no-hardlinks` of `repository` into `target`, then a
    /// detached checkout of `commit` (fetched explicitly if no ref reaches it).
    let private checkout (repository: string) (target: string) (commit: string) =
        git (Path.GetTempPath()) [ "clone"; "--no-hardlinks"; "--quiet"; "--no-checkout"; repository; target ]
        |> Result.bind (fun _ ->
            match git target [ "cat-file"; "-e"; $"{commit}^{{commit}}" ] with
            | Ok _ -> Ok()
            | Error _ -> git target [ "fetch"; "--quiet"; "--no-tags"; "origin"; commit ] |> Result.map ignore)
        |> Result.bind (fun () -> git target [ "-c"; "advice.detachedHead=false"; "checkout"; "--quiet"; "--detach"; commit ])
        |> Result.bind (fun _ -> git target [ "rev-parse"; "--verify"; "HEAD^{commit}" ])
        |> Result.bind (fun head -> if head = commit then git target [ "rev-parse"; $"{commit}^{{tree}}" ] else Error $"the clone checked out {head}, not {commit}")

    /// SHA-256 (hex) of a regular file tracked at `commit`, else `None`.
    let private observeProtected (clone: string) (commit: string) (path: string) =
        let normalized = path.Replace('\\', '/')

        if Path.IsPathRooted normalized || normalized.Split('/') |> Array.exists (fun segment -> segment = ".." || segment = "") then
            None
        else
            match git clone [ "ls-tree"; commit; "--"; normalized ] with
            | Ok line when line.StartsWith "100644 blob " || line.StartsWith "100755 blob " ->
                let file = Path.Combine(clone, normalized)
                if File.Exists file then Some(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes file)).ToLowerInvariant()) else None
            | _ -> None

    let private runValidation (clone: string) (validation: PolicyValidation) =
        match validation.Command with
        | [] -> CheckStatus.Unavailable, None, "no command\n"
        | executable :: arguments ->
            match ClaimProcess.run executable clone [] None validation.Timeout arguments with
            | Ok result when result.ExitCode = 0 -> CheckStatus.Passed, Some 0, result.Stdout + result.Stderr
            | Ok result -> CheckStatus.Failed, Some result.ExitCode, result.Stdout + result.Stderr
            | Error(ClaimProcessFailure.TimedOut after) -> CheckStatus.Unavailable, None, $"timed out after {after.TotalSeconds:F0}s\n"
            | Error(ClaimProcessFailure.NotStarted reason) -> CheckStatus.Unavailable, None, $"did not start: {reason}\n"

    let private receipt (key: VerifierKey) (policy: ValidationPolicy) (contractDigest: string) (request: VerifyRequest) treeId (validation: PolicyValidation) startedAt observedAt status exitCode (output: string) =
        let payload =
            { ReceiptId = Guid.NewGuid().ToString()
              WorkItemId = request.WorkItemId
              Claimant = request.Claimant
              FencingGeneration = request.FencingGeneration
              Commit = request.Commit
              TreeId = treeId
              OrdoContractDigest = contractDigest
              ValidationPolicyId = policy.PolicyId
              ValidationPolicyDigest = policy.Digest
              Validation = validation.Name
              EvidenceId = validation.EvidenceId
              Status = status
              ExitCode = exitCode
              Command = validation.Command
              OutputSha256 = CompletionGate.sha256 (UTF8Encoding(false).GetBytes output)
              StartedAt = startedAt
              ObservedAt = observedAt
              DurationMs = int64 (observedAt - startedAt).TotalMilliseconds
              Verifier = { Id = VerifierId; Version = version; KeyId = key.KeyId } }

        { Envelope = VerifierKey.sign key (VerificationReceipt.render payload); Payload = payload; Output = output }

    /// Runs the engine for one request; never throws.
    let run (key: VerifierKey) (policy: ValidationPolicy) (contractDigest: string) (request: VerifyRequest) : VerifierResponse =
        let now () = DateTimeOffset.UtcNow
        let repository = Path.GetFullPath request.RepoPath

        match FileWorkClaimStore.create repository |> fun store -> store.Read request.WorkItemId with
        | Error message -> VerifierResponse.Refused(VerifierEngine.StaleClaimCode, $"the claim cannot be read: {message}")
        | Ok current ->
            match VerifierEngine.admit (now ()) request current with
            | Error(code, detail) -> VerifierResponse.Refused(code, detail)
            | Ok _ ->
                let clone = Path.Combine(Path.GetTempPath(), $"praxis-verifier-{Guid.NewGuid():N}")

                try
                    try
                        match checkout repository clone request.Commit with
                        | Error message -> VerifierResponse.Refused(VerifierEngine.CommitUnavailableCode, message)
                        | Ok treeId ->
                            match VerifierEngine.protectedPathFindings policy (observeProtected clone request.Commit) with
                            | (_ :: _) as violations ->
                                let output = (violations |> String.concat "\n") + "\n"
                                let at = now ()

                                let receipts =
                                    policy.Validations
                                    |> List.map (fun validation -> receipt key policy contractDigest request treeId validation at at CheckStatus.Unavailable None output)

                                VerifierResponse.Signed(receipts, violations |> List.map (fun item -> $"{VerifierEngine.ProtectedPathCode}: {item}"))
                            | [] ->
                                let receipts =
                                    policy.Validations
                                    |> List.map (fun validation ->
                                        let startedAt = now ()
                                        let status, exitCode, output = runValidation clone validation
                                        receipt key policy contractDigest request treeId validation startedAt (now ()) status exitCode output)

                                VerifierResponse.Signed(receipts, [])
                    with error ->
                        VerifierResponse.Refused(VerifierEngine.UnavailableCode, error.Message)
                finally
                    try
                        if Directory.Exists clone then Directory.Delete(clone, true)
                    with _ ->
                        ()

/// The verifier's Unix domain socket: one newline-delimited request and one
/// response per connection, handled in arrival order.
[<RequireQualifiedAccess>]
module VerifierSocket =
    let EnvironmentVariable = "PRAXIS_VERIFIER_SOCKET"

    /// The startup line `{ keyId, publicKey, policyDigest }` a host pins.
    let startupLine (key: VerifierKey) (policy: ValidationPolicy) =
        let node = JsonObject()
        node["keyId"] <- JsonValue.Create key.KeyId
        node["publicKey"] <- JsonValue.Create key.PublicKey
        node["policyDigest"] <- JsonValue.Create policy.Digest
        node.ToJsonString()

    let private handle (key: VerifierKey) (policy: ValidationPolicy) (contractDigest: string) (connection: Socket) =
        use stream = new NetworkStream(connection, true)
        use reader = new StreamReader(stream, UTF8Encoding(false))
        use writer = new StreamWriter(stream, UTF8Encoding(false))
        let line = reader.ReadLine()

        let response =
            match VerifierWire.parseRequest (if isNull line then "" else line) with
            | Error message -> VerifierResponse.Refused(VerifierEngine.InvalidRequestCode, message)
            | Ok request -> TrustedVerifierEngine.run key policy contractDigest request

        writer.Write(VerifierWire.responseLine response + "\n")
        writer.Flush()

    /// Serves until the process is stopped. `ready` receives the startup line
    /// once the socket accepts connections.
    let serve (socketPath: string) (key: VerifierKey) (policy: ValidationPolicy) (contractDigest: string) (ready: string -> unit) =
        try
            if File.Exists socketPath then File.Delete socketPath
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath socketPath)) |> ignore
            use listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            listener.Bind(UnixDomainSocketEndPoint socketPath)
            listener.Listen 16
            AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> try File.Delete socketPath with _ -> ())
            ready (startupLine key policy)

            while true do
                let connection = listener.Accept()

                try
                    handle key policy contractDigest connection
                with error ->
                    eprintfn "verifier: request failed: %s" error.Message

            Ok()
        with error ->
            Error $"the verifier cannot serve on '{socketPath}': {error.Message}"

    /// One request over the socket at `socketPath`.
    let call (socketPath: string) (timeout: TimeSpan) (request: VerifyRequest) : Result<VerifierResponse, string> =
        try
            use socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            socket.ReceiveTimeout <- int timeout.TotalMilliseconds
            socket.Connect(UnixDomainSocketEndPoint socketPath)
            use stream = new NetworkStream(socket, true)
            use writer = new StreamWriter(stream, UTF8Encoding(false))
            use reader = new StreamReader(stream, UTF8Encoding(false))
            writer.Write(VerifierWire.requestLine request + "\n")
            writer.Flush()

            match reader.ReadLine() with
            | null -> Error "the verifier closed the connection without a response"
            | line -> VerifierWire.parseResponse line
        with error ->
            Error $"the verifier at '{socketPath}' is unavailable: {error.Message}"

/// `praxis work verify` composition and the receipt half of the I8 store.
[<RequireQualifiedAccess>]
module FileWorkVerification =
    /// `git status --porcelain` entries (untracked files included).
    let worktreeStatus (root: string) () =
        match ClaimProcess.run "git" root [] None FileWorkClaims.gitTimeout [ "status"; "--porcelain=v1"; "-z" ] with
        | Ok result when result.ExitCode = 0 -> Ok(result.Stdout.Split('\000', StringSplitOptions.RemoveEmptyEntries) |> Array.toList)
        | Ok result -> Error $"cannot inspect the worktree: {result.Stderr.Trim()}"
        | Error _ -> Error "cannot inspect the worktree: git is unavailable"

    let directory (root: string) (workItemId: string) = Path.Combine(TransientStore.receipts root, workItemId)

    /// `<generation>-<commit[0..11]>-<receiptId>.json` (the exact envelope)
    /// beside `.output.txt`; never overwritten.
    let store (root: string) (receipt: SignedReceipt) : Result<StoredReceipt, string> =
        try
            let payload = receipt.Payload
            let name = $"{payload.FencingGeneration}-{payload.Commit.Substring(0, 12)}-{payload.ReceiptId}"
            let folder = directory root payload.WorkItemId
            let path = Path.Combine(folder, name + ".json")
            TransientStore.publish (Path.Combine(folder, name + ".output.txt")) (UTF8Encoding(false).GetBytes receipt.Output)
            TransientStore.publish path (UTF8Encoding(false).GetBytes(VerificationReceipt.renderEnvelope receipt.Envelope))
            Ok { Path = path; Digest = VerificationReceipt.identity receipt.Envelope.Payload; Payload = payload }
        with error ->
            Error error.Message

    let ports (root: string) (socket: string option) (timeout: TimeSpan) : WorkVerifyPorts =
        { Claims = FileWorkClaimStore.create root
          Now = fun () -> DateTimeOffset.UtcNow
          RepoPath = Path.GetFullPath root
          ObserveHead = FileWorkClaims.observeHead root
          WorktreeStatus = worktreeStatus root
          Request =
            fun request ->
                match socket |> Option.filter (String.IsNullOrWhiteSpace >> not) with
                | None -> Error $"no trusted verifier: set {VerifierSocket.EnvironmentVariable} to the socket of 'praxis verifier serve'"
                | Some path -> VerifierSocket.call path timeout request
          Store = store root }

    let environmentSocket () = Environment.GetEnvironmentVariable VerifierSocket.EnvironmentVariable |> Option.ofObj

    /// Every stored receipt for `workItemId`, as derivation candidates.
    /// Unreadable files are skipped: the gate only ever sees verified ones.
    let candidates (root: string) (workItemId: string) : Result<EvidenceCandidate list, string> =
        try
            let folder = directory root workItemId

            if not (Directory.Exists folder) then
                Ok []
            else
                Directory.GetFiles(folder, "*.json")
                |> Array.choose (fun path ->
                    VerificationReceipt.parseEnvelope (File.ReadAllText path)
                    |> Result.bind (fun envelope -> VerificationReceipt.parsePayload envelope.Payload |> Result.map (fun payload -> envelope, payload))
                    |> function
                        | Ok(envelope, payload) when payload.WorkItemId = workItemId ->
                            Some
                                { Id = payload.EvidenceId
                                  Path = path
                                  Digest = VerificationReceipt.identity envelope.Payload
                                  Validation = payload.Validation
                                  Status = payload.Status
                                  Commit = payload.Commit
                                  Generation = payload.FencingGeneration
                                  ObservedAt = payload.ObservedAt
                                  Tiebreak = payload.ReceiptId }
                        | _ -> None)
                |> Array.toList
                |> Ok
        with error ->
            Error error.Message

/// `praxis verifier serve|run` composition.
[<RequireQualifiedAccess>]
module FileTrustedVerifier =
    let loadKey (keyFile: string option) =
        match keyFile with
        | Some path -> VerifierKey.fromPemFile path
        | None -> Ok(VerifierKey.generate ())

    let private validDigest (digest: string) =
        if CompletionGate.isSha256Digest digest then Ok digest else Error $"--contract-digest '{digest}' must be sha256:<64 hex>"

    let serve (socketPath: string) (policyPath: string) (contractDigest: string) (keyFile: string option) (ready: string -> unit) =
        validDigest contractDigest
        |> Result.bind (fun digest ->
            ValidationPolicyJson.readFile policyPath
            |> Result.bind (fun policy -> loadKey keyFile |> Result.bind (fun key -> VerifierSocket.serve socketPath key policy digest ready)))

    /// One-shot: the same engine, writing the response document to `out`.
    let runOnce (request: VerifyRequest) (policyPath: string) (contractDigest: string) (keyFile: string) (out: string) =
        validDigest contractDigest
        |> Result.bind (fun digest ->
            ValidationPolicyJson.readFile policyPath
            |> Result.bind (fun policy ->
                VerifierKey.fromPemFile keyFile
                |> Result.bind (fun key ->
                    let response = TrustedVerifierEngine.run key policy digest request

                    try
                        File.WriteAllText(out, VerifierWire.responseLine response + "\n", UTF8Encoding(false))
                        Ok(key, policy, response)
                    with error ->
                        Error $"cannot write '{out}': {error.Message}")))
