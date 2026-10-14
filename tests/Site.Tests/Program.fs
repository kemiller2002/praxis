module Site.Tests.Program

[<EntryPoint>]
let main _ =
    AccessibilityTests.tests
    @ CaseStudyTests.tests
    @ ClaimsTests.tests
    @ ContentTests.tests
    @ DesignTests.tests
    @ EvidenceTests.tests
    @ PerformanceTests.tests
    @ RecordTests.tests
    @ ResponsiveTests.tests
    @ SecurityTests.tests
    @ SiteTests.tests
    @ WorkflowTests.tests
    |> TestRunner.run
