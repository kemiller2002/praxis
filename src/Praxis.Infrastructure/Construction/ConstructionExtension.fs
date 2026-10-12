namespace Praxis.Infrastructure.Construction

open System
open System.IO
open System.Text
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work
open Ordo.Construction

/// One accepted handwritten file: a declared slot's exact bytes.
type ExtensionFile = { Slot: ConstructionExtensionSlot; Path: string; Content: string; Digest: string; Contracts: string list }

[<RequireQualifiedAccess>]
type ExtensionWriteKind = Create | Replace | Unchanged

[<RequireQualifiedAccess>]
type ExtensionOutcome =
    | Applied of candidate: string
    | AlreadyCurrent

type ExtensionReport =
    { Routing: ConstructionRouting
      RouteRecord: string
      TargetRef: string
      TargetCommit: string
      ConstructionCommit: string
      ConstructionId: string
      ExtensionId: string
      Files: (ExtensionFile * ExtensionWriteKind) list
      Outcome: ExtensionOutcome }

/// Host-controlled commit of handwritten extension files into host-declared
/// slots on top of an exact, already-applied construction (Praxis #243).
/// It reads submitted bytes and writes Git objects; it never runs a worker,
/// a build or any submitted code.
[<RequireQualifiedAccess>]
module ConstructionExtension =
    let private maxEntries = 64

    let private strict = UTF8Encoding(false, true)

    /// Every entry is judged on its own, so one refusal names every problem.
    let private judge (routing: ConstructionRouting) (directory: string) (entry: string) : Result<ExtensionFile, string> =
        let name = Path.GetFileName entry
        let declared = routing.Slots |> List.tryFind (fun slot -> slot.File = name)
        let usable = ConstructionRouting.usableSlots routing |> List.tryFind (fun slot -> slot.File = name)
        let refuse reason = Error(name + ": " + reason)
        let info = FileInfo entry
        if not (isNull info.LinkTarget) then refuse "symbolic link; only regular files are accepted"
        else
            match ConstructionExtensionSlot.denied name with
            | Some reason -> refuse reason
            | None ->
                if Directory.Exists entry then refuse "subdirectory; a slot is one regular file"
                elif not info.Exists || info.Attributes.HasFlag FileAttributes.ReparsePoint || info.Attributes.HasFlag FileAttributes.Device then refuse "not a regular file"
                else
                    match declared, usable with
                    | None, _ ->
                        match routing.Slots |> List.tryFind (fun slot -> String.Equals(slot.File, name, StringComparison.OrdinalIgnoreCase)) with
                        | Some slot -> refuse ("differs in case from declared slot " + slot.File)
                        | None -> refuse "not a host-declared extension slot"
                    | Some slot, None -> refuse ("slot " + slot.File + " implements no extension contract of this construction")
                    | Some _, Some slot ->
                        if info.Length = 0L then refuse "empty file"
                        elif info.Length > int64 slot.MaxBytes then refuse (sprintf "%d bytes exceeds the slot limit of %d" info.Length slot.MaxBytes)
                        else
                            let bytes = File.ReadAllBytes(Path.Combine(directory, name))
                            if bytes.LongLength <> info.Length then refuse "changed while being read"
                            elif bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then refuse "starts with a byte-order mark"
                            elif Array.contains 0uy bytes then refuse "contains a NUL byte; slots hold text"
                            else
                                match (try Ok(strict.GetString bytes) with _ -> Error()) with
                                | Error() -> refuse "not UTF-8 text"
                                | Ok content when content.StartsWith Artifacts.GeneratedMarker -> refuse "carries the generated-file marker; generated files are written only by construction"
                                | Ok content ->
                                    let contracts = routing.Custom |> List.filter (fun r -> r.Slot.File = slot.File) |> List.map _.Contract
                                    Ok { Slot = slot; Path = ConstructionTree.placed routing.Mount slot.File; Content = content
                                         Digest = ConstructionTree.digest content; Contracts = contracts }

    /// Read the submission directory without following any link.
    let read (routing: ConstructionRouting) (directory: string) : Result<ExtensionFile list, string> =
        try
            let full = Path.TrimEndingDirectorySeparator(Path.GetFullPath directory)
            if not (Directory.Exists full) then Error "extension files directory does not exist"
            elif not (LocalHostJournalFiles.noLinks full) then Error "extension files directory is or lies under a symbolic link"
            else
                let entries = Directory.GetFileSystemEntries full |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> Array.toList
                if entries.IsEmpty then Error "no extension files submitted"
                elif entries.Length > maxEntries then Error(sprintf "extension files directory has %d entries; at most %d are accepted" entries.Length maxEntries)
                else
                    let judged = entries |> List.map (judge routing full)
                    match judged |> List.choose (function Error reason -> Some reason | Ok _ -> None) with
                    | [] -> Ok(judged |> List.choose (function Ok file -> Some file | Error _ -> None))
                    | reasons -> Error("extension refused: " + String.concat "; " reasons)
        with error -> Error("extension files unreadable: " + error.Message)

    let private requirementPath (key: string) =
        let path = key.Split('#').[0].Split('/')
        if path.Length > 2 then Some(String.Join("/", path |> Array.skip 2)) else None

    let private directoryOf (path: string) = match path.LastIndexOf '/' with -1 -> "" | at -> path.Substring(0, at)

    /// Placement checks against the admitted binding and the target tree: no
    /// slot may land inside the generated root, on an ECIR or requirement
    /// source, or on anything but an absent or regular handwritten file.
    let private placement git root (routing: ConstructionRouting) (target: string) (file: ExtensionFile) =
        let b = routing.Binding
        let ecir = [ b.ManifestPath; b.BlueprintPath ]
        let refuse reason = Error(file.Path + ": " + reason)
        if file.Path.StartsWith(routing.OwnershipRoot, StringComparison.OrdinalIgnoreCase) then refuse "inside the generated ownership root"
        elif ecir |> List.exists (fun source -> String.Equals(source, file.Path, StringComparison.OrdinalIgnoreCase) || String.Equals(directoryOf source, directoryOf file.Path, StringComparison.OrdinalIgnoreCase)) then
            refuse "ECIR source document or its directory"
        elif b.RequirementKeys |> List.choose requirementPath |> List.exists (fun source -> String.Equals(source, file.Path, StringComparison.OrdinalIgnoreCase)) then
            refuse "requirement source document"
        else
            ConstructionTree.entryAt git root target file.Path |> Result.bind (function
                | None -> Ok(file, ExtensionWriteKind.Create)
                | Some("100644", Some existing) when existing.StartsWith Artifacts.GeneratedMarker -> refuse "the target holds a generated file there"
                | Some("100644", Some existing) when existing = file.Content -> Ok(file, ExtensionWriteKind.Unchanged)
                | Some("100644", Some _) -> Ok(file, ExtensionWriteKind.Replace)
                | Some(mode, _) -> refuse (sprintf "the target holds a non-regular entry (mode %s) there" mode))

    let private message (report: ExtensionReport) =
        let b = report.Routing.Binding
        String.concat "\n"
            ([ sprintf "Extend %s with handwritten slots for cohort %s" (if report.Routing.Mount = "." then "the construction mount" else report.Routing.Mount) b.CohortId
               ""
               "Committed by the Praxis host into host-declared extension slots."
               "No worker, build or submitted code was run. Obligations remain open."
               ""
               "Extension-Construction: " + report.ConstructionId + " " + report.ConstructionCommit
               "Extension-Plan: " + b.PlanDigest
               "Extension-Group: " + b.GroupId
               "Extension-Cohort: " + b.CohortId
               "Extension-Route: " + ConstructionRouting.routeId report.Routing
               "Extension-Policy: " + b.PolicyRevision + " approved by " + b.Approver ]
             @ (report.Files |> List.map (fun (file, _) -> "Extension-Slot: " + file.Path + " " + file.Digest + " " + String.concat " " file.Contracts)))

    /// Stable per (plan, route, target, observed head, files); the scope check
    /// (GH-234) recomputes it to match a journal entry to its inputs.
    let extensionId (routing: ConstructionRouting) targetRef targetCommit (files: ExtensionFile list) =
        let parts = [ routing.Binding.PlanDigest; ConstructionRouting.routeId routing; targetRef; targetCommit ] @ (files |> List.map (fun file -> file.Path + " " + file.Digest))
        "extend-" + (ConstructionTree.digest (String.concat "\n" parts)).Substring(7, 24)

    /// Fail unless the target already carries exactly this construction:
    /// every generated file unchanged and a construction commit with this
    /// plan's trailers in its history.
    let private carried (configuration: LocalHostConfiguration) (admitted: ConstructionAdmission.Admitted) targetRef =
        let git, root = configuration.Git, configuration.RepositoryRoot
        ConstructionApplication.preview configuration admitted targetRef |> Result.bind (fun preview ->
            match preview.Writes |> List.filter (fun write -> write.Kind <> ConstructionWriteKind.Unchanged) with
            | _ :: _ as pending ->
                Error(sprintf "target does not carry this exact construction (%d generated files would change); apply the construction first or re-plan" pending.Length)
            | [] ->
                let b = preview.Binding
                ConstructionTree.findCommitWithTrailers git root preview.TargetCommit
                    [ "Construction-Plan: " + b.PlanDigest; "Construction-Cohort: " + b.CohortId; "Construction-Group: " + b.GroupId ]
                |> Result.bind (function
                    | None -> Error "target history has no construction commit with this plan's digest, cohort and group trailers"
                    | Some commit ->
                        ConstructionTree.parentOf git root commit |> Result.map (fun parent ->
                            // The ID `construct apply` reported when it landed this commit on this ref.
                            let id = parent |> Option.map (fun parent -> ConstructionApplication.constructionId { preview with TargetCommit = parent }) |> Option.defaultValue "construct-unknown"
                            preview.TargetCommit, commit, id)))

    let extend (configuration: LocalHostConfiguration) (admitted: ConstructionAdmission.Admitted) (targetRef: string) (filesDirectory: string) (now: DateTimeOffset) : Result<ExtensionReport, string> =
        let git, root = configuration.Git, configuration.RepositoryRoot
        let at = ConstructionApplication.truncate now
        let routing = ConstructionRouting.classify configuration admitted
        read routing filesDirectory |> Result.bind (fun files ->
            ConstructionApplication.journal configuration |> Result.bind (fun (store, journal) ->
                FileLocalIntegrationJournal.acquire root store configuration.RepositoryIdentity |> Result.bind (fun lease ->
                    use _ = lease
                    ConstructionApplication.reconcile configuration journal at |> Result.bind (fun () ->
                        carried configuration admitted targetRef |> Result.bind (fun (targetCommit, constructionCommit, constructionId) ->
                            let placed = files |> List.map (placement git root routing targetCommit)
                            match placed |> List.choose (function Error reason -> Some reason | Ok _ -> None) with
                            | _ :: _ as reasons -> Error("extension refused: " + String.concat "; " reasons)
                            | [] ->
                                let planned = placed |> List.choose (function Ok value -> Some value | Error _ -> None)
                                ConstructionApplication.recordPlan configuration admitted
                                |> Result.bind (fun _ -> ConstructionRouting.record configuration routing) |> Result.bind (fun recordPath ->
                                    let report =
                                        { Routing = routing; RouteRecord = recordPath; TargetRef = targetRef; TargetCommit = targetCommit
                                          ConstructionCommit = constructionCommit; ConstructionId = constructionId
                                          ExtensionId = extensionId routing targetRef targetCommit files; Files = planned
                                          Outcome = ExtensionOutcome.AlreadyCurrent }
                                    let changes =
                                        planned |> List.choose (fun (file, kind) ->
                                            if kind = ExtensionWriteKind.Unchanged then None
                                            else Some { ConstructionTreeChange.Path = file.Path; Content = Some file.Content })
                                    match changes with
                                    | [] -> Ok report
                                    | changes ->
                                        let submission = ConstructionTree.digest (String.concat "\n" (files |> List.map (fun file -> file.Path + " " + file.Digest)))
                                        ConstructionApplication.land configuration store journal report.ExtensionId targetRef targetCommit changes (message report)
                                            routing.Binding.PlanDigest submission at
                                        |> Result.map (fun candidate -> { report with Outcome = ExtensionOutcome.Applied candidate })))))))
