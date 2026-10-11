namespace Praxis.Infrastructure.Planning

open System
open System.Text.Json
open System.Text.Json.Nodes

// Arrays and primitive DTO fields keep the disk contract independent of F#
// option/map runtime serialization. They contain public keys, never private keys.
[<CLIMutable>]
type internal LocalSignerDto = { KeyId: string; Approver: string; PublicKeyPem: string }
[<CLIMutable>]
type internal LocalMemberDto = { WorkItemId: string; RequirementKey: string }
[<CLIMutable>]
type internal LocalGrantDto =
    { GroupId: string; CohortId: string; Commit: string; ManifestPath: string
      BlueprintPath: string; SourceManifestDigest: string; BlueprintDigest: string
      OrdoExecutable: string; OrdoSha256: string; Members: LocalMemberDto array; ReceiptJson: string }
[<CLIMutable>]
type internal LocalPolicyDto =
    { Revision: string; RepositoryRoot: string; QualifiedRelease: bool
      Signers: LocalSignerDto array; Grants: LocalGrantDto array }
[<CLIMutable>]
type internal LocalAuditDto =
    { Id: string; Action: string; Approver: string; KeyId: string; GroupId: string
      PreviousRevision: string; NewRevision: string; OccurredAt: string; ReceiptSha256: string }
[<CLIMutable>]
type internal LocalPolicyStateDto =
    { SchemaVersion: string; InitialRevision: string; Policy: LocalPolicyDto; Audit: LocalAuditDto array }

