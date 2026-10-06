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

    /// Planner configuration from an explicit file, else the defaults.
    let configuration (root: string) (configurationFile: string option) : Result<PlannerConfiguration, string> =
        match configurationFile |> Option.map (resolve root) with
        | None -> Ok PlannerConfiguration.defaults
        | Some file when not (File.Exists file) -> Error $"{file} does not exist"
        | Some file -> PlanningJson.parseConfiguration (File.ReadAllText file)

    /// The standing of every recorded item, from the queue and live context.
    let standing (root: string) : Result<string -> MemberStanding, string> =
        FilePlanningRepository.readQueue root
        |> Result.bind (fun queue -> FilePlanningRepository.readLive root |> Result.map (MemberStanding.lookup queue))

    /// The decision context: stored groups, standings and the planner's own
    /// execution-location rule (`Grouping.executionLocation`, PRX-GRP-051).
    let context (root: string) (configurationFile: string option) (groups: StoredWorkGroup list) : Result<string * GroupContext, string> =
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
                      RepositoryOf = Grouping.executionLocation configured.Grouping repository queue >> fst })))

    /// Facts per member from the planner's read-only analysis, as of now.
    let memberFacts (root: string) (configurationFile: string option) : Result<string -> MemberFacts, string> =
        let port = FilePlanningRepository.create root None (configurationFile |> Option.map (resolve root))

        standing root
        |> Result.bind (fun standing ->
            Praxis.Application.Planning.PlanningOperations.analyze port (now ()) "work-group"
            |> Result.map (fun (_, analysis) -> MemberFacts.ofAnalysis analysis standing))

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
