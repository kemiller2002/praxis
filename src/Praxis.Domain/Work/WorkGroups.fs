namespace Praxis.Domain.Work

open System
open System.Text.RegularExpressions
open Praxis.Domain.Planning
open Praxis.Domain.Provenance

/// Durable execution groups (PRX-GRP-073, phase two): a declared group
/// recorded in Praxis state rather than in planner configuration. The stored
/// declaration is the planner's own `DeclaredGroup`, so `plan` reads it
/// exactly as it reads `grouping.groups`. Membership never changes a
/// member's lifecycle, evidence or attribution (PRX-GRP-002, PRX-GRP-043):
/// group decisions return a new group record and nothing else.

/// What one mutation of a group did, as recorded in its history.
[<RequireQualifiedAccess>]
type GroupOperation =
    | Created
    | MemberAdded
    | MemberRemoved

[<RequireQualifiedAccess>]
module GroupOperation =
    let all = [ GroupOperation.Created; GroupOperation.MemberAdded; GroupOperation.MemberRemoved ]

    let code operation =
        match operation with
        | GroupOperation.Created -> "created"
        | GroupOperation.MemberAdded -> "member-added"
        | GroupOperation.MemberRemoved -> "member-removed"

    let tryParse value = all |> List.tryFind (fun operation -> code operation = value)

/// One append-only provenance entry: who changed the group, when, and why.
/// Entries are never rewritten.
type GroupHistoryEntry =
    { Operation: GroupOperation
      Member: string option
      At: string
      Actor: Actor
      Reason: string option
      /// A removal that deliberately left the group empty (never implicit).
      ExplicitEmpty: bool
      /// The caller's active execution when the mutation ran (PRX-GRP-113).
      /// `None` when it had none, or the entry predates store version 2.
      ExecutionId: string option
      /// The member's recorded lifecycle state when it joined or left, so a
      /// member removed while open stays reported (PRX-GRP-116). `None` for
      /// the creation entry and for entries that predate store version 2.
      MemberState: string option }

/// A member as a group names it (PRX-GRP-100): a bare work-item ID means the
/// group's home repository; `owner/repo:WORK-ID` names another repository.
[<RequireQualifiedAccess>]
type MemberReference =
    | Local of workItemId: string
    | Qualified of repository: string * workItemId: string

[<RequireQualifiedAccess>]
module RepositoryName =
    let private pattern = Regex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)

    let isValid (value: string) = pattern.IsMatch value && not (value.EndsWith ".git")

    /// `owner/repo` from a remote URL (`https://host/owner/repo(.git)`,
    /// `git@host:owner/repo(.git)`, `ssh://...`), or `None`.
    let ofRemoteUrl (url: string) : string option =
        let trimmed = url.Trim().TrimEnd('/')
        let withoutSuffix = if trimmed.EndsWith ".git" then trimmed.Substring(0, trimmed.Length - 4) else trimmed
        let path = withoutSuffix.Replace(':', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)

        if path.Length >= 2 then
            let candidate = $"{path[path.Length - 2]}/{path[path.Length - 1]}"
            if isValid candidate then Some candidate else None
        else
            None

[<RequireQualifiedAccess>]
module MemberReference =
    let parse (value: string) : MemberReference =
        match value.LastIndexOf ':' with
        | index when index > 0 && RepositoryName.isValid (value.Substring(0, index)) ->
            MemberReference.Qualified(value.Substring(0, index), value.Substring(index + 1))
        | _ -> MemberReference.Local value

    let render (reference: MemberReference) =
        match reference with
        | MemberReference.Local id -> id
        | MemberReference.Qualified(repository, id) -> $"{repository}:{id}"

    let workItemId (reference: MemberReference) =
        match reference with
        | MemberReference.Local id
        | MemberReference.Qualified(_, id) -> id

    /// The repository a member is governed in, given the group's home.
    let repository (home: string option) (reference: MemberReference) =
        match reference with
        | MemberReference.Local _ -> home
        | MemberReference.Qualified(repository, _) -> Some repository

    /// A qualified name of the home itself is the bare ID (one spelling per
    /// member, so a member is never named twice).
    let normalize (home: string option) (value: string) =
        match parse value with
        | MemberReference.Qualified(repository, id) when Some repository = home -> id
        | _ -> value

    let isQualified (value: string) =
        match parse value with
        | MemberReference.Qualified _ -> true
        | MemberReference.Local _ -> false

/// The producer milestone a cross-repository dependency waits for
/// (PRX-GRP-105).
[<RequireQualifiedAccess>]
type ProducerMilestone =
    /// The producer completed in its own repository.
    | Complete
    /// The producer's latest recorded checkpoint commit is reachable from
    /// its repository's observed default branch.
    | Merged
    /// A named release tag of the producer's repository exists.
    | Released of tag: string

[<RequireQualifiedAccess>]
module ProducerMilestone =
    let code milestone =
        match milestone with
        | ProducerMilestone.Complete -> "complete"
        | ProducerMilestone.Merged -> "merged"
        | ProducerMilestone.Released tag -> $"released:{tag}"

    let tryParse (value: string) =
        match value with
        | "complete" -> Some ProducerMilestone.Complete
        | "merged" -> Some ProducerMilestone.Merged
        | released when released.StartsWith("released:", StringComparison.Ordinal) && released.Length > "released:".Length ->
            Some(ProducerMilestone.Released(released.Substring "released:".Length))
        | _ -> None

/// `consumer` may start only once `producer` reached `milestone`. Order is
/// derived from these edges; they never change a member's lifecycle.
type MemberDependency =
    { Consumer: string
      Producer: string
      Milestone: ProducerMilestone }

[<RequireQualifiedAccess>]
module MemberDependency =
    /// `CONSUMER=PRODUCER[@MILESTONE]`, milestone `complete` by default.
    let tryParse (value: string) : MemberDependency option =
        match value.IndexOf '=' with
        | index when index > 0 && index < value.Length - 1 ->
            let consumer = value.Substring(0, index)
            let rest = value.Substring(index + 1)

            let producer, milestone =
                match rest.LastIndexOf '@' with
                | at when at > 0 -> rest.Substring(0, at), ProducerMilestone.tryParse (rest.Substring(at + 1))
                | _ -> rest, Some ProducerMilestone.Complete

            milestone |> Option.map (fun milestone -> { Consumer = consumer; Producer = producer; Milestone = milestone })
        | _ -> None

    let render (dependency: MemberDependency) =
        $"{dependency.Consumer}={dependency.Producer}@{ProducerMilestone.code dependency.Milestone}"

/// Why a member repository's state could not be read (PRX-GRP-109).
[<RequireQualifiedAccess>]
type Unobservable =
    /// No configured source, the clone or ref is missing, or Git failed.
    | Unreachable of reason: string
    /// The ref holds no Praxis installation.
    | NotInstalled of reason: string
    /// Its Praxis state could not be parsed by this version.
    | UnsupportedSchema of reason: string
    | AccessDenied of reason: string

[<RequireQualifiedAccess>]
module Unobservable =
    let code value =
        match value with
        | Unobservable.Unreachable _ -> "unreachable"
        | Unobservable.NotInstalled _ -> "praxis-not-installed"
        | Unobservable.UnsupportedSchema _ -> "unsupported-schema"
        | Unobservable.AccessDenied _ -> "access-denied"

    let reason value =
        match value with
        | Unobservable.Unreachable reason
        | Unobservable.NotInstalled reason
        | Unobservable.UnsupportedSchema reason
        | Unobservable.AccessDenied reason -> reason

    let tryCreate (code: string) (reason: string) =
        match code with
        | "unreachable" -> Some(Unobservable.Unreachable reason)
        | "praxis-not-installed" -> Some(Unobservable.NotInstalled reason)
        | "unsupported-schema" -> Some(Unobservable.UnsupportedSchema reason)
        | "access-denied" -> Some(Unobservable.AccessDenied reason)
        | _ -> None

/// What reading a member repository showed (PRX-GRP-103): a dated
/// observation, never the member's lifecycle state itself.
[<RequireQualifiedAccess>]
type ObservedMember =
    /// The repository was read; `state` is `None` when it does not record
    /// that work item.
    | Read of state: string option
    | Unobservable of Unobservable

