namespace Ros.Application

[<RequireQualifiedAccess>]
module AssemblyInfo =
    [<Literal>]
    let Name = "Ros.Application"

    let Dependencies =
        [ Ros.Domain.AssemblyInfo.Name
          Ros.Contracts.AssemblyInfo.Name ]
