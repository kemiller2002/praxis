namespace Praxis.Infrastructure.Planning

open System
open System.IO
open Praxis.Contracts.Work
open Praxis.Domain.Git
open Praxis.Domain.Planning
open Praxis.Domain.Work
open Praxis.Infrastructure.Git
open Praxis.Infrastructure.Work

/// Read-only observation of another repository's Praxis state
/// (PRX-GRP-103, PRX-GRP-109). A member repository is read from a local
/// clone at a fetched ref with `git show REF:PATH`, parsed by the same
/// readers as this repository's own state. Nothing is fetched, written,
/// committed or pushed there (PRX-GRP-104, PRX-GRP-108).
[<RequireQualifiedAccess>]
module FileGroupObservation =
    let method' = "git-ref"

    let private lines (path: string) (arguments: string list) = ProcessGitRepository.readLines path arguments

    let private classify (failure: GitFailure) : Unobservable =
        let message = failure.Message

        if message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
           || message.Contains("access denied", StringComparison.OrdinalIgnoreCase)
           || message.Contains("could not read", StringComparison.OrdinalIgnoreCase) then
            Unobservable.AccessDenied message
        else
            Unobservable.Unreachable $"git failed: {message}"

    /// A file at a ref: `Ok None` when the ref does not hold it.
    let private show (path: string) (reference: string) (relative: string) : Result<string option, Unobservable> =
        match lines path [ "show"; $"{reference}:{relative}" ] with
        | Ok content -> Ok(Some(String.concat "\n" content))
        | Error failure when
            failure.Message.Contains("does not exist", StringComparison.Ordinal)
            || failure.Message.Contains("exists on disk, but not in", StringComparison.Ordinal)
            ->
            Ok None
        | Error failure -> Error(classify failure)

    /// One repository's state at its ref, read once per command.
    type RepositoryRead =
        { Repository: string
          Ref: string option
          Commit: string option
          SourceAsOf: string option
          State: Result<string -> string option, Unobservable>
          Checkpoints: string -> (string * string * string) option
          References: Result<GroupReference list, Unobservable>
          Groups: Result<StoredWorkGroup list, Unobservable> }

    let private failed (repository: string) (reference: string option) (why: Unobservable) =
        { Repository = repository
          Ref = reference
          Commit = None
          SourceAsOf = None
          State = Error why
          Checkpoints = fun _ -> None
          References = Error why
          Groups = Error why }

    /// When the ref was last updated here (its reflog), else its commit time.
    let private asOf (path: string) (reference: string) (commit: string) =
        match lines path [ "reflog"; "show"; "-n"; "1"; "--date=iso-strict"; "--format=%gd"; reference ] with
        | Ok [ selector ] when selector.Contains '{' ->
            let inner = selector.Substring(selector.IndexOf '{' + 1).TrimEnd('}')

            match DateTimeOffset.TryParse inner with
            | true, at -> Some(at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
            | _ -> None
        | _ -> None
        |> Option.orElse (
            match lines path [ "log"; "-1"; "--format=%cI"; commit ] with
            | Ok [ date ] ->
                match DateTimeOffset.TryParse date with
                | true, at -> Some(at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
                | _ -> None
            | _ -> None
        )

    /// Reads one configured repository. An unconfigured repository, a
    /// missing clone or ref, a clone of a different repository, a ref
    /// without Praxis, or state this version cannot parse each make every
    /// member there unobservable, with the reason (PRX-GRP-109).
    let read (sources: RepositorySource list) (repository: string) : RepositoryRead =
        match sources |> List.tryFind (fun source -> source.Repository = repository) with
        | None -> failed repository None (Unobservable.Unreachable $"no observation source is configured for {repository} (grouping.crossRepository.repositories)")
        | Some source when not (Directory.Exists source.Path) -> failed repository source.Ref (Unobservable.Unreachable $"the clone {source.Path} does not exist")
        | Some source ->
            let reference = source.Ref |> Option.defaultValue "origin/HEAD"

            let identity =
                match lines source.Path [ "remote"; "get-url"; "origin" ] with
                | Ok [ url ] -> RepositoryName.ofRemoteUrl url
                | _ -> None

            match identity, lines source.Path [ "rev-parse"; "--verify"; "--quiet"; $"{reference}^{{commit}}" ] with
            | Some other, _ when other <> repository ->
                failed repository (Some reference) (Unobservable.Unreachable $"{source.Path} is a clone of {other}, not {repository}")
            | _, Error failure -> failed repository (Some reference) (Unobservable.Unreachable $"ref {reference} is not available in {source.Path}: {failure.Message}")
            | _, Ok [] -> failed repository (Some reference) (Unobservable.Unreachable $"ref {reference} is not available in {source.Path}")
            | _, Ok(commit :: _) ->
                let file relative = show source.Path commit relative

                let installed =
                    match file "ros.json", file ".ros/work/queue.json" with
                    | Error why, _
                    | _, Error why -> Error why
                    | Ok None, Ok None -> Error(Unobservable.NotInstalled $"{repository} at {reference} has no ros.json and no .ros/work/queue.json")
                    | Ok _, Ok queue -> Ok queue

                let live =
                    match file ".ros/context/current.json" with
                    | Error why -> Error why
                    | Ok None -> Ok []
                    | Ok(Some content) -> FilePlanningRepository.parseLive content |> Result.mapError (fun message -> Unobservable.UnsupportedSchema $"live context: {message}")

                let state =
                    match installed, live with
                    | Error why, _
                    | _, Error why -> Error why
                    | Ok queue, Ok live ->
                        match queue |> Option.map FilePlanningRepository.parseQueue |> Option.defaultValue (Ok []) with
                        | Error message -> Error(Unobservable.UnsupportedSchema $"queue: {message}")
                        | Ok queue ->
                            let standing = MemberStanding.lookup queue live
                            Ok(fun id -> MemberStanding.state (standing id))

                let checkpoints =
                    match live with
                    | Ok items ->
                        let byId =
                            items
                            |> List.choose (fun item -> item.Checkpoint |> Option.map (fun checkpoint -> item.Id, (checkpoint.CheckpointId, checkpoint.Commit, checkpoint.Branch)))
                            |> Map.ofList

                        byId.TryFind
                    | Error _ -> fun _ -> None

                let store =
                    match file FileWorkGroupRepository.relativePath with
                    | Error why -> Error why
                    | Ok None -> Ok { Groups = []; References = [] }
                    | Ok(Some content) -> WorkGroupJson.readGroupStore content |> Result.mapError (fun message -> Unobservable.UnsupportedSchema $"groups: {message}")

                { Repository = repository
                  Ref = Some reference
                  Commit = Some commit
                  SourceAsOf = asOf source.Path reference commit
                  State = state
                  Checkpoints = checkpoints
                  References = store |> Result.map (fun store -> store.References)
                  Groups = store |> Result.map (fun store -> store.Groups) }

    /// One member's dated observation from a repository read.
    let observe (now: string) (groupId: string option) (read: RepositoryRead) (workItemId: string) : MemberObservation =
        { Member = $"{read.Repository}:{workItemId}"
          Repository = read.Repository
          WorkItemId = workItemId
          Method = method'
          Ref = read.Ref
          Commit = read.Commit
          ObservedAt = now
          SourceAsOf = read.SourceAsOf
          Outcome =
            match read.State with
            | Ok state -> ObservedMember.Read(state workItemId)
            | Error why -> ObservedMember.Unobservable why
          LatestCheckpoint = read.Checkpoints workItemId
          Linked =
            match groupId, read.References with
            | Some id, Ok references -> Some(references |> List.exists (fun reference -> reference.GroupId = id && reference.WorkItemId = workItemId))
            | _ -> None }

    /// Whether a repository's tag exists (`released:TAG`).
    let tagExists (path: string) (tag: string) : Result<bool, string> =
        match lines path [ "tag"; "--list"; tag ] with
        | Ok found -> Ok(found |> List.contains tag)
        | Error failure -> Error failure.Message

    /// Whether `commit` is reachable from `reference` (`merged`).
    let isMerged (path: string) (commit: string) (reference: string) : Result<bool, string> =
        match lines path [ "merge-base"; commit; reference ], lines path [ "rev-parse"; "--verify"; "--quiet"; $"{commit}^{{commit}}" ] with
        | Ok [ mergeBase ], Ok [ full ] -> Ok(mergeBase = full)
        | Ok _, Ok _ -> Ok false
        | Error failure, _
        | _, Error failure -> Error failure.Message
