module Ros.Tests.Program

[<EntryPoint>]
let main _ =
    ArchitectureTests.tests @ ArtifactTests.tests |> TestRunner.run
