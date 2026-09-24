namespace Ros.Domain.Telemetry

/// Presentation-ready trend point. None means no measurement was supplied;
/// it is never silently converted to zero.
type TutelaTrendPoint =
    { SubjectRef: string
      CollectedAt: System.DateTimeOffset
      Violated: int option
      Unknown: int option
      Stale: int option
      UnknownEffects: int option
      ActiveExceptions: int option
      IndependentVerificationCoverage: float option }

type TutelaTrend =
    { Points: TutelaTrendPoint list
      Latest: TutelaTrendPoint option
      Previous: TutelaTrendPoint option
      Regressions: string list
      Improvements: string list }

[<RequireQualifiedAccess>]
module TutelaTrend =
    let private rose previous current =
        match previous,current with Some p,Some c -> c > p | _ -> false
    let private fell previous current =
        match previous,current with Some p,Some c -> c < p | _ -> false
    let private coverageFell previous current =
        match previous,current with Some p,Some c -> c < p | _ -> false
    let private coverageRose previous current =
        match previous,current with Some p,Some c -> c > p | _ -> false

    let derive (snapshots: TutelaSnapshot list) =
        let points =
            snapshots |> List.sortBy (fun s -> s.CollectedAt) |> List.map (fun s ->
                { SubjectRef=s.SubjectRef; CollectedAt=s.CollectedAt; Violated=s.Violated; Unknown=s.Unknown
                  Stale=s.Stale; UnknownEffects=s.UnknownSecurityEffects; ActiveExceptions=s.ActiveExceptions
                  IndependentVerificationCoverage=s.IndependentVerificationCoverage })
        let latest = points |> List.tryLast
        let previous = if points.Length > 1 then Some points[points.Length-2] else None
        let regressions,improvements =
            match previous,latest with
            | Some p,Some c ->
                [ if rose p.Violated c.Violated then "violated invariants increased"
                  if rose p.Unknown c.Unknown then "unknown invariants increased"
                  if rose p.Stale c.Stale then "stale evidence increased"
                  if rose p.UnknownEffects c.UnknownEffects then "unknown security effects increased"
                  if rose p.ActiveExceptions c.ActiveExceptions then "active exceptions increased"
                  if coverageFell p.IndependentVerificationCoverage c.IndependentVerificationCoverage then "independent verification coverage decreased" ],
                [ if fell p.Violated c.Violated then "violated invariants decreased"
                  if fell p.Unknown c.Unknown then "unknown invariants decreased"
                  if fell p.Stale c.Stale then "stale evidence decreased"
                  if fell p.UnknownEffects c.UnknownEffects then "unknown security effects decreased"
                  if fell p.ActiveExceptions c.ActiveExceptions then "active exceptions decreased"
                  if coverageRose p.IndependentVerificationCoverage c.IndependentVerificationCoverage then "independent verification coverage increased" ]
            | _ -> [],[]
        { Points=points; Latest=latest; Previous=previous; Regressions=regressions; Improvements=improvements }
