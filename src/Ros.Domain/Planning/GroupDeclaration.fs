namespace Ros.Domain.Planning

open System
open System.Text.RegularExpressions
open Ros.Domain.Provenance
open Ros.Domain.Work

/// How a declared member stands when a group is created or validated. A
/// declaration only reads this; it never changes it (PRX-GRP-002).
[<RequireQualifiedAccess>]
type MemberStanding =
    | Unknown
    | Open of state: string
    | Terminal of state: string

[<RequireQualifiedAccess>]
type GroupDeclarationRejection =
    | InvalidGroupId of string
    | DuplicateGroupId of string
    | NoMembers
    | DuplicateMember of string
    | InvalidMemberId of string
    | UnknownMember of string
    | TerminalMember of workItemId: string * state: string
    | BlankEntry of field: string

[<RequireQualifiedAccess>]
module GroupDeclarationRejection =
    let message rejection =
        match rejection with
        | GroupDeclarationRejection.InvalidGroupId id ->
            $"'{id}' is not a valid group ID; use GROUP-<AREA>-<SEQUENCE> in upper case, for example GROUP-PRAXIS-PLANNING-001"
        | GroupDeclarationRejection.DuplicateGroupId id -> $"group {id} is already declared; group IDs are stable and never reused"
        | GroupDeclarationRejection.NoMembers -> "a group needs at least one member (--member ID)"
        | GroupDeclarationRejection.DuplicateMember id -> $"{id} is listed more than once"
        | GroupDeclarationRejection.InvalidMemberId id -> $"'{id}' is not a valid work-item ID"
        | GroupDeclarationRejection.UnknownMember id -> $"{id} is not a work item this repository tracks (neither in the backlog nor in live work)"
        | GroupDeclarationRejection.TerminalMember(id, state) -> $"{id} is {state}; a terminal work item cannot join a new group"
        | GroupDeclarationRejection.BlankEntry field -> $"{field} must not be blank"

/// A durable, human-declared execution group (PRX-GRP-073 phase two). The
/// `Group` is exactly a `grouping.groups` entry, so the planner reads a
/// stored declaration the way it reads planner configuration.
type StoredGroupDeclaration =
    { Group: DeclaredGroup
      DeclaredAt: string
      DeclaredBy: Actor }

