using Microsoft.Extensions.Configuration;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<CamusDBServerResource> camus = builder.AddCamusDB("camus");

// The playground keeps its data in a volume unless a run asks for a clean server.
if (!builder.Configuration.GetValue<bool>("Playground:Ephemeral"))
    camus.WithDataVolume();

if (builder.Configuration.GetValue<bool>("Playground:Authentication"))
    camus.WithAuthentication();

if (builder.Configuration.GetValue<bool>("Playground:Grpc"))
    camus.WithProtocol(CamusDBProtocol.Grpc);

IResourceBuilder<CamusDBDatabaseResource> appDb = camus.AddDatabase("appdb");

builder.AddProject<Projects.CamusDB_ApiService>("api")
    .WithReference(appDb)
    .WaitFor(appDb)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
