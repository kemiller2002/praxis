namespace Praxis.Cli

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Praxis.Application.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Artifacts
open Praxis.Infrastructure.Git
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module EnvelopeReconciliationCommands =
    let private localInstanceId root =
        let path = Path.Combine(root, ".praxis", "instance.json")

        if not (File.Exists path) then None
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                match document.RootElement.TryGetProperty "instanceId" with
                | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
                | _ -> None
            with _ -> None

    let private hashEnvelopeFile path =
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

    let private observe root (store: EnvelopeReconciliationStore) (envelope: EnvelopeReconciliationInput) =
        let branch, head = ProcessGitRepository.readBranchAndCommit root
        let baseExists = ProcessGitRepository.commitExists root envelope.BaseCommit
        let baseIsAncestor =
            if not baseExists then false
            else
                match head with
                | None -> false
                | Some headCommit ->
                    match (ProcessGitRepository.createHistory root).IsAncestor envelope.BaseCommit headCommit with
                    | Ok value -> value
                    | Error _ -> false
        { ActualBranch = branch |> Option.defaultValue ""
          HeadCommit = head |> Option.defaultValue ""
          BaseCommitExists = baseExists
          BaseCommitIsAncestor = baseIsAncestor
          TransactionAlreadyApplied = store.IsApplied envelope.TransactionId
          TransactionReplayMatches = true
          LocalPraxisInstanceId = localInstanceId root }

    let private applyRequests root envelopePath envelopeHash headCommit clock envelope =
        FileEnvelopeReconciliationDispatcher.apply root envelopePath envelopeHash headCommit clock envelope

    let private reconcileUnlocked root envelopePath =
        let fullEnvelopePath = Path.GetFullPath envelopePath
        let recovered = FileEnvelopeReconciliationTransaction.recoverPending root

        match recovered with
        | Error message ->
            eprintfn "PENDING %s" message
            1
        | Ok recoveredTransactions when not (File.Exists fullEnvelopePath) ->
            match recoveredTransactions |> List.tryFind (fun transaction -> transaction.EnvelopePath = fullEnvelopePath) with
            | Some transaction ->
                printfn "APPLIED %s %s" transaction.TransactionId transaction.CheckpointTag
                0
            | None ->
                eprintfn "REJECT envelope-not-found"
                2
        | Ok _ ->
            match ReconciliationEnvelopeJson.read fullEnvelopePath with
            | Error codes ->
                eprintfn "REJECT %s" (String.concat "," codes)
                2
            | Ok envelope ->
                let store = FileEnvelopeReconciliationStore.createWithInput root fullEnvelopePath
                let clock = fun () -> DateTimeOffset.UtcNow
                let envelopeHash = hashEnvelopeFile fullEnvelopePath
                let _, head = ProcessGitRepository.readBranchAndCommit root
                let effects =
                    { Observe = observe root store
                      ApplyRequests = applyRequests root fullEnvelopePath envelopeHash (head |> Option.defaultValue "") clock
                      HashEnvelope = fun _ -> envelopeHash
                      Clock = clock }
                match EnvelopeReconciliationOperations.reconcile store effects envelope with
                | EnvelopeReconciliationOutcome.Applied (receipt: EnvelopeReconciliationReceipt) ->
                    printfn "APPLIED %s %s" receipt.TransactionId (receipt.CheckpointTag |> Option.defaultValue receipt.HeadCommit)
                    0
                | EnvelopeReconciliationOutcome.NoOp (receipt: EnvelopeReconciliationReceipt) ->
                    printfn "NOOP %s" receipt.TransactionId
                    0
                | EnvelopeReconciliationOutcome.Rejected (receipt: EnvelopeReconciliationReceipt) ->
                    eprintfn "REJECT %s %s" receipt.TransactionId (String.concat "," receipt.FindingCodes)
                    2
                | EnvelopeReconciliationOutcome.Pending(transactionId, message) ->
                    eprintfn "PENDING %s %s" transactionId message
                    1

    let reconcile root envelopePath =
        match RegistryLock.acquire root "envelope-reconciliation" RegistryLock.defaultSettings with
        | Error failure ->
            eprintfn "PENDING %s" failure.Message
            1
        | Ok lease ->
            let result =
                try reconcileUnlocked root envelopePath
                with error ->
                    eprintfn "PENDING %s" error.Message
                    1
            match lease.Release() with
            | Error failure when result = 0 ->
                eprintfn "PENDING %s" failure.Message
                1
            | _ -> result

    let inbox root =
        let inputs = InputDocuments.inventory root
        for relative, path in inputs do
            printfn "%s\t%s" relative (Path.GetRelativePath(root, path))
        if inputs.IsEmpty then printfn "No pending input documents."
        0
