namespace Praxis.Infrastructure.Governance

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Governance
open Praxis.Domain.Work

/// The governance documents' wire formats. Parsing is strict about the
/// members that grant authority: a missing or mistyped member is an error,
/// never a default that trusts more.
[<RequireQualifiedAccess>]
module GovernanceJson =
    let private parseRoot (text: string) =
        match JsonNode.Parse(text) with
        | :? JsonObject as root -> root
        | _ -> failwith "the document is not a JSON object"

    let private str (node: JsonNode) (name: string) =
        match node[name] with
        | null -> failwith $"'{name}' is required"
        | value ->
            match value.GetValueKind() with
            | JsonValueKind.String when value.GetValue<string>() <> "" -> value.GetValue<string>()
            | _ -> failwith $"'{name}' must be a non-empty string"

    let private optStr (node: JsonNode) (name: string) =
        match node[name] with
        | null -> None
        | value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> failwith $"'{name}' must be a string or null"

    let private strings (node: JsonNode) (name: string) =
        match node[name] with
        | null -> []
        | :? JsonArray as items -> items |> Seq.map (fun item -> if isNull item then failwith $"'{name}' holds null" else item.GetValue<string>()) |> Seq.toList
        | _ -> failwith $"'{name}' must be an array of strings"

    let private flag (node: JsonNode) (name: string) (fallback: bool) =
        match node[name] with
        | null -> fallback
        | value when value.GetValueKind() = JsonValueKind.True -> true
        | value when value.GetValueKind() = JsonValueKind.False -> false
        | _ -> failwith $"'{name}' must be a boolean"

    let private int' (node: JsonNode) (name: string) (fallback: int) =
        match node[name] with
        | null -> fallback
        | value when value.GetValueKind() = JsonValueKind.Number -> value.GetValue<int>()
        | _ -> failwith $"'{name}' must be a number"

    let private instant (value: string) =
        match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
        | true, parsed -> parsed
        | _ -> failwith $"'{value}' is not a timestamp"

    let private time (node: JsonNode) (name: string) = instant (str node name)
    let private optTime (node: JsonNode) (name: string) = optStr node name |> Option.map instant

    let private objects (node: JsonNode) (name: string) =
        match node[name] with
        | null -> []
        | :? JsonArray as items -> items |> Seq.map (fun item -> if isNull item then failwith $"'{name}' holds null" else item) |> Seq.toList
        | _ -> failwith $"'{name}' must be an array"

    let private guard (f: unit -> 'a) =
        try
            Ok(f ())
        with error ->
            Error error.Message

    let private schema (root: JsonNode) (expected: string) =
        let actual = str root "schema"
        if actual <> expected then failwith $"schema must be {expected}, not {actual}"

    // ---- protected configuration -----------------------------------------

    let parseTrustRoot (text: string) : Result<VerifierTrustRoot, string> =
        guard (fun () ->
            let root = parseRoot text
            schema root VerifierTrust.Schema

            let verifier (node: JsonNode) =
                let identity =
                    match str node "kind" with
                    | "github-actions-oidc" ->
                        let events = strings node "events"
                        if events.IsEmpty then failwith "a workload verifier must name its events"

                        VerifierIdentity.Workload
                            { Issuer = optStr node "issuer" |> Option.defaultValue VerifierTrust.GitHubIssuer
                              Repository = str node "repository"
                              WorkflowRef = str node "workflowRef"
                              Events = events }
                    | "ecdsa-p256" ->
                        let key: TrustedVerifierKey = { KeyId = str node "keyId"; Algorithm = VerificationReceipt.Algorithm; PublicKey = str node "publicKey" }
                        let spki = Convert.FromBase64String key.PublicKey
                        if VerificationReceipt.keyId spki <> key.KeyId then failwith $"keyId {key.KeyId} is not the SHA-256 of its publicKey"
                        VerifierIdentity.PinnedKey key
                    | other -> failwith $"unknown verifier kind '{other}'"

                { Id = str node "id"
                  Identity = identity
                  ValidFrom = time node "validFrom"
                  ValidUntil = optTime node "validUntil"
                  RevokedAt = optTime node "revokedAt"
                  RevocationReason = optStr node "revocationReason"
                  PolicyScopes = strings node "policyScopes" }

            let verifiers = objects root "verifiers" |> List.map verifier
            let ids = verifiers |> List.map _.Id
            if ids.Length <> (List.distinct ids).Length then failwith "verifier ids must be unique"

            { ConfigVersion = int' root "configVersion" 0
              Repository = str root "repository"
              Verifiers = verifiers
              ApprovedBy = str root "approvedBy"
              ApprovedAt = time root "approvedAt" })

    let renderTrustRoot (root: VerifierTrustRoot) =
        let node = JsonObject()
        node["schema"] <- JsonValue.Create VerifierTrust.Schema
        node["configVersion"] <- JsonValue.Create root.ConfigVersion
        node["repository"] <- JsonValue.Create root.Repository
        node["approvedBy"] <- JsonValue.Create root.ApprovedBy
        node["approvedAt"] <- JsonValue.Create(VerifierTrust.utc root.ApprovedAt)
        let verifiers = JsonArray()

        for v in root.Verifiers do
            let entry = JsonObject()
            entry["id"] <- JsonValue.Create v.Id

            match v.Identity with
            | VerifierIdentity.Workload w ->
                entry["kind"] <- JsonValue.Create "github-actions-oidc"
                entry["issuer"] <- JsonValue.Create w.Issuer
                entry["repository"] <- JsonValue.Create w.Repository
                entry["workflowRef"] <- JsonValue.Create w.WorkflowRef
                entry["events"] <- JsonArray(w.Events |> List.map (fun e -> JsonValue.Create e :> JsonNode) |> List.toArray)
            | VerifierIdentity.PinnedKey key ->
                entry["kind"] <- JsonValue.Create "ecdsa-p256"
                entry["keyId"] <- JsonValue.Create key.KeyId
                entry["publicKey"] <- JsonValue.Create key.PublicKey

            entry["validFrom"] <- JsonValue.Create(VerifierTrust.utc v.ValidFrom)
            entry["validUntil"] <- (v.ValidUntil |> Option.map (fun t -> JsonValue.Create(VerifierTrust.utc t) :> JsonNode) |> Option.toObj)
            entry["revokedAt"] <- (v.RevokedAt |> Option.map (fun t -> JsonValue.Create(VerifierTrust.utc t) :> JsonNode) |> Option.toObj)
            entry["revocationReason"] <- (v.RevocationReason |> Option.map (fun t -> JsonValue.Create t :> JsonNode) |> Option.toObj)
            entry["policyScopes"] <- JsonArray(v.PolicyScopes |> List.map (fun e -> JsonValue.Create e :> JsonNode) |> List.toArray)
            verifiers.Add entry

        node["verifiers"] <- verifiers
        node

    let parseAuthorities (text: string) : Result<GovernanceAuthorities, string> =
        guard (fun () ->
            let root = parseRoot text
            schema root "praxis.governance-authorities/1"

            { WorkAuthorizers = strings root "workAuthorizers"
              ElevatedApprovers = strings root "elevatedApprovers"
              ExceptionAuthorities = strings root "exceptionAuthorities"
              AgentIdentities = strings root "agentIdentities" })

    let noAuthorities = { WorkAuthorizers = []; ElevatedApprovers = []; ExceptionAuthorities = []; AgentIdentities = [] }

    let parseStanding (text: string) : Result<StandingAuthorization list, string> =
        guard (fun () ->
            let root = parseRoot text
            schema root "praxis.standing-authorization/1"

            objects root "policies"
            |> List.map (fun node ->
                let categories =
                    objects node "categories"
                    |> List.map (fun c ->
                        let paths = strings c "allowedPaths"
                        if paths.IsEmpty then failwith "a standing category must name allowedPaths"
                        { Name = str c "name"; AllowedPaths = paths; MaxChangedFiles = int' c "maxChangedFiles" 20 })

                { Id = str node "id"
                  Repositories = strings node "repositories"
                  Categories = categories
                  ProhibitedPaths = strings node "prohibitedPaths"
                  ApprovedBy = str node "approvedBy"
                  ValidFrom = time node "validFrom"
                  ExpiresAt = time node "expiresAt"
                  RevokedAt = optTime node "revokedAt" }))

    let parseException (text: string) : Result<GovernanceException, string> =
        guard (fun () ->
            let root = parseRoot text
            schema root "praxis.governance-exception/1"

            { Id = str root "id"
              Repository = str root "repository"
              CandidateHeads = strings root "candidateHeads"
              Waives = strings root "waives"
              Reason = str root "reason"
              AuthorizedBy = str root "authorizedBy"
              Implementer = str root "implementer"
              CompensatingSafeguards = strings root "compensatingSafeguards"
              ResidualRisk = str root "residualRisk"
              FollowUp = strings root "followUp"
              GrantedAt = time root "grantedAt"
              ExpiresAt = time root "expiresAt" })

    /// `ros.json` `governance`, with Praxis defaults for absent members.
    let parseProfile (rosJson: string) : Result<GovernanceProfile * bool, string> =
        guard (fun () ->
            let root = parseRoot rosJson
            let d = PlatformGovernance.defaultProfile

            match root["governance"] with
            | null -> d, false
            | node ->
                let checks =
                    match node["requiredChecks"] with
                    | null -> d.RequiredChecks
                    | _ ->
                        objects node "requiredChecks"
                        |> List.map (fun c ->
                            { Context = str c "context"
                              IntegrationId =
                                match c["integrationId"] with
                                | null -> None
                                | v -> Some(v.GetValue<int64>()) })

                { RequiredChecks = checks
                  RequirePullRequest = flag node "requirePullRequest" d.RequirePullRequest
                  MinimumApprovals = int' node "minimumApprovals" d.MinimumApprovals
                  RequireUpToDate = flag node "requireUpToDate" d.RequireUpToDate
                  BlockForcePush = flag node "blockForcePush" d.BlockForcePush
                  BlockDeletion = flag node "blockDeletion" d.BlockDeletion
                  AllowAdminBypass = flag node "allowAdminBypass" d.AllowAdminBypass
                  MergeGateWorkflow = optStr node "mergeGateWorkflow" |> Option.defaultValue d.MergeGateWorkflow
                  RequireReleaseTagProtection = flag node "requireReleaseTagProtection" d.RequireReleaseTagProtection
                  RequireVerifiedMerge = flag node "requireVerifiedMerge" d.RequireVerifiedMerge },
                true)

    /// `workProtocol` flags and the repository identity of a `ros.json`.
    type RosFacts =
        { Repository: string option
          RequireVerifiedCompletion: bool
          RequireAttestedEvidence: bool
          PinnedKeyIds: string list }

    let parseRos (rosJson: string) : Result<RosFacts, string> =
        guard (fun () ->
            let root = parseRoot rosJson

            let repository =
                match root["repository"] with
                | null -> None
                | repo ->
                    match repo["identity"] with
                    | null -> None
                    | identity -> optStr identity "repository"

            let protocol = match root["workProtocol"] with null -> JsonObject() :> JsonNode | p -> p

            { Repository = repository
              RequireVerifiedCompletion = flag protocol "requireVerifiedCompletion" false
              RequireAttestedEvidence = flag protocol "requireAttestedEvidence" false
              PinnedKeyIds = strings protocol "trustedVerifierKeyIds" })

    /// The protected validation policy: its identity, digest (over the exact
    /// bytes, as `ValidationPolicyJson` computes it) and validation names.
    let parsePolicy (bytes: byte array) : Result<ProtectedPolicy, string> =
        guard (fun () ->
            let root = parseRoot (Encoding.UTF8.GetString bytes)
            if str root "schemaVersion" <> "echelon.validation-policy/1" then failwith "schemaVersion must be echelon.validation-policy/1"
            let names = objects root "validations" |> List.map (fun v -> str v "name")

            { Id = str root "policyId"
              Digest = CompletionGate.sha256 bytes
              Repository = optStr root "repository"
              RequiredValidations = names })

    // ---- receipts ---------------------------------------------------------

    [<RequireQualifiedAccess>]
    type ReceiptDocument =
        | Signed of ReceiptEnvelope
        | Workload of VerifierTrust.WorkloadReceipt

    let parseReceiptDocument (text: string) : Result<ReceiptDocument, string> =
        try
            let root = parseRoot text

            match root["schema"] with
            | null -> VerificationReceipt.parseEnvelope text |> Result.map ReceiptDocument.Signed
            | _ ->
                schema root "praxis.workload-receipt/1"

                Ok(
                    ReceiptDocument.Workload
                        { Payload = Convert.FromBase64String(str root "payload")
                          Token = str root "token" }
                )
        with error ->
            Error error.Message

    let renderWorkloadReceipt (receipt: VerifierTrust.WorkloadReceipt) =
        let node = JsonObject()
        node["schema"] <- JsonValue.Create "praxis.workload-receipt/1"
        node["payload"] <- JsonValue.Create(Convert.ToBase64String receipt.Payload)
        node["token"] <- JsonValue.Create receipt.Token
        node

    let parseJwks (text: string) : Result<VerifierTrust.IssuerKey list, string> =
        guard (fun () ->
            let root = parseRoot text

            objects root "keys"
            |> List.filter (fun k -> optStr k "kty" = Some "RSA")
            |> List.map (fun k ->
                { Kid = str k "kid"
                  Modulus = VerifierTrust.base64UrlDecode (str k "n")
                  Exponent = VerifierTrust.base64UrlDecode (str k "e") }))

    // ---- reports ----------------------------------------------------------

    let private arr (values: string list) = JsonArray(values |> List.map (fun v -> JsonValue.Create v :> JsonNode) |> List.toArray)

    let finding (f: GovernanceFinding) =
        let node = JsonObject()
        node["code"] <- JsonValue.Create f.Code
        node["detail"] <- JsonValue.Create f.Detail
        node["remedy"] <- JsonValue.Create f.Remedy
        node["resolvedBy"] <- JsonValue.Create(Principal.toWire f.ResolvedBy)
        node["agentResolvable"] <- JsonValue.Create(Principal.isAgentResolvable f.ResolvedBy)
        node

    let report (r: GovernanceReport) =
        let node = JsonObject()
        node["schema"] <- JsonValue.Create "praxis.governance-report/1"
        node["repository"] <- JsonValue.Create r.Repository
        node["branch"] <- JsonValue.Create r.Branch
        node["capturedAt"] <- JsonValue.Create(VerifierTrust.utc r.CapturedAt)
        node["level"] <- JsonValue.Create(ReadinessLevel.toWire r.Level)
        let ladder = JsonArray()

        for rung in r.Ladder do
            let entry = JsonObject()
            entry["level"] <- JsonValue.Create(ReadinessLevel.toWire rung.Level)
            entry["reached"] <- JsonValue.Create rung.Reached
            entry["blockers"] <- arr rung.Blockers
            ladder.Add entry

        node["ladder"] <- ladder
        let controls = JsonArray()

        for c in r.Controls do
            let entry = JsonObject()
            entry["id"] <- JsonValue.Create c.Id
            entry["title"] <- JsonValue.Create c.Title
            entry["status"] <- JsonValue.Create(ControlStatus.toWire c.Status)
            entry["detail"] <- JsonValue.Create c.Detail
            entry["remedy"] <- JsonValue.Create c.Remedy
            entry["resolvedBy"] <- JsonValue.Create(Principal.toWire c.ResolvedBy)
            controls.Add entry

        node["controls"] <- controls
        node["residualTrustBoundaries"] <- arr r.ResidualTrustBoundaries
        node

    let private statusOf (text: string) =
        match text with
        | "enforced" -> ControlStatus.Enforced
        | "not-enforced" -> ControlStatus.NotEnforced
        | "platform-unavailable" -> ControlStatus.PlatformUnavailable
        | "not-required" -> ControlStatus.NotRequired
        | _ -> ControlStatus.Unknown

    /// Reads back a saved report as a drift baseline (controls only).
    let parseReport (text: string) : Result<GovernanceReport, string> =
        guard (fun () ->
            let root = parseRoot text
            schema root "praxis.governance-report/1"

            { Repository = str root "repository"
              Branch = str root "branch"
              CapturedAt = time root "capturedAt"
              Controls =
                objects root "controls"
                |> List.map (fun c ->
                    { Id = str c "id"
                      Title = str c "title"
                      Status = statusOf (str c "status")
                      Detail = optStr c "detail" |> Option.defaultValue ""
                      Remedy = optStr c "remedy" |> Option.defaultValue ""
                      ResolvedBy = Principal.RepositoryAdministrator })
              Ladder = []
              Level = ReadinessLevel.NotInstalled
              ResidualTrustBoundaries = [] })

    let decision (d: MergeDecision) =
        let node = JsonObject()
        node["schema"] <- JsonValue.Create "praxis.merge-decision/1"
        node["outcome"] <- JsonValue.Create(MergeOutcome.toWire d.Outcome)
        node["permitsMerge"] <- JsonValue.Create(MergeOutcome.permitsMerge d.Outcome)

        match d.Outcome with
        | MergeOutcome.AuthorizedByException ids -> node["exceptions"] <- arr ids
        | _ -> ()

        node["repository"] <- JsonValue.Create d.Repository
        node["targetBranch"] <- JsonValue.Create d.TargetBranch
        node["targetHead"] <- JsonValue.Create d.TargetHead
        node["candidateHead"] <- JsonValue.Create d.CandidateHead
        node["workItems"] <- arr d.WorkItems
        node["authorizationBasis"] <- arr d.AuthorizationBasis
        node["verifiedCommit"] <- (d.VerifiedCommit |> Option.map (fun c -> JsonValue.Create c :> JsonNode) |> Option.toObj)
        let receipts = JsonArray()

        for r in d.Receipts do
            let entry = JsonObject()
            entry["digest"] <- JsonValue.Create r.Digest
            entry["verifier"] <- JsonValue.Create r.VerifierId
            entry["basis"] <- JsonValue.Create(AuthenticationBasis.toWire r.Basis)
            entry["workItem"] <- JsonValue.Create r.Payload.WorkItemId
            entry["generation"] <- JsonValue.Create r.Payload.FencingGeneration
            entry["validation"] <- JsonValue.Create r.Payload.Validation
            entry["status"] <- JsonValue.Create(CheckStatus.code r.Payload.Status)
            entry["commit"] <- JsonValue.Create r.Payload.Commit
            entry["treeId"] <- JsonValue.Create r.Payload.TreeId
            entry["policyDigest"] <- JsonValue.Create r.Payload.ValidationPolicyDigest
            entry["observedAt"] <- JsonValue.Create(VerifierTrust.utc r.Payload.ObservedAt)
            receipts.Add entry

        node["receipts"] <- receipts
        node["protectedChanges"] <- arr d.ProtectedChanges
        node["elevatedApprovals"] <- arr (d.ElevatedApprovals |> List.map (fun a -> $"{a.Approver} at {VerifierTrust.utc a.ApprovedAt}"))
        node["refusals"] <- JsonArray(d.Refusals |> List.map (fun f -> finding f :> JsonNode) |> List.toArray)
        node["waived"] <- JsonArray(d.Waived |> List.map (fun f -> finding f :> JsonNode) |> List.toArray)
        node["advisories"] <- arr d.Advisories
        node["evaluatedAt"] <- JsonValue.Create(VerifierTrust.utc d.EvaluatedAt)
        node

    let audit (r: IntegrationAuditReport) =
        let node = JsonObject()
        node["schema"] <- JsonValue.Create "praxis.integration-audit/1"
        node["branch"] <- JsonValue.Create r.Branch
        node["head"] <- JsonValue.Create r.Head
        node["examined"] <- JsonValue.Create r.Examined
        node["verifiedIntegrations"] <- JsonValue.Create r.VerifiedIntegrations
        node["historyRewritten"] <- JsonValue.Create r.HistoryRewritten
        let findings = JsonArray()

        for f in r.Findings do
            let entry = JsonObject()
            entry["commit"] <- JsonValue.Create f.Commit
            entry["code"] <- JsonValue.Create f.Code
            entry["severity"] <- JsonValue.Create f.Severity
            entry["detail"] <- JsonValue.Create f.Detail
            entry["remedy"] <- JsonValue.Create f.Remedy
            findings.Add entry

        node["findings"] <- findings
        node

    let diagnosis (d: CompletionAuthorityDiagnosis) =
        let node = JsonObject()
        node["schema"] <- JsonValue.Create "praxis.completion-authority-status/1"
        node["ready"] <- JsonValue.Create d.Ready
        node["path"] <- JsonValue.Create d.Path
        node["missing"] <- arr d.Missing
        node["why"] <- JsonValue.Create d.Why
        node["reusable"] <- arr d.Reusable
        node["resolvedBy"] <- JsonValue.Create(Principal.toWire d.ResolvedBy)
        node["procedure"] <- arr d.Procedure
        node["mayContinue"] <- arr d.MayContinue
        node["blocked"] <- arr d.Blocked
        node

    let compact (node: JsonNode) = node.ToJsonString(JsonSerializerOptions(WriteIndented = false))
    let indented (node: JsonNode) = node.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    /// SHA-256 of a document's exact bytes, `sha256:<hex>`.
    let digest (text: string) =
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()
