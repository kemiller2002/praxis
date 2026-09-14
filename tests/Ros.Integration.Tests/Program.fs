module Ros.Integration.Tests.Program

[<EntryPoint>]
let main _ =
    ActivityObservationTests.tests @ SerializationTests.tests |> TestRunner.run
