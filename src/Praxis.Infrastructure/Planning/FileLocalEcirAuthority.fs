namespace Praxis.Infrastructure.Planning

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open Praxis.Infrastructure.Artifacts
open Praxis.Infrastructure.Work

/// Concrete local storage, not a worker sandbox or operator identity provider.
/// Provision this directory outside the repo, inaccessible to worker identities.
/// Unix permission checks are necessary, not proof of that isolation.
type LocalEcirHostSettings =
    { RepositoryRoot: string; HostRoot: string; LockTimeout: TimeSpan }

type LocalEcirHostStore internal (settings: LocalEcirHostSettings, clock: unit -> DateTimeOffset) =
    let root = LocalHostJournalFiles.rootOutside settings.RepositoryRoot settings.HostRoot
    let policyFile = Path.Combine(root, "policy.json")
    let lockFile = Path.Combine(root, "policy.lock")
    let pendingFile = Path.Combine(root, "policy.pending")
    let mode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    let protect () =
        if OperatingSystem.IsWindows() then invalidOp "local ECIR host requires a separately qualified Windows ACL adapter"
        if not (LocalHostJournalFiles.noLinks root) || File.GetUnixFileMode root <> (mode ||| UnixFileMode.UserExecute) then
            invalidOp "local ECIR host directory must be non-link and mode 0700"
        for path in [ policyFile; lockFile; pendingFile ] do
            if not (LocalHostJournalFiles.noLinks path) then invalidOp "local ECIR host file is linked"
            if File.Exists path && File.GetUnixFileMode path <> mode then invalidOp "local ECIR host files must have mode 0600"
    let options create =
        let o = FileStreamOptions(Mode = create, Access = FileAccess.ReadWrite, Share = FileShare.None)
        o.UnixCreateMode <- mode
        o
    let locked action =
        try
            protect()
            let elapsed = Stopwatch.StartNew()
            let rec acquire () =
                try Ok(new FileStream(lockFile, options FileMode.OpenOrCreate))
                with :? IOException when elapsed.Elapsed < settings.LockTimeout -> Thread.Sleep 20; acquire()
            match acquire() with
            | Error error -> Error error
            | Ok handle ->
                use held = handle
                protect()
                if File.Exists pendingFile then Error "host policy has an unresolved pending write; operator reconciliation required"
                else action()
        with e -> Error("local ECIR host operation refused: " + e.GetType().Name + ": " + e.Message)
    let read () =
        if not (File.Exists policyFile) then Error "local ECIR host policy has not been provisioned"
        else
            use input = new FileStream(policyFile, FileMode.Open, FileAccess.Read, FileShare.Read)
            if input.Length > 4194304L then Error "oversized local ECIR host policy"
            else
                let buffer = Array.zeroCreate<byte> 4194305
                let mutable count = 0
                let mutable ended = false
                while count < buffer.Length && not ended do
                    let received = input.Read(buffer, count, buffer.Length - count)
                    if received = 0 then ended <- true else count <- count + received
                let content =
                    if count > 4194304 then Error "oversized local ECIR host policy"
                    else Ok(UTF8Encoding(false, true).GetString(buffer, 0, count))
                content |> Result.bind LocalEcirPolicyJson.read
                |> Result.bind (fun (initial, policy, audit) ->
                    let normalize path = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)
                    if normalize policy.RepositoryRoot <> normalize settings.RepositoryRoot then Error "local host policy repository differs"
                    else Ok(initial, policy, audit))
    let write initial policy audit replace =
        let content = LocalEcirPolicyJson.render initial policy audit
        LocalEcirPolicyJson.read content |> Result.bind (fun _ ->
            // A failed creation/write/flush leaves pending state, never silently
            // promotes readable bytes and never retries as a new authorization.
            use temporary = new FileStream(pendingFile, options FileMode.CreateNew)
            let bytes = Encoding.UTF8.GetBytes content
            temporary.Write(bytes, 0, bytes.Length)
            temporary.Flush(true)
            temporary.Dispose()
            File.Move(pendingFile, policyFile, replace)
            Ok())
    let allowedUpdate (current: EcirHostPolicy) (updated: EcirHostPolicy) (audit: EcirHostAudit) =
        if current.QualifiedRelease <> updated.QualifiedRelease then false
        else
            match audit.Action, audit.GroupId, audit.ReceiptSha256 with
            | "revoked", None, None ->
                current.TrustedSigners.ContainsKey audit.KeyId
                && updated.TrustedSigners = current.TrustedSigners.Remove audit.KeyId
                && updated.Grants = current.Grants
            | "issued", Some groupId, Some receiptHash ->
                match current.TrustedSigners |> Map.tryFind audit.KeyId,
                      updated.Grants |> List.filter (fun grant -> grant.GroupId = groupId) with
                | Some signer, [ grant ] when signer.Approver = audit.Approver ->
                    updated.TrustedSigners = current.TrustedSigners
                    && not (String.IsNullOrWhiteSpace grant.ReceiptJson)
                    && receiptHash = EcirDispatchTransaction.hashText grant.ReceiptJson
                    && updated.Grants = (current.Grants |> List.map (fun old ->
                        if old.GroupId = groupId then { old with ReceiptJson = grant.ReceiptJson } else old))
                | _ -> false
            | _ -> false
    member _.Load() = locked (fun () -> read() |> Result.map (fun (_, policy, _) -> policy))
    member _.Audit() = locked (fun () -> read() |> Result.map (fun (_, _, audit) -> audit))
    member internal _.RecoverNative() = locked (fun () ->
        EcirDispatchTransaction.recover settings.RepositoryRoot (Path.Combine(root, "dispatch.pending.json")) ignore
        |> Result.bind (fun () -> WorkStateTransaction.recover settings.RepositoryRoot |> Result.mapError _.Message)
        |> Result.bind (fun () -> BacklogStateTransaction.recover settings.RepositoryRoot |> Result.mapError _.Message)
        |> Result.bind (fun () -> FileNativeCompletionEffects.recoverBacklogLocked settings.RepositoryRoot)
        |> Result.bind (fun () -> BacklogStateTransaction.recover settings.RepositoryRoot |> Result.mapError _.Message))
    // Called only inside Authority.Commit after repository and policy leases.
    member internal _.CommitNative(writes) =
        let journal = Path.Combine(root, "dispatch.pending.json")
        EcirDispatchTransaction.prepare settings.RepositoryRoot journal writes
        |> Result.bind (fun () -> EcirDispatchTransaction.recover settings.RepositoryRoot journal ignore)
    /// Host operator provisioning only; no CLI or repository-config route.
    member _.Initialize(policy: EcirHostPolicy) = locked (fun () ->
        if File.Exists policyFile then Error "host policy already exists"
        elif Path.GetFullPath policy.RepositoryRoot <> Path.GetFullPath settings.RepositoryRoot then Error "host provisioning repository differs"
        else write policy.Revision policy [] false)
    member _.Apply(expected: string, updated: EcirHostPolicy, audit: EcirHostAudit) = locked (fun () ->
        read() |> Result.bind (fun (initial, current, history) ->
            if expected <> current.Revision then Error "host policy revision changed"
            elif audit.PreviousRevision <> expected || audit.NewRevision <> updated.Revision
                 || updated.Revision = expected || updated.RepositoryRoot <> current.RepositoryRoot then Error "host policy update/audit identity differs"
            elif not (allowedUpdate current updated audit) then Error "host policy change does not match issuance or revocation audit"
            elif audit.OccurredAt.Offset <> TimeSpan.Zero || audit.OccurredAt > clock()
                 || history |> List.exists (fun prior -> prior.OccurredAt > audit.OccurredAt) then Error "host policy audit time differs"
            else write initial updated (history @ [ audit ]) true))
    member _.Authority : EcirHostAuthority =
        { Load = fun () -> locked (fun () -> read() |> Result.map (fun (_, policy, _) -> policy))
          UtcNow = clock
          Commit = fun revision expires callback -> locked (fun () ->
              read() |> Result.bind (fun (_, current, history) ->
                  let now = clock()
                  if revision <> current.Revision then Error "host policy changed before repository commit"
                  elif not current.QualifiedRelease then Error "host release has not been qualified"
                  elif now.Offset <> TimeSpan.Zero || expires.Offset <> TimeSpan.Zero || now >= expires then Error "host authorization expired or clock is not UTC"
                  elif history |> List.exists (fun prior -> prior.OccurredAt > now) then Error "host clock precedes policy audit"
                  else callback())) }

