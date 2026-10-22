# CamusDB.Aspire.Hosting

The Aspire hosting integration for [CamusDB](https://github.com/camusdb/camusdb). Use it in an Aspire
AppHost project. It runs a standalone CamusDB node in a container, creates databases on it, and gives
connection strings to the projects that reference it.

## Install

```shell
dotnet add package CamusDB.Aspire.Hosting
```

## Usage

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var camus = builder.AddCamusDB("camus")
    .WithDataVolume();

var appDb = camus.AddDatabase("appdb");

builder.AddProject<Projects.ApiService>("api")
    .WithReference(appDb)
    .WaitFor(appDb);

builder.Build().Run();
```

`AddCamusDB` runs the `docker.io/camusdb/camusdb:0.15.3` image. The container has two endpoints:

| Endpoint | Container port | API |
| --- | --- | --- |
| `http` | 5095 | JSON/REST API and `/health` |
| `grpc` | 5096 | gRPC API, plaintext HTTP/2 |

The AppHost does an HTTP health check on `/health` of the `http` endpoint.

CamusDB does not create a database on first use. When the server becomes healthy, the AppHost runs
`CREATE DATABASE IF NOT EXISTS` for each `AddDatabase` resource. A database resource becomes healthy
only after that, so `WaitFor(appDb)` starts the project only when the database exists.

## Connection strings

The connection string uses the keys of the `CamusDB.Client` ADO.NET provider:

```text
Endpoint=http://localhost:61234;Database=appdb
```

A server resource gives a connection string with no `Database` key. A `CamusConnection` cannot open
without that key, so reference a database resource.

To use the gRPC API, select the protocol:

```csharp
builder.AddCamusDB("camus").WithProtocol(CamusDBProtocol.Grpc);
// Endpoint={grpc url};Protocol=grpc;BackupEndpoint={http url};Database=appdb
```

A referenced resource also gets these connection properties as environment variables, for example
`APPDB_HOST`: `Host`, `Port`, `GrpcPort`, `Uri`, `GrpcUri`, `Database`, and with authentication on,
`Username` and `Password`.

## Authentication

Authentication is off by default, as in CamusDB. To turn it on:

```csharp
var password = builder.AddParameter("camus-password", secret: true);

builder.AddCamusDB("camus")
    .WithAuthentication(password: password);   // the user is "admin"
```

When you do not give a password, Aspire generates one. The integration also generates the token key
(`CAMUSDB_AUTH_TOKEN_KEY`). The connection strings then carry `User`, `Password` and
`AllowInsecureCredentials=true`.

Read these limits before you use it:

- The server reads the bootstrap user and password only when its user catalog is empty. With a data
  volume, a changed password has no effect on a server that already has users.
- The password must not contain a single quote (`'`).
- The container serves plaintext HTTP, and other containers reach it by a host name that is not
  loopback. Thus the integration starts the server with `--require-tls-when-auth-enabled=false`.
  This setup is for local development only.

## Other settings

The server reads its log levels and its partition count from environment variables:

```csharp
builder.AddCamusDB("camus")
    .WithEnvironment("CAMUS_LOG_LEVEL", "Debug")
    .WithEnvironment("CAMUS_INITIAL_PARTITIONS", "8");
```

Use `WithImageTag("0.15.2")` to run a different server version. Use `WithDataBindMount(path)` to keep
the data in a host folder instead of a volume.
