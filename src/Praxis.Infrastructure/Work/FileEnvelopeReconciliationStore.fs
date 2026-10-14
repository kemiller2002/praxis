namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Text.Json
open Praxis.Domain.Work
open Praxis.Application.Work

[<RequireQualifiedAccess>]
module FileEnvelopeReconciliationStore =
    let private options = JsonSerializerOptions(WriteIndented = true)

    let private statusCode = function
        | EnvelopeReconciliationReceiptStatus.Applied -> "applied"
        | EnvelopeReconciliationReceiptStatus.Rejected -> "rejected"
        | EnvelopeReconciliationReceiptStatus.AlreadyApplied -> "already-applied"

    let serializeReceipt (receipt: EnvelopeReconciliationReceipt) =
        JsonSerializer.Serialize(
            {| transactionId = receipt.TransactionId
               workItem = receipt.WorkItem
               branch = receipt.Branch
               headCommit = receipt.HeadCommit
               envelopeHash = receipt.EnvelopeHash
               checkpointTag = receipt.CheckpointTag |> Option.toObj
               status = statusCode receipt.Status
               findingCodes = receipt.FindingCodes
               reconciledAt = receipt.ReconciledAt |},
            options) + "\n"

    let private safeId (value: string) =
        value
        |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' then c else '_')
        |> Seq.toArray
        |> String

    let private atomicWrite (path: string) (content: string) =
        let directory = Path.GetDirectoryName path
        Directory.CreateDirectory directory |> ignore
        let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        File.WriteAllText(temp, content)
        File.Move(temp, path, true)

    let private atomicWriteBytes (path: string) (content: byte array) =
        let directory = Path.GetDirectoryName path
        Directory.CreateDirectory directory |> ignore
        let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        File.WriteAllBytes(temp, content)
        File.Move(temp, path, true)

    let private receiptPath root transactionId =
        Path.Combine(root, ".praxis", "reconciled", safeId transactionId + ".json")

    let private quarantinePath root transactionId =
        Path.Combine(root, ".praxis", "rejected", safeId transactionId + ".json")

    let private quarantineInputPath root transactionId =
        Path.Combine(root, ".praxis", "rejected", safeId transactionId + ".input.json")

    let private appliedReceipt root transactionId =
        let path = receiptPath root transactionId
        let checkpointHash = FileEnvelopeReconciliationTransaction.checkpointAppliedHash root transactionId
        if not (File.Exists path) || checkpointHash.IsNone then None
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let value = document.RootElement
                match value.TryGetProperty "status", value.TryGetProperty "envelopeHash" with
                | (true, status), (true, envelopeHash)
                    when status.ValueKind = JsonValueKind.String
                         && envelopeHash.ValueKind = JsonValueKind.String
                         && status.GetString() = "applied"
                         && Some(envelopeHash.GetString()) = checkpointHash -> checkpointHash
                | _ -> None
            with _ -> None

    let private createInternal root inputPath : EnvelopeReconciliationStore =
        let fullRoot = Path.GetFullPath root
        { IsApplied = fun transactionId ->
              appliedReceipt fullRoot transactionId |> Option.isSome
          ReadAppliedHash = fun transactionId -> appliedReceipt fullRoot transactionId
          WriteReceipt = fun receipt ->
              // The dispatcher writes the applied receipt inside the same
              // journaled commit as canonical state. Do not replace that
              // committed receipt with a later wall-clock serialization.
              if receipt.Status <> EnvelopeReconciliationReceiptStatus.Applied
                 || not (FileEnvelopeReconciliationTransaction.checkpointExists fullRoot receipt.TransactionId) then
                  receipt
                  |> serializeReceipt
                  |> atomicWrite (receiptPath fullRoot receipt.TransactionId)
          Quarantine = fun envelope codes ->
              inputPath
              |> Option.filter File.Exists
              |> Option.iter (fun path -> File.ReadAllBytes path |> atomicWriteBytes (quarantineInputPath fullRoot envelope.TransactionId))
              let value =
                  {| schemaVersion = envelope.SchemaVersion
                     transactionId = envelope.TransactionId
                     workItem = envelope.WorkItem
                     branch = envelope.Branch
                     findingCodes = codes
                     // Rejected input remains durable and inspectable. The
                     // typed envelope retains step boundaries plus each raw
                     // measurement/provider JSON string; it is never applied
                     // as canonical state by this quarantine write.
                     envelope = envelope |}
              JsonSerializer.Serialize(value, options)
              |> atomicWrite (quarantinePath fullRoot envelope.TransactionId) }

    let create root = createInternal root None

    let createWithInput root inputPath = createInternal root (Some(Path.GetFullPath inputPath))
