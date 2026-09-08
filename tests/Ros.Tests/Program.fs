module Ros.Tests.Program

[<EntryPoint>]
let main _ =
    ArchitectureTests.tests @ ArtifactTests.tests @ PersistenceTests.tests |> TestRunner.run
