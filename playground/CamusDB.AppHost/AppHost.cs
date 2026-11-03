using Microsoft.Extensions.Configuration;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

bool ephemeral = builder.Configuration.GetValue<bool>("Playground:Ephemeral");
bool authentication = builder.Configuration.GetValue<bool>("Playground:Authentication");
bool grpc = builder.Configuration.GetValue<bool>("Playground:Grpc");
int clusterNodes = builder.Configuration.GetValue<int>("Playground:ClusterNodes");

IResourceBuilder<IResourceWithConnectionString> appDb;

if (clusterNodes > 0)
{
    IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus", nodes: clusterNodes);

    // The playground keeps its data in volumes unless a run asks for a clean cluster.
    if (!ephemeral)
        camus.WithDataVolume();

    if (authentication)
        camus.WithAuthentication();

    if (grpc)
        camus.WithProtocol(CamusDBProtocol.Grpc);

    appDb = camus.AddDatabase("appdb");
}
else
{
    IResourceBuilder<CamusDBServerResource> camus = builder.AddCamusDB("camus");

    // The playground keeps its data in a volume unless a run asks for a clean server.
    if (!ephemeral)
        camus.WithDataVolume();

    if (authentication)
        camus.WithAuthentication();

    if (grpc)
        camus.WithProtocol(CamusDBProtocol.Grpc);

    appDb = camus.AddDatabase("appdb");
}

builder.AddProject<Projects.CamusDB_ApiService>("api")
    .WithReference(appDb)
    .WaitFor(appDb)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
