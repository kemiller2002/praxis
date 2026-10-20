namespace Praxis.Infrastructure.Work

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Contracts.Provenance
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Json

/// Wire forms for claims: the stored claim record
/// (`praxis.work-claim/1`), the I1 event envelope, the Dokimos packet and
/// authority (I2), and the Dokimos receipt.
[<RequireQualifiedAccess>]
module WorkClaimJson =
    let ClaimSchemaVersion = "praxis.work-claim/1"

    let stamp (value: DateTimeOffset) =
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

    let private str (value: string) : JsonNode = JsonValue.Create value
    let private num (value: int64) : JsonNode = JsonValue.Create value
    let private flag (value: bool) : JsonNode = JsonValue.Create value
    let private time (value: DateTimeOffset) = str (stamp value)

    let private optional (value: string option) : JsonNode =
        match value with
        | Some text -> str text
        | None -> null

    let private strings (values: string list) : JsonNode =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(str value))
        array

    let private obj (members: (string * JsonNode) list) : JsonNode =
        let node = JsonObject()
        members |> List.iter (fun (name, value) -> node[name] <- value)
        node

    // ---- claim record ------------------------------------------------------

    let private completionNode (completion: VerifiedCompletion) =
        obj
            [ "packetDigest", str completion.PacketDigest
              "receiptDigest", str completion.ReceiptDigest
              "evidenceDigests", strings completion.EvidenceDigests
              "commit", str completion.Commit
              "verificationRevision", str completion.VerificationRevision
              "requirementIds", strings completion.RequirementIds
              "telemetryExecutions", strings completion.TelemetryExecutions
              "verifier", obj [ "name", str completion.VerifierName; "version", str completion.VerifierVersion ]
              "completedAt", time completion.CompletedAt ]

    let claimNode (claim: WorkClaim) =
        obj
            [ "schemaVersion", str ClaimSchemaVersion
              "workItemId", str claim.WorkItemId
              "revision", num claim.Revision
              "generation", num claim.Generation
              "status", str (ClaimStatus.code claim.Status)
              "holder",
              (match claim.Holder with
               | None -> null
               | Some holder ->
                   obj
                       [ "claimant", str holder.Claimant
                         "claimToken", str holder.ClaimToken
                         "acquiredAt", time holder.AcquiredAt
                         "leaseExpiresAt", time holder.LeaseExpiresAt ])
              "previousClaimant", optional claim.PreviousClaimant
              "pendingPacketDigest", optional claim.PendingPacketDigest
              "consumedPacketDigests", strings claim.ConsumedPacketDigests
              "completion", (claim.Completion |> Option.map completionNode |> Option.toObj)
              "updatedAt", time claim.UpdatedAt ]

    let renderClaim (claim: WorkClaim) =
        (claimNode claim).ToJsonString(JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)) + "\n"

    let private property (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
        | _ -> None

    let private text element name =
        property element name
        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
        |> Option.map _.GetString()

    let private requireText element name =
        text element name |> Option.defaultWith (fun () -> failwith $"'{name}' must be a string")

    let private requireTime element name =
        DateTimeOffset.Parse(requireText element name, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)

    let private texts element name =
        match property element name with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray() |> Seq.map (fun item -> item.GetString()) |> Seq.toList
        | Some _ -> failwith $"'{name}' must be an array"
        | None -> []

    let private parseClaimElement (root: JsonElement) =
        if text root "schemaVersion" <> Some ClaimSchemaVersion then failwith "unsupported claim schemaVersion"

        { WorkItemId = requireText root "workItemId"
          Revision = root.GetProperty("revision").GetInt64()
          Generation = root.GetProperty("generation").GetInt64()
          Status =
            requireText root "status"
            |> ClaimStatus.tryParse
            |> Option.defaultWith (fun () -> failwith "unknown claim status")
          Holder =
            property root "holder"
            |> Option.map (fun holder ->
                { ClaimHolder.Claimant = requireText holder "claimant"
                  ClaimToken = requireText holder "claimToken"
                  AcquiredAt = requireTime holder "acquiredAt"
                  LeaseExpiresAt = requireTime holder "leaseExpiresAt" })
          PreviousClaimant = text root "previousClaimant"
          PendingPacketDigest = text root "pendingPacketDigest"
          ConsumedPacketDigests = texts root "consumedPacketDigests"
          Completion =
            property root "completion"
            |> Option.map (fun completion ->
                let verifier = completion.GetProperty "verifier"

                { PacketDigest = requireText completion "packetDigest"
                  ReceiptDigest = requireText completion "receiptDigest"
                  EvidenceDigests = texts completion "evidenceDigests"
                  Commit = requireText completion "commit"
                  VerificationRevision = requireText completion "verificationRevision"
                  RequirementIds = texts completion "requirementIds"
                  TelemetryExecutions = texts completion "telemetryExecutions"
                  VerifierName = requireText verifier "name"
                  VerifierVersion = requireText verifier "version"
                  CompletedAt = requireTime completion "completedAt" })
          UpdatedAt = requireTime root "updatedAt" }

    let parseClaim (json: string) : Result<WorkClaim, string> =
        try
            use document = JsonDocument.Parse json
            Ok(parseClaimElement document.RootElement)
        with error ->
            Error $"claim record is malformed: {error.Message}"

    // ---- I1 events ----------------------------------------------------------

    let private payloadMembers (payload: ClaimEventPayload) : (string * JsonNode) list =
        match payload with
        | ClaimEventPayload.ClaimAcquired(claimant, token, generation, expires, revision, recovered, previous) ->
            [ "claimant", str claimant
              "claimToken", str token
              "generation", num generation
              "leaseExpiresAt", time expires
              "revision", num revision
              "recovered", flag recovered ]
            @ (previous |> Option.map (fun value -> "previousClaimant", str value) |> Option.toList)
        | ClaimEventPayload.ClaimRejected(claimant, reason, generation) ->
            [ "claimant", str claimant; "reason", str reason; "currentGeneration", num generation ]
        | ClaimEventPayload.ClaimRenewed(claimant, token, generation, expires) ->
            [ "claimant", str claimant; "claimToken", str token; "generation", num generation; "leaseExpiresAt", time expires ]
        | ClaimEventPayload.ClaimReleased(claimant, token, generation)
        | ClaimEventPayload.ClaimExpired(claimant, token, generation) ->
            [ "claimant", str claimant; "claimToken", str token; "generation", num generation ]
        | ClaimEventPayload.CompletionRejected(claimant, token, generation, codes, digest, verifier) ->
            [ "claimant", str claimant
              "claimToken", str token
              "generation", num generation
              "codes", strings codes ]
            @ (digest |> Option.map (fun value -> "packetDigest", str value) |> Option.toList)
            @ [ "verifier",
                (match verifier with
                 | Some identity -> obj [ "name", str identity.Name; "version", str identity.Version ]
                 | None -> null) ]
        | ClaimEventPayload.CompletionVerified(claimant, token, generation, completion) ->
            [ "claimant", str claimant
              "claimToken", str token
              "generation", num generation
              "commit", str completion.Commit
              "verificationRevision", str completion.VerificationRevision
              "requirementIds", strings completion.RequirementIds
              "packetDigest", str completion.PacketDigest
              "receiptDigest", str completion.ReceiptDigest
              "telemetryExecutions", strings completion.TelemetryExecutions ]
        | ClaimEventPayload.CompletionReplayed(token, generation, digest) ->
            [ "claimToken", str token; "generation", num generation; "packetDigest", str digest ]
        | ClaimEventPayload.ValidationObserved(claimant, token, generation, validation, evidenceId, revision, status, observedAt, resultDigest) ->
            [ "claimant", str claimant
              "claimToken", str token
              "generation", num generation
              "validation", str validation
              "evidenceId", str evidenceId
              "revision", str revision
              "status", str (CheckStatus.code status)
              "observedAt", time observedAt
              "resultDigest", str resultDigest ]

    /// The existing work-event envelope plus the I1 payload. `eventId` is the
    /// content hash of every member before it, as for every work event, so
    /// an identical event always has the identical id.
    let eventNode (repository: string) (protocolVersion: string) (execution: string option) (actor: Actor option) (event: ClaimEvent) =
        let node = JsonObject()
        node["schemaVersion"] <- str "1.0.0"
        node["type"] <- str (ClaimEvent.eventType event)
        node["workItem"] <- str event.WorkItemId
        node["repository"] <- str repository
        node["protocolVersion"] <- str protocolVersion
        node["occurredAt"] <- time event.OccurredAt
        execution |> Option.iter (fun value -> node["execution"] <- str value)
        payloadMembers event.Payload |> List.iter (fun (name, value) -> node[name] <- value)
        actor |> Option.iter (fun value -> node["actor"] <- ActorJson.node value)
        node["eventId"] <- str (CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact node))
        node

    // ---- Dokimos wire (I2) --------------------------------------------------

    let packetNode (packet: CompletionPacket) =
        obj
            [ "SchemaVersion", str CompletionGate.PacketSchemaVersion
              "WorkItemId", str packet.WorkItemId
              "ClaimToken", str packet.ClaimToken
              "FencingGeneration", num packet.FencingGeneration
              "Commit", str packet.Commit
              "VerificationRevision", str packet.VerificationRevision
              "OrdoContractVersion", str packet.OrdoContractVersion
              "OrdoContractDigest", str packet.OrdoContractDigest
              "RequirementIds", strings packet.RequirementIds
              "DecisionIds", strings packet.DecisionIds
              "ConflictResolutionIds", strings packet.ConflictResolutionIds
              "Validations",
              (let array = JsonArray()
               for validation in packet.Validations do
                   array.Add(
                       obj
                           [ "Name", str validation.Name
                             "Status", str (CheckStatus.code validation.Status)
                             "EvidenceIds", strings validation.EvidenceIds ]
                   )
               array)
              "Evidence",
              (let array = JsonArray()
               for evidence in packet.Evidence do
                   array.Add(obj [ "Id", str evidence.Id; "Digest", str evidence.Digest; "Revision", str evidence.Revision ])
               array) ]

    /// Dokimos refuses JSON `null` for an optional member: absent means
    /// "not asserted", so an unset optional member is omitted.
    let private present (name: string) (value: JsonNode option) =
        value |> Option.map (fun node -> name, node) |> Option.toList

    let authorityNode (authority: CompletionAuthority) =
        obj (
            [ "SchemaVersion", str CompletionGate.AuthoritySchemaVersion
              "WorkItemId", str authority.WorkItemId
              "ClaimToken", str authority.ClaimToken
              "FencingGeneration", num authority.FencingGeneration
              "ExpectedCommit", str authority.ExpectedCommit
              "ExpectedVerificationRevision", str authority.ExpectedVerificationRevision
              "SupportedOrdoContractVersions", strings authority.SupportedOrdoContractVersions
              "ExpectedOrdoContractDigest", str authority.ExpectedOrdoContractDigest
              "RequiredRequirementIds", strings authority.RequiredRequirementIds
              "RequiredDecisionIds", strings authority.RequiredDecisionIds
              "RequiredConflictResolutionIds", strings authority.RequiredConflictResolutionIds
              "RequiredValidationNames", strings authority.RequiredValidationNames
              "Evidence",
              (let array = JsonArray()
               for evidence in authority.Evidence do
                   array.Add(
                       obj (
                           [ "Id", str evidence.Id
                             "Digest", str evidence.Digest
                             "Revision", str evidence.Revision
                             "Status", str (CheckStatus.code evidence.Status)
                             "Reusable", flag evidence.Reusable
                             "ObservedAt", time evidence.ObservedAt
                             "Validation", str evidence.Validation ]
                           @ present "Attestation" (evidence.Attestation |> Option.map VerificationReceipt.envelopeNode)
                       )
                   )
               array)
              "ConsumedPacketDigests", strings authority.ConsumedPacketDigests ]
            @ (if authority.TrustedVerifierKeys.IsEmpty then
                   []
               else
                   [ "TrustedVerifierKeys",
                     (let array = JsonArray()
                      for key in authority.TrustedVerifierKeys do
                          array.Add(obj [ "KeyId", str key.KeyId; "Algorithm", str key.Algorithm; "PublicKey", str key.PublicKey ])
                      array :> JsonNode) ])
            @ present "ValidationPolicyDigest" (authority.ValidationPolicyDigest |> Option.map str)
            @ present "ClaimAcquiredAt" (authority.ClaimAcquiredAt |> Option.map time)
            @ present "LeaseExpiresAt" (authority.LeaseExpiresAt |> Option.map time)
            @ present "EvaluatedAt" (authority.EvaluatedAt |> Option.map time)
        )

    /// Strict receipt intake: every required member, nothing unknown.
    let parseReceipt (json: string) : Result<DokimosReceipt, string> =
        let required =
            set
                [ "Contract"; "SchemaVersion"; "DokimosVersion"; "Verdict"; "ExitCode"; "WorkItemId"; "Commit"; "VerificationRevision"
                  "PacketDigest"; "RequirementCount"; "EvidenceDigests"; "ReusedEvidenceIds"; "Diagnostics" ]

        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error "receipt is not a JSON object"
            else
                let names = root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

                if names <> required then
                    Error $"receipt members differ from dokimos.completion-verification/1: {Set.difference (Set.union names required) (Set.intersect names required) |> Set.toList}"
                else
                    Ok
                        { Contract = requireText root "Contract"
                          SchemaVersion = requireText root "SchemaVersion"
                          DokimosVersion = requireText root "DokimosVersion"
                          Verdict = requireText root "Verdict"
                          ExitCode = root.GetProperty("ExitCode").GetInt32()
                          WorkItemId = requireText root "WorkItemId"
                          Commit = requireText root "Commit"
                          VerificationRevision = requireText root "VerificationRevision"
                          PacketDigest = requireText root "PacketDigest"
                          RequirementCount = root.GetProperty("RequirementCount").GetInt32()
                          EvidenceDigests = texts root "EvidenceDigests"
                          ReusedEvidenceIds = texts root "ReusedEvidenceIds"
                          Diagnostics =
                            root.GetProperty("Diagnostics").EnumerateArray()
                            |> Seq.map (fun item ->
                                { Code = requireText item "Code"
                                  Subject = requireText item "Subject"
                                  Detail = requireText item "Detail" })
                            |> Seq.toList }
        with error ->
            Error $"receipt is not valid JSON: {error.Message}"

    /// The caller-supplied authority input (I3 minimal Ordo record).
    let parseAuthorityInput (json: string) : Result<CompletionAuthorityInput, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            Ok
                { OrdoContractVersion = requireText root "OrdoContractVersion"
                  OrdoContractDigest = requireText root "OrdoContractDigest"
                  SupportedOrdoContractVersions = texts root "SupportedOrdoContractVersions"
                  RequiredRequirementIds = texts root "RequiredRequirementIds"
                  RequiredDecisionIds = texts root "RequiredDecisionIds"
                  RequiredConflictResolutionIds = texts root "RequiredConflictResolutionIds"
                  RequiredValidationNames = texts root "RequiredValidationNames"
                  RequiredEvidenceIds = texts root "RequiredEvidenceIds"
                  ExpectedCommit = text root "ExpectedCommit"
                  ExpectedVerificationRevision = text root "ExpectedVerificationRevision"
                  TrustedVerifierKeys =
                    match property root "TrustedVerifierKeys" with
                    | Some keys when keys.ValueKind = JsonValueKind.Array ->
                        keys.EnumerateArray()
                        |> Seq.map (fun key ->
                            { TrustedVerifierKey.KeyId = requireText key "KeyId"
                              Algorithm = requireText key "Algorithm"
                              PublicKey = requireText key "PublicKey" })
                        |> Seq.toList
                    | Some _ -> failwith "'TrustedVerifierKeys' must be an array"
                    | None -> []
                  ValidationPolicyDigest = text root "ValidationPolicyDigest" }
        with error ->
            Error $"completion authority input is malformed: {error.Message}"

