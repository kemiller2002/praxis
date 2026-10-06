namespace Ros.Domain.Execution

open System

// The control-plane read model of an execution (PRX-CTL-005, PRX-CTL-011,
// PRX-CTL-012): what a step's receipt currently says and who hosts the
// execution, derived from the durable envelope and ledger alone. Nothing here
// compares receipts again; the recorded comparison is reported as recorded.

[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module StepStatus =
    /// The wire codes `execution show --json` reports for a step.
    let toWire status =
        match status with
        | StepStatus.NotStarted -> "not-started"
        | StepStatus.Satisfied -> "match"
        | StepStatus.Mismatched -> "mismatch"
        | StepStatus.EffectUnknown -> "indeterminate"
        | StepStatus.ReconciledNotOccurred -> "reconciled-not-occurred"
        | StepStatus.ReconciledOccurred -> "reconciled-occurred"

/// Where the receipt of a step's current attempt stands.
[<RequireQualifiedAccess>]
type ReceiptState =
    /// No attempt has started, so there is nothing to observe yet.
    | NotObserved
    /// The attempt started and no receipt was observed: its effect is unknown.
    | Indeterminate
    /// The recorded comparison of the observed receipt with the expected one.
    | Compared of ReceiptOutcome

type ReceiptObservation =
    { Observed: ObservedReceipt
      Comparison: ReceiptResult
      At: DateTimeOffset }

/// The expected and observed receipt of a step's current attempt.
type StepReceipt =
    { Attempt: int
      Expected: ExpectedReceipt
      State: ReceiptState
      Observation: ReceiptObservation option }

type StepReconciliation =
    { Attempt: int
      Finding: Reconciliation
      At: DateTimeOffset }

[<RequireQualifiedAccess>]
module ReceiptState =
    let toWire state =
        match state with
        | ReceiptState.NotObserved -> "not-observed"
        | ReceiptState.Indeterminate -> "indeterminate"
        | ReceiptState.Compared outcome -> ReceiptOutcome.toWire outcome

[<RequireQualifiedAccess>]
module StepReceipt =
    let private started (entry: StepEntry) =
        match entry with
        | StepEntry.Started(_, attempt, _) -> Some attempt
        | _ -> None

    let private observedIn (attempt: int) (entry: StepEntry) =
        match entry with
        | StepEntry.Observed(_, n, observed, comparison, at) when n = attempt -> Some { Observed = observed; Comparison = comparison; At = at }
        | _ -> None

    /// The current attempt is the latest one started (0 before any start);
    /// its receipt is its latest recorded observation.
    let current (step: StepView) : StepReceipt =
        let attempt = step.Entries |> List.choose started |> List.fold max 0
        let observation = step.Entries |> List.choose (observedIn attempt) |> List.tryLast

        let state =
            match observation, attempt with
            | Some recorded, _ -> ReceiptState.Compared recorded.Comparison.Outcome
            | None, 0 -> ReceiptState.NotObserved
            | None, _ -> ReceiptState.Indeterminate

        { Attempt = attempt
          Expected = step.Expected
          State = state
          Observation = observation }

    /// The latest reconciliation recorded for the step, if any.
    let reconciliation (step: StepView) : StepReconciliation option =
        step.Entries
        |> List.choose (fun entry ->
            match entry with
            | StepEntry.Reconciled(_, attempt, finding, at) -> Some { Attempt = attempt; Finding = finding; At = at }
            | _ -> None)
        |> List.tryLast

/// Who runs an execution, as host information: never the owner of the work
/// state, which stays with the repository's Praxis records (PRX-CTL-012).
type ExecutionHost =
    { Provider: string
      Model: string
      Runtime: string }

[<RequireQualifiedAccess>]
module ExecutionHost =
    let unknown = "unknown"

    let private orUnknown (value: string option) =
        value |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.defaultValue unknown

    let ofActor (actor: ExecutionActor) : ExecutionHost =
        { Provider = orUnknown actor.Provider
          Model = orUnknown actor.Model
          Runtime = orUnknown actor.Runtime }
