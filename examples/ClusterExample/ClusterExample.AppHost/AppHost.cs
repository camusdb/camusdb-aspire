IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// Three CamusDB nodes in cluster mode: camus-1, camus-2 and camus-3. Each node keeps its data in its own
// volume, so the notes stay when you run the example again.
IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus", nodes: 3)
    .WithDataVolume();

IResourceBuilder<CamusDBClusterDatabaseResource> notesDb = camus.AddDatabase("notesdb");

builder.AddProject<Projects.ClusterExample_Api>("api")
    .WithReference(notesDb)
    .WaitFor(notesDb)
    .WithHttpHealthCheck("/health")
    .WithUrlForEndpoint("http", url => url.DisplayText = "Notes");

builder.Build().Run();
