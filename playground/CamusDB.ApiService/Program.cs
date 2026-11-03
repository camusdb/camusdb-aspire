using CamusDB.Client;
using OpenTelemetry;
using OpenTelemetry.Trace;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddCamusDBClient("appdb");

builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddAspNetCoreInstrumentation());

// The AppHost sets the OTLP endpoint; a run without the AppHost exports nothing.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    builder.Services.AddOpenTelemetry().UseOtlpExporter();

WebApplication app = builder.Build();

app.MapHealthChecks("/health");

// With a cluster, the first request to a node that is down fails, and the client skips that node after
// that. Thus the table creation gets one more try, which goes to another node.
for (int attempt = 1; ; attempt++)
{
    try
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        await using CamusConnection connection = scope.ServiceProvider.GetRequiredService<CamusConnection>();
        await connection.OpenAsync();

        await using CamusCommand create = connection.CreateCamusCommand(
            "CREATE TABLE IF NOT EXISTS todos (id oid PRIMARY KEY NOT NULL, title string NOT NULL, done bool NOT NULL)");
        await create.ExecuteNonQueryAsync();
        break;
    }
    catch (CamusException) when (attempt < 2)
    {
    }
}

app.MapGet("/todos", async (CamusConnection connection) =>
{
    await connection.OpenAsync();

    await using CamusCommand select = connection.CreateCamusCommand("SELECT id, title, done FROM todos ORDER BY title");
    await using CamusDataReader reader = await select.ExecuteReaderAsync();

    List<Todo> todos = [];
    while (await reader.ReadAsync())
        todos.Add(new Todo(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));

    return todos;
});

app.MapPost("/todos", async (CamusConnection connection, NewTodo input) =>
{
    await connection.OpenAsync();

    await using CamusCommand insert = connection.CreateCamusCommand(
        "INSERT INTO todos (id, title, done) VALUES (gen_id(), @title, false) RETURNING id");
    insert.Parameters.Add("@title", ColumnType.String, input.Title);

    await using CamusDataReader reader = await insert.ExecuteReaderAsync();
    await reader.ReadAsync();

    Todo todo = new(reader.GetString(0), input.Title, false);
    return Results.Created($"/todos/{todo.Id}", todo);
});

app.Run();

internal sealed record Todo(string Id, string Title, bool Done);

internal sealed record NewTodo(string Title);
