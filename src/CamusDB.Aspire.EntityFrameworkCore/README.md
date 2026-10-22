# CamusDB.Aspire.EntityFrameworkCore

The Aspire client integration for the [CamusDB](https://github.com/camusdb/camusdb) Entity Framework
Core provider (`CamusDB.EntityFrameworkCore`). It registers a pooled `DbContext`, and it adds retries,
a health check, tracing and metrics.

## Install

```shell
dotnet add package CamusDB.Aspire.EntityFrameworkCore
```

## Usage

```csharp
builder.AddCamusDBDbContext<AppDbContext>("appdb");
```

The integration calls `AddDbContextPool<AppDbContext>` with `UseCamusDB(connectionString)`. Thus the
context must have a constructor that takes `DbContextOptions<AppDbContext>`.

To change the options of the context:

```csharp
builder.AddCamusDBDbContext<AppDbContext>(
    "appdb",
    configureDbContextOptions: options => options.EnableSensitiveDataLogging());
```

## Configuration

The connection string comes from `ConnectionStrings:{connectionName}`. Without it, the integration
reads the `Aspire:CamusDB:EntityFrameworkCore` section. It then reads
`Aspire:CamusDB:EntityFrameworkCore:{context type name}`, and a value there wins.

```json
{
  "Aspire": {
    "CamusDB": {
      "EntityFrameworkCore": {
        "DisableRetry": false,
        "DisableHealthChecks": false,
        "DisableTracing": false,
        "DisableMetrics": false
      }
    }
  }
}
```

| Setting | Effect when it is `false` (the default) |
| --- | --- |
| `DisableRetry` | The provider retries transient failures (`EnableRetryOnFailure()`). |
| `DisableHealthChecks` | A health check named after the context type calls `CanConnectAsync`. |
| `DisableTracing` | Tracing adds the `CamusDB.Client` activity source. |
| `DisableMetrics` | Metrics add the `CamusDB.Client` and `Microsoft.EntityFrameworkCore` meters. |
