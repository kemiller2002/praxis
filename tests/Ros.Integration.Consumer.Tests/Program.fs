module Ros.Integration.Consumer.Tests.Program

[<EntryPoint>]
let main _ = ConsumerScenarioTests.tests |> TestRunner.run
