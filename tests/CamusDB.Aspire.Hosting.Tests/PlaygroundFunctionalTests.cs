using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CamusDB.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CamusDB.Aspire.Hosting.Tests;

/// <summary>
/// Starts the playground AppHost with a real CamusDB container, or with a real cluster of three
/// containers. These tests need Docker. Run the other tests only with <c>--filter-not-trait "Category=Docker"</c>.
/// </summary>
[Trait("Category", "Docker")]
public sealed class PlaygroundFunctionalTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ClusterStartTimeout = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 3)]
    [InlineData(true, false, 3)]
    [InlineData(false, true, 3)]
    public async Task AppHost_CreatesDatabaseAndApiUsesIt(bool authentication, bool grpc, int clusterNodes)
    {
        using CancellationTokenSource cts = new(clusterNodes > 0 ? ClusterStartTimeout : StartTimeout);
        CancellationToken cancellationToken = cts.Token;

        IDistributedApplicationTestingBuilder builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.CamusDB_AppHost>(
            [
                "--Playground:Ephemeral=true",
                $"--Playground:Authentication={authentication}",
                $"--Playground:Grpc={grpc}",
                $"--Playground:ClusterNodes={clusterNodes}",
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

        CamusConnectionStringBuilder parsed = new(connectionString);
        string[] endpoints = parsed.Config["Endpoint"].Split(',');
        Assert.Equal(Math.Max(clusterNodes, 1), endpoints.Length);

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

        // Each node serves the row that the API wrote through the cluster.
        foreach (string endpoint in endpoints)
        {
            string nodeConnectionString = connectionString.Replace(parsed.Config["Endpoint"], endpoint);
            await using CamusConnection connection = new(new CamusConnectionStringBuilder(nodeConnectionString));
            await connection.OpenAsync(cancellationToken);

            await using CamusCommand count = connection.CreateCamusCommand("SELECT COUNT(*) FROM todos");
            Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)));
        }
    }

    [Fact]
    public async Task Cluster_ContinuesWhenOneNodeStops()
    {
        using CancellationTokenSource cts = new(ClusterStartTimeout);
        CancellationToken cancellationToken = cts.Token;

        IDistributedApplicationTestingBuilder builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.CamusDB_AppHost>(
            ["--Playground:Ephemeral=true", "--Playground:ClusterNodes=3"],
            cancellationToken);

        await using DistributedApplication app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);

        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("camus", cancellationToken);

        using HttpClient http = app.CreateHttpClient("api");
        await PostTodoAsync(http, "before the stop", cancellationToken);

        IResource node = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.Single(r => r.Name == "camus-3");
        ExecuteCommandResult stopped = await app.Services.GetRequiredService<ResourceCommandService>()
            .ExecuteCommandAsync(node, KnownResourceCommands.StopCommand, cancellationToken);
        Assert.True(stopped.Success, stopped.Message);

        // Two of three nodes are a majority, so the cluster continues, and it stays healthy with a count of
        // the healthy nodes.
        ResourceEvent majority = await app.ResourceNotifications.WaitForResourceAsync(
            "camus",
            e => e.Snapshot.HealthReports.Any(r => r.Name == "camus_check" && r.Description?.StartsWith("2 of 3") == true),
            cancellationToken);
        Assert.Equal(KnownResourceStates.Running, majority.Snapshot.State?.Text);
        Assert.Equal(HealthStatus.Healthy, majority.Snapshot.HealthStatus);

        // A resource that waits for the cluster can restart while the node is down.
        IResource api = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.Single(r => r.Name == "api");
        ExecuteCommandResult restarted = await app.Services.GetRequiredService<ResourceCommandService>()
            .ExecuteCommandAsync(api, KnownResourceCommands.RestartCommand, cancellationToken);
        Assert.True(restarted.Success, restarted.Message);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken);

        using HttpClient restartedHttp = app.CreateHttpClient("api");
        await PostTodoAsync(restartedHttp, "after the stop", cancellationToken);

        List<TodoDto>? todos = await restartedHttp.GetFromJsonAsync<List<TodoDto>>("/todos", cancellationToken);
        Assert.Equal(["after the stop", "before the stop"], todos!.Select(todo => todo.Title));
    }

    // The client marks a node as down after a request to it fails, and the next requests skip that node.
    // Thus the first request after a node stops can fail.
    private static async Task PostTodoAsync(HttpClient http, string title, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync("/todos", new { title }, cancellationToken);
            if (response.IsSuccessStatusCode)
                return;

            Assert.True(attempt < 3, await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }

    private sealed record TodoDto(string Id, string Title, bool Done);
}