type MemberObservation =
    { Member: string
      Repository: string
      WorkItemId: string
      /// How it was read (`git-ref`).
      Method: string
      /// The ref read and the commit it pointed at.
      Ref: string option
      Commit: string option
      /// When Praxis read it.
      ObservedAt: string
      /// How current the read ref was: when it was last fetched (its
      /// reflog), else its commit time. Staleness is judged from this.
      SourceAsOf: string option
      Outcome: ObservedMember
      /// The member's own latest checkpoint there (ID, commit, branch).
      LatestCheckpoint: (string * string * string) option
      /// Whether that repository holds this group's reference on the item;
      /// `None` when not read (PRX-GRP-102).
      Linked: bool option }

[<RequireQualifiedAccess>]
module MemberObservation =
    /// Observed now, from a source older than the configured maximum age:
    /// stale, not current (PRX-GRP-103). An unknown age is stale.
    let isStale (now: DateTimeOffset) (maxAgeMinutes: int) (observation: MemberObservation) =
        let asOf = observation.SourceAsOf |> Option.orElse (Some observation.ObservedAt)

        match asOf |> Option.bind (fun value -> match DateTimeOffset.TryParse value with | true, parsed -> Some parsed | _ -> None) with
        | Some at -> (now - at).TotalMinutes > float maxAgeMinutes
        | None -> true

    /// The state a reader may rely on: none when unobservable or stale.
    let currentState (now: DateTimeOffset) (maxAgeMinutes: int) (observation: MemberObservation) =
        match observation.Outcome with
        | ObservedMember.Read state when not (isStale now maxAgeMinutes observation) -> state
        | _ -> None

/// The only group data a member repository holds (PRX-GRP-102): an
/// immutable reference on one of its own items to a group recorded
/// elsewhere. Written by `work group link`; never a copy of the group.
type GroupReference =
    { GroupId: string
      HomeRepository: string
      WorkItemId: string
      LinkedAt: string
      Actor: Actor
      ExecutionId: string option }

/// A member's own latest durable checkpoint, referenced (never copied or
/// replaced) by a group checkpoint.
type MemberCheckpointReference =
    { WorkItemId: string
      CheckpointId: string
      Commit: string }

/// A group-level checkpoint (PRX-GRP-044): where the group stands after a
/// milestone, verified durable by the same Git rule as `work checkpoint`.
/// It claims no paths and no execution: attribution stays on members' own
/// checkpoints (PRX-GRP-043).
type GroupCheckpoint =
    { CheckpointId: string
      RecordedAt: string
      Actor: Actor
      Summary: string
      NextAction: string
      Decisions: string list
      Completed: string list
      Active: string list
      Blocked: string list
      Remaining: string list
      Abandoned: string list
      MemberCheckpoints: MemberCheckpointReference list
      /// Cross-repository members as observed when the checkpoint was
      /// recorded (PRX-GRP-107): labelled observations, never verifications
      /// of another repository's remote.
      MemberObservations: MemberObservation list
      Location: GitDurableLocation }

/// A declared group as Praxis state records it.
type StoredWorkGroup =
    { Declaration: DeclaredGroup
      /// The one repository holding this record (`owner/repo`,
      /// PRX-GRP-101). `None` for a group recorded before store version 2
      /// without one; it never changes implicitly.
      HomeRepository: string option
      /// Cross-repository order between members (PRX-GRP-105).
      Dependencies: MemberDependency list
      /// How each qualified member was observed when it joined (PRX-GRP-111):
      /// an unobservable one joined `verified: false`.
      Verifications: MemberObservation list
      CreatedAt: string
      CreatedBy: Actor
      History: GroupHistoryEntry list
      Checkpoints: GroupCheckpoint list }

/// Everything `.ros/work/groups.json` holds: the groups this repository is
/// home to, and the references its own items carry to groups recorded
/// elsewhere.
type GroupStore =
    { Groups: StoredWorkGroup list
      References: GroupReference list }

/// Whether a work item may join a group: it must be known and not terminal.
/// The state is the recorded lifecycle state (`QueuePresentation`).
[<RequireQualifiedAccess>]
type MemberStanding =
    | Unknown
    | Open of state: string
    | Terminal of state: string

[<RequireQualifiedAccess>]
module MemberStanding =
    let private terminalStates = set [ "complete"; "abandoned" ]

    let ofState (state: string) =
        if terminalStates.Contains state then MemberStanding.Terminal state else MemberStanding.Open state

    /// Every recorded item's standing, from the backlog queue and the live
    /// context, read the way every other work view reads them.
    let lookup (queue: PlanningQueueItem list) (live: PlanningLiveItem list) : string -> MemberStanding =
        let queued = queue |> List.map (fun item -> item.Id, item.Status) |> Map.ofList
        let current = live |> List.map (fun item -> item.Id, item.State) |> Map.ofList

        fun id ->
            QueuePresentation.effectiveStatus (queued.TryFind id) (current.TryFind id)
            |> Option.map ofState
            |> Option.defaultValue MemberStanding.Unknown

    let state standing =
        match standing with
        | MemberStanding.Unknown -> None
        | MemberStanding.Open state
        | MemberStanding.Terminal state -> Some state

/// Why a group command refused. Every independently knowable problem is
/// reported together; nothing is written when any is present.
[<RequireQualifiedAccess>]
type GroupRejection =
    | InvalidGroupId of groupId: string
    | InvalidMemberId of workItemId: string
    | DuplicateGroup of groupId: string
    | UnknownGroup of groupId: string
    | NoMembers
    | RepeatedMember of workItemId: string
    | UnknownMember of workItemId: string
    | TerminalMember of workItemId: string * state: string
    | NotMember of workItemId: string * groupId: string
    | RepositoryMismatch of workItemId: string * itemLocation: ExecutionLocation * groupRepository: string
    | LastMember of workItemId: string * groupId: string
    /// A cross-repository group must use the reserved area (PRX-GRP-100).
    | CrossRepositoryId of groupId: string
    /// A repository-local group may not use the reserved area.
    | ReservedArea of groupId: string
    /// A qualified member of another repository in a repository-local group.
    | ForeignMember of reference: string
    /// No `owner/repo` home could be determined for a cross-repository group.
    | HomeUnknown of groupId: string
    /// A dependency names a non-member.
    | DependencyNotMember of dependency: string * reference: string
    /// Dependencies that form a cycle (PRX-GRP-105), across repositories.
    | DependencyCycle of members: string list
    /// `link`: this repository already references the group under another home.
    | ConflictingReference of groupId: string * workItemId: string * recordedHome: string
    /// `link`: a reference names this repository itself as the home.
    | HomeIsThisRepository of groupId: string

