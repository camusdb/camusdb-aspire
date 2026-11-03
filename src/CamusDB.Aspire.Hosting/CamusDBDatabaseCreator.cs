/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Aspire.Hosting.ApplicationModel;
using CamusDB.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Creates the databases of a CamusDB server or cluster (<c>CREATE DATABASE IF NOT EXISTS</c>) when it
/// becomes ready, and registers the health checks that report the result.
/// </summary>
internal static class CamusDBDatabaseCreator
{
    private const string AuthenticationFailed = "CADB0516";

    /// <summary>Registers the health check of a database and returns its key.</summary>
    public static string AddHealthCheck(IDistributedApplicationBuilder builder, string resourceName, CamusDBDatabaseState state)
    {
        string healthCheckKey = $"{resourceName}_check";
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckKey,
            _ => new CamusDBDatabaseHealthCheck(state),
            failureStatus: default,
            tags: default));

        return healthCheckKey;
    }

    /// <summary>Creates each database with the connection string of its server, which has no <c>Database</c> key.</summary>
    public static async Task CreateAsync(
        ReferenceExpression serverConnectionString,
        IReadOnlyList<(IResource Resource, CamusDBDatabaseState State)> databases,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (databases.Count == 0)
            return;

        ResourceLoggerService loggers = services.GetRequiredService<ResourceLoggerService>();
        string? connectionString = await serverConnectionString.GetValueAsync(cancellationToken).ConfigureAwait(false);

        foreach ((IResource resource, CamusDBDatabaseState state) in databases)
        {
            ILogger logger = loggers.GetLogger(resource);

            try
            {
                CamusConnectionStringBuilder connectionStringBuilder = new($"{connectionString};Database={state.DatabaseName}");
                await CreateDatabaseAsync(connectionStringBuilder, cancellationToken).ConfigureAwait(false);

                state.CreationError = null;
                state.Created = true;
                logger.LogInformation("Database '{DatabaseName}' is ready.", state.DatabaseName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.CreationError = ex;
                logger.LogError(ex, "Failed to create database '{DatabaseName}'.", state.DatabaseName);
            }
        }
    }

    // /health answers before the server elects its Raft leaders and seeds the bootstrap superuser, so with
    // authentication on the first login can fail with CADB0516. The retries back off because the server
    // allows 20 logins per account per minute.
    private static async Task CreateDatabaseAsync(CamusConnectionStringBuilder connectionStringBuilder, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        TimeSpan delay = TimeSpan.FromMilliseconds(500);

        while (true)
        {
            try
            {
                await using CamusConnection connection = new(connectionStringBuilder);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await connection.CreateDatabaseAsync(ifNotExists: true, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (CamusException ex) when (ex.Code == AuthenticationFailed && DateTime.UtcNow + delay < deadline)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(8).Ticks));
            }
        }
    }
}
