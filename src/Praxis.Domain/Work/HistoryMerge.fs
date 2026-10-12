namespace Praxis.Domain.Work

open System

/// Values are opaque, normalized documents. Parsing and effects belong to adapters.
type WorkHistorySnapshot =
    { Metadata: Map<string, string>
      UpdatedAt: DateTimeOffset
      Items: (string * string) list
      Events: (string * string) list }

[<RequireQualifiedAccess>]
module WorkHistoryMerge =
    let private unique label values =
        if values |> List.exists (fun (key, _) -> String.IsNullOrWhiteSpace key) then
            Error $"{label}: missing identity"
        elif values |> List.map fst |> List.distinct |> List.length <> values.Length then
            Error $"{label}: duplicate identity"
        else Ok(Map.ofList values)

    let private mergeMap label (baselines: Map<string, string> list) (left: Map<string, string>) (right: Map<string, string>) =
        let keys = (baselines @ [ left; right ]) |> List.collect (Map.toList >> List.map fst) |> Set.ofList
        let rec loop remaining result =
            match remaining with
            | [] -> Ok result
            | key :: rest ->
                let versions = baselines |> List.map (Map.tryFind key) |> Set.ofList
                let l, r = Map.tryFind key left, Map.tryFind key right
                let selected =
                    if l = r then Ok l
                    elif versions.Contains l && not (versions.Contains r) then Ok r
                    elif versions.Contains r && not (versions.Contains l) then Ok l
                    else Error $"{label}: divergent identity {key}"
                match selected with
                | Error error -> Error error
                | Ok None -> loop rest result
                | Ok(Some value) -> loop rest (Map.add key value result)
        loop (Set.toList keys) Map.empty

    // Retain main's deterministic ordering graph; every branch edge must survive.
    let private mergeEventOrder (left: string list) (right: string list) =
        let order = (left @ right) |> List.distinct
        let ranks = order |> List.mapi (fun rank id -> id, rank) |> Map.ofList
        let edges = [left; right] |> List.collect List.pairwise |> List.distinct
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
                let targets = Map.tryFind id successors |> Option.defaultValue Set.empty
                let nextCounts, nextReady = targets |> Set.fold (fun ((counts: Map<string, int>), ready) target ->
                    let count = counts[target] - 1
                    Map.add target count counts, (if count = 0 then Set.add (ranks[target], target) ready else ready)) (counts, Set.remove (rank, id) ready)
                sort nextReady nextCounts (id :: result)
        sort ready counts []

    /// Only merges independent updates. Never chooses a lifecycle state by timestamp.
    let private planWith canonicalIncoming (baselines: WorkHistorySnapshot list) (left: WorkHistorySnapshot) (right: WorkHistorySnapshot) =
        let validate () = (baselines @ [left; right]) |> List.fold (fun acc snapshot ->
            acc |> Result.bind (fun maps ->
                unique "snapshot items" snapshot.Items |> Result.bind (fun items ->
                    unique "snapshot events" snapshot.Events |> Result.map (fun events -> maps @ [items, events])))) (Ok [])
        if baselines.IsEmpty || baselines.Length > 32 then Error "history requires one to 32 real merge bases"
        else validate() |> Result.bind (fun maps ->
                let baseMaps = maps |> List.take baselines.Length
                let li, le = maps[baselines.Length]
                let ri, re = maps[baselines.Length + 1]
                if (baselines @ [left; right]) |> List.exists (fun s -> s.UpdatedAt.Offset <> TimeSpan.Zero) then
                    Error "updatedAt must be UTC"
                elif baselines |> List.exists (fun baseline -> left.UpdatedAt < baseline.UpdatedAt || right.UpdatedAt < baseline.UpdatedAt) then
                    Error "updatedAt predates base history"
                elif baselines |> List.exists (fun baseline ->
                    Map.tryFind "repository" baseline.Metadata <> Map.tryFind "repository" left.Metadata
                    || Map.tryFind "repository" baseline.Metadata <> Map.tryFind "repository" right.Metadata) then
                    Error "repository identity changed"
                elif baselines |> List.collect _.Items |> List.exists (fun (id, _) -> not (li.ContainsKey id && ri.ContainsKey id)) then
                    Error "work-item deletion is not supported"
                elif List.zip baselines baseMaps |> List.exists (fun (baseline, (_, be)) ->
                    (left.Events |> List.filter (fun (id, _) -> be.ContainsKey id)) <> baseline.Events
                    || (right.Events |> List.filter (fun (id, _) -> be.ContainsKey id)) <> baseline.Events) then
                    Error "base event history was removed, rewritten or reordered"
                else
                    match mergeMap "metadata" (baselines |> List.map _.Metadata) left.Metadata right.Metadata,
                          mergeMap "work item" (baseMaps |> List.map fst) li ri,
                          mergeMap "event" [Map.empty] le re with
                    | Ok metadata, Ok items, Ok events ->
                        let itemOrder = ((baselines |> List.collect _.Items) @ left.Items @ right.Items) |> List.map fst |> List.distinct
                        let incomingIds = right.Events |> List.map fst
                        let ordering =
                            if canonicalIncoming then
                                let incoming = Set.ofList incomingIds
                                Ok(incomingIds @ (left.Events |> List.map fst |> List.filter (incoming.Contains >> not)))
                            else mergeEventOrder (List.map fst left.Events) incomingIds
                        match ordering with
                        | Error error -> Error error
                        | Ok eventOrder ->
                            Ok { Metadata = metadata; UpdatedAt = max left.UpdatedAt right.UpdatedAt
                                 Items = itemOrder |> List.map (fun id -> id, items[id])
                                 Events = eventOrder |> List.map (fun id -> id, events[id]) }
                    | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error)

    let planMany baselines left right = planWith false baselines left right

    /// Explicit planning policy, not write authority. Adapters must bind owner
    /// approval, exact commits, independent scopes and an auditable transaction.
    let planCanonicalIncoming baselines left right = planWith true baselines left right

    let plan baseline left right = planMany [baseline] left right
