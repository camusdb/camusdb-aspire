using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using CamusDB.Client;

namespace CamusDB.Aspire.Hosting.Tests;

/// <summary>
/// Starts the playground AppHost with a real CamusDB container. These tests need Docker. Run the other
/// tests only with <c>--filter "Category!=Docker"</c>.
/// </summary>
[Trait("Category", "Docker")]
public sealed class PlaygroundFunctionalTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AppHost_CreatesDatabaseAndApiUsesIt(bool authentication, bool grpc)
    {
        using CancellationTokenSource cts = new(StartTimeout);
        CancellationToken cancellationToken = cts.Token;

        IDistributedApplicationTestingBuilder builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.CamusDB_AppHost>(
            [
                "--Playground:Ephemeral=true",
                $"--Playground:Authentication={authentication}",
                $"--Playground:Grpc={grpc}",
            ],
            cancellationToken);

        await using DistributedApplication app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);

        await app.ResourceNotifications.WaitForResourceHealthyAsync("appdb", cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken);

        // The database exists and accepts SQL with the connection string that the AppHost gives out.
        string? connectionString = await app.GetConnectionStringAsync("appdb", cancellationToken);
        Assert.NotNull(connectionString);
        Assert.Contains("Database=appdb", connectionString);
        Assert.Equal(authentication, connectionString.Contains("Password="));
        Assert.Equal(grpc, connectionString.Contains("Protocol=grpc"));

        await using (CamusConnection connection = new(new CamusConnectionStringBuilder(connectionString)))
        {
            await connection.OpenAsync(cancellationToken);

            await using CamusCommand count = connection.CreateCamusCommand("SELECT COUNT(*) FROM todos");
            object? before = await count.ExecuteScalarAsync(cancellationToken);
            Assert.Equal(0L, Convert.ToInt64(before));
        }

        // The API reaches the same database through the client integration.
        using HttpClient http = app.CreateHttpClient("api");

        using HttpResponseMessage created = await http.PostAsJsonAsync("/todos", new { title = "write the Aspire integration" }, cancellationToken);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(cancellationToken));

        List<TodoDto>? todos = await http.GetFromJsonAsync<List<TodoDto>>("/todos", cancellationToken);
        TodoDto todo = Assert.Single(todos!);
        Assert.Equal("write the Aspire integration", todo.Title);
        Assert.False(todo.Done);

        using HttpResponseMessage health = await http.GetAsync("/health", cancellationToken);
        Assert.True(health.IsSuccessStatusCode);
    }

    private sealed record TodoDto(string Id, string Title, bool Done);
}
