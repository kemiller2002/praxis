namespace Praxis.Contracts.Work

open System
open System.Collections.Generic
open System.Globalization
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Execution
open Praxis.Domain.Work

/// Exact, bounded untrusted input. Decoding never grants execution authority.
[<RequireQualifiedAccess>]
module LocalAgentHandoffJson =
    [<Literal>]
    let MaxDocumentBytes = 65536

    let private fields (names: string list) (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then invalidOp "expected object"
        let actual = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        if actual.Length <> names.Length || Set.ofList actual <> Set.ofList names then
            invalidOp "missing, unexpected or duplicate fields"
    let private stringValue (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.String then invalidOp "expected string"
        let text = value.GetString()
        if String.IsNullOrWhiteSpace text || text.Length > 1024 then invalidOp "blank or oversized string"
        text
    let private text (name: string) (value: JsonElement) = value.GetProperty name |> stringValue
    let private array (name: string) read (value: JsonElement) =
        let items = value.GetProperty name
        if items.ValueKind <> JsonValueKind.Array || items.GetArrayLength() > 256 then invalidOp "invalid or oversized array"
        items.EnumerateArray() |> Seq.map read |> Seq.toList
    let private texts name value = array name stringValue value
    let private integer (name: string) (value: JsonElement) =
        let number = value.GetProperty name
        match number.TryGetInt32() with
        | true, result -> result
        | _ -> invalidOp "expected bounded integer"
    let private time name value =
        let formats = [| "yyyy-MM-dd'T'HH:mm:ss'Z'"; "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"; "yyyy-MM-dd'T'HH:mm:sszzz"; "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz" |]
        match DateTimeOffset.TryParseExact(text name value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, parsed when parsed.Offset = TimeSpan.Zero -> parsed
        | _ -> invalidOp "expected explicit ISO-8601 UTC timestamp"
    let private read parse (json: string) =
        try
            if isNull json || json.Length > MaxDocumentBytes || Encoding.UTF8.GetByteCount json > MaxDocumentBytes then
                Error "local handoff document exceeds 65536 UTF-8 bytes"
            else
                use document = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = 12))
                Ok(parse document.RootElement)
        with
        | :? JsonException as error -> Error("invalid local handoff JSON: " + error.Message)
        | :? InvalidOperationException as error -> Error("invalid local handoff shape: " + error.Message)
        | :? KeyNotFoundException as error -> Error("invalid local handoff fields: " + error.Message)
        | :? FormatException as error -> Error("invalid local handoff value: " + error.Message)

    let readPacket json =
        read (fun value ->
            fields [ "schemaVersion"; "dispatchId"; "attemptId"; "parentExecutionId"; "childExecutionId"; "workerId"; "role"
                     "repositoryIdentity"; "sourceCommit"; "sourceManifestDigest"; "blueprintDigest"; "groupId"; "cohortId"
                     "members"; "decisionIds"; "allowedPaths"; "prerequisites"; "acceptance"; "authorityRevision"; "receiptDigest"
                     "issuedAt"; "expiresAt"; "timeoutSeconds"; "maxOutputBytes" ] value
            let packet =
                { SchemaVersion = text "schemaVersion" value; DispatchId = text "dispatchId" value; AttemptId = text "attemptId" value
                  ParentExecutionId = text "parentExecutionId" value; ChildExecutionId = text "childExecutionId" value; WorkerId = text "workerId" value
                  Role =
                      let wire = text "role" value
                      ExecutionRole.tryParse wire |> Option.filter (fun role -> ExecutionRole.toWire role = wire) |> Option.defaultWith (fun () -> invalidOp "unknown role")
                  RepositoryIdentity = text "repositoryIdentity" value; SourceCommit = text "sourceCommit" value
                  SourceManifestDigest = text "sourceManifestDigest" value; BlueprintDigest = text "blueprintDigest" value
                  GroupId = text "groupId" value; CohortId = text "cohortId" value
                  Members = array "members" (fun item ->
                      fields [ "workItemId"; "executionId"; "requirementKeys" ] item
                      { WorkItemId = text "workItemId" item; ExecutionId = text "executionId" item; RequirementKeys = texts "requirementKeys" item }) value
                  DecisionIds = texts "decisionIds" value; AllowedPaths = texts "allowedPaths" value
                  Prerequisites = array "prerequisites" (fun item ->
                      fields [ "workItemId"; "evidenceDigest" ] item
                      { WorkItemId = text "workItemId" item; EvidenceDigest = text "evidenceDigest" item }) value
                  Acceptance = array "acceptance" (fun item ->
                      fields [ "obligationId"; "workItemId"; "requirementKeys"; "validatorId"; "inputDigest" ] item
                      { ObligationId = text "obligationId" item; WorkItemId = text "workItemId" item
                        RequirementKeys = texts "requirementKeys" item; ValidatorId = text "validatorId" item; InputDigest = text "inputDigest" item }) value
                  AuthorityRevision = text "authorityRevision" value; ReceiptDigest = text "receiptDigest" value
                  IssuedAt = time "issuedAt" value; ExpiresAt = time "expiresAt" value
                  TimeoutSeconds = integer "timeoutSeconds" value; MaxOutputBytes = integer "maxOutputBytes" value }
            match LocalAgentHandoff.packetProblems packet with
            | [] -> packet
            | problems -> invalidOp (String.concat "; " problems)) json

    let private outcome value =
        match text "outcome" value with
        | "submitted" -> LocalWorkerOutcome.Submitted
        | "blocked" -> LocalWorkerOutcome.Blocked
        | "failed" -> LocalWorkerOutcome.Failed
        | _ -> invalidOp "unknown outcome; completed is not a worker authority"
    let readResult json =
        read (fun value ->
            fields [ "schemaVersion"; "dispatchId"; "attemptId"; "childExecutionId"; "workerId"; "packetDigest"; "outputCommit"; "changedPaths"; "members" ] value
            let result =
                { SchemaVersion = text "schemaVersion" value; DispatchId = text "dispatchId" value; AttemptId = text "attemptId" value
                  ChildExecutionId = text "childExecutionId" value; WorkerId = text "workerId" value
                  PacketDigest = text "packetDigest" value; OutputCommit = text "outputCommit" value; ChangedPaths = texts "changedPaths" value
                  Members = array "members" (fun item ->
                      fields [ "workItemId"; "executionId"; "changedPaths"; "outcome"; "evidence" ] item
                      { WorkItemId = text "workItemId" item; ExecutionId = text "executionId" item; ChangedPaths = texts "changedPaths" item; Outcome = outcome item
                        Evidence = array "evidence" (fun evidence ->
                            fields [ "obligationId"; "artifactPath"; "digest" ] evidence
                            { ObligationId = text "obligationId" evidence; ArtifactPath = text "artifactPath" evidence; Digest = text "digest" evidence }) item }) value }
            match LocalAgentHandoff.resultProblems result with
            | [] -> result
            | problems -> invalidOp (String.concat "; " problems)) json

    let private str (value: string) : JsonNode = JsonValue.Create value
    let private obj (fields: (string * JsonNode) list) : JsonNode =
        let output = JsonObject()
        fields |> List.iter (fun (name, value) -> output[name] <- value)
        output
    let private list (values: JsonNode list) : JsonNode =
        let output = JsonArray()
        values |> List.iter output.Add
        output
    let private strings values = values |> List.map str |> list
    let renderPacket raw =
        let packet = LocalAgentHandoff.canonicalPacket raw
        obj [ "schemaVersion", str packet.SchemaVersion; "dispatchId", str packet.DispatchId; "attemptId", str packet.AttemptId
              "parentExecutionId", str packet.ParentExecutionId; "childExecutionId", str packet.ChildExecutionId; "workerId", str packet.WorkerId
              "role", str (ExecutionRole.toWire packet.Role); "repositoryIdentity", str packet.RepositoryIdentity; "sourceCommit", str packet.SourceCommit
              "sourceManifestDigest", str packet.SourceManifestDigest; "blueprintDigest", str packet.BlueprintDigest; "groupId", str packet.GroupId; "cohortId", str packet.CohortId;
              "members", packet.Members |> List.map (fun m -> obj [ "workItemId", str m.WorkItemId; "executionId", str m.ExecutionId; "requirementKeys", strings m.RequirementKeys ]) |> list
              "decisionIds", strings packet.DecisionIds; "allowedPaths", strings packet.AllowedPaths
              "prerequisites", packet.Prerequisites |> List.map (fun p -> obj [ "workItemId", str p.WorkItemId; "evidenceDigest", str p.EvidenceDigest ]) |> list
              "acceptance", packet.Acceptance |> List.map (fun a -> obj [ "obligationId", str a.ObligationId; "workItemId", str a.WorkItemId; "requirementKeys", strings a.RequirementKeys; "validatorId", str a.ValidatorId; "inputDigest", str a.InputDigest ]) |> list
              "authorityRevision", str packet.AuthorityRevision; "receiptDigest", str packet.ReceiptDigest
              "issuedAt", str (packet.IssuedAt.ToString("O")); "expiresAt", str (packet.ExpiresAt.ToString("O"))
              "timeoutSeconds", JsonValue.Create packet.TimeoutSeconds; "maxOutputBytes", JsonValue.Create packet.MaxOutputBytes ]
        |> fun value -> value.ToJsonString()
    let renderResult (result: LocalWorkerResult) =
        let code = function LocalWorkerOutcome.Submitted -> "submitted" | LocalWorkerOutcome.Blocked -> "blocked" | LocalWorkerOutcome.Failed -> "failed"
        obj [ "schemaVersion", str result.SchemaVersion; "dispatchId", str result.DispatchId; "attemptId", str result.AttemptId
              "childExecutionId", str result.ChildExecutionId; "workerId", str result.WorkerId; "packetDigest", str result.PacketDigest
              "outputCommit", str result.OutputCommit; "changedPaths", strings result.ChangedPaths
              "members", result.Members |> List.map (fun m -> obj [ "workItemId", str m.WorkItemId; "executionId", str m.ExecutionId; "changedPaths", strings m.ChangedPaths; "outcome", str (code m.Outcome);
                                                                  "evidence", m.Evidence |> List.map (fun e -> obj [ "obligationId", str e.ObligationId; "artifactPath", str e.ArtifactPath; "digest", str e.Digest ]) |> list ]) |> list ]
        |> fun value -> value.ToJsonString()
