namespace Ros.Domain.Work

open System

type EnvelopeReconciliationReceiptStatus =
    | Applied
    | Rejected
    | AlreadyApplied

type EnvelopeReconciliationReceipt = {
    TransactionId: string
    WorkItem: string
    Branch: string
    HeadCommit: string
    EnvelopeHash: string
    CheckpointTag: string option
    Status: EnvelopeReconciliationReceiptStatus
    FindingCodes: string list
    ReconciledAt: DateTimeOffset
}

module EnvelopeReconciliationReceipt =
    let path transactionId = $".praxis/reconciled/{transactionId}.json"

    let safeTransactionId (value: string) =
        value
        |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' then c else '_')
        |> Seq.toArray
        |> String

    let checkpointTag transactionId = "praxis-reconcile/" + safeTransactionId transactionId
