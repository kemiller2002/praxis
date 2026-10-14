namespace Praxis.Domain.Planning

open System
open System.Text.RegularExpressions

/// The dependency graph (PRX-PLAN-044..046), the collision graph kept
/// separately from it (PRX-PLAN-081..083) and context affinity (PRX-PLAN-071).
[<RequireQualifiedAccess>]
module Graph =
    /// The work items an item still waits on through an unresolved hard
    /// work-item dependency. Satisfied dependencies never block (PRX-PLAN-042).
    let openHardPrerequisites (item: ItemAnalysis) =
        item.Dependencies
        |> List.choose (fun resolved ->
            match resolved.Dependency.Target, resolved.Dependency.Kind, resolved.Status with
            | DependencyTarget.WorkItem target, DependencyKind.Hard, status when status <> DependencyStatus.Satisfied -> Some target
            | _ -> None)
        |> Text.distinctOrdinal

    let private pending (items: ItemAnalysis list) =
        items |> List.filter (fun item -> not (PlanningWorkState.isTerminal item.PlanningState))

    let private edges (items: ItemAnalysis list) =
        let ids = items |> List.map (fun item -> item.Id) |> Set.ofList

        items
        |> List.map (fun item -> item.Id, openHardPrerequisites item |> List.filter ids.Contains)
        |> Map.ofList

    type private Tarjan =
        { Index: Map<string, int>
          Low: Map<string, int>
          Stack: string list
          OnStack: Set<string>
          Counter: int
          Found: string list list }

    /// Strongly connected components with more than one member, or a self
    /// loop, in ordinal order. Tarjan's algorithm over ordinally sorted ids
    /// and successors, so the result is deterministic.
    let cycles (items: ItemAnalysis list) : string list list =
        let graph = edges (pending items)
        let nodes = graph |> Map.toList |> List.map fst |> Text.sortOrdinal

        let rec strongConnect (node: string) (state: Tarjan) : Tarjan =
            let entered =
                { state with
                    Index = state.Index.Add(node, state.Counter)
                    Low = state.Low.Add(node, state.Counter)
                    Stack = node :: state.Stack
                    OnStack = state.OnStack.Add node
                    Counter = state.Counter + 1 }

            let visited =
                graph[node]
                |> Text.sortOrdinal
                |> List.fold
                    (fun (current: Tarjan) successor ->
                        if not (current.Index.ContainsKey successor) then
                            let after = strongConnect successor current
                            { after with Low = after.Low.Add(node, min after.Low[node] after.Low[successor]) }
                        elif current.OnStack.Contains successor then
                            { current with Low = current.Low.Add(node, min current.Low[node] current.Index[successor]) }
                        else
                            current)
                    entered

            if visited.Low[node] = visited.Index[node] then
                let rec pop (stack: string list) (members: string list) : string list * string list =
                    match stack with
                    | top :: rest when top = node -> rest, node :: members
                    | top :: rest -> pop rest (top :: members)
                    | [] -> [], members

                let stack, members = pop visited.Stack []
                let isCycle = members.Length > 1 || (graph[node] |> List.contains node)

                { visited with
                    Stack = stack
                    OnStack = members |> List.fold (fun set value -> Set.remove value set) visited.OnStack
                    Found = if isCycle then Text.sortOrdinal members :: visited.Found else visited.Found }
            else
                visited

        let initial =
            { Index = Map.empty
              Low = Map.empty
              Stack = []
              OnStack = Set.empty
              Counter = 0
              Found = [] }

        let final =
            nodes |> List.fold (fun (state: Tarjan) node -> if state.Index.ContainsKey node then state else strongConnect node state) initial

        final.Found |> List.sortWith (fun left right -> Text.ordinal (List.head left) (List.head right))

    let private dependentsOf (items: ItemAnalysis list) =
        let graph = edges (pending items)

        graph
        |> Map.toList
        |> List.collect (fun (item, prerequisites) -> prerequisites |> List.map (fun prerequisite -> prerequisite, item))
        |> List.groupBy fst
        |> List.map (fun (prerequisite, pairs) -> prerequisite, pairs |> List.map snd |> Text.distinctOrdinal)
        |> Map.ofList

    /// PRX-PLAN-046: an explainable measure of what completing an item unlocks.
    let unlocks (items: ItemAnalysis list) : UnlockValue list =
        let open' = pending items
        let byId = open' |> List.map (fun item -> item.Id, item) |> Map.ofList
        let dependents = dependentsOf items

        let rec transitive (visited: Set<string>) (frontier: string list) =
            match frontier with
            | [] -> visited
            | next :: rest ->
                let fresh = dependents.TryFind next |> Option.defaultValue [] |> List.filter (visited.Contains >> not)
                transitive (fresh |> List.fold (fun set value -> Set.add value set) visited) (rest @ fresh)

        open'
        |> List.choose (fun item ->
            match dependents.TryFind item.Id with
            | None -> None
            | Some direct ->
                let unlocked =
                    direct
                    |> List.filter (fun dependent ->
                        byId.TryFind dependent
                        |> Option.exists (fun candidate -> openHardPrerequisites candidate |> List.filter byId.ContainsKey = [ item.Id ]))

                let all = transitive Set.empty [ item.Id ] |> Set.remove item.Id |> Set.toList |> Text.sortOrdinal

                let explanation =
                    match unlocked with
                    | [] -> $"{all.Length} pending item(s) transitively depend on {item.Id}; none becomes runnable from it alone"
                    | _ ->
                        let names = String.concat ", " unlocked
                        $"completing {item.Id} leaves {names} with no open hard prerequisite; {all.Length} pending item(s) transitively depend on it"

                Some
                    { WorkItem = item.Id
                      DirectlyUnlocks = unlocked
                      TransitiveDependents = all
                      Explanation = explanation })

    /// PRX-PLAN-045: the longest chain of hard dependencies through pending
    /// work, by item count, ties broken by known expected duration then id.
    let criticalPath (items: ItemAnalysis list) : CriticalPath =
        let open' = pending items
        let inCycle = cycles items |> List.concat |> Set.ofList
        let byId = open' |> List.filter (fun item -> not (inCycle.Contains item.Id)) |> List.map (fun item -> item.Id, item) |> Map.ofList
        let dependents = dependentsOf items
        let knownMs (id: string) = byId.TryFind id |> Option.bind (fun item -> item.RemainingDuration.Expected) |> Option.defaultValue 0L

        let better (left: string list) (right: string list) =
            match compare left.Length right.Length with
            | 0 ->
                match compare (List.sumBy knownMs left) (List.sumBy knownMs right) with
                | 0 -> if Text.ordinal (String.Join("|", left)) (String.Join("|", right)) <= 0 then left else right
                | order -> if order > 0 then left else right
            | order -> if order > 0 then left else right

        let rec longestFrom (id: string) : string list =
            let downstream =
                dependents.TryFind id |> Option.defaultValue [] |> List.filter byId.ContainsKey |> List.map longestFrom

            match downstream with
            | [] -> [ id ]
            | first :: rest -> id :: (rest |> List.fold better first)

        let path =
            byId
            |> Map.toList
            |> List.map fst
            |> List.filter (fun id -> byId[id] |> openHardPrerequisites |> List.filter byId.ContainsKey |> List.isEmpty)
            |> List.map longestFrom
            |> List.fold (fun best candidate -> match best with | [] -> candidate | _ -> better best candidate) []

        let path = if path.Length < 2 then [] else path

        { WorkItems = path
          ExpectedDuration = path |> List.map (fun id -> byId[id].RemainingDuration) |> Estimate.sumDurations
          Explanation =
            match path with
            | [] -> "no pending work item waits on another through a hard dependency"
            | _ ->
                let chain = String.concat " -> " path
                $"longest hard-dependency chain through pending work: {chain} ({path.Length} items)" }

    // ---- collisions and affinity ---------------------------------------------

    let private areaTags (configuration: PlannerConfiguration) (item: ItemAnalysis) =
        let generic = configuration.GenericTags |> List.map (fun tag -> tag.ToLowerInvariant()) |> Set.ofList
        item.Tags |> List.map (fun tag -> tag.ToLowerInvariant()) |> List.filter (generic.Contains >> not) |> Text.distinctOrdinal

    let private declaredPaths (configuration: PlannerConfiguration) (id: string) =
        configuration.Areas |> List.tryFind (fun (item, _) -> item = id) |> Option.map snd |> Option.defaultValue []

    let private overlaps (left: string) (right: string) =
        let normalize (value: string) = value.Replace('\\', '/').TrimEnd('/')
        let left, right = normalize left, normalize right
        left = right || left.StartsWith(right + "/", StringComparison.Ordinal) || right.StartsWith(left + "/", StringComparison.Ordinal)

    let private hasScope (configuration: PlannerConfiguration) (item: ItemAnalysis) =
        not (areaTags configuration item).IsEmpty || not (declaredPaths configuration item.Id).IsEmpty || item.Checkpoint.IsSome || not item.ChangedPaths.IsEmpty

    let collision (configuration: PlannerConfiguration) (left: ItemAnalysis) (right: ItemAnalysis) : Collision =
        let left, right = if Text.ordinal left.Id right.Id <= 0 then left, right else right, left

        let signals =
            [ match left.Checkpoint, right.Checkpoint with
              | Some a, Some b when a.Branch = b.Branch -> yield CollisionSignal.SameBranch a.Branch
              | _ -> ()
              yield!
                  configuration.Conflicts
                  |> List.filter (fun declared -> (declared.Left = left.Id && declared.Right = right.Id) || (declared.Left = right.Id && declared.Right = left.Id))
                  |> List.map (fun declared -> CollisionSignal.DeclaredConflict declared.Reason)
              yield!
                  declaredPaths configuration left.Id
                  |> List.collect (fun path -> declaredPaths configuration right.Id |> List.filter (overlaps path) |> List.map (fun _ -> path))
                  |> Text.distinctOrdinal
                  |> List.map CollisionSignal.SharedDeclaredPath
              yield!
                  left.ChangedPaths
                  |> List.filter (fun path -> right.ChangedPaths |> List.contains path)
                  |> Text.distinctOrdinal
                  |> List.map CollisionSignal.SharedChangedPath
              yield!
                  left.ContestedPaths
                  |> List.filter (fun entry -> right.ContestedPaths |> List.contains entry)
                  |> List.map (fun (path, merges) -> CollisionSignal.HistoricalConflict $"{path} ({merges} merge(s) changed it on both sides)")
              yield!
                  Set.intersect (areaTags configuration left |> Set.ofList) (areaTags configuration right |> Set.ofList)
                  |> Set.toList
                  |> Text.sortOrdinal
                  |> List.map CollisionSignal.SharedArea
              if not (hasScope configuration left) then yield CollisionSignal.InsufficientScopeEvidence left.Id
              if not (hasScope configuration right) then yield CollisionSignal.InsufficientScopeEvidence right.Id
              if not configuration.PraxisStateMergeSafe then yield CollisionSignal.PraxisStateFiles ]

        let risk =
            signals
            |> List.map CollisionSignal.risk
            |> List.fold (fun worst risk -> if CollisionRisk.rank risk > CollisionRisk.rank worst then risk else worst) CollisionRisk.Safe

        { Left = left.Id
          Right = right.Id
          Risk = risk
          Signals = signals }

    /// Pairwise relations between every two schedulable items.
    let collisions (configuration: PlannerConfiguration) (items: ItemAnalysis list) : Collision list =
        let schedulable = items |> List.filter (fun item -> PlanningWorkState.isSchedulable item.PlanningState) |> List.sortWith (fun a b -> Text.ordinal a.Id b.Id)

        schedulable
        |> List.mapi (fun index left -> schedulable |> List.skip (index + 1) |> List.map (collision configuration left))
        |> List.concat

    let private familySuffix = Regex(@"-\d+$", RegexOptions.CultureInvariant)

    /// A requirement family: the id without a trailing number, when that still
    /// names more than one segment (PRAXIS-REMOTE-12 -> PRAXIS-REMOTE).
    let family (id: string) =
        if familySuffix.IsMatch id then
            let stem = familySuffix.Replace(id, "")
            if stem.Contains '-' then Some stem else None
        else
            None

    /// PRX-PLAN-071: why two items share context, if they do.
    let affinity (configuration: PlannerConfiguration) (left: ItemAnalysis) (right: ItemAnalysis) : string list =
        [ yield!
              Set.intersect (areaTags configuration left |> Set.ofList) (areaTags configuration right |> Set.ofList)
              |> Set.toList
              |> Text.sortOrdinal
              |> List.map (fun tag -> $"same area '{tag}'")
          match family left.Id, family right.Id with
          | Some a, Some b when a = b -> yield $"same requirement family {a}"
          | _ -> ()
          match left.Checkpoint, right.Checkpoint with
          | Some a, Some b when a.Branch = b.Branch -> yield $"same branch {a.Branch}"
          | _ -> () ]