[<RequireQualifiedAccess>]
module internal LocalEcirPolicyJson =
    let private options =
        let resolver = Serialization.Metadata.DefaultJsonTypeInfoResolver()
        let dtoTypes =
            [ typeof<LocalSignerDto>; typeof<LocalMemberDto>; typeof<LocalGrantDto>
              typeof<LocalPolicyDto>; typeof<LocalAuditDto>; typeof<LocalPolicyStateDto> ]
        resolver.Modifiers.Add(fun info ->
            if info.Kind = Serialization.Metadata.JsonTypeInfoKind.Object
               && List.contains info.Type dtoTypes then
                info.CreateObject <- fun () -> Activator.CreateInstance(info.Type, true)
                info.Properties.Clear()
                for property in info.Type.GetProperties(Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic) do
                    let field = info.CreateJsonPropertyInfo(property.PropertyType, property.Name)
                    field.Get <- fun instance -> property.GetValue instance
                    field.Set <- fun instance value -> property.SetValue(instance, value)
                    info.Properties.Add field)
        JsonSerializerOptions(TypeInfoResolver = resolver)
    let private policyDto (p: EcirHostPolicy) : LocalPolicyDto =
        { Revision = p.Revision; RepositoryRoot = p.RepositoryRoot; QualifiedRelease = p.QualifiedRelease
          Signers = p.TrustedSigners |> Map.toArray |> Array.map (fun (key, s) ->
              { KeyId = key; Approver = s.Approver; PublicKeyPem = s.PublicKeyPem })
          Grants = p.Grants |> List.map (fun g ->
              { GroupId = g.GroupId; CohortId = g.CohortId; Commit = g.Source.Commit
                ManifestPath = g.Source.ManifestPath; BlueprintPath = g.Source.BlueprintPath
                SourceManifestDigest = g.Source.SourceManifestDigest; BlueprintDigest = g.BlueprintDigest
                OrdoExecutable = g.Ordo.Executable; OrdoSha256 = g.Ordo.Sha256; ReceiptJson = g.ReceiptJson
                Members = g.MemberRequirements |> Map.toArray |> Array.map (fun (id, key) ->
                    { WorkItemId = id; RequirementKey = key }) }) |> List.toArray }

    let private auditDto (a: EcirHostAudit) : LocalAuditDto =
        { Id = a.Id; Action = a.Action; Approver = a.Approver; KeyId = a.KeyId
          GroupId = Option.toObj a.GroupId; PreviousRevision = a.PreviousRevision
          NewRevision = a.NewRevision; OccurredAt = a.OccurredAt.ToString("O")
          ReceiptSha256 = Option.toObj a.ReceiptSha256 }

    let render initial policy audit =
        JsonSerializer.Serialize(
            { SchemaVersion = "praxis.local-ecir-policy/1"; InitialRevision = initial
              Policy = policyDto policy; Audit = audit |> List.map auditDto |> List.toArray }, options)

    let private required text =
        if String.IsNullOrWhiteSpace text || text <> text.Trim() || text |> Seq.exists Char.IsControl then
            invalidOp "missing or malformed host policy identity"
        text

    let private unique (values: string array) =
        values |> Array.iter (required >> ignore)
        if values.Length <> (Set.ofArray values).Count then invalidOp "duplicate host policy identity"

    let private noDuplicates element =
        let rec walk (value: JsonElement) =
            match value.ValueKind with
            | JsonValueKind.Object ->
                let fields = value.EnumerateObject() |> Seq.toArray
                unique (fields |> Array.map _.Name)
                fields |> Array.iter (fun field -> walk field.Value)
            | JsonValueKind.Array -> value.EnumerateArray() |> Seq.iter walk
            | _ -> ()
        walk element

    let read (text: string) =
        try
            if Text.Encoding.UTF8.GetByteCount text > 4194304 then invalidOp "oversized host policy"
            use document = JsonDocument.Parse(text, JsonDocumentOptions(MaxDepth = 16))
            noDuplicates document.RootElement
            let dto = JsonSerializer.Deserialize<LocalPolicyStateDto>(text, options)
            if not (JsonNode.DeepEquals(JsonNode.Parse text, JsonSerializer.SerializeToNode(dto, options))) then
                invalidOp "unknown or missing host policy fields"
            if dto.SchemaVersion <> "praxis.local-ecir-policy/1" then invalidOp "unsupported host policy schema"
            let p = dto.Policy
            required p.Revision |> ignore
            required p.RepositoryRoot |> ignore
            if not (IO.Path.IsPathFullyQualified p.RepositoryRoot) then invalidOp "host policy repository must be absolute"
            if p.Signers.Length > 256 || p.Grants.Length > 256 || dto.Audit.Length > 4096 then invalidOp "host policy inventory exceeds limits"
            unique (p.Signers |> Array.map _.KeyId)
            unique (p.Grants |> Array.map _.GroupId)
            let policy : EcirHostPolicy =
                { Revision = p.Revision; RepositoryRoot = p.RepositoryRoot; QualifiedRelease = p.QualifiedRelease
                  TrustedSigners = p.Signers |> Array.map (fun s ->
                      if String.IsNullOrWhiteSpace s.PublicKeyPem || s.PublicKeyPem.Length > 8192 then invalidOp "missing or oversized signer public key"
                      use key = Security.Cryptography.ECDsa.Create()
                      key.ImportFromPem s.PublicKeyPem
                      if s.PublicKeyPem <> key.ExportSubjectPublicKeyInfoPem() then invalidOp "signer must contain only canonical public PEM"
                      s.KeyId, { Approver = required s.Approver; PublicKeyPem = s.PublicKeyPem }) |> Map.ofArray
                  Grants = p.Grants |> Array.map (fun g ->
                      unique (g.Members |> Array.map _.WorkItemId)
                      unique (g.Members |> Array.map _.RequirementKey)
                      if g.Members.Length = 0 || g.Members.Length > 256 then invalidOp "host grant member bounds"
                      { GroupId = g.GroupId; CohortId = required g.CohortId
                        Source = { Commit = required g.Commit; ManifestPath = required g.ManifestPath
                                   BlueprintPath = required g.BlueprintPath; SourceManifestDigest = required g.SourceManifestDigest }
                        BlueprintDigest = required g.BlueprintDigest
                        Ordo = { Executable = required g.OrdoExecutable; Sha256 = required g.OrdoSha256 }
                        MemberRequirements = g.Members |> Array.map (fun m -> m.WorkItemId, m.RequirementKey) |> Map.ofArray
                        ReceiptJson = if isNull g.ReceiptJson then invalidOp "missing host receipt" else g.ReceiptJson }) |> Array.toList }
            let mutable revision = required dto.InitialRevision
            let revisions = Collections.Generic.HashSet<string>()
            revisions.Add revision |> ignore
            unique (dto.Audit |> Array.map _.Id)
            let mutable lastTime = DateTimeOffset.MinValue
            let audit : EcirHostAudit list =
                dto.Audit
                |> Array.map (fun a ->
                    if a.PreviousRevision <> revision || not (revisions.Add(required a.NewRevision)) then
                        invalidOp "host policy audit revision chain differs"
                    revision <- a.NewRevision
                    let at = DateTimeOffset.Parse a.OccurredAt
                    if at.Offset <> TimeSpan.Zero || at < lastTime then invalidOp "host audit times must be ordered UTC"
                    lastTime <- at
                    let entry : EcirHostAudit =
                        { Id = a.Id; Action = required a.Action; Approver = required a.Approver; KeyId = required a.KeyId
                          GroupId = Option.ofObj a.GroupId; PreviousRevision = a.PreviousRevision; NewRevision = a.NewRevision
                          OccurredAt = at; ReceiptSha256 = Option.ofObj a.ReceiptSha256 }
                    entry)
                |> Array.toList
            if revision <> policy.Revision then invalidOp "host policy does not match final audit revision"
            Ok(dto.InitialRevision, policy, audit)
        with e -> Error("local host policy refused: " + e.GetType().Name + ": " + e.Message)
