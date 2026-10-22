# CamusDB.Aspire.Client

The Aspire client integration for [CamusDB](https://github.com/camusdb/camusdb). It registers the
`CamusDB.Client` ADO.NET provider in dependency injection, and it adds a health check, tracing and
metrics.

## Install

```shell
dotnet add package CamusDB.Aspire.Client
```

## Usage

In `Program.cs` of the project that references the database resource:

```csharp
builder.AddCamusDBClient("appdb");
```

Then get a `CamusConnection` from dependency injection:

```csharp
app.MapGet("/todos", async (CamusConnection connection) =>
{
    await connection.OpenAsync();

    await using CamusCommand select = connection.CreateCamusCommand("SELECT id, title FROM todos");
    await using CamusDataReader reader = await select.ExecuteReaderAsync();
    // ...
});
```

The integration registers these services:

| Service | Lifetime |
| --- | --- |
| `CamusConnectionStringBuilder` | Singleton. It holds the endpoint pool, the endpoint health and the access token. |
| `CamusConnection` | Transient. |

For more than one database, use keyed services:

```csharp
builder.AddKeyedCamusDBClient("orders");
builder.AddKeyedCamusDBClient("users");

app.MapGet("/", ([FromKeyedServices("orders")] CamusConnection orders) => { /* ... */ });
```

## Configuration

The connection string comes from `ConnectionStrings:{connectionName}`. Without it, the integration
reads the `Aspire:CamusDB:Client` section:

```json
{
  "Aspire": {
    "CamusDB": {
      "Client": {
        "ConnectionString": "Endpoint=http://localhost:5095;Database=appdb",
        "DisableHealthChecks": false,
        "DisableTracing": false,
        "DisableMetrics": false
      }
    }
  }
}
```

A keyed client also reads `Aspire:CamusDB:Client:{name}`, and a value there wins. You can also change
the settings in code:

```csharp
builder.AddCamusDBClient("appdb", settings => settings.DisableHealthChecks = true);
```

## Health check, tracing and metrics

- The health check (`CamusDB`, or `CamusDB_{name}` for a keyed client) pings the server.
- Tracing adds the `CamusDB.Client` activity source.
- Metrics add the `CamusDB.Client` meter.

Your project still configures the OpenTelemetry exporter, usually in its service defaults.
