/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting;

/// <summary>Extension methods that add a CamusDB cluster to an <see cref="IDistributedApplicationBuilder"/>.</summary>
public static class CamusDBClusterBuilderExtensions
{
    private const string ClusterHealthPath = "/v1/cluster/health";

    /// <summary>
    /// Adds a CamusDB cluster to the application model. The cluster runs <paramref name="nodes"/> containers
    /// of the <c>camusdb/camusdb</c> image in cluster mode, with authentication off. The nodes are named
    /// <c>{name}-1</c>, <c>{name}-2</c>, and so on, and they reach each other by these names on the
    /// container network.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The nodes connect to each other over TLS on the Raft port. The AppHost makes a certificate authority
    /// and one certificate for each node on each run, and it copies them into the containers.
    /// </para>
    /// <para>
    /// The connection string lists the endpoints of all nodes. The client spreads its requests across them
    /// and skips a node that does not answer.
    /// </para>
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the cluster resource. It is also the name of the connection string.</param>
    /// <param name="nodes">
    /// The number of nodes. Use an odd number: a cluster commits while a majority of its nodes runs, so 3
    /// nodes continue after the loss of 1, and 5 nodes after the loss of 2. The default is 3.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBClusterResource> AddCamusDBCluster(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int nodes = 3)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(nodes, 1);

        CamusDBClusterResource cluster = new(name);

