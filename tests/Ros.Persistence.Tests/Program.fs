module Ros.Persistence.Tests.Program

[<EntryPoint>]
let main _ = ActivityStoreTests.tests @ OutboxStoreTests.tests |> TestRunner.run
