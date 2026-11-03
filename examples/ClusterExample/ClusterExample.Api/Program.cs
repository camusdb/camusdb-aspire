using System.Diagnostics;
using System.Net;
using System.Text;
using CamusDB.Client;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The AppHost gives "notesdb" a connection string that lists the endpoint of each cluster node.
builder.AddCamusDBClient("notesdb");

WebApplication app = builder.Build();

app.MapHealthChecks("/health");

await RetryOnceAsync(async () =>
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    await using CamusConnection connection = scope.ServiceProvider.GetRequiredService<CamusConnection>();
    await connection.OpenAsync();

    await using CamusCommand create = connection.CreateCamusCommand(
        "CREATE TABLE IF NOT EXISTS notes (id oid PRIMARY KEY NOT NULL, body string NOT NULL, created int64 NOT NULL)");
    await create.ExecuteNonQueryAsync();
});

// The page asks each node for the notes by itself, so it shows that every node has the same data. Stop a
// node in the dashboard, and the page shows that node as down while you can still add notes.
app.MapGet("/", async (CamusConnection connection, IConfiguration configuration) =>
{
    string connectionString = configuration.GetConnectionString("notesdb")!;
    string endpointList = new CamusConnectionStringBuilder(connectionString).Config["Endpoint"];

    StringBuilder html = new();
    html.Append("""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>CamusDB cluster example</title>
        <style>
          body { font-family: system-ui, sans-serif; margin: 2rem; max-width: 48rem; }
          table { border-collapse: collapse; width: 100%; margin-bottom: 1.5rem; }
          th, td { border-bottom: 1px solid #ccc; padding: .4rem; text-align: left; }
          .up { color: #18794e; } .down { color: #c62828; }
        </style></head><body>
        <h1>CamusDB cluster example</h1>
        <h2>Nodes</h2>
        <table><tr><th>Endpoint</th><th>Status</th><th>Notes on this node</th><th>Time</th></tr>
        """);

    foreach (string endpoint in endpointList.Split(','))
    {
        string nodeConnectionString = connectionString.Replace(endpointList, endpoint);
        Stopwatch stopwatch = Stopwatch.StartNew();

        // A stopped node does not refuse the connection at once, so the page waits 2 seconds at most.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));

        try
        {
            await using CamusConnection node = new(new CamusConnectionStringBuilder(nodeConnectionString));
            await node.OpenAsync(timeout.Token);

            await using CamusCommand count = node.CreateCamusCommand("SELECT COUNT(*) FROM notes");
            long notes = Convert.ToInt64(await count.ExecuteScalarAsync(timeout.Token));

            html.Append($"<tr><td>{Encode(endpoint)}</td><td class=\"up\">up</td><td>{notes}</td><td>{stopwatch.ElapsedMilliseconds} ms</td></tr>");
        }
        catch (Exception ex)
        {
            html.Append($"<tr><td>{Encode(endpoint)}</td><td class=\"down\">down</td><td title=\"{Encode(ex.Message)}\">-</td><td>{stopwatch.ElapsedMilliseconds} ms</td></tr>");
        }
    }

    html.Append("""
        </table>
        <h2>Notes</h2>
        <form method="post" action="/notes">
          <input name="text" required maxlength="200" placeholder="Write a note" autofocus>
          <button type="submit">Add</button>
        </form>
        <ul>
        """);

    // This query uses the cluster connection string, so the client sends it to the next node in turn.
    await connection.OpenAsync();
    await using CamusCommand select = connection.CreateCamusCommand("SELECT body FROM notes ORDER BY created DESC LIMIT 20");
    await using CamusDataReader reader = await select.ExecuteReaderAsync();

    while (await reader.ReadAsync())
        html.Append($"<li>{Encode(reader.GetString(0))}</li>");

    html.Append("</ul></body></html>");
    return Results.Content(html.ToString(), "text/html");
});

app.MapPost("/notes", async (CamusConnection connection, HttpRequest request) =>
{
    IFormCollection form = await request.ReadFormAsync();
    string text = form["text"].ToString().Trim();

    if (text.Length > 0)
    {
        await RetryOnceAsync(async () =>
        {
            await connection.OpenAsync();

            await using CamusCommand insert = connection.CreateCamusCommand(
                "INSERT INTO notes (id, body, created) VALUES (gen_id(), @body, @created)");
            insert.Parameters.Add("@body", ColumnType.String, text);
            insert.Parameters.Add("@created", ColumnType.Integer64, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync();
        });
    }

    return Results.Redirect("/");
});

app.Run();

static string Encode(string value) => WebUtility.HtmlEncode(value);

// When a request goes to a node that is down, the request fails, and the client skips that node for 30
// seconds. The client does not send the request again, so the example tries one more time, and the second
// try goes to another node.
static async Task RetryOnceAsync(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (CamusException)
    {
        await action();
    }
}
