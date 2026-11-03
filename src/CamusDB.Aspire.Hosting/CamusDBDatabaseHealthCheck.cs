/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting;

/// <summary>
/// Reports a CamusDB database as healthy after the AppHost creates it. A resource that waits for the
/// database thus starts only when the database exists.
/// </summary>
internal sealed class CamusDBDatabaseHealthCheck(CamusDBDatabaseState database) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (database.Created)
            return Task.FromResult(HealthCheckResult.Healthy());

        if (database.CreationError is { } error)
            return Task.FromResult(new HealthCheckResult(
                context.Registration.FailureStatus,
                $"Failed to create database '{database.DatabaseName}'.",
                error));

        return Task.FromResult(new HealthCheckResult(
            context.Registration.FailureStatus,
            $"Database '{database.DatabaseName}' is not created yet."));
    }
}
