namespace Praxis.Domain.Work

open System

/// Values are opaque, normalized documents. Parsing and effects belong to adapters.
type WorkHistorySnapshot =
    { Metadata: Map<string, string>
      UpdatedAt: DateTimeOffset
      Items: (string * string) list
      Events: (string * string) list }

/// Opaque, normalized `queue.json` values. `Reidentified` names the ids this
/// side's own rows declare as `reidentifiedFrom`: the only native backlog
/// operation that removes an id (`work reidentify`).
type WorkBacklogSnapshot =
    { Metadata: Map<string, string>
      NextSeq: int
      Items: (string * string) list
      Reidentified: Set<string> }

[<RequireQualifiedAccess>]
module WorkHistoryMerge =
    let internal unique label values =
        if values |> List.exists (fun (key, _) -> String.IsNullOrWhiteSpace key) then
            Error $"{label}: missing identity"
        elif values |> List.map fst |> List.distinct |> List.length <> values.Length then
            Error $"{label}: duplicate identity"
        else Ok(Map.ofList values)

    let internal mergeMap label (baseline: Map<string, string>) (left: Map<string, string>) (right: Map<string, string>) =
        let keys = [ baseline; left; right ] |> List.collect (Map.toList >> List.map fst) |> Set.ofList
        let rec loop remaining result =
            match remaining with
            | [] -> Ok result
            | key :: rest ->
                let b, l, r = Map.tryFind key baseline, Map.tryFind key left, Map.tryFind key right
                let selected = if l = r then Ok l elif l = b then Ok r elif r = b then Ok l else Error $"{label}: divergent identity {key}"
                match selected with
                | Error error -> Error error
                | Ok None -> loop rest result
                | Ok(Some value) -> loop rest (Map.add key value result)
        loop (Set.toList keys) Map.empty

    let private eventOrder (left: (string * string) list) (right: (string * string) list) =
        let order = (left @ right) |> List.map fst |> List.distinct
        let ranks = order |> List.mapi (fun rank id -> id, rank) |> Map.ofList
        let edges = [left; right] |> List.collect (List.map fst >> List.pairwise) |> List.distinct
        let successors = edges |> List.fold (fun map (before, after) ->
            let targets = Map.tryFind before map |> Option.defaultValue Set.empty
            Map.add before (Set.add after targets) map) Map.empty
        let counts = edges |> List.fold (fun (map: Map<string, int>) (_, after) -> Map.add after (map[after] + 1) map) (order |> List.map (fun id -> id, 0) |> Map.ofList)
        let ready = counts |> Map.toList |> List.choose (fun (id, count) -> if count = 0 then Some(ranks[id], id) else None) |> Set.ofList
        let rec sort ready (counts: Map<string, int>) result =
            if Set.isEmpty ready then
                if List.length result = order.Length then Ok(List.rev result)
                else Error "event histories have incompatible ordering (cycle)"
            else
                let rank, id = Set.minElement ready
                let nextReady = Set.remove (rank, id) ready
                let targets = Map.tryFind id successors |> Option.defaultValue Set.empty
                let nextCounts, nextReady = targets |> Set.fold (fun ((counts: Map<string, int>), ready) target ->
                    let count = counts[target] - 1
                    Map.add target count counts, (if count = 0 then Set.add (ranks[target], target) ready else ready)) (counts, nextReady)
                sort nextReady nextCounts (id :: result)
        sort ready counts []

    /// Only merges independent updates. Never chooses a lifecycle state by timestamp.
    let plan (baseline: WorkHistorySnapshot) (left: WorkHistorySnapshot) (right: WorkHistorySnapshot) =
        let merge () =
            match unique "base items" baseline.Items, unique "left items" left.Items, unique "right items" right.Items,
                  unique "base events" baseline.Events, unique "left events" left.Events, unique "right events" right.Events with
            | Ok bi, Ok li, Ok ri, Ok be, Ok le, Ok re ->
                if [baseline; left; right] |> List.exists (fun s -> s.UpdatedAt.Offset <> TimeSpan.Zero) then
                    Error "updatedAt must be UTC"
                elif left.UpdatedAt < baseline.UpdatedAt || right.UpdatedAt < baseline.UpdatedAt then
                    Error "updatedAt predates base history"
                elif Map.tryFind "repository" baseline.Metadata <> Map.tryFind "repository" left.Metadata
                     || Map.tryFind "repository" baseline.Metadata <> Map.tryFind "repository" right.Metadata then
                    Error "repository identity changed"
                elif baseline.Items |> List.exists (fun (id, _) -> not (li.ContainsKey id && ri.ContainsKey id)) then
                    Error "work-item deletion is not supported"
                elif (left.Events |> List.filter (fun (id, _) -> be.ContainsKey id)) <> baseline.Events
                     || (right.Events |> List.filter (fun (id, _) -> be.ContainsKey id)) <> baseline.Events then
                    Error "base event history was removed, rewritten or reordered"
                else
                    match mergeMap "metadata" baseline.Metadata left.Metadata right.Metadata,
                          mergeMap "work item" bi li ri,
                          mergeMap "event" Map.empty le re with
                    | Ok metadata, Ok items, Ok events ->
                        let itemOrder = (baseline.Items @ left.Items @ right.Items) |> List.map fst |> List.distinct
                        match eventOrder left.Events right.Events with
                        | Error error -> Error error
                        | Ok eventOrder ->
                            Ok { Metadata = metadata; UpdatedAt = max left.UpdatedAt right.UpdatedAt
                                 Items = itemOrder |> List.map (fun id -> id, items[id])
                                 Events = eventOrder |> List.map (fun id -> id, events[id]) }
                    | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error
            | Error error, _, _, _, _, _ | _, Error error, _, _, _, _ | _, _, Error error, _, _, _
            | _, _, _, Error error, _, _ | _, _, _, _, Error error, _ | _, _, _, _, _, Error error -> Error error
        merge ()

