/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace CamusDB.Aspire.EntityFrameworkCore;

/// <summary>
/// The settings of the CamusDB Entity Framework Core integration. They bind from the
/// <c>Aspire:CamusDB:EntityFrameworkCore</c> configuration section.
/// </summary>
public sealed class CamusDBEntityFrameworkCoreSettings
{
    /// <summary>
    /// Gets or sets the connection string, for example <c>Endpoint=http://localhost:5095;Database=app</c>.
    /// A value in <c>ConnectionStrings:{connectionName}</c> takes precedence.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets a value that turns off the retry of transient failures. When it is <see langword="false"/>,
    /// the integration calls <c>EnableRetryOnFailure()</c> with the defaults of the provider.
    /// </summary>
    public bool DisableRetry { get; set; }

    /// <summary>Gets or sets a value that turns off the <see cref="Microsoft.EntityFrameworkCore.DbContext"/> health check.</summary>
    public bool DisableHealthChecks { get; set; }

    /// <summary>Gets or sets a value that turns off the OpenTelemetry spans of the driver.</summary>
    public bool DisableTracing { get; set; }

    /// <summary>Gets or sets a value that turns off the OpenTelemetry metrics of the driver and of Entity Framework Core.</summary>
    public bool DisableMetrics { get; set; }
}
