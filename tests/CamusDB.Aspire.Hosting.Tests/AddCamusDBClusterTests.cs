using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CamusDB.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CamusDB.Aspire.Hosting.Tests;

public sealed class AddCamusDBClusterTests
{
    [Fact]
    public void AddCamusDBCluster_AddsNodeContainersWithImageEndpointsAndParent()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus");

        CamusDBClusterResource cluster = Assert.Single(builder.Resources.OfType<CamusDBClusterResource>());
        Assert.Same(camus.Resource, cluster);
        Assert.Equal(["camus-1", "camus-2", "camus-3"], cluster.Nodes.Select(node => node.Name));
        Assert.Equal([1, 2, 3], cluster.Nodes.Select(node => node.NodeId));
        Assert.Equal(3, builder.Resources.OfType<CamusDBClusterNodeResource>().Count());

        HealthCheckAnnotation clusterHealthCheck = Assert.Single(cluster.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal("camus_check", clusterHealthCheck.Key);

        foreach (CamusDBClusterNodeResource node in cluster.Nodes)
        {
            Assert.Same(cluster, node.Cluster);

            ContainerImageAnnotation image = Assert.Single(node.Annotations.OfType<ContainerImageAnnotation>());
            Assert.Equal("docker.io", image.Registry);
            Assert.Equal("camusdb/camusdb", image.Image);
            Assert.Equal("0.17.0", image.Tag);
            Assert.Equal("sh", node.Entrypoint);

            EndpointAnnotation http = node.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "http");
            Assert.Equal(5095, http.TargetPort);
            EndpointAnnotation grpc = node.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "grpc");
            Assert.Equal(5096, grpc.TargetPort);

            Assert.Single(node.Annotations.OfType<HealthCheckAnnotation>());

