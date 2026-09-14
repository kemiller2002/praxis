module Ros.Host.Program

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Ros.Persistence

/// Where Central's provisional file-backed store lives. Entirely
/// separate from repository-local `.ros/` state -- this host has no
/// dependency on, and no effect on, any individual repository's own
/// ROS state, per the "local repos stay independent" rule in
/// docs/migrations/central-integration/MIGRATION-PLAN.md.
let private dataRoot () =
    match Environment.GetEnvironmentVariable "ROS_CENTRAL_DATA_DIR" with
    | null
    | "" -> Path.Combine(Directory.GetCurrentDirectory(), ".ros-central")
    | value -> value

/// Migration control, not permanent architecture (see MIGRATION-PLAN.md
/// "Feature flags during migration"): the ingestion endpoint refuses
/// traffic until this is explicitly turned on, so deploying this host
/// is never itself enough to start accepting external activity.
let private externalActivityEnabled () =
    match Environment.GetEnvironmentVariable "ROS_EXTERNAL_ACTIVITY_ENABLED" with
    | "true" -> true
    | _ -> false

let private outcomeToResult (outcome: IngestOutcome) : IResult =
    match outcome with
    | Accepted activityId -> Results.Json({| activityId = activityId; state = "accepted" |}, statusCode = Nullable 202)
    | Malformed message -> Results.Json({| error = message |}, statusCode = Nullable 400)
    | Invalid errors -> Results.Json({| errors = errors |> List.map string |}, statusCode = Nullable 422)
    | Conflict message -> Results.Json({| error = message |}, statusCode = Nullable 409)

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)
    let app = builder.Build()
    let root = dataRoot ()

    app.MapGet("/health", Func<IResult>(fun () -> Results.Ok {| status = "ok" |})) |> ignore

    app.MapGet("/version", Func<IResult>(fun () -> Results.Ok {| version = AssemblyInfo.Version |}))
    |> ignore

    app.MapPost(
        "/integration/v1/activities",
        Func<HttpRequest, Threading.Tasks.Task<IResult>>(fun request ->
            task {
                if not (externalActivityEnabled ()) then
                    return Results.Json({| error = "external activity ingestion is disabled (ROS_EXTERNAL_ACTIVITY_ENABLED)" |}, statusCode = Nullable 503)
                else
                    use reader = new StreamReader(request.Body)
                    let! json = reader.ReadToEndAsync()

                    let source =
                        match request.Headers.["X-Ros-Producer"] |> Seq.tryHead with
                        | Some value when not (String.IsNullOrWhiteSpace value) -> value
                        | _ -> "unspecified"

                    let outcome =
                        ActivityIngestion.handle
                            (ActivityStore.tryFind root)
                            (ActivityStore.save root)
                            (fun () -> DateTimeOffset.UtcNow)
                            source
                            json

                    return outcomeToResult outcome
            })
    )
    |> ignore

    app.Run()
    0
