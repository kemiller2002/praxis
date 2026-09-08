module Ros.Tests.Program

[<EntryPoint>]
let main _ =
    ArchitectureTests.tests |> TestRunner.run