            // The node is shown under the cluster, but it must not inherit the health check of the cluster.
            ResourceRelationshipAnnotation parent = Assert.Single(node.Annotations.OfType<ResourceRelationshipAnnotation>(), r => r.Type == "Parent");
            Assert.Same(cluster, parent.Resource);
            Assert.IsNotAssignableFrom<IResourceWithParent>(node);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddCamusDBCluster_RefusesLessThanOneNode(int nodes)
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddCamusDBCluster("camus", nodes));
    }

    [Fact]
    public void ConnectionString_ListsTheRestEndpointOfEveryNode()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus");
        IResourceBuilder<CamusDBClusterDatabaseResource> db = camus.AddDatabase("app");

        Assert.Equal(
            "Endpoint={camus-1.bindings.http.url},{camus-2.bindings.http.url},{camus-3.bindings.http.url}",
            camus.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal(
            "Endpoint={camus-1.bindings.http.url},{camus-2.bindings.http.url},{camus-3.bindings.http.url};Database=app",
            db.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void ConnectionString_ListsTheGrpcEndpointOfEveryNodeWithGrpcProtocol()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBClusterDatabaseResource> db = builder.AddCamusDBCluster("camus")
            .WithProtocol(CamusDBProtocol.Grpc)
            .AddDatabase("app");

        Assert.Equal(
            "Endpoint={camus-1.bindings.grpc.url},{camus-2.bindings.grpc.url},{camus-3.bindings.grpc.url};" +
            "Protocol=grpc;BackupEndpoint={camus-1.bindings.http.url};Database=app",
            db.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task ConnectionString_ParsesWithCamusDBClientAsEndpointList()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();
        builder.Configuration["Parameters:camus-pass"] = "pa;ss";

        IResourceBuilder<ParameterResource> pass = builder.AddParameter("camus-pass", secret: true);
        IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus").WithAuthentication(password: pass);
        IResourceBuilder<CamusDBClusterDatabaseResource> db = camus.AddDatabase("app");

        int port = 15095;
        foreach (CamusDBClusterNodeResource node in camus.Resource.Nodes)
        {
            foreach (EndpointAnnotation endpoint in node.Annotations.OfType<EndpointAnnotation>())
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", port++);
        }

        string? connectionString = await db.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        Assert.Equal(
            "Endpoint=http://localhost:15095,http://localhost:15097,http://localhost:15099;Database=app;" +
            "User=admin;Password='pa;ss';AllowInsecureCredentials=true",
            connectionString);

        CamusConnectionStringBuilder parsed = new(connectionString!);
        Assert.Equal("http://localhost:15095,http://localhost:15097,http://localhost:15099", parsed.Config["Endpoint"]);
        Assert.Equal("app", parsed.Config["Database"]);
        Assert.Equal("pa;ss", parsed.Config["Password"]);
    }

    [Fact]
    public async Task ConnectionProperties_ListEveryNodeAndIncludeDatabase()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus", nodes: 2);
        IResourceBuilder<CamusDBClusterDatabaseResource> db = camus.AddDatabase("app");

        foreach (CamusDBClusterNodeResource node in camus.Resource.Nodes)
            ResourceTestHelpers.AllocateEndpoints(node);

        Dictionary<string, string?> properties = [];
        foreach ((string key, ReferenceExpression value) in ((IResourceWithConnectionString)db.Resource).GetConnectionProperties())
            properties[key] = await value.GetValueAsync(CancellationToken.None);

        Assert.Equal("http://localhost:5095,http://localhost:5095", properties["Uri"]);
        Assert.Equal("http://localhost:5096,http://localhost:5096", properties["GrpcUri"]);
        Assert.Equal("app", properties["Database"]);
        Assert.False(properties.ContainsKey("Password"));
    }

    [Fact]
    public void AddDatabase_UsesDatabaseNameAndRegistersHealthCheck()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus");
        IResourceBuilder<CamusDBClusterDatabaseResource> db = camus.AddDatabase("app", databaseName: "app_main");

        Assert.Equal("app_main", db.Resource.DatabaseName);
        Assert.Same(camus.Resource, db.Resource.Parent);
        Assert.Equal("app_main", camus.Resource.Databases["app"]);

        HealthCheckAnnotation healthCheck = Assert.Single(db.Resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal("app_check", healthCheck.Key);
    }

    [Fact]
    public async Task NodeCommand_ListsTheOtherNodesAsPeers()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus");

        IReadOnlyList<object> args = await ResourceTestHelpers.GetArgsAsync(camus.Resource.Nodes[1]);
        Assert.Equal("-c", args[0]);
        Assert.Equal(
            "exec dotnet CamusDB.dll --mode=cluster --raft-nodename=camus-2 --raft-nodeid=2 --raft-host=camus-2 " +
            "--raft-port=7070 --raft-certificate=/camusdb/certs/node.pfx --config=/camusdb/config.yml " +
            "--http-port=5095 --data-dir=/data " +
            "--initial-cluster-partitions=${CAMUS_INITIAL_PARTITIONS:-4} --memory-profile=${CAMUS_MEMORY_PROFILE:-dev} " +
            "--initial-cluster camus-1:7070 camus-3:7070 --http-peers camus-1:5095 camus-3:5095",
            (string)args[1]);

        Dictionary<string, string?> environment = await ResourceTestHelpers.GetEnvironmentAsync(camus.Resource.Nodes[1]);
        Assert.Equal("/camusdb/certs/ca.pem", environment["SSL_CERT_FILE"]);
        Assert.False(environment.ContainsKey("CAMUSDB_AUTH_ENABLED"));

        // The client gRPC port stays plaintext, and the Raft port asks for no client certificate.
        Assert.Equal("grpc_tls_enabled: false\n", CamusDBClusterBuilderExtensions.BuildNodeConfig(camus.Resource));
    }

    [Fact]
    public async Task NodeCommand_HasNoPeersInAOneNodeCluster()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus", nodes: 1);

        IReadOnlyList<object> args = await ResourceTestHelpers.GetArgsAsync(Assert.Single(camus.Resource.Nodes));
        Assert.Contains("--mode=cluster", (string)args[1]);
        Assert.DoesNotContain("--initial-cluster ", (string)args[1]);
        Assert.DoesNotContain("--http-peers", (string)args[1]);
    }

    [Fact]
    public async Task WithAuthentication_SharesSecretsAcrossNodesAndTurnsOnMutualTls()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();
        builder.Configuration["Parameters:camus-user"] = "root";
        builder.Configuration["Parameters:camus-pass"] = "p;ss";

        IResourceBuilder<ParameterResource> user = builder.AddParameter("camus-user");
        IResourceBuilder<ParameterResource> pass = builder.AddParameter("camus-pass", secret: true);

        IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus").WithAuthentication(user, pass);
        IResourceBuilder<CamusDBClusterDatabaseResource> db = camus.AddDatabase("app");

        Assert.True(camus.Resource.AuthenticationEnabled);
        Assert.Equal("camus-token-key", camus.Resource.TokenKeyParameter!.Name);
        Assert.Equal("camus-node-secret", camus.Resource.NodeSecretParameter!.Name);
        Assert.Equal(
            "Endpoint={camus-1.bindings.http.url},{camus-2.bindings.http.url},{camus-3.bindings.http.url};Database=app;" +
            "User={camus-user.value};Password='{camus-pass.value}';AllowInsecureCredentials=true",
            db.Resource.ConnectionStringExpression.ValueExpression);

        List<Dictionary<string, string?>> environments = [];
        foreach (CamusDBClusterNodeResource node in camus.Resource.Nodes)
        {
            Dictionary<string, string?> environment = await ResourceTestHelpers.GetEnvironmentAsync(node);
            Assert.Equal("true", environment["CAMUSDB_AUTH_ENABLED"]);
            Assert.Equal("root", environment["CAMUSDB_BOOTSTRAP_USER"]);
            Assert.Equal("p;ss", environment["CAMUSDB_BOOTSTRAP_PASSWORD"]);
            Assert.True(environment["CAMUSDB_AUTH_TOKEN_KEY"]!.Length >= 32);
            Assert.True(environment["CAMUSDB_NODE_SECRET"]!.Length >= 32);
            environments.Add(environment);

            IReadOnlyList<object> args = await ResourceTestHelpers.GetArgsAsync(node);
            Assert.Contains("--config=/camusdb/config.yml", (string)args[1]);
            Assert.Contains("--require-tls-when-auth-enabled=false", (string)args[1]);
        }

        // Every node must accept the tokens and the forwarded requests of the other nodes.
        Assert.Single(environments.Select(e => e["CAMUSDB_AUTH_TOKEN_KEY"]).Distinct());
        Assert.Single(environments.Select(e => e["CAMUSDB_NODE_SECRET"]).Distinct());

        Assert.Equal(
            "grpc_tls_enabled: false\nraft_mutual_tls_enabled: true\nraft_peer_ca: /camusdb/certs/ca.pem\n",
            CamusDBClusterBuilderExtensions.BuildNodeConfig(camus.Resource));
    }

    [Fact]
    public void WithDataVolume_MountsOneVolumePerNode()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus").WithDataVolume("camus-data");

        Assert.Equal(
            ["camus-data-1", "camus-data-2", "camus-data-3"],
            camus.Resource.Nodes.Select(node =>
            {
                ContainerMountAnnotation mount = Assert.Single(node.Annotations.OfType<ContainerMountAnnotation>());
                Assert.Equal("/data", mount.Target);
                Assert.Equal(ContainerMountType.Volume, mount.Type);
                return mount.Source;
            }));
    }

    [Fact]
    public void WithDataVolume_GeneratesDistinctNamesWithoutAPrefix()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus").WithDataVolume();

        List<string?> names = [.. camus.Resource.Nodes.Select(node => node.Annotations.OfType<ContainerMountAnnotation>().Single().Source)];
        Assert.Equal(3, names.Distinct().Count());
    }

    [Fact]
    public void WithDataBindMount_MountsOneSubfolderPerNode()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();
        string source = Path.Combine(Path.GetTempPath(), "camus-cluster-data");

        IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus", nodes: 2).WithDataBindMount(source);

        Assert.Equal(
            [Path.Combine(source, "camus-1"), Path.Combine(source, "camus-2")],
            camus.Resource.Nodes.Select(node => node.Annotations.OfType<ContainerMountAnnotation>().Single().Source));
    }

    [Fact]
    public void ConfigureNodes_AppliesToEveryNode()
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        IResourceBuilder<CamusDBClusterResource> camus = builder.AddCamusDBCluster("camus")
            .ConfigureNodes(node => node.WithImageTag("0.16.4"));

        Assert.All(camus.Resource.Nodes, node => Assert.Equal("0.16.4", node.Annotations.OfType<ContainerImageAnnotation>().Single().Tag));
    }

    [Fact]
    public void Certificates_SignOneCertificatePerNodeWithTheClusterCa()
    {
        using CamusDBClusterCertificates certificates = CamusDBClusterCertificates.Create("camus", ["camus-1", "camus-2"]);

        X509Certificate2Collection roots = [];
        roots.ImportFromPem(certificates.CaPem);
        X509Certificate2 ca = Assert.Single(roots);

        foreach (string node in new[] { "camus-1", "camus-2" })
        {
            string path = certificates.GetNodeCertificatePath(node);
            Assert.StartsWith(certificates.Folder, path);

            // The server loads the PFX file without a password.
            using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
            Assert.True(certificate.HasPrivateKey);
            Assert.True(certificate.MatchesHostname(node));
            Assert.True(certificate.MatchesHostname("localhost"));
            Assert.False(certificate.MatchesHostname("camus-9"));

            // The same chain check as the server, for the server and the client role of a node.
            foreach (string usage in new[] { "1.3.6.1.5.5.7.3.1", "1.3.6.1.5.5.7.3.2" })
            {
                using X509Chain chain = new();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid(usage));
                Assert.True(chain.Build(certificate), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
            }
        }

        string folder = certificates.Folder;
        certificates.Dispose();
        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData(new[] { "NotStarted", "NotStarted", "NotStarted" }, false, "NotStarted", false)]
    [InlineData(new[] { "Starting", "Running", "NotStarted" }, false, "Starting", false)]
    [InlineData(new[] { "Running", "Running", "Running" }, false, "Running", true)]
    [InlineData(new[] { "Running", "Exited", "Running" }, true, "Running", true)]
    [InlineData(new[] { "Running", "Starting", "Running" }, true, "Running", true)]
    [InlineData(new[] { "Exited", "Exited", "Exited" }, true, "Exited", false)]
    [InlineData(new[] { "FailedToStart", "Exited", "Exited" }, false, "FailedToStart", false)]
    [InlineData(new[] { "FailedToStart", "Starting", "Running" }, false, "FailedToStart", false)]
    public void ClusterState_FollowsTheNodes(string[] nodeStates, bool reachedRunning, string expected, bool expectedReachedRunning)
    {
        string state = CamusDBClusterStateTracker.GetClusterState(nodeStates, ref reachedRunning);

        Assert.Equal(expected, state);
        Assert.Equal(expectedReachedRunning, reachedRunning);
    }

    [Theory]
    [InlineData(3, false, HealthStatus.Healthy)]
    [InlineData(2, false, HealthStatus.Unhealthy)]
    [InlineData(2, true, HealthStatus.Healthy)]
    [InlineData(1, true, HealthStatus.Unhealthy)]
    [InlineData(0, true, HealthStatus.Unhealthy)]
    public async Task ClusterHealth_NeedsAllNodesToStartAndAMajorityAfterThat(int healthyNodes, bool startCompleted, HealthStatus expected)
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();
        CamusDBClusterResource cluster = builder.AddCamusDBCluster("camus").Resource;
        cluster.StartCompleted = startCompleted;

        for (int i = 0; i < cluster.Nodes.Count; i++)
            cluster.NodeStatus[cluster.Nodes[i]] = i < healthyNodes
                ? (KnownResourceStates.Running, HealthStatus.Healthy)
                : (KnownResourceStates.Running, HealthStatus.Unhealthy);

        CamusDBClusterHealthCheck check = new(cluster);
        HealthCheckContext context = new()
        {
            Registration = new HealthCheckRegistration("camus_check", check, HealthStatus.Unhealthy, tags: null),
        };

        HealthCheckResult result = await check.CheckHealthAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.Contains($"{healthyNodes}", result.Description);
        Assert.Equal(startCompleted || healthyNodes == cluster.Nodes.Count, cluster.StartCompleted);
    }
}
