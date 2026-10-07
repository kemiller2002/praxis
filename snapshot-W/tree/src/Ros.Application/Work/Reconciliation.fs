namespace Ros.Application.Work

open Ros.Application.Git
open Ros.Domain.Git
open Ros.Domain.Work

/// Orchestrates post-hoc attribution reconciliation: establishes the Git
/// evidence through the `GitHistory` port, then hands it to the pure
/// `WorkReconciliation.decide`. Nothing here writes anything.
[<RequireQualifiedAccess>]
module WorkReconciliationOperations =
    let private resolve (history: GitHistory) (reference: string) : Result<string, ReconciliationRejection> =
        match history.ResolveCommit reference with
        | GitRefResolution.Resolved sha -> Ok sha
        | GitRefResolution.NotFound message -> Error(ReconciliationRejection.ReferenceNotFound(reference, message))
        | GitRefResolution.Ambiguous message -> Error(ReconciliationRejection.AmbiguousReference(reference, message))
        | GitRefResolution.Unavailable failure -> Error(ReconciliationRejection.GitUnavailable failure)

    let private requireAncestor (history: GitHistory) ancestor descendant (rejection: ReconciliationRejection) =
        match history.IsAncestor ancestor descendant with
        | Error failure -> Error [ ReconciliationRejection.GitUnavailable failure ]
        | Ok true -> Ok()
        | Ok false -> Error [ rejection ]

    let private resolveSelector (history: GitHistory) (head: string) (selector: GitEvidenceSelector) =
        match selector with
        | GitEvidenceSelector.Commit reference ->
            resolve history reference
            |> Result.mapError List.singleton
            |> Result.bind (fun sha ->
                requireAncestor history sha head (ReconciliationRejection.NotInCurrentHistory(reference, sha))
                |> Result.map (fun () ->
                    { Selector = selector
                      BaseCommit = None
                      HeadCommit = sha
                      Commits = [ sha ] }))
        | GitEvidenceSelector.Range(baseReference, headReference) ->
            let range = GitEvidenceSelector.text selector

            match resolve history baseReference, resolve history headReference with
            | Error left, Error right -> Error [ left; right ]
            | Error rejection, Ok _
            | Ok _, Error rejection -> Error [ rejection ]
            | Ok baseSha, Ok headSha ->
                requireAncestor history baseSha headSha (ReconciliationRejection.RangeBaseNotAncestor range)
                |> Result.bind (fun () ->
                    requireAncestor history headSha head (ReconciliationRejection.NotInCurrentHistory(headReference, headSha)))
                |> Result.bind (fun () ->
                    match history.ListRange baseSha headSha with
                    | Error failure -> Error [ ReconciliationRejection.GitUnavailable failure ]
                    | Ok [] -> Error [ ReconciliationRejection.EmptyRange range ]
                    | Ok commits ->
                        Ok
                            { Selector = selector
                              BaseCommit = Some baseSha
                              HeadCommit = headSha
                              Commits = commits })

    let private collect (results: Result<'value, ReconciliationRejection list> list) =
        match results |> List.collect (function Error rejections -> rejections | Ok _ -> []) with
        | [] -> Ok(results |> List.choose (function Ok value -> Some value | Error _ -> None))
        | rejections -> Error rejections

    /// Establishes what Git proves about the selected commits. Every
    /// selected commit must be in the history of the current HEAD, a range's
    /// BASE must be an ancestor of its HEAD, and every rejection across all
    /// selectors is reported together.
    let gatherEvidence (history: GitHistory) (selectors: GitEvidenceSelector list) =
        if selectors.IsEmpty then
            Error [ ReconciliationRejection.NoEvidence ]
        else
            match history.Head() with
            | GitRefResolution.Unavailable failure -> Error [ ReconciliationRejection.GitUnavailable failure ]
            | GitRefResolution.NotFound message
            | GitRefResolution.Ambiguous message -> Error [ ReconciliationRejection.ReferenceNotFound("HEAD", message) ]
            | GitRefResolution.Resolved head ->
                selectors
                |> List.map (resolveSelector history head)
                |> collect
                |> Result.bind (fun resolved ->
                    resolved
                    |> List.collect _.Commits
                    |> List.distinct
                    |> List.map (history.ReadCommit >> Result.mapError (ReconciliationRejection.GitUnavailable >> List.singleton))
                    |> collect
                    |> Result.map (fun commits ->
                        { Head = head
                          Selectors = resolved
                          Commits = commits }))

    /// Gathers evidence, then decides. `requestFor` supplies everything the
    /// decision needs besides the evidence (the target, existing
    /// attribution, and path policy), read by the caller under its lock.
    let plan
        (history: GitHistory)
        (selectors: GitEvidenceSelector list)
        (requestFor: GitReconciliationEvidence -> ReconciliationRequest)
        : ReconciliationDecision =
        match gatherEvidence history selectors with
        | Error rejections -> ReconciliationDecision.Rejected rejections
        | Ok evidence -> WorkReconciliation.decide (requestFor evidence)
