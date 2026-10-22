/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using CamusDB.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CamusDB.Aspire.Client;

/// <summary>Pings a CamusDB server with the connection string of the integration.</summary>
internal sealed class CamusDBHealthCheck(CamusConnectionStringBuilder connectionStringBuilder) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using CamusConnection connection = new(connectionStringBuilder);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using CamusPingCommand ping = connection.CreatePingCommand();
            int alive = await ping.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return alive == 1
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, "The CamusDB server did not answer the ping.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