        string healthCheckKey = $"{name}_check";
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckKey,
            _ => new CamusDBClusterHealthCheck(cluster),
            failureStatus: default,
            tags: default));

        IResourceBuilder<CamusDBClusterResource> clusterBuilder = builder.AddResource(cluster)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "CamusDBCluster",
                State = KnownResourceStates.NotStarted,
                Properties = [],
            })
            .WithIconName("DatabaseMultiple")
            .WithHealthCheck(healthCheckKey)
            .OnInitializeResource(CamusDBClusterStateTracker.StartAsync)
            .OnResourceReady(CreateDatabasesAsync);

        for (int nodeId = 1; nodeId <= nodes; nodeId++)
        {
            CamusDBClusterNodeResource node = new($"{name}-{nodeId}", cluster, nodeId);
            cluster.AddNode(node);
            AddNode(builder, node);
        }

        return clusterBuilder;
    }

    /// <summary>
    /// Adds a database to a CamusDB cluster. The AppHost creates the database (<c>CREATE DATABASE IF NOT EXISTS</c>)
    /// when all the nodes are healthy, and the database resource becomes healthy after that.
    /// </summary>
    /// <param name="builder">The CamusDB cluster resource builder.</param>
    /// <param name="name">The name of the resource. It is also the name of the connection string.</param>
    /// <param name="databaseName">The name of the database on the cluster. When it is <see langword="null"/>, the resource name is used.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBClusterDatabaseResource> AddDatabase(
        this IResourceBuilder<CamusDBClusterResource> builder,
        [ResourceName] string name,
        string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        databaseName ??= name;

        CamusDBClusterDatabaseResource database = new(name, databaseName, builder.Resource);
        builder.Resource.AddDatabase(database);

        string healthCheckKey = CamusDBDatabaseCreator.AddHealthCheck(builder.ApplicationBuilder, name, database.State);

        return builder.ApplicationBuilder.AddResource(database)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "CamusDBDatabase",
                State = KnownResourceStates.NotStarted,
                Properties = [],
            })
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Applies a configuration to every node of the cluster, for example an image tag or an environment
    /// variable. All nodes must run the same image and the same settings.
    /// </summary>
    /// <param name="builder">The CamusDB cluster resource builder.</param>
    /// <param name="configure">The configuration to apply to the builder of each node.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBClusterResource> ConfigureNodes(
        this IResourceBuilder<CamusDBClusterResource> builder,
        Action<IResourceBuilder<CamusDBClusterNodeResource>> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        foreach (CamusDBClusterNodeResource node in builder.Resource.Nodes)
            configure(builder.ApplicationBuilder.CreateResourceBuilder(node));

        return builder;
    }

    /// <summary>
    /// Adds one named volume for the data folder of each node, so the data stays when the containers restart.
    /// </summary>
    /// <param name="builder">The CamusDB cluster resource builder.</param>
    /// <param name="name">
    /// The prefix of the volume names. Each node gets the volume <c>{name}-{node ID}</c>. When it is
    /// <see langword="null"/>, Aspire generates a name from the application and node names.
    /// </param>
    /// <param name="isReadOnly">A flag that tells if the volumes are read-only.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBClusterResource> WithDataVolume(
        this IResourceBuilder<CamusDBClusterResource> builder,
        string? name = null,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.ConfigureNodes(node => node.WithVolume(
            name is null ? VolumeNameGenerator.Generate(node, "data") : $"{name}-{node.Resource.NodeId}",
            CamusDBServerResource.DataTargetFolder,
            isReadOnly));
    }

    /// <summary>
    /// Adds a bind mount for the data folder of each node. Each node mounts the subfolder of
    /// <paramref name="source"/> that has the name of the node, for example <c>{source}/camus-1</c>.
    /// </summary>
    /// <param name="builder">The CamusDB cluster resource builder.</param>
    /// <param name="source">The host folder that holds one subfolder for each node.</param>
    /// <param name="isReadOnly">A flag that tells if the mounts are read-only.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBClusterResource> WithDataBindMount(
        this IResourceBuilder<CamusDBClusterResource> builder,
        string source,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.ConfigureNodes(node => node.WithBindMount(
            Path.Combine(source, node.Resource.Name),
            CamusDBServerResource.DataTargetFolder,
            isReadOnly));
    }

    /// <summary>
    /// Selects the wire protocol of the connection string. With <see cref="CamusDBProtocol.Grpc"/>, the
    /// connection string lists the <c>grpc</c> endpoints of the nodes and sets <c>Protocol=grpc</c>.
    /// </summary>
    /// <param name="builder">The CamusDB cluster resource builder.</param>
    /// <param name="protocol">The protocol.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBClusterResource> WithProtocol(
        this IResourceBuilder<CamusDBClusterResource> builder,
        CamusDBProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.Protocol = protocol;
        return builder;
    }

    /// <summary>
    /// Turns on authentication. The cluster creates a superuser from <paramref name="userName"/> and
    /// <paramref name="password"/> on its first start, and the connection strings carry these credentials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With authentication on, the nodes authenticate each other: the Raft port asks for a client
    /// certificate that the cluster CA signed (<c>raft_mutual_tls_enabled</c>), and forwarded requests carry
    /// a node secret. The AppHost generates the token key and the node secret, and all nodes share them.
    /// </para>
    /// <para>
    /// The server reads the bootstrap credentials only when its user catalog is empty. With data volumes, a
    /// changed password has no effect on a cluster that already has users.
    /// </para>
    /// <para>
    /// The client endpoints serve plaintext HTTP, and other containers reach them by a host name that is
    /// not loopback. Thus this method starts the nodes with <c>--require-tls-when-auth-enabled=false</c>, and
    /// the connection strings set <c>AllowInsecureCredentials=true</c>. Do not use this setup outside local
    /// development.
    /// </para>
    /// </remarks>
    /// <param name="builder">The CamusDB cluster resource builder.</param>
    /// <param name="userName">The parameter for the superuser name. When it is <see langword="null"/>, the name is <c>admin</c>.</param>
    /// <param name="password">
    /// The parameter for the superuser password. When it is <see langword="null"/>, Aspire generates a password.
    /// The password must not contain a single quote.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBClusterResource> WithAuthentication(
        this IResourceBuilder<CamusDBClusterResource> builder,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        CamusDBClusterResource cluster = builder.Resource;
        IDistributedApplicationBuilder app = builder.ApplicationBuilder;

        cluster.UserNameParameter = userName?.Resource;
        cluster.PasswordParameter = password?.Resource
            ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(app, $"{cluster.Name}-password", special: false);

        // The server refuses a token key or a node secret shorter than 32 bytes.
        cluster.TokenKeyParameter = ParameterResourceBuilderExtensions.CreateGeneratedParameter(
            app,
            $"{cluster.Name}-token-key",
            secret: true,
            new GenerateParameterDefault { MinLength = 64, Special = false });

        cluster.NodeSecretParameter = ParameterResourceBuilderExtensions.CreateGeneratedParameter(
            app,
            $"{cluster.Name}-node-secret",
            secret: true,
            new GenerateParameterDefault { MinLength = 64, Special = false });

        // The command line and the config file of each node read AuthenticationEnabled when the container
        // starts, so only the environment is set here.
        return builder.ConfigureNodes(node => node
            .WithEnvironment("CAMUSDB_AUTH_ENABLED", "true")
            .WithEnvironment("CAMUSDB_AUTH_TOKEN_KEY", cluster.TokenKeyParameter)
            .WithEnvironment("CAMUSDB_NODE_SECRET", cluster.NodeSecretParameter)
            .WithEnvironment("CAMUSDB_BOOTSTRAP_USER", cluster.UserNameReference)
            .WithEnvironment("CAMUSDB_BOOTSTRAP_PASSWORD", cluster.PasswordParameter));
    }

    private static void AddNode(IDistributedApplicationBuilder builder, CamusDBClusterNodeResource node)
    {
        CamusDBClusterResource cluster = node.Cluster;

        builder.AddResource(node)
            .WithImage(CamusDBContainerImageTags.Image, CamusDBContainerImageTags.Tag)
            .WithImageRegistry(CamusDBContainerImageTags.Registry)
            .WithHttpEndpoint(targetPort: CamusDBServerResource.DefaultHttpPort, name: CamusDBServerResource.HttpEndpointName)
            .WithHttpEndpoint(targetPort: CamusDBServerResource.DefaultGrpcPort, name: CamusDBServerResource.GrpcEndpointName)
            .WithHttpHealthCheck(ClusterHealthPath, endpointName: CamusDBServerResource.HttpEndpointName)
            .WithIconName("Database")
            .WithParentRelationship(cluster)
            // .NET on Linux reads SSL_CERT_FILE as its trust bundle, and it still reads the system
            // certificate folder. The node thus trusts the certificates of its peers without root access.
            .WithEnvironment("SSL_CERT_FILE", $"{CamusDBClusterResource.FilesFolder}/{CamusDBClusterResource.CertificatesFolderName}/{CamusDBClusterResource.CaFileName}")
            .WithContainerFiles(CamusDBClusterResource.FilesFolder, (context, _) => Task.FromResult(GetNodeFiles(node, context)))
            .WithEntrypoint("sh")
            .WithArgs(context =>
            {
                context.Args.Add("-c");
                context.Args.Add(BuildNodeCommand(node));
            });
    }

    private static IEnumerable<ContainerFileSystemItem> GetNodeFiles(CamusDBClusterNodeResource node, ContainerFileSystemCallbackContext context)
    {
        CamusDBClusterCertificates certificates = node.Cluster.GetCertificates(created =>
            context.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(created.Dispose));

        List<ContainerFileSystemItem> items =
        [
            new ContainerDirectory
            {
                Name = CamusDBClusterResource.CertificatesFolderName,
                Entries =
                [
                    new ContainerFile
                    {
                        Name = CamusDBClusterResource.NodeCertificateFileName,
                        SourcePath = certificates.GetNodeCertificatePath(node.Name),
                    },
                    new ContainerFile
                    {
                        Name = CamusDBClusterResource.CaFileName,
                        Contents = certificates.CaPem,
                    },
                ],
            },
        ];

        items.Add(new ContainerFile { Name = CamusDBClusterResource.ConfigFileName, Contents = BuildNodeConfig(node.Cluster) });

        return items;
    }

    // The server reads these keys from config.yml only. In cluster mode, the client gRPC port uses the Raft
    // certificate and serves TLS, but the clients do not trust the cluster CA. grpc_tls_enabled: false
    // keeps that port plaintext (h2c), as on a standalone server.
    //
    // With authentication on, the Raft and Kahuna services refuse a peer that shows no user token, unless
    // the peer shows a client certificate that chains to raft_peer_ca.
    internal static string BuildNodeConfig(CamusDBClusterResource cluster)
    {
        StringBuilder config = new("grpc_tls_enabled: false\n");

        if (cluster.AuthenticationEnabled)
        {
            config.Append("raft_mutual_tls_enabled: true\n");
            config.Append($"raft_peer_ca: {CamusDBClusterResource.FilesFolder}/{CamusDBClusterResource.CertificatesFolderName}/{CamusDBClusterResource.CaFileName}\n");
        }

        return config.ToString();
    }

    // --initial-cluster lists the Raft address of every other node, never the node itself, and
    // --http-peers lists their HTTP addresses in the same order. A one-node cluster gets neither flag,
    // because the server refuses a flag with no value. The ${...} defaults let a node configuration
    // change the partition count and the memory profile with environment variables.
    internal static string BuildNodeCommand(CamusDBClusterNodeResource node)
    {
        CamusDBClusterResource cluster = node.Cluster;
        string certificates = $"{CamusDBClusterResource.FilesFolder}/{CamusDBClusterResource.CertificatesFolderName}";

        StringBuilder command = new("exec dotnet CamusDB.dll --mode=cluster");
        command.Append($" --raft-nodename={node.Name}");
        command.Append($" --raft-nodeid={node.NodeId}");
        command.Append($" --raft-host={node.Name}");
        command.Append($" --raft-port={CamusDBClusterResource.RaftPort}");
        command.Append($" --raft-certificate={certificates}/{CamusDBClusterResource.NodeCertificateFileName}");
        command.Append($" --config={CamusDBClusterResource.FilesFolder}/{CamusDBClusterResource.ConfigFileName}");
        command.Append($" --http-port={CamusDBServerResource.DefaultHttpPort}");
        command.Append($" --data-dir={CamusDBServerResource.DataTargetFolder}");
        command.Append(" --initial-cluster-partitions=${CAMUS_INITIAL_PARTITIONS:-4}");
        command.Append(" --memory-profile=${CAMUS_MEMORY_PROFILE:-dev}");

        List<CamusDBClusterNodeResource> peers = [.. cluster.Nodes.Where(peer => peer != node)];
        if (peers.Count > 0)
        {
            command.Append(" --initial-cluster");
            foreach (CamusDBClusterNodeResource peer in peers)
                command.Append(' ').Append(peer.RaftAddress);

            command.Append(" --http-peers");
            foreach (CamusDBClusterNodeResource peer in peers)
                command.Append(' ').Append(peer.HttpPeerAddress);
        }

        if (cluster.AuthenticationEnabled)
            command.Append(" --require-tls-when-auth-enabled=false");

        return command.ToString();
    }

    private static Task CreateDatabasesAsync(CamusDBClusterResource cluster, ResourceReadyEvent @event, CancellationToken cancellationToken) =>
        CamusDBDatabaseCreator.CreateAsync(
            cluster.ConnectionStringExpression,
            [.. cluster.DatabaseResources.Select(database => ((IResource)database, database.State))],
            @event.Services,
            cancellationToken);
}
