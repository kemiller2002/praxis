namespace Praxis.Domain.Work

open System

type LocalHandoffDependencyExplanation =
    { WorkItemId: string; EvidenceDigest: string; ProposedProducers: string list }
type LocalHandoffConflict =
    { LeftDispatchId: string; RightDispatchId: string; Paths: string list }
type LocalHandoffAssignmentExplanation =
    { Packet: LocalWorkerPacket; PacketDigest: string option
      Dependencies: LocalHandoffDependencyExplanation list; BlockingReasons: string list }
type LocalHandoffExplanation =
    { AsOf: DateTimeOffset; Assignments: LocalHandoffAssignmentExplanation list
      WriteConflicts: LocalHandoffConflict list; Limitations: string list }

/// Advisory inspection of proposals only. No approval, scheduling or work-state
/// ports are accepted, so an explanation cannot activate an execution path.
[<RequireQualifiedAccess>]
module LocalHandoffExplanation =
    [<Literal>]
    let MaxPackets = 64

    let explain (asOf: DateTimeOffset) (packets: LocalWorkerPacket list) =
        if packets.IsEmpty || packets.Length > MaxPackets then Error "local explanation requires 1..64 packets"
        elif asOf.Offset <> TimeSpan.Zero then Error "local explanation requires an explicit UTC observation time"
        else
            let indexed = packets |> List.indexed
            let producerIndex =
                indexed |> List.collect (fun (i, p) -> p.Members |> List.map (fun m -> (p.RepositoryIdentity, m.WorkItemId), (i, p)))
                |> List.groupBy fst |> List.map (fun (key, entries) -> key, entries |> List.map snd |> List.distinctBy fst) |> Map.ofList
            let producers (packet: LocalWorkerPacket) workItem =
                Map.tryFind (packet.RepositoryIdentity, workItem) producerIndex |> Option.defaultValue []
            let edges =
                indexed |> List.map (fun (i, p) ->
                    i, p.Prerequisites |> List.collect (fun d -> producers p d.WorkItemId |> List.map fst) |> Set.ofList) |> Map.ofList
            // Bounded iterative reachability avoids recursive stack growth.
            let inCycle start =
                let mutable pending = Map.find start edges
                let mutable visited = Set.empty
                let mutable found = false
                while not pending.IsEmpty && not found do
                    let next = Set.minElement pending
                    pending <- Set.remove next pending
                    if next = start then found <- true
                    elif not (Set.contains next visited) then
                        visited <- Set.add next visited
                        pending <- Set.union pending (Map.find next edges |> Set.filter (fun n -> not (Set.contains n visited)))
                found
            let conflicts =
                [ for i, left in indexed do
                      for j, right in indexed do
                          if i < j && left.RepositoryIdentity = right.RepositoryIdentity then
                              let overlap = left.AllowedPaths |> List.filter (fun path -> right.AllowedPaths |> List.exists (fun other -> String.Equals(path, other, StringComparison.OrdinalIgnoreCase))) |> List.distinct |> List.sort
                              if not overlap.IsEmpty then
                                  yield { LeftDispatchId = left.DispatchId; RightDispatchId = right.DispatchId; Paths = overlap } ]
            let assignments =
                indexed |> List.map (fun (i, packet) ->
                    let intrinsic = LocalAgentHandoff.packetProblems packet
                    let duplicate selector value = indexed |> List.filter (fun (_, p) -> selector p = value) |> List.length > 1
                    let reasons =
                        [ yield! intrinsic
                          yield "local-controller-authority-unobserved"
                          yield "local-worker-adapter-unqualified"
                          if asOf < packet.IssuedAt || asOf >= packet.ExpiresAt then yield "local-packet-expired-or-future"
                          if duplicate _.DispatchId packet.DispatchId then yield "local-plan-duplicate-dispatch"
                          if duplicate _.AttemptId packet.AttemptId then yield "local-plan-duplicate-attempt"
                          if duplicate _.ChildExecutionId packet.ChildExecutionId then yield "local-plan-duplicate-child-execution"
                          if inCycle i then yield "local-plan-dependency-cycle"
                          for j, other in indexed do
                              if i <> j then
                                  let otherMembers = other.Members |> List.map _.WorkItemId |> Set.ofList
                                  let otherSources = other.Members |> List.collect _.RequirementKeys |> Set.ofList
                                  let otherExecutions = other.Members |> List.map _.ExecutionId |> Set.ofList
                                  if packet.WorkerId = other.WorkerId then yield "local-plan-worker-requires-serialization:" + other.DispatchId
                                  if packet.RepositoryIdentity = other.RepositoryIdentity then
                                      if packet.SourceCommit <> other.SourceCommit || packet.SourceManifestDigest <> other.SourceManifestDigest || packet.BlueprintDigest <> other.BlueprintDigest then
                                          yield "local-plan-input-revision-mismatch:" + other.DispatchId
                                      for memberItem in packet.Members do
                                          if Set.contains memberItem.WorkItemId otherMembers then yield "local-plan-duplicate-member:" + memberItem.WorkItemId
                                          for key in memberItem.RequirementKeys do
                                              if Set.contains key otherSources then yield "local-plan-duplicate-source:" + key
                                  for memberItem in packet.Members do
                                      if Set.contains memberItem.ExecutionId otherExecutions then yield "local-plan-duplicate-member-execution:" + memberItem.ExecutionId
                          for dependency in packet.Prerequisites do yield "local-prerequisite-unobserved:" + dependency.WorkItemId
                          for conflict in conflicts do
                              if conflict.LeftDispatchId = packet.DispatchId then yield "local-plan-write-requires-serialization:" + conflict.RightDispatchId
                              elif conflict.RightDispatchId = packet.DispatchId then yield "local-plan-write-requires-serialization:" + conflict.LeftDispatchId ]
                        |> List.distinct |> List.sort
                    { Packet = LocalAgentHandoff.canonicalPacket packet
                      PacketDigest = if intrinsic.IsEmpty then Some(LocalAgentHandoff.packetDigest packet) else None
                      Dependencies = packet.Prerequisites |> List.sortBy _.WorkItemId |> List.map (fun d ->
                          { WorkItemId = d.WorkItemId; EvidenceDigest = d.EvidenceDigest
                            ProposedProducers = producers packet d.WorkItemId |> List.map (snd >> _.DispatchId) |> List.distinct |> List.sort })
                      BlockingReasons = reasons })
            Ok { AsOf = asOf; Assignments = assignments; WriteConflicts = conflicts
                 Limitations = [ "Proposal inspection only; no process or work-state mutation."
                                 "Controller authority, prerequisite evidence and filesystem containment are unobserved."
                                 "Declared producers are proposals, not completed prerequisite evidence."
                                 "Coverage is limited to supplied packets; the complete intake blueprint is unobserved."
                                 "No dispatch order, concurrency reservation or integration is authorized." ] }
