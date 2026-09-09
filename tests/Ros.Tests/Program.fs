module Ros.Tests.Program

[<EntryPoint>]
let main _ =
    ArchitectureTests.tests
    @ ArtifactTests.tests
    @ PersistenceTests.tests
    @ GitTests.tests
    @ WorkTests.tests
    @ WorkPlanTests.tests
    @ BacklogTests.tests
    @ TelemetryTests.tests
    @ TelemetryResolutionTests.tests
    @ PathFilterTests.tests
    @ WorkAttributionTests.tests
    @ QueueValidationTests.tests
    @ QueuePresentationTests.tests
    @ BacklogTransitionEffectTests.tests
    @ WorkCaptureTests.tests
    @ WorkCaptureEffectTests.tests
    |> TestRunner.run
