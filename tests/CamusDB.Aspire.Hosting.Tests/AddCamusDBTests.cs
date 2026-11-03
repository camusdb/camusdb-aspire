using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CamusDB.Client;

namespace CamusDB.Aspire.Hosting.Tests;

public sealed class AddCamusDBTests
{
    [Fact]
    public void AddCamusDB_AddsContainerWithImageAndEndpoints()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        builder.AddCamusDB("camus", httpPort: 15095, grpcPort: 15096);

        CamusDBServerResource resource = Assert.Single(builder.Resources.OfType<CamusDBServerResource>());
        Assert.Equal("camus", resource.Name);

        ContainerImageAnnotation image = Assert.Single(resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("docker.io", image.Registry);
        Assert.Equal("camusdb/camusdb", image.Image);
        Assert.Equal("0.17.0", image.Tag);

        EndpointAnnotation http = resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "http");
        Assert.Equal(5095, http.TargetPort);
        Assert.Equal(15095, http.Port);
        Assert.Equal("http", http.UriScheme);

        EndpointAnnotation grpc = resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "grpc");
        Assert.Equal(5096, grpc.TargetPort);
        Assert.Equal(15096, grpc.Port);

        Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());
    }

    [Fact]
    public void ConnectionString_UsesRestEndpointByDefault()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBServerResource> camus = builder.AddCamusDB("camus");
        IResourceBuilder<CamusDBDatabaseResource> db = camus.AddDatabase("app");

        Assert.Equal("Endpoint={camus.bindings.http.url}", camus.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("Endpoint={camus.bindings.http.url};Database=app", db.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void ConnectionString_UsesGrpcEndpointWithGrpcProtocol()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBDatabaseResource> db = builder.AddCamusDB("camus")
            .WithProtocol(CamusDBProtocol.Grpc)
            .AddDatabase("app");

        Assert.Equal(
            "Endpoint={camus.bindings.grpc.url};Protocol=grpc;BackupEndpoint={camus.bindings.http.url};Database=app",
            db.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void AddDatabase_UsesDatabaseNameAndRegistersHealthCheck()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBServerResource> camus = builder.AddCamusDB("camus");
        IResourceBuilder<CamusDBDatabaseResource> db = camus.AddDatabase("app", databaseName: "app_main");

        Assert.Equal("app_main", db.Resource.DatabaseName);
        Assert.Same(camus.Resource, db.Resource.Parent);
        Assert.Equal("app_main", camus.Resource.Databases["app"]);

        HealthCheckAnnotation healthCheck = Assert.Single(db.Resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal("app_check", healthCheck.Key);
    }

    [Fact]
    public void WithDataVolume_MountsDataFolder()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBServerResource> camus = builder.AddCamusDB("camus").WithDataVolume("camus-data");

        ContainerMountAnnotation mount = Assert.Single(camus.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("camus-data", mount.Source);
        Assert.Equal("/data", mount.Target);
        Assert.Equal(ContainerMountType.Volume, mount.Type);
    }

    [Fact]
    public async Task WithAuthentication_SetsEnvironmentEntrypointAndCredentials()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();
        builder.Configuration["Parameters:camus-user"] = "root";
        builder.Configuration["Parameters:camus-pass"] = "p;ss";

        IResourceBuilder<ParameterResource> user = builder.AddParameter("camus-user");
        IResourceBuilder<ParameterResource> pass = builder.AddParameter("camus-pass", secret: true);

        IResourceBuilder<CamusDBServerResource> camus = builder.AddCamusDB("camus").WithAuthentication(user, pass);
        IResourceBuilder<CamusDBDatabaseResource> db = camus.AddDatabase("app");

        Assert.True(camus.Resource.AuthenticationEnabled);
        Assert.Equal("sh", camus.Resource.Entrypoint);
        Assert.Equal(
            "Endpoint={camus.bindings.http.url};Database=app;User={camus-user.value};Password='{camus-pass.value}';AllowInsecureCredentials=true",
            db.Resource.ConnectionStringExpression.ValueExpression);

        Dictionary<string, string?> environment = await ResourceTestHelpers.GetEnvironmentAsync(camus.Resource);
        Assert.Equal("true", environment["CAMUSDB_AUTH_ENABLED"]);
        Assert.Equal("root", environment["CAMUSDB_BOOTSTRAP_USER"]);
        Assert.Equal("p;ss", environment["CAMUSDB_BOOTSTRAP_PASSWORD"]);
        Assert.True(environment["CAMUSDB_AUTH_TOKEN_KEY"]!.Length >= 32);

        IReadOnlyList<object> args = await ResourceTestHelpers.GetArgsAsync(camus.Resource);
        Assert.Equal("-c", args[0]);
        Assert.Contains("--require-tls-when-auth-enabled=false", (string)args[1]);
    }

    [Fact]
    public async Task WithAuthentication_GeneratesPasswordAndUsesDefaultUser()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBServerResource> camus = builder.AddCamusDB("camus").WithAuthentication();

        Assert.Equal("camus-password", camus.Resource.PasswordParameter!.Name);
        Assert.Equal("admin", await camus.Resource.UserNameReference.GetValueAsync(CancellationToken.None));
        Assert.False(string.IsNullOrEmpty(await camus.Resource.PasswordParameter.GetValueAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task ConnectionString_ParsesWithCamusDBClient()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();
        builder.Configuration["Parameters:camus-pass"] = "pa;ss";

        IResourceBuilder<ParameterResource> pass = builder.AddParameter("camus-pass", secret: true);
        IResourceBuilder<CamusDBServerResource> camus = builder.AddCamusDB("camus").WithAuthentication(password: pass);
        IResourceBuilder<CamusDBDatabaseResource> db = camus.AddDatabase("app");

        ResourceTestHelpers.AllocateEndpoints(camus.Resource);

        string? connectionString = await db.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.Equal("Endpoint=http://localhost:5095;Database=app;User=admin;Password='pa;ss';AllowInsecureCredentials=true", connectionString);

        CamusConnectionStringBuilder parsed = new(connectionString!);
        Assert.Equal("http://localhost:5095", parsed.Config["Endpoint"]);
        Assert.Equal("app", parsed.Config["Database"]);
        Assert.Equal("pa;ss", parsed.Config["Password"]);
    }

    [Fact]
    public async Task ConnectionProperties_IncludeDatabase()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBServerResource> camus = builder.AddCamusDB("camus");
        IResourceBuilder<CamusDBDatabaseResource> db = camus.AddDatabase("app");

        ResourceTestHelpers.AllocateEndpoints(camus.Resource);

        Dictionary<string, string?> properties = [];
        foreach ((string key, ReferenceExpression value) in ((IResourceWithConnectionString)db.Resource).GetConnectionProperties())
            properties[key] = await value.GetValueAsync(CancellationToken.None);

        Assert.Equal("localhost", properties["Host"]);
        Assert.Equal("5095", properties["Port"]);
        Assert.Equal("5096", properties["GrpcPort"]);
        Assert.Equal("http://localhost:5095", properties["Uri"]);
        Assert.Equal("app", properties["Database"]);
        Assert.False(properties.ContainsKey("Password"));
    }
}