[<RequireQualifiedAccess>]
module GroupRejection =
    let code rejection =
        match rejection with
        | GroupRejection.CrossRepositoryId _ -> "cross-repository-id"
        | GroupRejection.ReservedArea _ -> "reserved-area"
        | GroupRejection.ForeignMember _ -> "foreign-member"
        | GroupRejection.HomeUnknown _ -> "home-repository-unknown"
        | GroupRejection.DependencyNotMember _ -> "dependency-not-member"
        | GroupRejection.DependencyCycle _ -> "dependency-cycle"
        | GroupRejection.ConflictingReference _ -> "conflicting-reference"
        | GroupRejection.HomeIsThisRepository _ -> "home-is-this-repository"
        | GroupRejection.InvalidGroupId _ -> "invalid-group-id"
        | GroupRejection.InvalidMemberId _ -> "invalid-member-id"
        | GroupRejection.DuplicateGroup _ -> "duplicate-group"
        | GroupRejection.UnknownGroup _ -> "unknown-group"
        | GroupRejection.NoMembers -> "no-members"
        | GroupRejection.RepeatedMember _ -> "repeated-member"
        | GroupRejection.UnknownMember _ -> "unknown-member"
        | GroupRejection.TerminalMember _ -> "terminal-member"
        | GroupRejection.NotMember _ -> "not-member"
        | GroupRejection.RepositoryMismatch _ -> "repository-mismatch"
        | GroupRejection.LastMember _ -> "last-member"

    let message rejection =
        match rejection with
        | GroupRejection.CrossRepositoryId id -> $"{id} is cross-repository; its ID must be GROUP-ECHELON-<AREA>-<SEQUENCE> (PRX-GRP-100)"
        | GroupRejection.ReservedArea id -> $"{id} uses the reserved ECHELON area, which only a cross-repository group may use (PRX-GRP-100)"
        | GroupRejection.ForeignMember reference -> $"{reference} belongs to another repository; only a cross-repository group may name it (PRX-GRP-051)"
        | GroupRejection.HomeUnknown id -> $"{id} needs a home repository (owner/repo); pass --home-repository or give the checkout a GitHub-style origin remote (PRX-GRP-101)"
        | GroupRejection.DependencyNotMember(dependency, reference) -> $"dependency {dependency} names {reference}, which is not a member of the group"
        | GroupRejection.DependencyCycle members -> $"""dependencies form a cycle: {String.concat " -> " members} (PRX-GRP-105)"""
        | GroupRejection.ConflictingReference(group, item, home) -> $"{item} already references {group} with home {home}; a reference is immutable (PRX-GRP-102)"
        | GroupRejection.HomeIsThisRepository id -> $"this repository is the home named for {id}; add the item with work group add instead of linking it"
        | GroupRejection.InvalidGroupId id -> $"'{id}' is not a group ID; use GROUP-<AREA>-<SEQUENCE> in upper case (PRX-GRP-010)"
        | GroupRejection.InvalidMemberId id -> $"'{id}' is not a valid work-item ID"
        | GroupRejection.DuplicateGroup id -> $"group {id} already exists with a different declaration; an identical repeat is accepted unchanged (PRX-GRP-114)"
        | GroupRejection.UnknownGroup id -> $"group {id} is not recorded"
        | GroupRejection.NoMembers -> "a group needs at least one member"
        | GroupRejection.RepeatedMember id -> $"{id} is named more than once"
        | GroupRejection.UnknownMember id -> $"{id} is not a recorded work item (not in the backlog queue or the live context)"
        | GroupRejection.TerminalMember(id, state) -> $"{id} is {state}; a terminal item cannot join a group"
        | GroupRejection.NotMember(id, group) -> $"{id} is not a member of {group} and its removal was never recorded"
        | GroupRejection.RepositoryMismatch(id, item, group) ->
            $"{id} executes in {ExecutionLocation.describe item} but the group executes in {group}; declare the group cross-repository or split it (PRX-GRP-051)"
        | GroupRejection.LastMember(id, group) -> $"{id} is the last member of {group}; removing it would leave the group empty"

    /// A refusal caused by the arguments themselves (exit 2), not by state.
    let isArgumentError rejection =
        match rejection with
        | GroupRejection.InvalidGroupId _
        | GroupRejection.InvalidMemberId _
        | GroupRejection.NoMembers
        | GroupRejection.RepeatedMember _ -> true
        | _ -> false

    /// The reserved cross-repository area (PRX-GRP-100).
    let echelonPrefix = "GROUP-ECHELON-"

/// Repository facts a group decision reads. Supplied by the caller so the
/// decisions stay pure.
type GroupContext =
    { Groups: StoredWorkGroup list
      Standing: string -> MemberStanding
      /// Where a work item executes, by the planner's own rule
      /// (`Grouping.executionLocation`, PRX-GRP-051).
      RepositoryOf: string -> ExecutionLocation
      /// This checkout's own `owner/repo`, when known.
      ThisRepository: string option
      /// Reads a member repository's Praxis state (repository, work item):
      /// read-only and dated (PRX-GRP-103).
      Observe: string -> string -> MemberObservation }

type GroupCreateRequest =
    { GroupId: string
      Members: string list
      Kind: GroupKind option
      Origin: GroupOrigin
      SharedContext: string list
      ExecutionRepository: string
      CrossRepository: bool
      ArchitectureNotes: string list
      OccurredAt: string
      Actor: Actor
      Reason: string option
      /// The caller's active execution, when it has one (PRX-GRP-113).
      ExecutionId: string option
      /// `--home-repository`, else this checkout's `owner/repo`.
      HomeRepository: string option
      Dependencies: MemberDependency list }

/// What a mutation did (PRX-GRP-114): the resulting group, and whether the
/// request changed it. A repeat whose end state already holds is accepted
/// with `Changed = false` and appends no history.
type GroupChange =
    { Group: StoredWorkGroup
      Changed: bool
      /// Accepted with a caveat, such as an unobservable member recorded
      /// `verified: false` (PRX-GRP-111).
      Warnings: string list }

/// Where a member stands inside its group (PRX-GRP-042): each member
/// completes, or not, on its own.
[<RequireQualifiedAccess>]
type MemberCategory =
    | Completed
    | Abandoned
    | Active
    | Blocked
    | Remaining
    | Unknown

[<RequireQualifiedAccess>]
module MemberCategory =
    let code category =
        match category with
        | MemberCategory.Completed -> "completed"
        | MemberCategory.Abandoned -> "abandoned"
        | MemberCategory.Active -> "active"
        | MemberCategory.Blocked -> "blocked"
        | MemberCategory.Remaining -> "remaining"
        | MemberCategory.Unknown -> "unknown"

    let ofState (state: string option) =
        match state with
        | Some "complete" -> MemberCategory.Completed
        | Some "abandoned" -> MemberCategory.Abandoned
        | Some "active" -> MemberCategory.Active
        | Some "blocked" -> MemberCategory.Blocked
        | Some _ -> MemberCategory.Remaining
        | None -> MemberCategory.Unknown

/// What is known about one member now: its recorded lifecycle state, the
/// planner's view of it when available, and the work items it still waits on.
type MemberFacts =
    { State: string option
      PlanningState: string option
      WaitsOn: string list
      /// The prerequisites this member waits on that are themselves blocked
      /// (PRX-GRP-103: "waits on a blocked prerequisite").
      WaitsOnBlocked: string list
      /// A cross-repository member's dated observation (PRX-GRP-103).
      Observation: MemberObservation option
      /// The observation is older than the configured maximum age; its
      /// state is then not relied on (`State` is `None`).
      Stale: bool }

[<RequireQualifiedAccess>]
module MemberFacts =
    /// Facts from the recorded state alone, without a planner analysis.
    let ofStanding (standing: string -> MemberStanding) (id: string) =
        { State = MemberStanding.state (standing id)
          PlanningState = None
          WaitsOn = []
          WaitsOnBlocked = []
          Observation = None
          Stale = false }

    /// Facts for a member of another repository, from its observation
    /// alone: an unobservable or stale member has no state (PRX-GRP-103).
    let ofObservation (now: DateTimeOffset) (maxAgeMinutes: int) (observation: MemberObservation) =
        { State = MemberObservation.currentState now maxAgeMinutes observation
          PlanningState = None
          WaitsOn = []
          WaitsOnBlocked = []
          Observation = Some observation
          Stale =
            (match observation.Outcome with
             | ObservedMember.Read _ -> MemberObservation.isStale now maxAgeMinutes observation
             | ObservedMember.Unobservable _ -> false) }

    let private blockedStates =
        set [ PlanningWorkState.Blocked; PlanningWorkState.AwaitingEvidence; PlanningWorkState.AwaitingHuman ]

    /// Facts per member from the planner's read-only analysis: recorded
    /// state, planning state, the work items it still waits on, and which of
    /// those are blocked. An item the analysis does not know falls back to
    /// its recorded standing.
    let ofAnalysis (analysis: PlanningAnalysis) (standing: string -> MemberStanding) : string -> MemberFacts =
        let byId = analysis.Items |> List.map (fun item -> item.Id, item) |> Map.ofList

        fun id ->
            match byId.TryFind id with
            | None -> ofStanding standing id
            | Some item ->
                let waitsOn =
                    item.Dependencies
                    |> List.filter (fun resolved -> resolved.Status <> DependencyStatus.Satisfied)
                    |> List.choose (fun resolved ->
                        match resolved.Dependency.Target with
                        | DependencyTarget.WorkItem target -> Some target
                        | _ -> None)
                    |> List.distinct

                { State = Some item.LifecycleState
                  PlanningState = Some(PlanningWorkState.code item.PlanningState)
                  WaitsOn = waitsOn
                  WaitsOnBlocked =
                    waitsOn
                    |> List.filter (fun target -> byId.TryFind target |> Option.exists (fun prerequisite -> blockedStates.Contains prerequisite.PlanningState))
                  Observation = None
                  Stale = false }

type MemberProgress =
    { WorkItemId: string
      Category: MemberCategory
      State: string option
      PlanningState: string option
      /// Work items this member still waits on.
      WaitsOn: string list
      /// Prerequisites it waits on that are themselves blocked.
      WaitsOnBlocked: string list
      /// Open members of the same group that wait on this member.
      Gates: string list
      /// The repository the member is governed in (the home for a bare ID).
      Repository: string option
      Observation: MemberObservation option
      Stale: bool }

/// A group's partial-completion view. It never says the group succeeded:
/// `Completed` lists exactly the members that completed on their own.
type GroupProgress =
    { Members: MemberProgress list
      Completed: string list
      Abandoned: string list
      Active: string list
      Blocked: string list
      Remaining: string list
      Unknown: string list }