/// Command results for `praxis work claim|renew|release|recover|expire|
/// claims|complete-verified`: one JSON document on stdout.
[<RequireQualifiedAccess>]
module WorkClaimOutcomeJson =
    open Praxis.Application.Work

    let private render (node: JsonNode) =
        node.ToJsonString(JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))

    let private view (now: DateTimeOffset) (claim: WorkClaim) =
        let node = WorkClaimJson.claimNode claim :?> JsonObject
        node["effectiveStatus"] <- JsonValue.Create(ClaimStatus.code (WorkClaim.effectiveStatus now claim))
        node :> JsonNode

    let private document (members: (string * JsonNode) list) =
        let node = JsonObject()
        members |> List.iter (fun (name, value) -> node[name] <- value)
        render node

    let private findings (items: CompletionFinding list) =
        let array = JsonArray()

        for item in items do
            let entry = JsonObject()
            entry["code"] <- JsonValue.Create item.Code
            entry["detail"] <- JsonValue.Create item.Detail
            array.Add(entry :> JsonNode)

        array :> JsonNode

    /// `FileWorkClaimStore.LegacyStoreCode`: a claim only the legacy tracked
    /// store holds is a refusal (exit 3) naming the migration, not a failure.
    let LegacyStoreCode = "PRX.CLAIM.LEGACY_STORE"

    let legacy (reason: string) =
        document
            [ "outcome", JsonValue.Create "refused"
              "code", JsonValue.Create LegacyStoreCode
              "detail", JsonValue.Create(reason.Substring(LegacyStoreCode.Length).TrimStart(':', ' ')) ],
        3

    /// The document and the documented exit code: 0 done (or replayed),
    /// 3 refused with a structured conflict, 1 storage or event failure.
    let claimOutcome (now: DateTimeOffset) (outcome: ClaimOutcome) =
        match outcome with
        | ClaimOutcome.Succeeded(claim, replayed) ->
            document [ "outcome", JsonValue.Create(if replayed then "replayed" else "recorded"); "claim", view now claim ], 0
        | ClaimOutcome.Refused(conflict, current) ->
            document
                [ "outcome", JsonValue.Create "refused"
                  "code", JsonValue.Create(ClaimConflict.code conflict)
                  "reason", JsonValue.Create(ClaimConflict.reason conflict)
                  "detail", JsonValue.Create(ClaimConflict.detail conflict)
                  "claim", (current |> Option.map (view now) |> Option.toObj) ],
            3
        | ClaimOutcome.Failed reason when reason.StartsWith(LegacyStoreCode, StringComparison.Ordinal) -> legacy reason
        | ClaimOutcome.Failed reason -> document [ "outcome", JsonValue.Create "failed"; "detail", JsonValue.Create reason ], 1

    let claims (now: DateTimeOffset) (claims: WorkClaim list) =
        let array = JsonArray()
        claims |> List.iter (fun claim -> array.Add(view now claim))
        document [ "claims", array ]

    let completionOutcome (now: DateTimeOffset) (outcome: CompletionOutcome) =
        match outcome with
        | CompletionOutcome.Completed(claim, completion)
        | CompletionOutcome.Replayed(claim, completion) ->
            let replayed =
                match outcome with
                | CompletionOutcome.Replayed _ -> true
                | _ -> false

            document
                [ "outcome", JsonValue.Create(if replayed then "replayed" else "verified")
                  "packetDigest", JsonValue.Create completion.PacketDigest
                  "receiptDigest", JsonValue.Create completion.ReceiptDigest
                  "claim", view now claim ],
            0
        | CompletionOutcome.Rejected(items, current) ->
            document
                [ "outcome", JsonValue.Create "rejected"
                  "codes", (items |> List.map _.Code |> List.distinct |> List.map (fun code -> JsonValue.Create code :> JsonNode) |> Array.ofList |> JsonArray :> JsonNode)
                  "findings", findings items
                  "claim", (current |> Option.map (view now) |> Option.toObj) ],
            3
        | CompletionOutcome.Failed reason when reason.StartsWith(LegacyStoreCode, StringComparison.Ordinal) -> legacy reason
        | CompletionOutcome.Failed reason -> document [ "outcome", JsonValue.Create "failed"; "detail", JsonValue.Create reason ], 1
