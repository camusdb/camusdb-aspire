/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using CamusDB.Aspire.EntityFrameworkCore;
using CamusDB.Client;
using CamusDB.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>Extension methods that register a CamusDB <see cref="DbContext"/> in an <see cref="IHostApplicationBuilder"/>.</summary>
public static class AspireCamusDBEFCoreExtensions
{
    private const string DefaultConfigSectionName = "Aspire:CamusDB:EntityFrameworkCore";

    /// <summary>
    /// Registers <typeparamref name="TContext"/> as a pooled <see cref="DbContext"/> that uses the CamusDB provider,
    /// and adds retries, a health check, tracing and metrics. The settings also bind from
    /// <c>Aspire:CamusDB:EntityFrameworkCore:{TContext name}</c>.
    /// </summary>
    /// <typeparam name="TContext">The <see cref="DbContext"/> type.</typeparam>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/>.</param>
    /// <param name="connectionName">The name of the connection string in the <c>ConnectionStrings</c> configuration section.</param>
    /// <param name="configureSettings">An optional delegate that changes the <see cref="CamusDBEntityFrameworkCoreSettings"/>.</param>
    /// <param name="configureDbContextOptions">An optional delegate that changes the <see cref="DbContextOptionsBuilder"/>.</param>
    /// <exception cref="InvalidOperationException">The configuration has no connection string.</exception>
    public static void AddCamusDBDbContext<TContext>(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<CamusDBEntityFrameworkCoreSettings>? configureSettings = null,
        Action<DbContextOptionsBuilder>? configureDbContextOptions = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        CamusDBEntityFrameworkCoreSettings settings = new();
        builder.Configuration.GetSection(DefaultConfigSectionName).Bind(settings);
        builder.Configuration.GetSection($"{DefaultConfigSectionName}:{typeof(TContext).Name}").Bind(settings);

        if (builder.Configuration.GetConnectionString(connectionName) is { } connectionString)
            settings.ConnectionString = connectionString;

        configureSettings?.Invoke(settings);

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            throw new InvalidOperationException(
                $"A CamusDB connection string is missing. Set it in 'ConnectionStrings:{connectionName}' or in the " +
                $"'ConnectionString' key of the '{DefaultConfigSectionName}' configuration section.");

        string resolvedConnectionString = settings.ConnectionString;

        builder.Services.AddDbContextPool<TContext>(options =>
        {
            options.UseCamusDB(resolvedConnectionString, camus =>
            {
                if (!settings.DisableRetry)
                    camus.EnableRetryOnFailure();
            });

            configureDbContextOptions?.Invoke(options);
        });

        if (!settings.DisableHealthChecks)
            builder.Services.AddHealthChecks().AddDbContextCheck<TContext>(name: typeof(TContext).Name);

        if (!settings.DisableTracing)
            builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(CamusTelemetry.ActivitySourceName));

        if (!settings.DisableMetrics)
            builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
                .AddMeter(CamusTelemetry.MeterName)
                .AddMeter("Microsoft.EntityFrameworkCore"));
    }
}
