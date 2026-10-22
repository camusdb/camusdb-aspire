/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace CamusDB.Aspire.Client;

/// <summary>
/// The settings of the CamusDB client integration. They bind from the <c>Aspire:CamusDB:Client</c>
/// configuration section.
/// </summary>
public sealed class CamusDBClientSettings
{
    /// <summary>
    /// Gets or sets the connection string, for example <c>Endpoint=http://localhost:5095;Database=app</c>.
    /// A value in <c>ConnectionStrings:{connectionName}</c> takes precedence.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Gets or sets a value that turns off the CamusDB health check.</summary>
    public bool DisableHealthChecks { get; set; }

    /// <summary>Gets or sets a value that turns off the OpenTelemetry spans of the driver.</summary>
    public bool DisableTracing { get; set; }

    /// <summary>Gets or sets a value that turns off the OpenTelemetry metrics of the driver.</summary>
    public bool DisableMetrics { get; set; }
}
