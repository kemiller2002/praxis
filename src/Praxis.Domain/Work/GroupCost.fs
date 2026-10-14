namespace Praxis.Domain.Work

open System

/// One line of a group execution's apportioned total (PRX-GRP-154).
type CostLine =
    { /// The member, or `None` for the group-shared line.
      Member: string option
      Amount: decimal option
      /// `observed`/`derived` for a member's own usage, `group-shared` for
      /// the remainder, `allocated` for an allocation: never `observed`.
      Quality: string }

/// A group execution's total, apportioned deterministically by a recorded
/// method (PRX-GRP-154). Raw data is never rewritten; changing the method
/// recomputes the lines.
type CostApportionment =
    { Method: string
      Total: decimal option
      Currency: string option
      /// Usage inside each member's own execution window.
      Direct: CostLine list
      /// The remainder: analysis before the first member, shared
      /// infrastructure, gaps; it keeps the rounding residue.
      GroupShared: CostLine
      /// Per-member totals: direct plus an equal share of the group-shared
      /// line, labelled `allocated`.
      Allocated: CostLine list
      Statement: string }

[<RequireQualifiedAccess>]
module GroupCost =
    let equalShare = "equal-share"

    /// The allocation unit: amounts are floored to this many decimals so the
    /// lines always sum exactly to the total.
    let private places = 6

    let private floorTo (value: decimal) =
        let factor = decimal (Math.Pow(10.0, float places))
        Math.Floor(value * factor) / factor

    /// Apportions `total` over `members`. `direct` gives a member's own
    /// recorded usage (unknown when it recorded none, counted as zero direct
    /// usage, all of it then shared). An unknown total, or direct usage above
    /// the total, leaves every allocation unknown (PRX-GRP-156).
    let apportion (total: decimal option) (currency: string option) (members: string list) (direct: string -> decimal option) : CostApportionment =
        let directLines =
            members |> List.map (fun memberId -> { Member = Some memberId; Amount = direct memberId; Quality = "derived" })

        let known = directLines |> List.sumBy (fun line -> line.Amount |> Option.defaultValue 0m)

        match total with
        | Some total when known <= total && not members.IsEmpty ->
            let shared = total - known
            let share = floorTo (shared / decimal members.Length)
            let residue = shared - share * decimal members.Length

            { Method = equalShare
              Total = Some total
              Currency = currency
              Direct = directLines
              GroupShared = { Member = None; Amount = Some residue; Quality = "group-shared" }
              Allocated =
                directLines
                |> List.map (fun line -> { line with Amount = Some((line.Amount |> Option.defaultValue 0m) + share); Quality = "allocated" })
              Statement =
                $"{total} {currency |> Option.defaultValue String.Empty} apportioned {equalShare}: {known} direct, {shared} group-shared allocated as {share} per member, residue {residue} kept group-shared" }
        | _ ->
            let reason =
                match total with
                | None -> "the group total is unknown"
                | Some _ when members.IsEmpty -> "no member was begun"
                | Some total -> $"direct usage {known} exceeds the group total {total}"

            { Method = equalShare
              Total = total
              Currency = currency
              Direct = directLines
              GroupShared = { Member = None; Amount = None; Quality = "group-shared" }
              Allocated = directLines |> List.map (fun line -> { line with Amount = None; Quality = "allocated" })
              Statement = $"allocation unknown: {reason} (PRX-GRP-156)" }

    /// The shared total of a group execution's ingested sessions for one
    /// metric, when every value shares one currency (never summed across).
    let sharedTotal (execution: GroupExecutionRecord) (metricId: string) : (decimal * string option) option =
        let values = execution.Telemetry |> List.collect (fun snapshot -> snapshot.Metrics |> List.filter (fun metric -> metric.MetricId = metricId))

        match values, values |> List.map (fun metric -> metric.Currency) |> List.distinct with
        | [], _ -> None
        | values, [ currency ] -> Some(values |> List.sumBy (fun metric -> metric.Value), currency)
        | _ -> None
