namespace Praxis.Tests

open System.IO

/// `telemetry record` refuses a money-valued metric that `validate` would
/// reject (PRAXIS-TELEMETRY-COST-UNIT): the write path and the validator
/// share one rule, so a recorded cost can never make the repository invalid.
[<RequireQualifiedAccess>]
module TelemetryCostUnitTests =
    open PremergeFence

    let private agent =
        [ "PRAXIS_ACTOR_KIND", "agent"
          "PRAXIS_ACTOR", "example/agent"
          "PRAXIS_TELEMETRY_PROVIDER", "example"
          "PRAXIS_TELEMETRY_RUNTIME", "runtime"
          "PRAXIS_TELEMETRY_SESSION_ID", "session" ]

    /// A committed installation with FEAT-1 started under a declared agent.
    let private started label =
        let root = temporaryDirectory label
        git root [ "init"; "-q"; "-b"; "main" ] |> ignore
        configureGitIdentity root
        cli root [ "init"; "--project"; "Cost Unit" ] |> ok |> ignore
        commitAll root "install" |> ignore
        cliWith root agent [ "work"; "start"; "--id"; "FEAT-1"; "--type"; "mechanical"; "--occurred-at"; now () ] |> ok |> ignore
        let execution = (workItem root "FEAT-1").["telemetryExecutionIds"] |> strings |> List.head
        root, Path.Combine(root, ".ros", "telemetry", "executions", $"{execution}.json")

    let private record root (arguments: string list) =
        cliWith root agent ([ "telemetry"; "record"; "FEAT-1"; "--metric"; "cost.session_cumulative"; "--value"; "0.42"; "--quiet" ] @ arguments)

    let private costMetrics (executionFile: string) =
        (readJson executionFile).["metrics"] |> array |> List.filter (fun metric -> text metric.["id"] = "cost.session_cumulative")

    let tests =
        [ { Name = "telemetry cost unit: a cost recorded in a non-currency unit is refused and nothing is written"
            Run =
              fun () ->
                  let root, file = started "cost-unit-refused"
                  let before = File.ReadAllText file

                  [ [ "--unit"; "USD"; "--currency"; "USD" ]
                    [ "--unit"; "currency" ]
                    [ "--unit"; "currency"; "--currency"; "usd" ] ]
                  |> List.iter (fun arguments ->
                      let refused = record root arguments
                      let label = String.concat " " arguments
                      Assert.isTrue (refused.ExitCode <> 0) $"'{label}' was accepted"
                      contains "cost metric requires unit 'currency' and an ISO-style three-letter currency" refused.Error label
                      Assert.equal before (File.ReadAllText file))

                  cli root [ "validate" ] |> ok |> ignore }

          { Name = "telemetry cost unit: a valid cost records, defaults its unit from the registry, and validates"
            Run =
              fun () ->
                  let root, file = started "cost-unit-valid"
                  // No --unit: the registry's own unit, currency, is used.
                  record root [ "--currency"; "USD" ] |> ok |> ignore
                  record root [ "--unit"; "currency"; "--currency"; "USD" ] |> ok |> ignore

                  let recorded = costMetrics file |> List.map (fun metric -> text metric.["unit"], text metric.["currency"])
                  Assert.equal [ "currency", "USD"; "currency", "USD" ] recorded
                  cli root [ "validate" ] |> ok |> ignore }

          { Name = "telemetry cost unit: non-cost metrics keep accepting their own units"
            Run =
              fun () ->
                  let root, _ = started "cost-unit-tokens"

                  cliWith root agent [ "telemetry"; "record"; "FEAT-1"; "--metric"; "tokens.input"; "--value"; "10"; "--unit"; "tokens"; "--quiet" ]
                  |> ok
                  |> ignore

                  cli root [ "validate" ] |> ok |> ignore } ]
