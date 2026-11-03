/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Publishes the state of a CamusDB cluster and of its databases from the states of the node containers.
/// </summary>
/// <remarks>
/// <para>
/// Aspire sets the state of a container, and it copies that state to the children of the container. The
/// cluster is not a container, so nothing else sets its state. Without a state, the health checks of the
/// cluster never run, the cluster never becomes ready, and <c>WaitFor</c> on the cluster or on one of its
/// databases waits forever.
/// </para>
/// <para>
/// The cluster is <c>Starting</c> until all nodes are running for the first time. After that, it stays
/// <c>Running</c> while at least one node runs, because the health check reports the nodes that are down.
/// When all nodes stop, the cluster is <c>Exited</c>, or <c>FailedToStart</c> if a node failed to start.
/// </para>
/// </remarks>
internal static class CamusDBClusterStateTracker
{
    public static Task StartAsync(CamusDBClusterResource cluster, InitializeResourceEvent @event, CancellationToken cancellationToken)
    {
        IHostApplicationLifetime lifetime = @event.Services.GetRequiredService<IHostApplicationLifetime>();

        // The initialize event must not block, so the watch runs in the background until the AppHost stops.
        _ = Task.Run(() => WatchAsync(cluster, @event, lifetime.ApplicationStopping), CancellationToken.None);
        return Task.CompletedTask;
    }

    private static async Task WatchAsync(CamusDBClusterResource cluster, InitializeResourceEvent @event, CancellationToken cancellationToken)
    {
        ResourceNotificationService notifications = @event.Notifications;
        Dictionary<CamusDBClusterNodeResource, string?> nodeStates = cluster.Nodes.ToDictionary(node => node, _ => (string?)null);

        string? publishedState = null;
        bool reachedRunning = false;

        try
        {
            await foreach (ResourceEvent resourceEvent in notifications.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                if (resourceEvent.Resource is not CamusDBClusterNodeResource node || node.Cluster != cluster)
                    continue;

                nodeStates[node] = resourceEvent.Snapshot.State?.Text;
                cluster.NodeStatus[node] = (resourceEvent.Snapshot.State?.Text, resourceEvent.Snapshot.HealthStatus);

                string state = GetClusterState(nodeStates.Values, ref reachedRunning);
                if (state == publishedState)
                    continue;

                bool becameRunning = state == KnownResourceStates.Running && publishedState != KnownResourceStates.Running;
                publishedState = state;

                if (KnownResourceStates.TerminalStates.Contains(state))
                    cluster.StartCompleted = false;

                await PublishStateAsync(cluster, notifications, state).ConfigureAwait(false);

                if (becameRunning)
                    await PublishConnectionStringsAsync(cluster, @event, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            @event.Logger.LogError(ex, "Stopped tracking the state of CamusDB cluster '{ClusterName}'.", cluster.Name);
        }
    }

    internal static string GetClusterState(IEnumerable<string?> nodeStates, ref bool reachedRunning)
    {
        List<string?> states = [.. nodeStates];

        if (states.All(state => state is not null && KnownResourceStates.TerminalStates.Contains(state)))
        {
            reachedRunning = false;
            return states.Contains(KnownResourceStates.FailedToStart) ? KnownResourceStates.FailedToStart : KnownResourceStates.Exited;
        }

        if (states.All(state => state == KnownResourceStates.Running))
            reachedRunning = true;

        if (reachedRunning)
            return KnownResourceStates.Running;

        if (states.Contains(KnownResourceStates.FailedToStart))
            return KnownResourceStates.FailedToStart;

        if (states.All(state => state is null || state == KnownResourceStates.NotStarted))
            return KnownResourceStates.NotStarted;

        return KnownResourceStates.Starting;
    }

    private static async Task PublishStateAsync(CamusDBClusterResource cluster, ResourceNotificationService notifications, string state)
    {
        DateTime? startTimeStamp = state == KnownResourceStates.Running ? DateTime.UtcNow : null;
        DateTime? stopTimeStamp = KnownResourceStates.TerminalStates.Contains(state) ? DateTime.UtcNow : null;

        CustomResourceSnapshot Update(CustomResourceSnapshot snapshot) => snapshot with
        {
            State = state,
            StartTimeStamp = startTimeStamp ?? snapshot.StartTimeStamp,
            StopTimeStamp = stopTimeStamp,
        };

        await notifications.PublishUpdateAsync(cluster, Update).ConfigureAwait(false);

        foreach (CamusDBClusterDatabaseResource database in cluster.DatabaseResources)
            await notifications.PublishUpdateAsync(database, Update).ConfigureAwait(false);
    }

    // The orchestrator shows a connection string in the dashboard when this event fires. It fires the
    // event for a container and its children only, so the cluster fires it for itself and its databases.
    private static async Task PublishConnectionStringsAsync(CamusDBClusterResource cluster, InitializeResourceEvent @event, CancellationToken cancellationToken)
    {
        await @event.Eventing.PublishAsync(new ConnectionStringAvailableEvent(cluster, @event.Services), cancellationToken).ConfigureAwait(false);

        foreach (CamusDBClusterDatabaseResource database in cluster.DatabaseResources)
            await @event.Eventing.PublishAsync(new ConnectionStringAvailableEvent(database, @event.Services), cancellationToken).ConfigureAwait(false);
    }
}
