namespace Praxis.Domain.Planning

open System

/// What one execution arm costs per member (PRX-GRP-155): medians over at
/// least the minimum number of measured samples, else unknown. Never
/// assumed, never summed across currencies (PRX-GRP-156).
type ArmPrice =
    { Samples: int
      CostSamples: int
      CostPerMember: decimal option
      /// The lowest and highest per-member cost sampled, when priced.
      CostRange: (decimal * decimal) option
      Currency: string option
      ActiveMsPerMember: int64 option
      /// "N of M executions carry cost.execution_total".
      Coverage: string }

/// Grouped against independent execution, priced only from measured
/// samples (PRX-GRP-155, PRX-GRP-061).
type GroupPricing =
    { Grouped: ArmPrice
      Independent: ArmPrice
      Statement: string }

[<RequireQualifiedAccess>]
module GroupPricing =
    /// PRX-GRP-155: the existing sample threshold (at least three).
    let minimumSamples = 3

    let private median (values: decimal list) =
        match List.sort values with
        | [] -> None
        | sorted ->
            let count = sorted.Length

            if count % 2 = 1 then Some sorted[count / 2]
            else Some((sorted[count / 2 - 1] + sorted[count / 2]) / 2m)

    let private arm (label: string) (total: int) (costs: (decimal * string option) list) (active: int64 list) =
        let currencies = costs |> List.map snd |> List.distinct
        let singleCurrency = match currencies with [ currency ] -> Some currency | _ -> None
        let priced = costs.Length >= minimumSamples && singleCurrency.IsSome
        let amounts = costs |> List.map fst

        { Samples = total
          CostSamples = costs.Length
          CostPerMember = if priced then median amounts else None
          CostRange = if priced then Some(List.min amounts, List.max amounts) else None
          Currency = if priced then singleCurrency |> Option.flatten else None
          ActiveMsPerMember =
            if active.Length >= minimumSamples then active |> List.map decimal |> median |> Option.map (Math.Round >> int64) else None
          Coverage =
            let mixed = if currencies.Length > 1 then "; currencies differ, so cost is not combined" else ""
            $"{costs.Length} of {total} {label} carry cost.execution_total{mixed}" }

    let private describe (name: string) (price: ArmPrice) =
        let cost =
            match price.CostPerMember, price.Currency with
            | Some amount, currency ->
                let unit = currency |> Option.map (fun code -> " " + code) |> Option.defaultValue ""
                let shown = amount.ToString("0.####", Globalization.CultureInfo.InvariantCulture)
                $"{shown}{unit} per member"
            | None, _ -> $"cost per member unknown (fewer than {minimumSamples} priced samples)"

        let time =
            match price.ActiveMsPerMember with
            | Some ms -> $"{ms / 60_000L} min active per member"
            | None -> "active time per member unknown"

        $"{name}: {cost}, {time} ({price.Coverage})"

    /// Independent samples are finalized executions that no grouped-mode
    /// group execution covered; grouped samples are ended group executions,
    /// their shared total divided over the members they began.
    let price (executions: HistoricalExecution list) (samples: GroupedSample list) : GroupPricing =
        let grouped = samples |> List.filter (fun sample -> sample.Mode = "grouped" && sample.Members > 0)
        let covered = grouped |> List.collect (fun sample -> sample.ExecutionIds) |> Set.ofList

        let independent =
            executions |> List.filter (fun execution -> execution.Status = ExecutionStatus.Finalized && not (covered.Contains execution.ExecutionId))

        let groupedArm =
            arm
                "grouped executions"
                grouped.Length
                (grouped |> List.choose (fun sample -> sample.CostTotal |> Option.map (fun total -> total / decimal sample.Members, sample.Currency)))
                (grouped |> List.choose (fun sample -> sample.ActiveMs |> Option.map (fun ms -> ms / int64 sample.Members)))

        let independentArm =
            arm
                "independent executions"
                independent.Length
                (independent |> List.choose History.executionCost)
                (independent |> List.choose (fun execution -> execution.Session.ActiveMs))

        { Grouped = groupedArm
          Independent = independentArm
          Statement =
            $"""priced from measured samples only, never assumed (PRX-GRP-155): {describe "grouped" groupedArm}; {describe "independent" independentArm}""" }