[<RequireQualifiedAccess>]
module FileLocalEcirAuthority =
    let create settings clock =
        if settings.LockTimeout < TimeSpan.Zero || settings.LockTimeout > TimeSpan.FromSeconds 30. then
            invalidArg "settings" "host lock timeout must be between zero and 30 seconds"
        new LocalEcirHostStore(settings, clock)

    /// One lock order for host dispatch: native work, group membership, policy.
    /// Recovery callbacks must use locked native primitives, never reacquire.
    let dispatch settings (store: LocalEcirHostStore) recover groupId validate stage =
        let withNativeLock resource action =
            match RegistryLock.acquire settings.RepositoryRoot resource RegistryLock.defaultSettings with
            | Error failure -> Error failure.Message
            | Ok lease ->
                let result =
                    try action()
                    with e -> Error("local host dispatch refused: " + e.GetType().Name)
                match lease.Release(), result with
                | Error failure, Ok _ -> Error("host lock release uncertain: " + failure.Message)
                | _, value -> value
        withNativeLock "work-protocol" (fun () ->
            withNativeLock "work-groups" (fun () ->
                recover() |> Result.bind (fun () ->
                    let members () = FileWorkGroupRepository.read settings.RepositoryRoot |> Result.bind (fun groups ->
                        match groups |> List.filter (fun group -> group.Declaration.Id = groupId) with
                        | [ group ] -> Ok group.Declaration.Members
                        | _ -> Error "host dispatch requires exactly one stored group")
                    EcirHostPolicy.dispatch store.Authority settings.RepositoryRoot groupId members validate stage)))

    /// Composes existing native staging with protected journal preparation and
    /// recovery. It exposes no work-begin bypass to a CLI or worker process.
    let dispatchNative settings (store: LocalEcirHostStore) groupId validate stageNative =
        let stage evidence =
            stageNative evidence |> Result.map (fun (staged: EcirNativeStageResult) ->
                use held = staged
                let writes = staged.Writes
                fun () -> store.CommitNative writes)
        dispatch settings store store.RecoverNative groupId validate stage
