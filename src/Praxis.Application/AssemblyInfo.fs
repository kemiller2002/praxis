namespace Praxis.Application

[<RequireQualifiedAccess>]
module AssemblyInfo =
    [<Literal>]
    let Name = "Praxis.Application"

    let Dependencies =
        [ Praxis.Domain.AssemblyInfo.Name
          Praxis.Contracts.AssemblyInfo.Name ]
