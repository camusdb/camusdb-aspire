# CamusDB for Aspire

[Aspire](https://aspire.dev) integrations for [CamusDB](https://github.com/camusdb/camusdb). They are
built on the CamusDB .NET client libraries from
[camusdb-dotnet](https://github.com/camusdb/camusdb-dotnet).

| Package | Use it in | What it does |
| --- | --- | --- |
| [`CamusDB.Aspire.Hosting`](src/CamusDB.Aspire.Hosting/README.md) | The AppHost | Runs a CamusDB container, creates databases, gives connection strings. |
| [`CamusDB.Aspire.Client`](src/CamusDB.Aspire.Client/README.md) | A service project | Registers `CamusDB.Client` connections, with a health check, tracing and metrics. |
| [`CamusDB.Aspire.EntityFrameworkCore`](src/CamusDB.Aspire.EntityFrameworkCore/README.md) | A service project | Registers a pooled `DbContext` on the CamusDB EF Core provider. |

## Quick start

In the AppHost:

```csharp
var camus = builder.AddCamusDB("camus").WithDataVolume();
var appDb = camus.AddDatabase("appdb");

builder.AddProject<Projects.ApiService>("api")
    .WithReference(appDb)
    .WaitFor(appDb);
```

In the service:

```csharp
builder.AddCamusDBClient("appdb");                  // ADO.NET
// or
builder.AddCamusDBDbContext<AppDbContext>("appdb"); // EF Core
```

## Versions

| Dependency | Version |
| --- | --- |
| Aspire | 13.6.3 |
| `CamusDB.Client`, `CamusDB.EntityFrameworkCore` | 0.16.1 |
| Server image `camusdb/camusdb` | 0.15.3 |
| Target frameworks | Hosting: `net8.0`. Client: `net8.0`, `net9.0`, `net10.0`. EF Core: `net10.0`. |

## Repository layout

```text
src/          the three packages
tests/        unit tests, and functional tests that start a real CamusDB container
playground/   an AppHost and a minimal API that use the packages
```

## Build and test

The repository needs the .NET 10 SDK. The tests use Microsoft.Testing.Platform, which `global.json`
selects.

```shell
dotnet build CamusDB.Aspire.slnx

# Unit tests only
dotnet test --project tests/CamusDB.Aspire.Hosting.Tests --filter-not-trait "Category=Docker"
dotnet test --project tests/CamusDB.Aspire.Client.Tests

# Functional tests: they need Docker, and they take about 4 minutes
dotnet test --project tests/CamusDB.Aspire.Hosting.Tests --filter-trait "Category=Docker"
```

The functional tests start the playground AppHost three times: with no authentication, with
authentication, and with the gRPC protocol. Each run creates the database, runs SQL with the
connection string from the AppHost, and calls the API.

## Make the packages

```shell
scripts/pack-nuget.sh
```

The script runs the unit tests, and then it packs the three packages into `artifacts/packages`. It
pushes to nuget.org only when `PUSH=1`. See [scripts/README.md](scripts/README.md) for the options,
the use of the packages from a local folder, and the release steps.

## Run the playground

```shell
dotnet run --project playground/CamusDB.AppHost
```

The AppHost reads three flags: `Playground:Authentication`, `Playground:Grpc` and
`Playground:Ephemeral` (no data volume). For example:

```shell
dotnet run --project playground/CamusDB.AppHost -- --Playground:Authentication=true
```

The API has `GET /todos` and `POST /todos` with the body `{ "title": "..." }`.
