/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using CamusDB.Aspire.Client;
using CamusDB.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>Extension methods that register CamusDB.Client in an <see cref="IHostApplicationBuilder"/>.</summary>
public static class AspireCamusDBExtensions
{
    private const string DefaultConfigSectionName = "Aspire:CamusDB:Client";

    /// <summary>
    /// Registers a singleton <see cref="CamusConnectionStringBuilder"/> and a transient <see cref="CamusConnection"/>,
    /// and adds a health check, tracing and metrics.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/>.</param>
    /// <param name="connectionName">The name of the connection string in the <c>ConnectionStrings</c> configuration section.</param>
    /// <param name="configureSettings">An optional delegate that changes the <see cref="CamusDBClientSettings"/>.</param>
    /// <exception cref="InvalidOperationException">The configuration has no connection string.</exception>
    public static void AddCamusDBClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<CamusDBClientSettings>? configureSettings = null)
        => AddCamusDBClient(builder, DefaultConfigSectionName, configureSettings, connectionName, serviceKey: null);

    /// <summary>
    /// Registers a keyed singleton <see cref="CamusConnectionStringBuilder"/> and a keyed transient
    /// <see cref="CamusConnection"/>, with <paramref name="name"/> as the service key. It also adds a health check,
    /// tracing and metrics. The settings also bind from <c>Aspire:CamusDB:Client:{name}</c>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/>.</param>
    /// <param name="name">The name of the connection string, and the service key.</param>
    /// <param name="configureSettings">An optional delegate that changes the <see cref="CamusDBClientSettings"/>.</param>
    /// <exception cref="InvalidOperationException">The configuration has no connection string.</exception>
    public static void AddKeyedCamusDBClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<CamusDBClientSettings>? configureSettings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddCamusDBClient(builder, $"{DefaultConfigSectionName}:{name}", configureSettings, connectionName: name, serviceKey: name);
    }

    private static void AddCamusDBClient(
        IHostApplicationBuilder builder,
        string configurationSectionName,
        Action<CamusDBClientSettings>? configureSettings,
        string connectionName,
        object? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        CamusDBClientSettings settings = new();
        IConfigurationSection configSection = builder.Configuration.GetSection(DefaultConfigSectionName);
        configSection.Bind(settings);

        // A keyed client binds the shared section first and its own section after it, so a named value wins.
        if (configurationSectionName != DefaultConfigSectionName)
            builder.Configuration.GetSection(configurationSectionName).Bind(settings);

        if (builder.Configuration.GetConnectionString(connectionName) is { } connectionString)
            settings.ConnectionString = connectionString;

        configureSettings?.Invoke(settings);

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            throw new InvalidOperationException(
                $"A CamusDB connection string is missing. Set it in 'ConnectionStrings:{connectionName}' or in the " +
                $"'ConnectionString' key of the '{configurationSectionName}' configuration section.");

        // The builder holds the endpoint pool, the endpoint health and the access token, so one instance is
        // shared. A connection is cheap and holds no socket of its own.
        string resolvedConnectionString = settings.ConnectionString;

        if (serviceKey is null)
        {
            builder.Services.AddSingleton(_ => new CamusConnectionStringBuilder(resolvedConnectionString));
            builder.Services.AddTransient(sp => new CamusConnection(sp.GetRequiredService<CamusConnectionStringBuilder>()));
        }
        else
        {
            builder.Services.AddKeyedSingleton(serviceKey, (_, _) => new CamusConnectionStringBuilder(resolvedConnectionString));
            builder.Services.AddKeyedTransient(serviceKey, (sp, key) => new CamusConnection(sp.GetRequiredKeyedService<CamusConnectionStringBuilder>(key)));
        }

        if (!settings.DisableHealthChecks)
        {
            string healthCheckName = serviceKey is null ? "CamusDB" : $"CamusDB_{connectionName}";

            builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
                healthCheckName,
                sp => new CamusDBHealthCheck(serviceKey is null
                    ? sp.GetRequiredService<CamusConnectionStringBuilder>()
                    : sp.GetRequiredKeyedService<CamusConnectionStringBuilder>(serviceKey)),
                failureStatus: default,
                tags: default));
        }

        if (!settings.DisableTracing)
            builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(CamusTelemetry.ActivitySourceName));

        if (!settings.DisableMetrics)
            builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(CamusTelemetry.MeterName));
    }
}
