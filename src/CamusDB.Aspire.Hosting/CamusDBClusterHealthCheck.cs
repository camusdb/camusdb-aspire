/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting;

/// <summary>
/// Reports the health of a CamusDB cluster from the health of its nodes. Each node checks
/// <c>/v1/cluster/health</c>, which answers 200 only when the node can serve requests.
/// </summary>
/// <remarks>
/// <para>
/// Before the first start completes, the cluster is healthy only when all its nodes are healthy. A
/// resource that waits for the cluster thus starts only after every node is ready, and the AppHost creates
/// the databases on a complete cluster.
/// </para>
/// <para>
/// After that, the cluster stays healthy while a majority of the nodes is healthy, because a majority can
/// still commit. The description gives the number of healthy nodes. Aspire waits for a healthy state only,
/// so a degraded state would stop the restart of every resource that waits for the cluster while one node
/// is down. Without a majority, the cluster is unhealthy. When all nodes stop, the rule of the first start
/// applies again.
/// </para>
/// </remarks>
internal sealed class CamusDBClusterHealthCheck(CamusDBClusterResource cluster) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        int total = cluster.Nodes.Count;
        int healthy = cluster.Nodes.Count(IsHealthy);

        if (healthy == total)
        {
            cluster.StartCompleted = true;
            return Task.FromResult(HealthCheckResult.Healthy($"All {total} nodes are healthy."));
        }

        string description = $"{healthy} of {total} nodes are healthy.";

        if (cluster.StartCompleted && healthy >= (total / 2) + 1)
            return Task.FromResult(HealthCheckResult.Healthy($"{description} A majority can commit."));

        return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, description));
    }

    private bool IsHealthy(CamusDBClusterNodeResource node) =>
        cluster.NodeStatus.TryGetValue(node, out (string? State, HealthStatus? Health) status)
        && status.State == KnownResourceStates.Running
        && status.Health == HealthStatus.Healthy;
}
