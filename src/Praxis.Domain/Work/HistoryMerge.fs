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

    let private mergeMap label (baseline: Map<string, string>) (left: Map<string, string>) (right: Map<string, string>) =
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

    // Shared observations are anchors. Independent gaps have no causal order
    // across branches, so retain the existing deterministic left-before-right tie.
    let private mergeEventOrder (left: string list) (right: string list) =
        let shared = Set.intersect (Set.ofList left) (Set.ofList right)
        let anchors = left |> List.filter shared.Contains
        if anchors <> (right |> List.filter shared.Contains) then
            Error "event histories impose conflicting order"
        else
            let rec weave remaining l r reversed =
                match remaining with
                | [] -> Ok(List.rev reversed @ l @ r)
                | anchor :: rest ->
                    let ln = l |> List.findIndex ((=) anchor)
                    let rn = r |> List.findIndex ((=) anchor)
                    let gap = List.take ln l @ List.take rn r @ [anchor]
                    weave rest (List.skip (ln + 1) l) (List.skip (rn + 1) r) (List.rev gap @ reversed)
            weave anchors left right []

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
                        match mergeEventOrder (List.map fst left.Events) (List.map fst right.Events) with
                        | Error error -> Error error
                        | Ok eventOrder ->
                            Ok { Metadata = metadata; UpdatedAt = max left.UpdatedAt right.UpdatedAt
                                 Items = itemOrder |> List.map (fun id -> id, items[id])
                                 Events = eventOrder |> List.map (fun id -> id, events[id]) }
                    | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error
            | Error error, _, _, _, _, _ | _, Error error, _, _, _, _ | _, _, Error error, _, _, _
            | _, _, _, Error error, _, _ | _, _, _, _, Error error, _ | _, _, _, _, _, Error error -> Error error
        merge ()
