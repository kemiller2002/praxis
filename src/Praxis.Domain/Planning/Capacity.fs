namespace Praxis.Domain.Planning

/// Provider capacity in planning (PRX-QUAL-009). Pure. Capacity can only
/// reorder work so provider-free items run first while a provider is
/// constrained or exhausted; it never switches provider or model (the
/// planner has no knowledge of capability or model compatibility, so that
/// choice stays with the executor), never drops an item and never treats
/// unknown capacity as zero.
[<RequireQualifiedAccess>]
module Capacity =
    /// Items carrying this tag need no model provider (human review, waiting
    /// on CI, manual release steps) and can proceed while capacity is limited.
    [<Literal>]
    let ProviderFreeTag = "provider-free"

    let isProviderFree (item: ItemAnalysis) = item.Tags |> List.contains ProviderFreeTag

    let private limitedProviders (capacity: ProviderCapacity list) =
        capacity |> List.filter (fun provider -> CapacityState.isLimited provider.State)

    let assess (capacity: ProviderCapacity list) (runnable: ItemAnalysis list) : CapacityAssessment =
        let limited = limitedProviders capacity
        let free = runnable |> List.filter isProviderFree |> List.map _.Id
        let affects = not limited.IsEmpty && not free.IsEmpty && free.Length < runnable.Length

        let describe (providers: ProviderCapacity list) =
            providers |> List.map (fun provider -> $"{provider.Provider} {CapacityState.describe provider.State}") |> String.concat "; "

        let statement =
            match capacity, limited with
            | [], _ -> "no provider capacity was observed: capacity is unknown (not zero) and did not affect ordering"
            | _, [] when capacity |> List.exists (fun provider -> provider.State = CapacityState.Available) && capacity |> List.forall (fun provider -> provider.State = CapacityState.Available) ->
                $"provider capacity available ({describe capacity}); ordering unaffected"
            | _, [] -> $"provider capacity partly unknown ({describe capacity}); ordering unaffected"
            | _, _ when affects -> $"provider capacity limited ({describe limited}): provider-free items are ordered first"
            | _, _ -> $"provider capacity limited ({describe limited}); no runnable provider-free item to order first, so ordering is unchanged"

        { Providers = capacity
          Limited = not limited.IsEmpty
          ProviderFreeItems = free
          AffectsOrdering = affects
          Statement = statement }

    /// Stable partition: provider-free items first, each group in the
    /// strategy's own order. The identity when capacity does not affect ordering.
    let prioritize (assessment: CapacityAssessment) (order: ItemAnalysis list -> ItemAnalysis list) =
        if not assessment.AffectsOrdering then
            order
        else
            fun items ->
                let ordered = order items
                (ordered |> List.filter isProviderFree) @ (ordered |> List.filter (isProviderFree >> not))

    /// The reason an entry carries when capacity affected ordering.
    let reason (assessment: CapacityAssessment) (item: ItemAnalysis) : PlanningReason option =
        if not assessment.AffectsOrdering then
            None
        else
            let limited =
                assessment.Providers
                |> List.filter (fun provider -> CapacityState.isLimited provider.State)
                |> List.map (fun provider -> $"{provider.Provider} {CapacityState.describe provider.State}")
                |> String.concat "; "

            if isProviderFree item then
                Some(PlanningReason.create ReasonCode.ProviderFreeFirst $"ordered first: needs no model provider while capacity is limited ({limited})" [])
            else
                Some(
                    PlanningReason.create
                        ReasonCode.ProviderCapacityDeferred
                        $"ordered after provider-free work: provider capacity is limited ({limited}); checkpoint or wait, or run it on another provider only where capability and model compatibility are established (the planner does not choose providers)"
                        []
                )
