namespace Praxis.Domain.Work

open System
open Praxis.Domain.Execution

/// Untrusted transport; the local controller derives the expected packet from
/// operator delegation and verified ECIR evidence, outside model authority.
type LocalHandoffMember = { WorkItemId: string; ExecutionId: string; RequirementKeys: string list }
type LocalHandoffPrerequisite = { WorkItemId: string; EvidenceDigest: string }
type LocalHandoffAcceptance =
    { ObligationId: string; WorkItemId: string; RequirementKeys: string list
      ValidatorId: string; InputDigest: string }
type LocalWorkerPacket =
    { SchemaVersion: string
      DispatchId: string; AttemptId: string; ParentExecutionId: string; ChildExecutionId: string; WorkerId: string
      Role: ExecutionRole
      RepositoryIdentity: string; SourceCommit: string; SourceManifestDigest: string; BlueprintDigest: string
      GroupId: string; CohortId: string
      Members: LocalHandoffMember list; DecisionIds: string list; AllowedPaths: string list
      Prerequisites: LocalHandoffPrerequisite list; Acceptance: LocalHandoffAcceptance list
      AuthorityRevision: string; ReceiptDigest: string
      IssuedAt: DateTimeOffset; ExpiresAt: DateTimeOffset
      TimeoutSeconds: int; MaxOutputBytes: int }

/// This is a controller observation, not a JSON approval or a new authority.
/// Merely constructing this record does not establish operator authentication.
type LocalHandoffAuthority = { Enabled: bool; Revision: string; ExpectedPacket: LocalWorkerPacket }

[<RequireQualifiedAccess>]
type LocalWorkerOutcome = Submitted | Blocked | Failed

type LocalWorkerEvidence = { ObligationId: string; ArtifactPath: string; Digest: string }
type LocalWorkerMemberResult =
    { WorkItemId: string; ExecutionId: string; ChangedPaths: string list
      Outcome: LocalWorkerOutcome; Evidence: LocalWorkerEvidence list }
type LocalWorkerResult =
    { SchemaVersion: string; DispatchId: string; AttemptId: string; ChildExecutionId: string; WorkerId: string
      PacketDigest: string; OutputCommit: string; ChangedPaths: string list; Members: LocalWorkerMemberResult list }

/// Each result is independently observed by the controller. A worker's exit
/// code, claimed digest or claimed validator success cannot supply these facts.
type LocalAcceptanceObservation =
    { ValidatorId: string; InputDigest: string; Verdict: Result<unit, string> }
type LocalMemberResultObservation =
    { ExecutionId: string; ChangedPaths: Result<string list, string> }
type LocalWorkerResultObservation =
    { WorkerId: string; ChildExecutionId: string; AttemptId: string; ReservedPacketDigest: string
      OutputCommit: string option; SourceIsAncestor: bool
      ChangedPaths: Result<string list, string>
      MemberResults: Map<string, LocalMemberResultObservation>
      Evidence: Map<string, string * string>
      Acceptance: Map<string, LocalAcceptanceObservation> }

/// Successful checking admits a result for later serialized integration. It
/// does not complete a member, integrate a commit, spawn a process or sign.
[<RequireQualifiedAccess>]
type LocalResultDisposition = AwaitingIntegration | NoIntegration

