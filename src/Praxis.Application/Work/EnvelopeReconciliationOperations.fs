namespace Praxis.Application.Work

open System
open Praxis.Domain.Work

type EnvelopeReconciliationStore = {
    IsApplied: string -> bool
    ReadAppliedHash: string -> string option
    WriteReceipt: EnvelopeReconciliationReceipt -> unit
    Quarantine: EnvelopeReconciliationInput -> string list -> unit
}

type EnvelopeReconciliationEffects = {
    Observe: EnvelopeReconciliationInput -> EnvelopeReconciliationObservation
    ApplyRequests: EnvelopeReconciliationInput -> Result<unit, EnvelopeApplyFailure>
    HashEnvelope: EnvelopeReconciliationInput -> string
    Clock: unit -> DateTimeOffset
}

and EnvelopeApplyFailure =
    | Invalid of string list
    | Indeterminate of string

type EnvelopeReconciliationOutcome =
    | Applied of EnvelopeReconciliationReceipt
    | Rejected of EnvelopeReconciliationReceipt
    | NoOp of EnvelopeReconciliationReceipt
    | Pending of transactionId: string * message: string

[<RequireQualifiedAccess>]
module EnvelopeReconciliationOperations =
    let reconcile (store: EnvelopeReconciliationStore) (effects: EnvelopeReconciliationEffects) (envelope: EnvelopeReconciliationInput) =
        let observed0 = effects.Observe envelope
        let hash = effects.HashEnvelope envelope
        let applied = store.IsApplied envelope.TransactionId
        let observed =
            { observed0 with
                TransactionAlreadyApplied = applied
                TransactionReplayMatches = not applied || store.ReadAppliedHash envelope.TransactionId = Some hash }
        let receipt status codes =
            { TransactionId = envelope.TransactionId
              WorkItem = envelope.WorkItem
              Branch = observed.ActualBranch
              HeadCommit = observed.HeadCommit
              EnvelopeHash = hash
              CheckpointTag =
                if status = EnvelopeReconciliationReceiptStatus.Applied || status = EnvelopeReconciliationReceiptStatus.AlreadyApplied then
                    Some(EnvelopeReconciliationReceipt.checkpointTag envelope.TransactionId)
                else None
              Status = status
              FindingCodes = codes
              ReconciledAt = effects.Clock() }

        match EnvelopeReconciliation.decide envelope observed with
        | EnvelopeReconciliationDecision.AlreadyApplied ->
            NoOp (receipt EnvelopeReconciliationReceiptStatus.AlreadyApplied [])
        | EnvelopeReconciliationDecision.Reject findings ->
            let codes = findings |> List.map EnvelopeReconciliation.findingCode
            store.Quarantine envelope codes
            let value = receipt EnvelopeReconciliationReceiptStatus.Rejected codes
            store.WriteReceipt value
            Rejected value
        | EnvelopeReconciliationDecision.Accept ->
            match effects.ApplyRequests(EnvelopeReconciliation.withLocalInstance observed envelope) with
            | Ok () ->
                let value = receipt EnvelopeReconciliationReceiptStatus.Applied []
                store.WriteReceipt value
                Applied value
            | Error (EnvelopeApplyFailure.Invalid codes) ->
                store.Quarantine envelope codes
                let value = receipt EnvelopeReconciliationReceiptStatus.Rejected codes
                store.WriteReceipt value
                Rejected value
            | Error (EnvelopeApplyFailure.Indeterminate message) ->
                Pending(envelope.TransactionId, message)
