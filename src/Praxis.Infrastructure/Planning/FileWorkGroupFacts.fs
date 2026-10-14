namespace Praxis.Infrastructure.Planning

open System
open System.Globalization
open System.IO
open Praxis.Contracts.Planning
open Praxis.Contracts.Work
open Praxis.Domain.Planning
open Praxis.Domain.Provenance
open Praxis.Domain.Telemetry
open Praxis.Domain.Work
open Praxis.Infrastructure.Git
open Praxis.Infrastructure.Work

/// The repository facts every work-group command reads (PRX-GRP-110..116):
/// member standings, where items execute, the planner's view of members,
/// the caller's execution and the committed store. Read-only; every
/// decision stays in `Praxis.Domain.Work.WorkGroups`.
[<RequireQualifiedAccess>]
module FileWorkGroupFacts =
    let now () =
        DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

    let private resolve (root: string) (path: string) =
        if Path.IsPathRooted path then path else Path.GetFullPath(Path.Combine(root, path))

    /// Planner configuration from an explicit file, else the repository's
    /// own (`ros.json` `planner`), else the defaults.
    let configuration (root: string) (configurationFile: string option) : Result<PlannerConfiguration, string> =
        FilePlanningRepository.readBaseConfiguration root (configurationFile |> Option.map (resolve root))

    /// The standing of every recorded item, from the queue and live context.
    let standing (root: string) : Result<string -> MemberStanding, string> =
        FilePlanningRepository.readQueue root
        |> Result.bind (fun queue -> FilePlanningRepository.readLive root |> Result.map (MemberStanding.lookup queue))

    /// This checkout's `owner/repo`, from its `origin` remote.
    let thisRepository (root: string) : string option =
        match ProcessGitRepository.readLines root [ "remote"; "get-url"; "origin" ] with
        | Ok [ url ] -> RepositoryName.ofRemoteUrl url
        | _ -> None

    /// Member repositories read at most once per command, read-only.
    let private reader (sources: RepositorySource list) : string -> FileGroupObservation.RepositoryRead =
        let cache = Collections.Generic.Dictionary<string, FileGroupObservation.RepositoryRead>()

        fun repository ->
            match cache.TryGetValue repository with
            | true, read -> read
            | _ ->
                let read = FileGroupObservation.read sources repository
                cache[repository] <- read
                read

    /// `Observe` for a group: dated observations of members elsewhere.
    let observer (sources: RepositorySource list) (groupId: string option) : string -> string -> MemberObservation =
        let read = reader sources
        let at = now ()
        fun repository workItemId -> FileGroupObservation.observe at groupId (read repository) workItemId

    /// The decision context: stored groups, standings, the planner's own
    /// execution-location rule (`Grouping.executionLocation`, PRX-GRP-051)
    /// and read-only observation of member repositories (PRX-GRP-103).
    let contextFor (root: string) (configurationFile: string option) (groupId: string option) (groups: StoredWorkGroup list) : Result<string * GroupContext, string> =
        FilePlanningRepository.readQueue root
        |> Result.bind (fun queue ->
            FilePlanningRepository.readLive root
            |> Result.bind (fun live ->
                configuration root configurationFile
                |> Result.map (fun configured ->
                    let repository = (FilePlanningRepository.readRepository root).Name

                    repository,
                    { Groups = groups
                      Standing = MemberStanding.lookup queue live
                      RepositoryOf = Grouping.executionLocation configured.Grouping repository queue >> fst
                      ThisRepository = thisRepository root
                      Observe = observer configured.Grouping.CrossRepository.Sources groupId })))

    let context (root: string) (configurationFile: string option) (groups: StoredWorkGroup list) = contextFor root configurationFile None groups

    /// Facts per member as of now: the planner's read-only analysis for
    /// home members, and a dated observation for members of other
    /// repositories, stale after `maxObservationAgeMinutes` (PRX-GRP-103).
    let memberFactsFor (root: string) (configurationFile: string option) (groupId: string option) : Result<string -> MemberFacts, string> =
        let port = FilePlanningRepository.create root None (configurationFile |> Option.map (resolve root))

        configuration root configurationFile
        |> Result.bind (fun configured ->
            standing root
            |> Result.bind (fun standing ->
                Praxis.Application.Planning.PlanningOperations.analyze port (now ()) "work-group"
                |> Result.map (fun (_, analysis) ->
                    let local = MemberFacts.ofAnalysis analysis standing
                    let cross = configured.Grouping.CrossRepository
                    let observe = observer cross.Sources groupId
                    let at = DateTimeOffset.UtcNow

                    fun memberId ->
                        match MemberReference.parse memberId with
                        | MemberReference.Local id -> local id
                        | MemberReference.Qualified(repository, id) -> MemberFacts.ofObservation at cross.MaxObservationAgeMinutes (observe repository id))))

    let memberFacts (root: string) (configurationFile: string option) = memberFactsFor root configurationFile None

    /// Whether a member reached a milestone (PRX-GRP-105): home members from
    /// this checkout, others from their observed clone. An unobservable or
    /// stale producer is an error (`unknown`), never a pass.
    let milestones (root: string) (configurationFile: string option) (group: StoredWorkGroup) : Result<string -> ProducerMilestone -> Result<bool, string>, string> =
        configuration root configurationFile
        |> Result.bind (fun configured ->
            memberFactsFor root configurationFile (Some group.Declaration.Id)
            |> Result.map (fun facts ->
                let sources = configured.Grouping.CrossRepository.Sources

                let clone memberId =
                    match MemberReference.parse memberId with
                    | MemberReference.Local _ -> Ok(root, "HEAD")
                    | MemberReference.Qualified(repository, _) ->
                        match sources |> List.tryFind (fun source -> source.Repository = repository) with
                        | Some source -> Ok(source.Path, source.Ref |> Option.defaultValue "origin/HEAD")
                        | None -> Error $"no observation source is configured for {repository}"

                fun producer milestone ->
                    let fact = facts producer

                    if fact.Stale then
                        Error $"{producer}'s observation is stale"
                    else
                        match milestone with
                        | ProducerMilestone.Complete ->
                            match fact.State, fact.Observation with
                            | Some state, _ -> Ok(state = "complete")
                            | None, Some _ -> Error $"{producer} could not be observed"
                            | None, None -> Error $"{producer} is not a recorded work item"
                        | ProducerMilestone.Released tag -> clone producer |> Result.bind (fun (path, _) -> FileGroupObservation.tagExists path tag)
                        | ProducerMilestone.Merged ->
                            let checkpoint =
                                match fact.Observation with
                                | Some observation -> observation.LatestCheckpoint |> Option.map (fun (_, commit, _) -> commit)
                                | None ->
                                    FilePlanningRepository.readLive root
                                    |> Result.toOption
                                    |> Option.bind (List.tryFind (fun item -> item.Id = producer))
                                    |> Option.bind (fun item -> item.Checkpoint)
                                    |> Option.map (fun checkpoint -> checkpoint.Commit)

                            match checkpoint with
                            | None -> Ok false
                            | Some commit -> clone producer |> Result.bind (fun (path, reference) -> FileGroupObservation.isMerged path commit reference)))

    /// Whether a group's home lists a reference's item (PRX-GRP-102):
    /// `None` when the home cannot be observed.
    let homeMembership (root: string) (configurationFile: string option) : Result<GroupReference -> bool option, string> =
        configuration root configurationFile
        |> Result.map (fun configured ->
            let read = reader configured.Grouping.CrossRepository.Sources
            let self = thisRepository root

            fun reference ->
                match (read reference.HomeRepository).Groups, self with
                | Ok groups, Some repository ->
                    Some(
                        WorkGroups.tryFind groups reference.GroupId
                        |> Option.exists (fun group -> group.Declaration.Members |> List.contains $"{repository}:{reference.WorkItemId}")
                    )
                | _ -> None)

    /// The caller's own active execution, recorded on group history when
    /// there is exactly one (PRX-GRP-113).
    let callerExecution (root: string) (overrides: IdentityInputs) : string option =
        match FileCheckpointRepository.resolveCallerExecution root overrides with
        | ExecutionObservation.Resolved(executionId, _) -> Some executionId
        | _ -> None

    /// The committed store at each revision the append-only rule compares
    /// against (PRX-GRP-113): `HEAD`, and `$ROS_BASE_REF` when it resolves.
    /// A revision without the store, or with an unreadable one, contributes
    /// nothing; its own problems are reported by validating that revision.
    let committed (root: string) : (string * StoredWorkGroup list) list =
        let baseRef =
            match Environment.GetEnvironmentVariable "ROS_BASE_REF" with
            | null -> []
            | value when String.IsNullOrWhiteSpace value -> []
            | value -> [ value.Trim() ]

        "HEAD" :: baseRef
        |> List.distinct
        |> List.choose (fun revision ->
            ProcessGitRepository.readFileAtRevision root revision FileWorkGroupRepository.relativePath
            |> Option.bind (fun content ->
                match WorkGroupJson.readStore content with
                | Ok groups -> Some(revision, groups)
                | Error _ -> None))