[<RequireQualifiedAccess>]
module LocalAgentHandoff =
    [<Literal>]
    let PacketSchema = "praxis.local-worker-packet/1"
    [<Literal>]
    let ResultSchema = "praxis.local-worker-result/1"

    let private clean (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length <= 1024
        && value = value.Trim() && not (value |> Seq.exists Char.IsControl)
    let private hex length (value: string) =
        not (isNull value) && value.Length = length
        && (value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
    let isCommit value = hex 40 value
    let isDigest (value: string) =
        not (isNull value) && value.StartsWith("sha256:", StringComparison.Ordinal) && hex 64 (value.Substring 7)
    let private unique values = List.length values = (Set.ofList values).Count
    let private identifiers values = List.length values <= 256 && List.forall clean values && unique values

    /// Portable, exact relative resources only in v1; no globs or OS commands.
    /// Git/work/host state cannot be authorized as worker integration output.
    let isResourcePath (value: string) =
        if not (clean value) || value.StartsWith('/') || value.Contains('\\') || value.Contains(':') || value.IndexOfAny([| '*'; '?'; '<'; '>'; '|'; '"' |]) >= 0 then false
        else
            let parts = value.Split('/')
            parts |> Array.forall (fun p -> p <> "" && p <> "." && p <> ".." && not (p.EndsWith '.') && not (p.EndsWith ' '))
            && parts |> Array.forall (fun p -> not ([ ".git"; ".ros"; ".praxis" ] |> List.exists (fun denied -> String.Equals(p, denied, StringComparison.OrdinalIgnoreCase))))
            && not (String.Equals(value, "ros.json", StringComparison.OrdinalIgnoreCase))
    let private paths values =
        identifiers values && List.forall isResourcePath values
        && unique (values |> List.map _.ToUpperInvariant())

    /// Set-valued lists have stable ordering. Identity is never synthesized.
    let canonicalPacket (packet: LocalWorkerPacket) =
        { packet with
            Members = packet.Members |> List.map (fun m -> { m with RequirementKeys = List.sort m.RequirementKeys }) |> List.sortBy _.WorkItemId
            DecisionIds = List.sort packet.DecisionIds; AllowedPaths = List.sort packet.AllowedPaths
            Prerequisites = List.sortBy _.WorkItemId packet.Prerequisites
            Acceptance = packet.Acceptance |> List.map (fun a -> { a with RequirementKeys = List.sort a.RequirementKeys }) |> List.sortBy _.ObligationId }

    /// Versioned UTF-8 byte-length framing, independent of JSON property order.
    /// This fingerprints assignment identity; it is not a signature or authority.
    let packetDigest packet =
        let packet = canonicalPacket packet
        let buffer = System.Text.StringBuilder()
        let frame (value: string) =
            buffer.Append(System.Text.Encoding.UTF8.GetByteCount value).Append(':').Append(value).Append('|') |> ignore
        let frames values = frame (string (List.length values)); values |> List.iter frame
        frame "praxis.local-worker-packet-identity/1"
        frames [ packet.SchemaVersion; packet.DispatchId; packet.AttemptId; packet.ParentExecutionId; packet.ChildExecutionId
                 packet.WorkerId; ExecutionRole.toWire packet.Role; packet.RepositoryIdentity; packet.SourceCommit
                 packet.SourceManifestDigest; packet.BlueprintDigest; packet.GroupId; packet.CohortId; packet.AuthorityRevision
                 packet.ReceiptDigest; packet.IssuedAt.ToString("O"); packet.ExpiresAt.ToString("O")
                 string packet.TimeoutSeconds; string packet.MaxOutputBytes ]
        frame (string packet.Members.Length)
        for memberItem in packet.Members do frame memberItem.WorkItemId; frame memberItem.ExecutionId; frames memberItem.RequirementKeys
        frames packet.DecisionIds
        frames packet.AllowedPaths
        frame (string packet.Prerequisites.Length)
        for prerequisite in packet.Prerequisites do frame prerequisite.WorkItemId; frame prerequisite.EvidenceDigest
        frame (string packet.Acceptance.Length)
        for obligation in packet.Acceptance do
            frames [ obligation.ObligationId; obligation.WorkItemId; obligation.ValidatorId; obligation.InputDigest ]
            frames obligation.RequirementKeys
        "sha256:" + (System.Text.Encoding.UTF8.GetBytes(buffer.ToString()) |> System.Security.Cryptography.SHA256.HashData |> Convert.ToHexString).ToLowerInvariant()

    let packetProblems (packet: LocalWorkerPacket) =
        let members = packet.Members |> List.map _.WorkItemId
        let sources = packet.Members |> List.collect _.RequirementKeys
        let acceptanceIds = packet.Acceptance |> List.map _.ObligationId
        [ if packet.SchemaVersion <> PacketSchema then yield "local-packet-schema"
          if not ([ packet.DispatchId; packet.AttemptId; packet.ParentExecutionId; packet.ChildExecutionId; packet.WorkerId
                    packet.RepositoryIdentity; packet.GroupId; packet.CohortId; packet.AuthorityRevision ] |> List.forall clean)
             || packet.ParentExecutionId = packet.ChildExecutionId then yield "local-packet-identity"
          if not (isCommit packet.SourceCommit && isDigest packet.SourceManifestDigest && isDigest packet.BlueprintDigest && isDigest packet.ReceiptDigest) then yield "local-packet-input-digest"
          if packet.Members.IsEmpty || not (identifiers members) || sources.Length > 4096 || not (unique sources)
             || packet.Members |> List.exists (fun m -> m.RequirementKeys.IsEmpty || not (identifiers m.RequirementKeys)) then yield "local-packet-source-coverage"
          if not (identifiers (packet.Members |> List.map _.ExecutionId)) then yield "local-packet-member-executions"
          if packet.DecisionIds.IsEmpty || not (identifiers packet.DecisionIds) then yield "local-packet-decisions"
          if not (paths packet.AllowedPaths) then yield "local-packet-resources"
          match packet.Role with
          | ExecutionRole.Implementation when packet.AllowedPaths.IsEmpty -> yield "local-packet-mutation-boundary"
          | ExecutionRole.Verification | ExecutionRole.Review when not packet.AllowedPaths.IsEmpty -> yield "local-packet-readonly-role"
          | ExecutionRole.Implementation | ExecutionRole.Verification | ExecutionRole.Review -> ()
          | _ -> yield "local-packet-worker-role"
          if not (identifiers (packet.Prerequisites |> List.map _.WorkItemId))
             || packet.Prerequisites |> List.exists (fun p -> List.contains p.WorkItemId members || not (isDigest p.EvidenceDigest)) then yield "local-packet-prerequisites"
          if packet.Acceptance.IsEmpty || not (identifiers acceptanceIds)
             || packet.Acceptance |> List.exists (fun a ->
                 not (clean a.ValidatorId && isDigest a.InputDigest) || a.RequirementKeys.IsEmpty || not (identifiers a.RequirementKeys)
                 || not (packet.Members |> List.exists (fun m -> m.WorkItemId = a.WorkItemId && Set.isSubset (Set.ofList a.RequirementKeys) (Set.ofList m.RequirementKeys)))) then yield "local-packet-acceptance"
          for memberItem in packet.Members do
              let covered = packet.Acceptance |> List.filter (fun a -> a.WorkItemId = memberItem.WorkItemId) |> List.collect _.RequirementKeys |> Set.ofList
              if covered <> Set.ofList memberItem.RequirementKeys then yield "local-packet-acceptance-coverage:" + memberItem.WorkItemId
          if packet.IssuedAt.Offset <> TimeSpan.Zero || packet.ExpiresAt.Offset <> TimeSpan.Zero
             || packet.ExpiresAt <= packet.IssuedAt || packet.ExpiresAt - packet.IssuedAt > TimeSpan.FromHours 24. then yield "local-packet-time-window"
          if packet.TimeoutSeconds <= 0 || packet.TimeoutSeconds > 86400 || packet.MaxOutputBytes <= 0 || packet.MaxOutputBytes > 67108864 then yield "local-packet-budget" ]

    /// Re-read controller authority and dependency evidence at every boundary.
    /// This is a pure check; callers must authenticate/protect the observations.
    let validatePacket (authority: LocalHandoffAuthority) now (prerequisites: Map<string, string>) packet =
        let errors =
            [ yield! packetProblems packet
              if not (packetProblems authority.ExpectedPacket).IsEmpty then yield "local-controller-packet-invalid"
              if not authority.Enabled then yield "local-authority-disabled"
              if not (clean authority.Revision) || authority.Revision <> packet.AuthorityRevision then yield "local-authority-revision"
              if canonicalPacket packet <> canonicalPacket authority.ExpectedPacket then yield "local-packet-not-delegated"
              if now < packet.IssuedAt || now >= packet.ExpiresAt then yield "local-packet-expired-or-future"
              for dependency in packet.Prerequisites do
                  if Map.tryFind dependency.WorkItemId prerequisites <> Some dependency.EvidenceDigest then yield "local-prerequisite-unverified:" + dependency.WorkItemId ]
        if errors.IsEmpty then Ok() else Error errors

    let resultProblems (result: LocalWorkerResult) =
        let evidence = result.Members |> List.collect _.Evidence
        [ if result.SchemaVersion <> ResultSchema then yield "local-result-schema"
          if not ([ result.DispatchId; result.AttemptId; result.ChildExecutionId; result.WorkerId ] |> List.forall clean) then yield "local-result-identity-shape"
          if not (isCommit result.OutputCommit && isDigest result.PacketDigest) then yield "local-result-digest-shape"
          if not (paths result.ChangedPaths) then yield "local-result-resource-shape"
          if result.Members.IsEmpty || not (identifiers (result.Members |> List.map _.WorkItemId)) then yield "local-result-member-shape"
          if not (identifiers (result.Members |> List.map _.ExecutionId))
             || result.Members |> List.exists (fun m -> not (paths m.ChangedPaths)) then yield "local-result-member-attribution-shape"
          if Set.ofList result.ChangedPaths <> (result.Members |> List.collect _.ChangedPaths |> Set.ofList) then yield "local-result-attribution-coverage"
          if not (identifiers (evidence |> List.map _.ObligationId)) then yield "local-result-evidence-shape"
          if evidence |> List.exists (fun claim -> not (isResourcePath claim.ArtifactPath && isDigest claim.Digest)) then yield "local-result-artifact-shape" ]

    /// Admit exact member results only after fresh packet checking and host
    /// evidence observation. Caller must still serialize/revalidate integration.
    let validateResult authority now prerequisites (packet: LocalWorkerPacket) (observed: LocalWorkerResultObservation) (result: LocalWorkerResult) =
        let submitted = result.Members |> List.filter (fun m -> m.Outcome = LocalWorkerOutcome.Submitted)
        let allEvidence = result.Members |> List.collect _.Evidence
        let errors =
            [ yield! resultProblems result
              match validatePacket authority now prerequisites packet with
              | Error problems -> yield! problems
              | Ok() -> ()
              if result.SchemaVersion <> ResultSchema then yield "local-result-schema"
              if result.DispatchId <> packet.DispatchId || result.AttemptId <> packet.AttemptId
                 || result.ChildExecutionId <> packet.ChildExecutionId || result.WorkerId <> packet.WorkerId
                 || observed.WorkerId <> packet.WorkerId || observed.ChildExecutionId <> packet.ChildExecutionId
                 || observed.AttemptId <> packet.AttemptId then yield "local-result-identity"
              if not (isDigest result.PacketDigest) || result.PacketDigest <> observed.ReservedPacketDigest
                 || not (packetProblems packet).IsEmpty || result.PacketDigest <> packetDigest packet then yield "local-result-packet-digest"
              if not (isCommit result.OutputCommit) || observed.OutputCommit <> Some result.OutputCommit || not observed.SourceIsAncestor then yield "local-result-commit-unverified"
              if not (paths result.ChangedPaths) || not (Set.isSubset (Set.ofList result.ChangedPaths) (Set.ofList packet.AllowedPaths)) then yield "local-result-resource-escape"
              match observed.ChangedPaths with
              | Error _ -> yield "local-result-paths-unavailable"
              | Ok actual when not (paths actual) || Set.ofList actual <> Set.ofList result.ChangedPaths -> yield "local-result-paths-mismatch"
              | Ok _ -> ()
              let memberIds = result.Members |> List.map _.WorkItemId
              if not (identifiers memberIds) || Set.ofList memberIds <> (packet.Members |> List.map _.WorkItemId |> Set.ofList) then yield "local-result-members-not-exact"
              if allEvidence.Length > 256 || not (identifiers (allEvidence |> List.map _.ObligationId)) then yield "local-result-evidence-duplicated"
              for memberResult in result.Members do
                  match packet.Members |> List.tryFind (fun m -> m.WorkItemId = memberResult.WorkItemId), Map.tryFind memberResult.WorkItemId observed.MemberResults with
                  | Some assigned, Some actual when assigned.ExecutionId = memberResult.ExecutionId && actual.ExecutionId = assigned.ExecutionId ->
                      match actual.ChangedPaths with
                      | Ok actualPaths when paths actualPaths && Set.ofList actualPaths = Set.ofList memberResult.ChangedPaths -> ()
                      | _ -> yield "local-result-member-paths-unverified:" + memberResult.WorkItemId
                  | _ -> yield "local-result-member-execution-unverified:" + memberResult.WorkItemId
                  let required = packet.Acceptance |> List.filter (fun a -> a.WorkItemId = memberResult.WorkItemId)
                  let claims = memberResult.Evidence |> List.map _.ObligationId |> Set.ofList
                  let expected = required |> List.map _.ObligationId |> Set.ofList
                  if not (Set.isSubset claims expected) || (memberResult.Outcome = LocalWorkerOutcome.Submitted && claims <> expected) then yield "local-result-obligations-not-exact:" + memberResult.WorkItemId
                  for claim in memberResult.Evidence do
                      if not (isResourcePath claim.ArtifactPath && isDigest claim.Digest)
                         || Map.tryFind claim.ObligationId observed.Evidence <> Some(claim.ArtifactPath, claim.Digest) then yield "local-result-evidence-unverified:" + claim.ObligationId
                  if memberResult.Outcome = LocalWorkerOutcome.Submitted then
                      for obligation in required do
                          match Map.tryFind obligation.ObligationId observed.Acceptance with
                          | Some check when check.ValidatorId = obligation.ValidatorId && check.InputDigest = obligation.InputDigest && check.Verdict = Ok() -> ()
                          | _ -> yield "local-result-acceptance-unverified:" + obligation.ObligationId ]
        if not errors.IsEmpty then Error errors
        elif submitted.Length = result.Members.Length then Ok LocalResultDisposition.AwaitingIntegration
        else Ok LocalResultDisposition.NoIntegration
