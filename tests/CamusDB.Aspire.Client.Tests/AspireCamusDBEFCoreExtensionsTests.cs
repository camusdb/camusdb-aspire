using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CamusDB.Aspire.Client.Tests;

public sealed class AspireCamusDBEFCoreExtensionsTests
{
    private const string ConnectionString = "Endpoint=http://localhost:5095;Database=app";

    [Fact]
    public void AddCamusDBDbContext_RegistersContextWithConnectionString()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:camus"] = ConnectionString;

        builder.AddCamusDBDbContext<TestDbContext>("camus");

        using IHost host = builder.Build();
        using IServiceScope scope = host.Services.CreateScope();
        TestDbContext context = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        Assert.Equal("CamusDB.EntityFrameworkCore", context.Database.ProviderName);
        Assert.Equal(ConnectionString, context.Database.GetConnectionString());
    }

    [Fact]
    public void AddCamusDBDbContext_AddsHealthCheck()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:camus"] = ConnectionString;

        builder.AddCamusDBDbContext<TestDbContext>("camus");

        using IHost host = builder.Build();
        HealthCheckServiceOptions options = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        Assert.Equal(nameof(TestDbContext), Assert.Single(options.Registrations).Name);
    }

    [Fact]
    public void AddCamusDBDbContext_ThrowsWithoutConnectionString()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);

        Assert.Throws<InvalidOperationException>(() => builder.AddCamusDBDbContext<TestDbContext>("camus"));
    }

    [Fact]
    public void AddCamusDBDbContext_AppliesConfigureDbContextOptions()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:camus"] = ConnectionString;

        builder.AddCamusDBDbContext<TestDbContext>("camus", configureDbContextOptions: options => options.EnableSensitiveDataLogging());

        using IHost host = builder.Build();
        DbContextOptions<TestDbContext> options = host.Services.GetRequiredService<DbContextOptions<TestDbContext>>();

        Assert.True(options.FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>()!.IsSensitiveDataLoggingEnabled);
    }

    public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<Item> Items => Set<Item>();
    }

    public sealed class Item
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";
    }
}