[<RequireQualifiedAccess>]
module GroupProgress =
    let summary (progress: GroupProgress) =
        let count (values: string list) = values.Length

        $"{count progress.Completed} of {progress.Members.Length} complete ({count progress.Active} active, {count progress.Blocked} blocked, {count progress.Remaining} remaining, {count progress.Abandoned} abandoned, {count progress.Unknown} unknown)"

/// A group's status, always derived from its members' own states and never
/// stored or set by any command (PRX-GRP-103, PRX-GRP-116). Listed in the
/// order of precedence the derivation applies.
[<RequireQualifiedAccess>]
type GroupStatus =
    /// Every current member completed on its own evidence.
    | Complete
    /// At least one member's state is not known, and so the group's is not.
    | Unknown
    /// At least one current member completed and at least one did not.
    | PartiallyComplete
    /// Every open member is blocked or waits on a blocked prerequisite.
    | Blocked
    /// At least one member is active.
    | Active
    | NotStarted

[<RequireQualifiedAccess>]
module GroupStatus =
    let all =
        [ GroupStatus.Complete
          GroupStatus.Unknown
          GroupStatus.PartiallyComplete
          GroupStatus.Blocked
          GroupStatus.Active
          GroupStatus.NotStarted ]

    let code status =
        match status with
        | GroupStatus.Complete -> "complete"
        | GroupStatus.Unknown -> "unknown"
        | GroupStatus.PartiallyComplete -> "partially-complete"
        | GroupStatus.Blocked -> "blocked"
        | GroupStatus.Active -> "active"
        | GroupStatus.NotStarted -> "not-started"

    let tryParse value = all |> List.tryFind (fun status -> code status = value)

    /// The PRX-GRP-103 precedence over the members' own standings. An empty
    /// group has no member that completed, so it is never `Complete`.
    let derive (progress: GroupProgress) : GroupStatus =
        let rows = progress.Members
        let isIn category (row: MemberProgress) = row.Category = category

        let blockedMembers =
            rows |> List.filter (isIn MemberCategory.Blocked) |> List.map (fun row -> row.WorkItemId) |> Set.ofList

        let open' =
            rows |> List.filter (fun row -> not (isIn MemberCategory.Completed row || isIn MemberCategory.Abandoned row))

        let effectivelyBlocked (row: MemberProgress) =
            isIn MemberCategory.Blocked row
            || not row.WaitsOnBlocked.IsEmpty
            || row.WaitsOn |> List.exists blockedMembers.Contains

        if not rows.IsEmpty && rows |> List.forall (isIn MemberCategory.Completed) then GroupStatus.Complete
        elif rows |> List.exists (isIn MemberCategory.Unknown) then GroupStatus.Unknown
        elif rows |> List.exists (isIn MemberCategory.Completed) then GroupStatus.PartiallyComplete
        elif not open'.IsEmpty && open' |> List.forall effectivelyBlocked then GroupStatus.Blocked
        elif rows |> List.exists (isIn MemberCategory.Active) then GroupStatus.Active
        else GroupStatus.NotStarted

/// A member that left the group while it was still open (PRX-GRP-116): the
/// group must never read as if every member succeeded.
type RemovedOpenMember =
    { WorkItemId: string
      RemovedAt: string
      Reason: string option
      /// Its state when it was removed; `None` for a removal recorded before
      /// store version 2, judged then by its current state.
      StateAtRemoval: string option }

/// One row of `work group list` (PRX-GRP-110): what a reader needs to find
/// a group, never a stored status.
type GroupSummary =
    { GroupId: string
      Kind: GroupKind option
      Origin: GroupOrigin
      /// The repository holding the group record (PRX-GRP-101).
      Home: string option
      ExecutionRepository: string option
      CrossRepository: bool
      MemberCount: int
      Status: GroupStatus
      Progress: GroupProgress
      RemovedOpen: RemovedOpenMember list
      /// The group's execution mode (PRX-GRP-130); `None` while unknown.
      ExecutionMode: string option
      LatestCheckpointAt: string option }

/// `work group list` filters; every given filter must hold.
type GroupListFilter =
    { Status: GroupStatus option
      Member: string option
      Repository: string option }

/// Whether a dependency's producer reached its milestone (PRX-GRP-105).
/// `Waiting` and `Unknown` are planning states: they never change the
/// consumer's lifecycle state.
[<RequireQualifiedAccess>]
type EdgeState =
    | Satisfied
    | Waiting
    | Unknown of reason: string

[<RequireQualifiedAccess>]
module EdgeState =
    let code state =
        match state with
        | EdgeState.Satisfied -> "satisfied"
        | EdgeState.Waiting -> "waiting"
        | EdgeState.Unknown _ -> "unknown"

type OrderRow =
    { Dependency: MemberDependency
      State: EdgeState }

/// Progress of one repository's members (PRX-GRP-106).
type RepositoryProgress =
    { Repository: string
      Completed: int
      Total: int
      Unknown: int }

/// `work group link` (PRX-GRP-102), run in the member's own repository.
type GroupLinkRequest =
    { GroupId: string
      HomeRepository: string
      WorkItemId: string
      OccurredAt: string
      Actor: Actor
      ExecutionId: string option }

/// What `link` did: the store and whether the reference is new.
type LinkChange =
    { Store: GroupStore
      Reference: GroupReference
      Changed: bool }

/// One membership change (`work group add`, `work group remove`).
type GroupMemberRequest =
    { GroupId: string
      WorkItemId: string
      OccurredAt: string
      Actor: Actor
      Reason: string option
      /// Removal only: deliberately allow leaving the group empty.
      AllowEmpty: bool
      /// The caller's active execution, when it has one (PRX-GRP-113).
      ExecutionId: string option
      /// `add` only: order edges for the joining member (PRX-GRP-105).
      Dependencies: MemberDependency list }

type GroupCheckpointRequest =
    { GroupId: string
      /// Chosen by the caller (a digest of the checkpoint), so the decision
      /// stays pure.
      CheckpointId: string
      Summary: string
      NextAction: string
      Decisions: string list
      OccurredAt: string
      Actor: Actor }

/// Why a group checkpoint was refused: the group, ownership, or durability.
[<RequireQualifiedAccess>]
type GroupCheckpointRejection =
    | Group of GroupRejection
    /// No member is active, so no grouped execution is in progress.
    | NoActiveMember of groupId: string
    /// No active member's execution belongs to the caller (member, reason).
    | NoOwnExecution of reasons: (string * string) list
    | Durability of CheckpointRejection

[<RequireQualifiedAccess>]
module GroupCheckpointRejection =
    let code rejection =
        match rejection with
        | GroupCheckpointRejection.Group group -> GroupRejection.code group
        | GroupCheckpointRejection.NoActiveMember _ -> "no-active-member"
        | GroupCheckpointRejection.NoOwnExecution _ -> "no-own-execution"
        | GroupCheckpointRejection.Durability durability -> CheckpointRejection.code durability

    let message rejection =
        match rejection with
        | GroupCheckpointRejection.Group group -> GroupRejection.message group
        | GroupCheckpointRejection.NoActiveMember id ->
            $"no member of {id} is active; start a member's work before recording a group checkpoint (as `work checkpoint` requires an active item)"
        | GroupCheckpointRejection.NoOwnExecution reasons ->
            let listed = reasons |> List.map (fun (id, reason) -> $"{id}: {reason}") |> String.concat "; "
            $"no active member's execution belongs to the caller ({listed}); a group checkpoint is recorded only under the caller's own execution"
        | GroupCheckpointRejection.Durability durability -> CheckpointRejection.message durability

    let isArgumentError rejection =
        match rejection with
        | GroupCheckpointRejection.Group group -> GroupRejection.isArgumentError group
        | GroupCheckpointRejection.NoActiveMember _
        | GroupCheckpointRejection.NoOwnExecution _ -> false
        | GroupCheckpointRejection.Durability durability -> CheckpointRejection.isArgumentError durability