[<RequireQualifiedAccess>]
module WorkBacklogMerge =
    /// Unions independent backlog rows. A row both sides changed differently is
    /// refused by id; a base row may disappear only through that side's own
    /// new native reidentification while the other side left it unchanged.
    /// `nextSeq` is never lowered; rows keep base order, then left additions,
    /// then right additions, matching the append-only writer.
    let plan (baseline: WorkBacklogSnapshot) (left: WorkBacklogSnapshot) (right: WorkBacklogSnapshot) =
        match WorkHistoryMerge.unique "base backlog rows" baseline.Items,
              WorkHistoryMerge.unique "left backlog rows" left.Items,
              WorkHistoryMerge.unique "right backlog rows" right.Items with
        | Ok bi, Ok li, Ok ri ->
            let removedWithoutNativeEvidence (side: WorkBacklogSnapshot) (items: Map<string, string>) =
                baseline.Items |> List.tryPick (fun (id, _) ->
                    if items.ContainsKey id || (side.Reidentified.Contains id && not (baseline.Reidentified.Contains id)) then None else Some id)
            if left.Metadata <> baseline.Metadata || right.Metadata <> baseline.Metadata then
                Error "backlog metadata (schemaVersion, repository) changed"
            else
                match removedWithoutNativeEvidence left li, removedWithoutNativeEvidence right ri with
                | Some id, _ | _, Some id -> Error $"backlog row deletion is not supported: {id}"
                | None, None ->
                    match WorkHistoryMerge.mergeMap "backlog row" bi li ri with
                    | Error error -> Error error
                    | Ok items ->
                        let order = (baseline.Items @ left.Items @ right.Items) |> List.map fst |> List.distinct |> List.filter items.ContainsKey
                        Ok { Metadata = baseline.Metadata
                             NextSeq = List.max [ baseline.NextSeq; left.NextSeq; right.NextSeq ]
                             Items = order |> List.map (fun id -> id, items[id])
                             Reidentified = Set.union left.Reidentified right.Reidentified }
        | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error