[<RequireQualifiedAccess>]
module GroupDeclaration =
    let private groupIdPattern = Regex(@"^GROUP-[A-Z0-9]+(?:-[A-Z0-9]+)*$", RegexOptions.CultureInvariant)

    let isValidGroupId (id: string) = not (isNull id) && groupIdPattern.IsMatch id

    let private isTerminalState (state: string) = state = "complete" || state = "abandoned"

    let private liveStateCode state =
        match state with
        | LiveWorkState.Ready -> "ready"
        | LiveWorkState.Active -> "active"
        | LiveWorkState.Blocked -> "blocked"
        | LiveWorkState.Complete -> "complete"
        | LiveWorkState.Abandoned -> "abandoned"

    let private classify (state: string) =
        if isTerminalState state then MemberStanding.Terminal state else MemberStanding.Open state

    /// Live work state wins over the backlog record, which keeps the state
    /// the item had when it was promoted.
    let standing (queue: PlanningQueueItem list) (live: PlanningLiveItem list) (id: string) : MemberStanding =
        match live |> List.tryFind (fun item -> item.Id = id), queue |> List.tryFind (fun item -> item.Id = id) with
        | Some item, _ -> classify (liveStateCode item.State)
        | None, Some item -> classify item.Status
        | None, None -> MemberStanding.Unknown

    let private duplicates (values: string list) =
        values
        |> List.countBy id
        |> List.filter (fun (_, count) -> count > 1)
        |> List.map fst

    let private blanks (group: DeclaredGroup) =
        [ "--shared-context", group.SharedContext
          "--architecture-note", group.ArchitectureNotes
          "--execution-repository", Option.toList group.ExecutionRepository ]
        |> List.filter (fun (_, values) -> values |> List.exists String.IsNullOrWhiteSpace)
        |> List.map (fst >> GroupDeclarationRejection.BlankEntry)

    /// Shape problems a group has regardless of when it is checked.
    let private structural (group: DeclaredGroup) =
        [ if not (isValidGroupId group.Id) then
              yield GroupDeclarationRejection.InvalidGroupId group.Id
          if group.Members.IsEmpty then
              yield GroupDeclarationRejection.NoMembers
          yield! group.Members |> List.filter (WorkItemId.isValid >> not) |> List.map GroupDeclarationRejection.InvalidMemberId
          yield! duplicates group.Members |> List.map GroupDeclarationRejection.DuplicateMember
          yield! blanks group ]

    /// Decides whether a new declaration may be recorded. Every rejection is
    /// reported at once; nothing is recorded unless there are none.
    let decide
        (existing: DeclaredGroup list)
        (standingOf: string -> MemberStanding)
        (candidate: DeclaredGroup)
        : Result<DeclaredGroup, GroupDeclarationRejection list> =
        let membership =
            candidate.Members
            |> List.distinct
            |> List.filter WorkItemId.isValid
            |> List.choose (fun id ->
                match standingOf id with
                | MemberStanding.Unknown -> Some(GroupDeclarationRejection.UnknownMember id)
                | MemberStanding.Terminal state -> Some(GroupDeclarationRejection.TerminalMember(id, state))
                | MemberStanding.Open _ -> None)

        let duplicateId =
            existing
            |> List.filter (fun group -> group.Id = candidate.Id)
            |> List.truncate 1
            |> List.map (fun group -> GroupDeclarationRejection.DuplicateGroupId group.Id)

        match structural candidate @ duplicateId @ membership with
        | [] -> Ok candidate
        | rejections -> Error rejections

    /// Findings for stored groups (`validate`). A member that completed or
    /// was abandoned after the group was declared is partial completion
    /// (PRX-GRP-042), not an error; a member Praxis cannot find is.
    let findings (standingOf: string -> MemberStanding) (stored: StoredGroupDeclaration list) : (string * string) list =
        let duplicateIds =
            duplicates (stored |> List.map (fun entry -> entry.Group.Id))
            |> List.map (fun id -> $"groups[{id}].id", GroupDeclarationRejection.message (GroupDeclarationRejection.DuplicateGroupId id))

        let perGroup =
            stored
            |> List.collect (fun entry ->
                let group = entry.Group

                let unknown =
                    group.Members
                    |> List.distinct
                    |> List.filter WorkItemId.isValid
                    |> List.filter (fun id -> standingOf id = MemberStanding.Unknown)
                    |> List.map GroupDeclarationRejection.UnknownMember

                let origin =
                    if group.Origin = GroupOrigin.HumanDeclared then []
                    else [ $"groups[{group.Id}].origin", $"a stored group is human-declared; found {GroupOrigin.code group.Origin}" ]

                let declaredAt =
                    if String.IsNullOrWhiteSpace entry.DeclaredAt then [ $"groups[{group.Id}].declaredAt", "declaredAt is required" ] else []

                (structural group @ unknown |> List.map (fun rejection -> $"groups[{group.Id}]", GroupDeclarationRejection.message rejection))
                @ origin
                @ declaredAt)

        duplicateIds @ perGroup

    /// The planner configuration with stored declarations appended to
    /// `grouping.groups`. An ID declared in both places is refused rather
    /// than silently resolved.
    let mergeInto (configuration: PlannerConfiguration) (stored: StoredGroupDeclaration list) : Result<PlannerConfiguration, string> =
        let configured = configuration.Grouping.Groups |> List.map (fun group -> group.Id) |> Set.ofList

        match stored |> List.map (fun entry -> entry.Group.Id) |> List.filter configured.Contains with
        | [] ->
            Ok
                { configuration with
                    Grouping =
                        { configuration.Grouping with
                            Groups = configuration.Grouping.Groups @ (stored |> List.map (fun entry -> entry.Group)) } }
        | clashes ->
            let names = String.Join(", ", clashes)
            Error $"group {names} is declared both in planner configuration (grouping.groups) and in .ros/work/groups.json; remove one declaration"