[<RequireQualifiedAccess>]
module WorkGroups =
    let private groupIdPattern = Regex("^GROUP-[A-Z0-9]+(-[A-Z0-9]+)*$", RegexOptions.CultureInvariant)

    let isValidGroupId (value: string) = groupIdPattern.IsMatch value

    let tryFind (groups: StoredWorkGroup list) (groupId: string) =
        groups |> List.tryFind (fun group -> group.Declaration.Id = groupId)

    /// Groups are kept ordered by ID so the same state always serializes to
    /// the same bytes (PRX-GRP-075).
    let upsert (groups: StoredWorkGroup list) (group: StoredWorkGroup) =
        group :: (groups |> List.filter (fun existing -> existing.Declaration.Id <> group.Declaration.Id))
        |> List.sortWith (fun left right -> String.CompareOrdinal(left.Declaration.Id, right.Declaration.Id))

    let private repeated (values: string list) =
        values |> List.countBy id |> List.filter (fun (_, count) -> count > 1) |> List.map fst

    /// The one join rule for every way an item enters a group. A bare ID
    /// names a home item: a valid ID of a known, non-terminal item that
    /// executes where the group does (unless the group is
    /// cross-repository). A qualified `owner/repo:ID` may join only a
    /// cross-repository group; it is checked by observation when reachable
    /// and joins `verified: false` with a warning when not (PRX-GRP-111).
    let admission
        (context: GroupContext)
        (declaration: DeclaredGroup)
        (memberId: string)
        : Result<MemberObservation option * string list, GroupRejection list> =
        match MemberReference.parse memberId with
        | MemberReference.Qualified(repository, workItemId) ->
            if not declaration.CrossRepository then Error [ GroupRejection.ForeignMember memberId ]
            elif not (WorkItemId.isValid workItemId) then Error [ GroupRejection.InvalidMemberId memberId ]
            else
                let observation = context.Observe repository workItemId

                match observation.Outcome with
                | ObservedMember.Read None -> Error [ GroupRejection.UnknownMember memberId ]
                | ObservedMember.Read(Some state) ->
                    match MemberStanding.ofState state with
                    | MemberStanding.Terminal state -> Error [ GroupRejection.TerminalMember(memberId, state) ]
                    | _ -> Ok(Some observation, [])
                | ObservedMember.Unobservable why ->
                    Ok(
                        Some observation,
                        [ $"{memberId} could not be observed ({Unobservable.code why}: {Unobservable.reason why}); recorded verified: false" ]
                    )
        | MemberReference.Local workItemId ->
            if not (WorkItemId.isValid workItemId) then
                Error [ GroupRejection.InvalidMemberId workItemId ]
            else
                match
                    [ match context.Standing workItemId with
                      | MemberStanding.Unknown -> yield GroupRejection.UnknownMember workItemId
                      | MemberStanding.Terminal state -> yield GroupRejection.TerminalMember(workItemId, state)
                      | MemberStanding.Open _ -> ()
                      match declaration.ExecutionRepository with
                      | Some repository when not declaration.CrossRepository ->
                          match context.RepositoryOf workItemId with
                          | ExecutionLocation.Repository itemRepository when itemRepository = repository -> ()
                          | location -> yield GroupRejection.RepositoryMismatch(workItemId, location, repository)
                      | _ -> () ]
                with
                | [] -> Ok(None, [])
                | rejections -> Error rejections

    /// Rejections only, for callers that need no observation.
    let eligibility (context: GroupContext) (declaration: DeclaredGroup) (memberId: string) : GroupRejection list =
        match admission context declaration memberId with
        | Ok _ -> []
        | Error rejections -> rejections

    /// Order edges must join members, and must not form a cycle; cycle
    /// detection spans repositories because members are qualified names.
    let dependencyRejections (members: string list) (dependencies: MemberDependency list) : GroupRejection list =
        let present = Set.ofList members

        let notMembers =
            dependencies
            |> List.collect (fun dependency ->
                [ dependency.Consumer; dependency.Producer ]
                |> List.distinct
                |> List.filter (present.Contains >> not)
                |> List.map (fun reference -> GroupRejection.DependencyNotMember(MemberDependency.render dependency, reference)))

        let nodes = dependencies |> List.collect (fun dependency -> [ dependency.Consumer; dependency.Producer ]) |> List.distinct

        let producers consumer =
            dependencies |> List.filter (fun dependency -> dependency.Consumer = consumer) |> List.map (fun dependency -> dependency.Producer)

        notMembers @ (Grouping.stronglyConnected nodes producers |> List.map GroupRejection.DependencyCycle)

    /// The ID rule for the reserved area (PRX-GRP-100).
    let identityRejections (groupId: string) (crossRepository: bool) =
        let reserved = groupId.StartsWith(GroupRejection.echelonPrefix, StringComparison.Ordinal)

        [ if crossRepository && not reserved then GroupRejection.CrossRepositoryId groupId
          if not crossRepository && reserved then GroupRejection.ReservedArea groupId ]

    let private entry operation memberId (at: string) (actor: Actor) (reason: string option) explicitEmpty executionId memberState =
        { Operation = operation
          Member = memberId
          At = at
          Actor = actor
          Reason = reason
          ExplicitEmpty = explicitEmpty
          ExecutionId = executionId
          MemberState = memberState }

    let private changed group warnings = Ok { Group = group; Changed = true; Warnings = warnings }
    let private unchanged group = Ok { Group = group; Changed = false; Warnings = [] }

    /// Collects every member's admission: all rejections together, or the
    /// observations and warnings of those admitted.
    let private admitAll (context: GroupContext) (declaration: DeclaredGroup) (members: string list) =
        let results = members |> List.map (admission context declaration)
        let rejections = results |> List.collect (function Error rejections -> rejections | Ok _ -> [])
        let observations = results |> List.choose (function Ok(observation, _) -> observation | Error _ -> None)
        let warnings = results |> List.collect (function Ok(_, warnings) -> warnings | Error _ -> [])
        rejections, observations, warnings

    /// `work group create`: a new declared group, or every reason it cannot be.
    let create (context: GroupContext) (request: GroupCreateRequest) : Result<GroupChange, GroupRejection list> =
        let home = request.HomeRepository |> Option.orElse context.ThisRepository
        let members = request.Members |> List.map (MemberReference.normalize home)

        let dependencies =
            request.Dependencies
            |> List.map (fun dependency ->
                { dependency with
                    Consumer = MemberReference.normalize home dependency.Consumer
                    Producer = MemberReference.normalize home dependency.Producer })

        let declaration =
            { Id = request.GroupId
              Members = members
              Kind = request.Kind
              Origin = request.Origin
              SharedContext = request.SharedContext
              ExecutionRepository = Some request.ExecutionRepository
              CrossRepository = request.CrossRepository
              ArchitectureNotes = request.ArchitectureNotes }

        let argumentRejections =
            [ if not (isValidGroupId request.GroupId) then
                  yield GroupRejection.InvalidGroupId request.GroupId
              if members.IsEmpty then
                  yield GroupRejection.NoMembers
              yield! repeated members |> List.map GroupRejection.RepeatedMember ]

        match argumentRejections, tryFind context.Groups request.GroupId with
        // An identical declaration already holds: accepted, nothing appended
        // (PRX-GRP-114). Eligibility is not re-judged: members may have
        // completed since the group was created.
        | [], Some existing when
            existing.Declaration = declaration
            && existing.Dependencies = dependencies
            && (home.IsNone || existing.HomeRepository.IsNone || existing.HomeRepository = home)
            ->
            unchanged existing
        | rejections, existing ->
            let admitted, observations, warnings = admitAll context declaration (List.distinct members)

            let all =
                [ yield! rejections |> List.filter (function GroupRejection.InvalidGroupId _ -> true | _ -> false)
                  if existing.IsSome && isValidGroupId request.GroupId then
                      yield GroupRejection.DuplicateGroup request.GroupId
                  if isValidGroupId request.GroupId then
                      yield! identityRejections request.GroupId request.CrossRepository
                  if request.CrossRepository && home.IsNone then
                      yield GroupRejection.HomeUnknown request.GroupId
                  yield! rejections |> List.filter (function GroupRejection.InvalidGroupId _ -> false | _ -> true)
                  yield! admitted
                  yield! dependencyRejections members dependencies ]

            match all with
            | [] ->
                changed
                    { Declaration = declaration
                      HomeRepository = home
                      Dependencies = dependencies
                      Verifications = observations
                      CreatedAt = request.OccurredAt
                      CreatedBy = request.Actor
                      History = [ entry GroupOperation.Created None request.OccurredAt request.Actor request.Reason false request.ExecutionId None ]
                      Checkpoints = [] }
                    warnings
            | rejections -> Error rejections

    /// The group a membership change names, or why it cannot be found.
    let private existing (context: GroupContext) (groupId: string) : Result<StoredWorkGroup, GroupRejection list> =
        if not (isValidGroupId groupId) then Error [ GroupRejection.InvalidGroupId groupId ]
        else tryFind context.Groups groupId |> Option.map Ok |> Option.defaultValue (Error [ GroupRejection.UnknownGroup groupId ])

    /// `work group add`: one more member, joining by the same rule as at
    /// creation, recorded with who added it. The member's own record is
    /// not touched.
    let add (context: GroupContext) (request: GroupMemberRequest) : Result<GroupChange, GroupRejection list> =
        existing context request.GroupId
        |> Result.bind (fun group ->
            let declaration = group.Declaration
            let home = group.HomeRepository
            let memberId = MemberReference.normalize home request.WorkItemId

            let dependencies =
                request.Dependencies
                |> List.map (fun dependency ->
                    { dependency with
                        Consumer = MemberReference.normalize home dependency.Consumer
                        Producer = MemberReference.normalize home dependency.Producer })

            let newEdges = dependencies |> List.filter (fun edge -> not (group.Dependencies |> List.contains edge))

            if declaration.Members |> List.contains memberId && newEdges.IsEmpty then
                unchanged group
            else
                let joining = not (declaration.Members |> List.contains memberId)
                let members = if joining then declaration.Members @ [ memberId ] else declaration.Members

                let admitted =
                    if joining then admission context declaration memberId else Ok(None, [])

                let edgeRejections = dependencyRejections members (group.Dependencies @ newEdges)

                match admitted, edgeRejections with
                | Ok(observation, warnings), [] ->
                    changed
                        { group with
                            Declaration = { declaration with Members = members }
                            Dependencies = group.Dependencies @ newEdges
                            Verifications = group.Verifications @ Option.toList observation
                            History =
                                group.History
                                @ [ entry
                                        GroupOperation.MemberAdded
                                        (Some memberId)
                                        request.OccurredAt
                                        request.Actor
                                        request.Reason
                                        false
                                        request.ExecutionId
                                        (match observation with
                                         | Some observed ->
                                             match observed.Outcome with
                                             | ObservedMember.Read state -> state
                                             | ObservedMember.Unobservable _ -> None
                                         | None -> MemberStanding.state (context.Standing memberId)) ] }
                        warnings
                | Error rejections, others -> Error(rejections @ others)
                | Ok _, rejections -> Error rejections)

    /// The latest history entry that names a member, if any.
    let private lastEntryFor (group: StoredWorkGroup) (workItemId: string) =
        group.History |> List.filter (fun entry -> entry.Member = Some workItemId) |> List.tryLast

    /// `work group remove`: one member leaves. Any member may leave,
    /// whatever its state; its lifecycle, evidence and attribution are not
    /// touched. The last member leaves only with `AllowEmpty`, which the
    /// history records. Its order edges leave with it.
    let remove (context: GroupContext) (request: GroupMemberRequest) : Result<GroupChange, GroupRejection list> =
        existing context request.GroupId
        |> Result.bind (fun group ->
            let declaration = group.Declaration
            let memberId = MemberReference.normalize group.HomeRepository request.WorkItemId

            match declaration.Members |> List.contains memberId, declaration.Members.Length with
            | false, _ ->
                // Already removed: the end state holds (PRX-GRP-114).
                match lastEntryFor group memberId with
                | Some last when last.Operation = GroupOperation.MemberRemoved -> unchanged group
                | _ -> Error [ GroupRejection.NotMember(memberId, declaration.Id) ]
            | true, 1 when not request.AllowEmpty -> Error [ GroupRejection.LastMember(memberId, declaration.Id) ]
            | true, remaining ->
                let state =
                    match MemberReference.parse memberId with
                    | MemberReference.Local id -> MemberStanding.state (context.Standing id)
                    | MemberReference.Qualified(repository, id) ->
                        match (context.Observe repository id).Outcome with
                        | ObservedMember.Read state -> state
                        | ObservedMember.Unobservable _ -> None

                changed
                    { group with
                        Declaration = { declaration with Members = declaration.Members |> List.filter ((<>) memberId) }
                        Dependencies = group.Dependencies |> List.filter (fun edge -> edge.Consumer <> memberId && edge.Producer <> memberId)
                        History =
                            group.History
                            @ [ entry
                                    GroupOperation.MemberRemoved
                                    (Some memberId)
                                    request.OccurredAt
                                    request.Actor
                                    request.Reason
                                    (remaining = 1)
                                    request.ExecutionId
                                    state ] }
                    [])

    /// The declarations the planner reads: every configured group, then every
    /// stored group whose ID the configuration does not already declare (an
    /// explicit per-invocation configuration is the narrower source).
    let declarations (configured: DeclaredGroup list) (stored: StoredWorkGroup list) : DeclaredGroup list =
        let configuredIds = configured |> List.map (fun group -> group.Id) |> Set.ofList
        configured @ (stored |> List.map (fun group -> group.Declaration) |> List.filter (fun group -> not (configuredIds.Contains group.Id)))

    /// A group may be empty only when its latest history entry deliberately
    /// removed its last member.
    let private explicitlyEmpty (group: StoredWorkGroup) =
        group.History |> List.tryLast |> Option.exists (fun last -> last.ExplicitEmpty)

    /// `validate`'s view of stored groups: (group ID or "", field, message).
    /// Terminal members are valid: members complete inside their group
    /// (PRX-GRP-042); only joining requires a non-terminal item. Repository
    /// locality is checked when an item joins, against the configuration
    /// supplied then; it is not re-derived here without that configuration.
    let findings (context: GroupContext) : (string * string * string) list =
        let groups = context.Groups

        [ for duplicate in groups |> List.map (fun group -> group.Declaration.Id) |> repeated do
              yield duplicate, "id", $"group {duplicate} is recorded more than once"
          for group in groups do
              let declaration = group.Declaration
              let id = declaration.Id

              if not (isValidGroupId id) then
                  yield id, "id", $"'{id}' is not a group ID (GROUP-<AREA>-<SEQUENCE>)"
              if declaration.Members.IsEmpty && not (explicitlyEmpty group) then
                  yield id, "members", $"group {id} has no members and no explicit empty removal records why"
              for repeatedMember in repeated declaration.Members do
                  yield id, "members", $"{repeatedMember} is a member of {id} more than once"
              for memberId in declaration.Members |> List.distinct do
                  match MemberReference.parse memberId with
                  | MemberReference.Qualified(_, workItemId) ->
                      // Governed in its own repository; observed, not validated here.
                      if not (WorkItemId.isValid workItemId) then
                          yield id, "members", $"'{memberId}' is not a valid qualified work-item ID"
                  | MemberReference.Local _ ->
                      if not (WorkItemId.isValid memberId) then
                          yield id, "members", $"'{memberId}' is not a valid work-item ID"
                      elif context.Standing memberId = MemberStanding.Unknown then
                          yield id, "members", $"member {memberId} of {id} is not a recorded work item"
              match group.History with
              | first :: _ when first.Operation = GroupOperation.Created -> ()
              | _ -> yield id, "history", $"group {id}'s history must begin with its creation"
              if group.History |> List.skip (min 1 group.History.Length) |> List.exists (fun entry -> entry.Operation = GroupOperation.Created) then
                  yield id, "history", $"group {id} records more than one creation"
              for entry in group.History do
                  if entry.Operation <> GroupOperation.Created && entry.Member.IsNone then
                      yield id, "history", $"a {GroupOperation.code entry.Operation} entry of {id} names no member" ]

    /// Re-validates every stored group checkpoint offline: it was durable
    /// when recorded (local commit equals the remote commit), its text is
    /// present, its member standings are disjoint, and every member
    /// checkpoint it references is one of that member's own recorded
    /// checkpoints. `ownerOf` maps a recorded work checkpoint's ID to the
    /// work item that recorded it.
    let checkpointFindings (ownerOf: string -> string option) (groups: StoredWorkGroup list) : (string * string * string) list =
        [ for group in groups do
              let id = group.Declaration.Id

              for duplicate in group.Checkpoints |> List.map (fun checkpoint -> checkpoint.CheckpointId) |> repeated do
                  yield id, "checkpoints", $"group checkpoint {duplicate} is recorded more than once"

              for checkpoint in group.Checkpoints do
                  let field = $"checkpoints.{checkpoint.CheckpointId}"
                  let location = checkpoint.Location

                  if location.LocalCommit <> location.RemoteCommit then
                      yield id, field, $"recorded commit {location.LocalCommit.Value} is not the remote commit {location.RemoteCommit.Value}; the checkpoint was not durable"

                  if (RequiredText.tryCreate checkpoint.Summary).IsNone then
                      yield id, field, "summary is blank"

                  if (RequiredText.tryCreate checkpoint.NextAction).IsNone then
                      yield id, field, "nextAction is blank"

                  let standings =
                      checkpoint.Completed @ checkpoint.Active @ checkpoint.Blocked @ checkpoint.Remaining @ checkpoint.Abandoned

                  for member' in standings |> repeated do
                      yield id, field, $"member {member'} is listed under more than one standing"

                  for reference in checkpoint.MemberCheckpoints do
                      if not (standings |> List.contains reference.WorkItemId) then
                          yield id, field, $"references a checkpoint of {reference.WorkItemId}, which the checkpoint does not list as a member"

                      match ownerOf reference.CheckpointId with
                      | Some owner when owner = reference.WorkItemId -> ()
                      | Some owner ->
                          yield id, field, $"references checkpoint {reference.CheckpointId} as {reference.WorkItemId}'s, but {owner} recorded it"
                      | None ->
                          yield id, field, $"references checkpoint {reference.CheckpointId} of {reference.WorkItemId}, which is not a recorded work checkpoint" ]

    /// Each member's own standing and who it gates inside the group; the
    /// same classification serves `work group show` and group checkpoints.
    let progress (group: StoredWorkGroup) (facts: string -> MemberFacts) : GroupProgress =
        let members = group.Declaration.Members
        let known = members |> List.map (fun id -> id, facts id) |> Map.ofList

        let open' id =
            match MemberCategory.ofState known[id].State with
            | MemberCategory.Completed
            | MemberCategory.Abandoned -> false
            | _ -> true

        let rows =
            members
            |> List.map (fun id ->
                let fact = known[id]

                { WorkItemId = id
                  Category = MemberCategory.ofState fact.State
                  State = fact.State
                  PlanningState = fact.PlanningState
                  WaitsOn = fact.WaitsOn
                  WaitsOnBlocked = fact.WaitsOnBlocked
                  Repository = MemberReference.repository group.HomeRepository (MemberReference.parse id)
                  Observation = fact.Observation
                  Stale = fact.Stale
                  Gates = members |> List.filter (fun other -> other <> id && open' other && known[other].WaitsOn |> List.contains id) })

        let inCategory category =
            rows |> List.filter (fun row -> row.Category = category) |> List.map (fun row -> row.WorkItemId)

        { Members = rows
          Completed = inCategory MemberCategory.Completed
          Abandoned = inCategory MemberCategory.Abandoned
          Active = inCategory MemberCategory.Active
          Blocked = inCategory MemberCategory.Blocked
          Remaining = inCategory MemberCategory.Remaining
          Unknown = inCategory MemberCategory.Unknown }

    /// Members removed while open, each with its latest removal reason
    /// (PRX-GRP-116). A member that rejoined is current again and is not
    /// listed. `currentState` judges removals recorded before store version 2.
    let removedOpen (group: StoredWorkGroup) (currentState: string -> string option) : RemovedOpenMember list =
        let current = Set.ofList group.Declaration.Members
        let terminal (state: string option) = state = Some "complete" || state = Some "abandoned"

        group.History
        |> List.choose (fun entry -> entry.Member)
        |> List.distinct
        |> List.filter (fun id -> not (current.Contains id))
        |> List.choose (fun id ->
            match lastEntryFor group id with
            | Some last when last.Operation = GroupOperation.MemberRemoved ->
                let state = last.MemberState |> Option.orElse (currentState id)

                if terminal state then None
                else
                    Some
                        { WorkItemId = id
                          RemovedAt = last.At
                          Reason = last.Reason
                          StateAtRemoval = last.MemberState }
            | _ -> None)

    /// PRX-GRP-113: history is append-only. Every group in the committed
    /// store must still exist, and its committed history must be an exact
    /// prefix of the current one: nothing rewritten, reordered or truncated.
    /// `source` names the committed revision in the messages.
    let historyFindings (source: string) (committed: StoredWorkGroup list) (current: StoredWorkGroup list) : (string * string * string) list =
        [ for before in committed do
              let id = before.Declaration.Id

              match tryFind current id with
              | None -> yield id, "history", $"group {id} is recorded in {source} but no longer in the store; groups and their history are append-only"
              | Some after ->
                  let kept = before.History.Length

                  if before.HomeRepository.IsSome && after.HomeRepository <> before.HomeRepository then
                      yield id, "homeRepository", $"group {id}'s home changed from {before.HomeRepository.Value} recorded in {source}; a home never changes implicitly (PRX-GRP-101)"

                  if after.History.Length < kept then
                      yield id, "history", $"group {id}'s history has {after.History.Length} entries but {source} records {kept}; history is append-only and was truncated"
                  elif after.History |> List.truncate kept <> before.History then
                      yield id, "history", $"group {id}'s history differs from {source} in its first {kept} entries; history is append-only and was rewritten or reordered" ]

    /// One `work group list` row, derived from the members' own facts.
    let summarize (group: StoredWorkGroup) (facts: string -> MemberFacts) : GroupSummary =
        let declaration = group.Declaration
        let standing = progress group facts

        { GroupId = declaration.Id
          Kind = declaration.Kind
          Origin = declaration.Origin
          Home = group.HomeRepository |> Option.orElse declaration.ExecutionRepository
          ExecutionRepository = declaration.ExecutionRepository
          CrossRepository = declaration.CrossRepository
          MemberCount = declaration.Members.Length
          Status = GroupStatus.derive standing
          Progress = standing
          RemovedOpen = removedOpen group (fun id -> (facts id).State)
          ExecutionMode = None
          LatestCheckpointAt = group.Checkpoints |> List.tryLast |> Option.map (fun checkpoint -> checkpoint.RecordedAt) }

    /// `work group list`: read-only, every filter applied, sorted by ID.
    let list (filter: GroupListFilter) (summaries: GroupSummary list) : GroupSummary list =
        let matches (summary: GroupSummary) =
            filter.Status |> Option.forall ((=) summary.Status)
            && filter.Member |> Option.forall (fun id -> summary.Progress.Members |> List.exists (fun row -> row.WorkItemId = id))
            && filter.Repository
               |> Option.forall (fun repository ->
                   summary.Home = Some repository
                   || summary.ExecutionRepository = Some repository
                   || summary.Progress.Members |> List.exists (fun row -> row.Repository = Some repository))

        summaries
        |> List.filter matches
        |> List.sortWith (fun left right -> String.CompareOrdinal(left.GroupId, right.GroupId))

    /// Each order edge judged against its producer (PRX-GRP-105).
    /// `reached` answers whether a member reached a milestone: `Some` when
    /// known, `None` when its repository is unobservable or stale.
    let order (group: StoredWorkGroup) (reached: string -> ProducerMilestone -> Result<bool, string>) : OrderRow list =
        group.Dependencies
        |> List.map (fun dependency ->
            { Dependency = dependency
              State =
                match reached dependency.Producer dependency.Milestone with
                | Ok true -> EdgeState.Satisfied
                | Ok false -> EdgeState.Waiting
                | Error reason -> EdgeState.Unknown reason })

    /// A consumer's planning state over its edges: `waiting` when a
    /// producer has not reached its milestone, `unknown` when one cannot be
    /// observed, else `None` (free to proceed).
    let consumerState (rows: OrderRow list) (consumer: string) : string option =
        let mine = rows |> List.filter (fun row -> row.Dependency.Consumer = consumer)

        if mine |> List.exists (fun row -> row.State = EdgeState.Waiting) then Some "waiting"
        elif mine |> List.exists (fun row -> match row.State with EdgeState.Unknown _ -> true | _ -> false) then Some "unknown"
        else None

    /// `k of n complete` per repository, home first then by name (PRX-GRP-106).
    let repositoryProgress (progress: GroupProgress) : RepositoryProgress list =
        progress.Members
        |> List.groupBy (fun row -> row.Repository |> Option.defaultValue "unknown")
        |> List.map (fun (repository, rows) ->
            { Repository = repository
              Completed = rows |> List.filter (fun row -> row.Category = MemberCategory.Completed) |> List.length
              Total = rows.Length
              Unknown = rows |> List.filter (fun row -> row.Category = MemberCategory.Unknown) |> List.length })
        |> List.sortWith (fun left right -> String.CompareOrdinal(left.Repository, right.Repository))

    /// Qualified members whose repository was read and holds no reference
    /// to this group (PRX-GRP-102): `unlinked`, a warning.
    let unlinkedMembers (progress: GroupProgress) : string list =
        progress.Members
        |> List.filter (fun row -> row.Observation |> Option.bind (fun observation -> observation.Linked) = Some false)
        |> List.map (fun row -> row.WorkItemId)

    /// `work group link`: the member repository records an immutable
    /// reference to a group whose home is elsewhere. It writes nothing else.
    let link (context: GroupContext) (store: GroupStore) (request: GroupLinkRequest) : Result<LinkChange, GroupRejection list> =
        let reference =
            { GroupId = request.GroupId
              HomeRepository = request.HomeRepository
              WorkItemId = request.WorkItemId
              LinkedAt = request.OccurredAt
              Actor = request.Actor
              ExecutionId = request.ExecutionId }

        let recorded =
            store.References |> List.tryFind (fun existing -> existing.GroupId = request.GroupId && existing.WorkItemId = request.WorkItemId)

        let rejections =
            [ if not (isValidGroupId request.GroupId) then
                  yield GroupRejection.InvalidGroupId request.GroupId
              else
                  yield! identityRejections request.GroupId true
              if not (WorkItemId.isValid request.WorkItemId) then
                  yield GroupRejection.InvalidMemberId request.WorkItemId
              else
                  match context.Standing request.WorkItemId with
                  | MemberStanding.Unknown -> yield GroupRejection.UnknownMember request.WorkItemId
                  | MemberStanding.Terminal state when recorded.IsNone -> yield GroupRejection.TerminalMember(request.WorkItemId, state)
                  | _ -> ()
              if context.ThisRepository = Some request.HomeRepository || (tryFind store.Groups request.GroupId).IsSome then
                  yield GroupRejection.HomeIsThisRepository request.GroupId
              match recorded with
              | Some existing when existing.HomeRepository <> request.HomeRepository ->
                  yield GroupRejection.ConflictingReference(request.GroupId, request.WorkItemId, existing.HomeRepository)
              | _ -> () ]

        match rejections, recorded with
        | [], Some existing -> Ok { Store = store; Reference = existing; Changed = false }
        | [], None ->
            let references =
                store.References @ [ reference ]
                |> List.sortWith (fun left right -> String.CompareOrdinal($"{left.GroupId}\u0000{left.WorkItemId}", $"{right.GroupId}\u0000{right.WorkItemId}"))

            Ok { Store = { store with References = references }; Reference = reference; Changed = true }
        | rejections, _ -> Error rejections

    /// References on this repository's items whose home was read and does
    /// not list the item (PRX-GRP-102). `membership` reads the home:
    /// `Some true|false`, or `None` when it cannot be observed.
    let unlinkedReferences (references: GroupReference list) (thisRepository: string option) (membership: GroupReference -> bool option) =
        references
        |> List.filter (fun reference -> membership reference = Some false)
        |> List.map (fun reference ->
            let qualified = thisRepository |> Option.map (fun repository -> $"{repository}:{reference.WorkItemId}") |> Option.defaultValue reference.WorkItemId
            reference, $"{reference.WorkItemId} references {reference.GroupId} at {reference.HomeRepository}, whose group does not list {qualified}: unlinked")

    /// `validate`'s checks of the cross-repository parts of the store.
    let crossRepositoryFindings (store: GroupStore) (standing: string -> MemberStanding) : (string * string * string) list =
        [ for group in store.Groups do
              let declaration = group.Declaration
              let id = declaration.Id

              if isValidGroupId id then
                  for rejection in identityRejections id declaration.CrossRepository do
                      yield id, "id", GroupRejection.message rejection

              if declaration.CrossRepository && group.HomeRepository.IsNone then
                  yield id, "homeRepository", $"cross-repository group {id} records no home repository"

              for memberId in declaration.Members do
                  if MemberReference.isQualified memberId && not declaration.CrossRepository then
                      yield id, "members", GroupRejection.message (GroupRejection.ForeignMember memberId)

              for rejection in dependencyRejections declaration.Members group.Dependencies do
                  yield id, "dependencies", GroupRejection.message rejection
          for reference in store.References do
              let field = $"references.{reference.GroupId}.{reference.WorkItemId}"

              for rejection in identityRejections reference.GroupId true do
                  yield reference.GroupId, field, GroupRejection.message rejection

              if not (RepositoryName.isValid reference.HomeRepository) then
                  yield reference.GroupId, field, $"home '{reference.HomeRepository}' is not owner/repo"

              if standing reference.WorkItemId = MemberStanding.Unknown then
                  yield reference.GroupId, field, $"{reference.WorkItemId} is not a recorded work item"
          for duplicate in store.References |> List.countBy (fun reference -> reference.GroupId, reference.WorkItemId) |> List.filter (fun (_, count) -> count > 1) |> List.map fst do
              yield fst duplicate, "references", $"{snd duplicate} references {fst duplicate} more than once" ]

    /// The ownership half of `work checkpoint`'s rule, over the group: at
    /// least one member is active, and at least one active member's
    /// execution resolves to the caller.
    let private ownership (group: StoredWorkGroup) (active: string list) (execution: string -> ExecutionObservation) =
        let unowned id =
            match execution id with
            | ExecutionObservation.Resolved _ -> None
            | ExecutionObservation.NoneActive -> Some(id, "no active execution belongs to the caller")
            | ExecutionObservation.Ambiguous ids -> Some(id, $"""several executions could be the caller ({String.concat ", " ids})""")
            | ExecutionObservation.Refused reason -> Some(id, reason)

        match active, active |> List.choose unowned with
        | [], _ -> [ GroupCheckpointRejection.NoActiveMember group.Declaration.Id ]
        | _, reasons when reasons.Length = active.Length -> [ GroupCheckpointRejection.NoOwnExecution reasons ]
        | _ -> []

    /// `work group checkpoint`: a durable group-level checkpoint over the
    /// members' own. `location` is the shared Git verification's verdict
    /// (`CheckpointVerification.verifyLocation`); `execution` observes the
    /// caller's execution for a member, which `work checkpoint` also
    /// requires; `memberCheckpoint` gives a member's own latest verified
    /// checkpoint. Nothing about any member is written.
    let checkpoint
        (context: GroupContext)
        (request: GroupCheckpointRequest)
        (facts: string -> MemberFacts)
        (execution: string -> ExecutionObservation)
        (memberCheckpoint: string -> MemberCheckpointReference option)
        (location: Result<GitDurableLocation, CheckpointRejection list>)
        : Result<StoredWorkGroup * GroupCheckpoint, GroupCheckpointRejection list> =
        let found = existing context request.GroupId |> Result.mapError (List.map GroupCheckpointRejection.Group)

        let owned =
            match found with
            | Ok group -> ownership group (progress group facts).Active execution
            | Error _ -> []

        let text =
            [ if (RequiredText.tryCreate request.Summary).IsNone then
                  GroupCheckpointRejection.Durability CheckpointRejection.BlankSummary
              if (RequiredText.tryCreate request.NextAction).IsNone then
                  GroupCheckpointRejection.Durability CheckpointRejection.BlankNextAction ]

        let durability =
            match location with
            | Ok _ -> []
            | Error rejections -> rejections |> List.map GroupCheckpointRejection.Durability

        match found, text @ owned @ durability, location with
        | Ok group, [], Ok git ->
            let standing = progress group facts

            let recorded =
                { CheckpointId = request.CheckpointId
                  RecordedAt = request.OccurredAt
                  Actor = request.Actor
                  Summary = request.Summary.Trim()
                  NextAction = request.NextAction.Trim()
                  Decisions = request.Decisions
                  Completed = standing.Completed
                  Active = standing.Active
                  Blocked = standing.Blocked
                  Remaining = standing.Remaining @ standing.Unknown
                  Abandoned = standing.Abandoned
                  MemberCheckpoints = group.Declaration.Members |> List.filter (MemberReference.isQualified >> not) |> List.choose memberCheckpoint
                  MemberObservations =
                    group.Declaration.Members
                    |> List.choose (fun memberId ->
                        match MemberReference.parse memberId with
                        | MemberReference.Qualified(repository, workItemId) -> Some(context.Observe repository workItemId)
                        | MemberReference.Local _ -> None)
                  Location = git }

            Ok({ group with Checkpoints = group.Checkpoints @ [ recorded ] }, recorded)
        | Error rejections, others, _ -> Error(rejections @ others)
        | Ok _, rejections, _ -> Error rejections
