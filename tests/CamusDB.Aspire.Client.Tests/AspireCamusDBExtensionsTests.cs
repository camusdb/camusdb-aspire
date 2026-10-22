using CamusDB.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CamusDB.Aspire.Client.Tests;

public sealed class AspireCamusDBExtensionsTests
{
    private const string ConnectionString = "Endpoint=http://localhost:5095;Database=app";

    [Fact]
    public void AddCamusDBClient_ReadsConnectionStringsSection()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:camus"] = ConnectionString;

        builder.AddCamusDBClient("camus");

        using IHost host = builder.Build();
        using CamusConnection connection = host.Services.GetRequiredService<CamusConnection>();

        Assert.Equal("http://localhost:5095", connection.DataSource);
        Assert.Equal("app", connection.Database);
    }

    [Fact]
    public void AddCamusDBClient_SharesOneBuilderAndCreatesNewConnections()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:camus"] = ConnectionString;

        builder.AddCamusDBClient("camus");

        using IHost host = builder.Build();

        Assert.Same(
            host.Services.GetRequiredService<CamusConnectionStringBuilder>(),
            host.Services.GetRequiredService<CamusConnectionStringBuilder>());
        Assert.NotSame(
            host.Services.GetRequiredService<CamusConnection>(),
            host.Services.GetRequiredService<CamusConnection>());
    }

    [Fact]
    public void AddCamusDBClient_ConnectionStringsSectionWinsOverSettingsSection()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:camus"] = ConnectionString;
        builder.Configuration["Aspire:CamusDB:Client:ConnectionString"] = "Endpoint=http://other:5095;Database=other";

        builder.AddCamusDBClient("camus");

        using IHost host = builder.Build();
        using CamusConnection connection = host.Services.GetRequiredService<CamusConnection>();

        Assert.Equal("app", connection.Database);
    }

    [Fact]
    public void AddCamusDBClient_UsesSettingsSectionWithoutConnectionStrings()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["Aspire:CamusDB:Client:ConnectionString"] = ConnectionString;

        builder.AddCamusDBClient("camus");

        using IHost host = builder.Build();
        using CamusConnection connection = host.Services.GetRequiredService<CamusConnection>();

        Assert.Equal("app", connection.Database);
    }

    [Fact]
    public void AddCamusDBClient_ThrowsWithoutConnectionString()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => builder.AddCamusDBClient("camus"));

        Assert.Contains("ConnectionStrings:camus", ex.Message);
    }

    [Fact]
    public void AddKeyedCamusDBClient_RegistersKeyedServices()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:orders"] = "Endpoint=http://localhost:5095;Database=orders";
        builder.Configuration["ConnectionStrings:users"] = "Endpoint=http://localhost:5095;Database=users";

        builder.AddKeyedCamusDBClient("orders");
        builder.AddKeyedCamusDBClient("users");

        using IHost host = builder.Build();
        using CamusConnection orders = host.Services.GetRequiredKeyedService<CamusConnection>("orders");
        using CamusConnection users = host.Services.GetRequiredKeyedService<CamusConnection>("users");

        Assert.Equal("orders", orders.Database);
        Assert.Equal("users", users.Database);
        Assert.Null(host.Services.GetService<CamusConnection>());
    }

    [Fact]
    public void AddCamusDBClient_AddsHealthCheck()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:camus"] = ConnectionString;
        builder.Configuration["ConnectionStrings:users"] = ConnectionString;

        builder.AddCamusDBClient("camus");
        builder.AddKeyedCamusDBClient("users");

        using IHost host = builder.Build();
        HealthCheckServiceOptions options = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        Assert.Equal(["CamusDB", "CamusDB_users"], options.Registrations.Select(r => r.Name).Order());
    }

    [Fact]
    public void AddCamusDBClient_DisableHealthChecksSkipsHealthCheck()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:camus"] = ConnectionString;
        builder.Configuration["Aspire:CamusDB:Client:DisableHealthChecks"] = "true";

        builder.AddCamusDBClient("camus");

        using IHost host = builder.Build();
        HealthCheckServiceOptions? options = host.Services.GetService<IOptions<HealthCheckServiceOptions>>()?.Value;

        Assert.Empty(options?.Registrations ?? []);
    }

    [Fact]
    public async Task HealthCheck_IsUnhealthyWhenServerIsDown()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:camus"] = "Endpoint=http://127.0.0.1:1;Database=app;Timeout=2";

        builder.AddCamusDBClient("camus");

        using IHost host = builder.Build();
        HealthReport report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
    }
}
