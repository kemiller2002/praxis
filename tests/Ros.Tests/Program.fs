module Ros.Tests.Program

[<EntryPoint>]
let main _ =
    ArchitectureTests.tests @ ArtifactTests.tests @ PersistenceTests.tests @ GitTests.tests @ WorkTests.tests @ WorkPlanTests.tests @ TelemetryTests.tests
    |> TestRunner.run
