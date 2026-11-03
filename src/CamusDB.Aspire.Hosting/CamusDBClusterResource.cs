/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Collections.Concurrent;
using Aspire.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a CamusDB cluster: a group of <see cref="CamusDBClusterNodeResource"/>
/// containers that replicate each write through Raft and act as one database. The resource has no process
/// of its own. Its state and its health come from its nodes.
/// </summary>
/// <param name="name">The name of the resource. It is also the name of the connection string.</param>
public class CamusDBClusterResource(string name) : Resource(name), IResourceWithConnectionString
{
    internal const int RaftPort = 7070;
    internal const string FilesFolder = "/camusdb";
    internal const string CertificatesFolderName = "certs";
    internal const string NodeCertificateFileName = "node.pfx";
    internal const string CaFileName = "ca.pem";
    internal const string ConfigFileName = "config.yml";

    private readonly List<CamusDBClusterNodeResource> nodes = [];
    private readonly Dictionary<string, string> databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CamusDBClusterDatabaseResource> databaseResources = [];
    private readonly object certificatesLock = new();
    private CamusDBClusterCertificates? certificates;

    // The last state and health that Aspire published for each node. The state tracker writes it and the
    // cluster health check reads it.
    internal ConcurrentDictionary<CamusDBClusterNodeResource, (string? State, HealthStatus? Health)> NodeStatus { get; } = new();

    // True after all nodes were healthy at the same time. The cluster health check sets it, and the state
    // tracker clears it when all nodes stop.
    private volatile bool startCompleted;

    internal bool StartCompleted
    {
        get => startCompleted;
        set => startCompleted = value;
    }

    /// <summary>Gets the nodes of the cluster, in node ID order.</summary>
    public IReadOnlyList<CamusDBClusterNodeResource> Nodes => nodes;

    /// <summary>
    /// Gets the wire protocol that the connection string selects. Change it with
    /// <see cref="CamusDBClusterBuilderExtensions.WithProtocol"/>.
    /// </summary>
    public CamusDBProtocol Protocol { get; internal set; } = CamusDBProtocol.Rest;

    /// <summary>
    /// Gets a value that is <see langword="true"/> when the cluster enforces authentication. Turn it on with
    /// <see cref="CamusDBClusterBuilderExtensions.WithAuthentication"/>.
    /// </summary>
    public bool AuthenticationEnabled => PasswordParameter is not null;

    /// <summary>Gets the parameter that holds the name of the bootstrap superuser, or <see langword="null"/> for the default name.</summary>
    public ParameterResource? UserNameParameter { get; internal set; }

    /// <summary>Gets the parameter that holds the password of the bootstrap superuser. It is <see langword="null"/> when authentication is off.</summary>
    public ParameterResource? PasswordParameter { get; internal set; }

    /// <summary>Gets the parameter that holds the key the nodes use to hash access tokens. It is <see langword="null"/> when authentication is off.</summary>
    public ParameterResource? TokenKeyParameter { get; internal set; }

    /// <summary>Gets the parameter that holds the secret the nodes use to authenticate forwarded requests. It is <see langword="null"/> when authentication is off.</summary>
    public ParameterResource? NodeSecretParameter { get; internal set; }

    /// <summary>Gets an expression for the name of the bootstrap superuser.</summary>
    public ReferenceExpression UserNameReference =>
        UserNameParameter is not null
            ? ReferenceExpression.Create($"{UserNameParameter}")
            : ReferenceExpression.Create($"{CamusDBServerResource.DefaultUserName}");

    /// <summary>Gets the databases of this cluster, as a map from resource name to database name.</summary>
    public IReadOnlyDictionary<string, string> Databases => databases;

    internal IReadOnlyList<CamusDBClusterDatabaseResource> DatabaseResources => databaseResources;

    /// <summary>
    /// Gets the connection string expression for the cluster. <c>Endpoint</c> lists every node, so the
    /// client spreads its requests across the nodes and skips a node that does not answer. It has no
    /// <c>Database</c> key, so reference a database resource to get one.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => BuildConnectionString(databaseName: null);

    internal void AddNode(CamusDBClusterNodeResource node) => nodes.Add(node);

    internal void AddDatabase(CamusDBClusterDatabaseResource database)
    {
        databases.TryAdd(database.Name, database.DatabaseName);
        databaseResources.Add(database);
    }

    /// <summary>
    /// Gets the certificates of the cluster for this AppHost run. They are made on the first call, and the
    /// <paramref name="registerCleanup"/> callback receives them once, so the caller can delete them when
    /// the AppHost stops. Every node must get its certificate from the same CA, also when one node restarts.
    /// </summary>
    internal CamusDBClusterCertificates GetCertificates(Action<CamusDBClusterCertificates> registerCleanup)
    {
        lock (certificatesLock)
        {
            if (certificates is null)
            {
                certificates = CamusDBClusterCertificates.Create(Name, [.. nodes.Select(node => node.Name)]);
                registerCleanup(certificates);
            }

            return certificates;
        }
    }

    // The keys follow CamusDB.Client's CamusConnectionStringBuilder. The client reads a comma-separated
    // Endpoint value as a list, and it rotates through it. BackupEndpoint names one node because the backup
    // API is a node-level REST API.
    internal ReferenceExpression BuildConnectionString(string? databaseName)
    {
        ReferenceExpressionBuilder builder = new();

        if (Protocol == CamusDBProtocol.Grpc)
        {
            builder.AppendLiteral("Endpoint=");
            AppendEndpointList(builder, grpc: true);
            builder.Append($";Protocol=grpc;BackupEndpoint={nodes[0].HttpEndpoint.Property(EndpointProperty.Url)}");
        }
        else
        {
            builder.AppendLiteral("Endpoint=");
            AppendEndpointList(builder, grpc: false);
        }

        if (databaseName is not null)
            builder.AppendLiteral($";Database={databaseName}");

        if (PasswordParameter is not null)
            builder.Append($";User={UserNameReference};Password='{PasswordParameter}';AllowInsecureCredentials=true");

        return builder.Build();
    }

    private ReferenceExpression BuildEndpointList(bool grpc)
    {
        ReferenceExpressionBuilder builder = new();
        AppendEndpointList(builder, grpc);
        return builder.Build();
    }

    private void AppendEndpointList(ReferenceExpressionBuilder builder, bool grpc)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (i > 0)
                builder.AppendLiteral(",");

            EndpointReference endpoint = grpc ? nodes[i].GrpcEndpoint : nodes[i].HttpEndpoint;
            builder.Append($"{endpoint.Property(EndpointProperty.Url)}");
        }
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Uri", BuildEndpointList(grpc: false));
        yield return new("GrpcUri", BuildEndpointList(grpc: true));

        if (PasswordParameter is not null)
        {
            yield return new("Username", UserNameReference);
            yield return new("Password", ReferenceExpression.Create($"{PasswordParameter}"));
        }
    }
}
