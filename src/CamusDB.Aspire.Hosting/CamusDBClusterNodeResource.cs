/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents one node of a CamusDB cluster. Each node is a container that runs the
/// server in cluster mode. Other nodes reach it on the container network by its resource name.
/// </summary>
/// <remarks>
/// A node has a parent relationship with its <see cref="CamusDBClusterResource"/> for the dashboard, but
/// it is not an <see cref="IResourceWithParent"/>. The health of the cluster comes from the health of the
/// nodes, so a node must not inherit the health check of the cluster.
/// </remarks>
public class CamusDBClusterNodeResource : ContainerResource
{
    /// <summary>Initializes a new instance of the <see cref="CamusDBClusterNodeResource"/> class.</summary>
    /// <param name="name">The name of the resource. It is also the host name of the node on the container network.</param>
    /// <param name="cluster">The cluster that the node is part of.</param>
    /// <param name="nodeId">The numeric Raft node ID, from 1.</param>
    public CamusDBClusterNodeResource(string name, CamusDBClusterResource cluster, int nodeId) : base(name)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentOutOfRangeException.ThrowIfLessThan(nodeId, 1);

        Cluster = cluster;
        NodeId = nodeId;
    }

    /// <summary>Gets the cluster that the node is part of.</summary>
    public CamusDBClusterResource Cluster { get; }

    /// <summary>Gets the numeric Raft node ID. The first node has the ID 1.</summary>
    public int NodeId { get; }

    /// <summary>Gets the endpoint of the JSON/REST API.</summary>
    public EndpointReference HttpEndpoint => field ??= new(this, CamusDBServerResource.HttpEndpointName);

    /// <summary>Gets the endpoint of the gRPC API.</summary>
    public EndpointReference GrpcEndpoint => field ??= new(this, CamusDBServerResource.GrpcEndpointName);

    /// <summary>Gets the Raft address of the node (<c>host:port</c>), as the other nodes dial it.</summary>
    public string RaftAddress => $"{Name}:{CamusDBClusterResource.RaftPort}";

    /// <summary>Gets the HTTP address of the node (<c>host:port</c>), as the other nodes dial it.</summary>
    public string HttpPeerAddress => $"{Name}:{CamusDBServerResource.DefaultHttpPort}";
}
