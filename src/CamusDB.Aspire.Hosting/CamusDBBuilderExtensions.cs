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

/// <summary>Extension methods that add CamusDB resources to an <see cref="IDistributedApplicationBuilder"/>.</summary>
public static class CamusDBBuilderExtensions
{
    /// <summary>
    /// Adds a CamusDB server container to the application model. The container runs one standalone node
    /// from the <c>camusdb/camusdb</c> image, with authentication off.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. It is also the name of the connection string.</param>
    /// <param name="httpPort">The host port of the JSON/REST API. When it is <see langword="null"/>, Aspire selects a random port.</param>
    /// <param name="grpcPort">The host port of the gRPC API. When it is <see langword="null"/>, Aspire selects a random port.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBServerResource> AddCamusDB(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? httpPort = null,
        int? grpcPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        CamusDBServerResource resource = new(name);

        return builder.AddResource(resource)
            .WithImage(CamusDBContainerImageTags.Image, CamusDBContainerImageTags.Tag)
            .WithImageRegistry(CamusDBContainerImageTags.Registry)
            .WithHttpEndpoint(port: httpPort, targetPort: CamusDBServerResource.DefaultHttpPort, name: CamusDBServerResource.HttpEndpointName)
            .WithHttpEndpoint(port: grpcPort, targetPort: CamusDBServerResource.DefaultGrpcPort, name: CamusDBServerResource.GrpcEndpointName)
            .WithHttpHealthCheck("/health", endpointName: CamusDBServerResource.HttpEndpointName)
            .WithIconName("Database")
            .OnResourceReady(CreateDatabasesAsync);
    }

    /// <summary>
    /// Adds a database to a CamusDB server. The AppHost creates the database (<c>CREATE DATABASE IF NOT EXISTS</c>)
    /// when the server becomes ready, and the database resource becomes healthy after that.
    /// </summary>
    /// <param name="builder">The CamusDB server resource builder.</param>
    /// <param name="name">The name of the resource. It is also the name of the connection string.</param>
    /// <param name="databaseName">The name of the database on the server. When it is <see langword="null"/>, the resource name is used.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBDatabaseResource> AddDatabase(
        this IResourceBuilder<CamusDBServerResource> builder,
        [ResourceName] string name,
        string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        databaseName ??= name;

        CamusDBDatabaseResource database = new(name, databaseName, builder.Resource);
        builder.Resource.AddDatabase(database);

        string healthCheckKey = $"{name}_check";
        builder.ApplicationBuilder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckKey,
            _ => new CamusDBDatabaseHealthCheck(database),
            failureStatus: default,
            tags: default));

        return builder.ApplicationBuilder.AddResource(database)
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Adds a named volume for the data folder of the CamusDB server, so the data stays when the container restarts.
    /// </summary>
    /// <param name="builder">The CamusDB server resource builder.</param>
    /// <param name="name">The name of the volume. When it is <see langword="null"/>, Aspire generates a name from the application and resource names.</param>
    /// <param name="isReadOnly">A flag that tells if the volume is read-only.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBServerResource> WithDataVolume(
        this IResourceBuilder<CamusDBServerResource> builder,
        string? name = null,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), CamusDBServerResource.DataTargetFolder, isReadOnly);
    }

    /// <summary>Adds a bind mount for the data folder of the CamusDB server.</summary>
    /// <param name="builder">The CamusDB server resource builder.</param>
    /// <param name="source">The host folder to mount.</param>
    /// <param name="isReadOnly">A flag that tells if the mount is read-only.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBServerResource> WithDataBindMount(
        this IResourceBuilder<CamusDBServerResource> builder,
        string source,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, CamusDBServerResource.DataTargetFolder, isReadOnly);
    }

    /// <summary>
    /// Selects the wire protocol of the connection string. With <see cref="CamusDBProtocol.Grpc"/>, the
    /// connection string addresses the <c>grpc</c> endpoint and sets <c>Protocol=grpc</c>.
    /// </summary>
    /// <param name="builder">The CamusDB server resource builder.</param>
    /// <param name="protocol">The protocol.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBServerResource> WithProtocol(
        this IResourceBuilder<CamusDBServerResource> builder,
        CamusDBProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.Protocol = protocol;
        return builder;
    }

    /// <summary>
    /// Turns on authentication. The server creates a superuser from <paramref name="userName"/> and
    /// <paramref name="password"/> on its first start, and the connection strings carry these credentials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server reads the bootstrap credentials only when its user catalog is empty. With a data volume, a
    /// changed password has no effect on a server that already has users.
    /// </para>
    /// <para>
    /// The container serves plaintext HTTP, and other containers reach it by a host name that is not
    /// loopback. Thus this method starts the server with <c>--require-tls-when-auth-enabled=false</c>, and the
    /// connection strings set <c>AllowInsecureCredentials=true</c>. Do not use this setup outside local development.
    /// </para>
    /// </remarks>
    /// <param name="builder">The CamusDB server resource builder.</param>
    /// <param name="userName">The parameter for the superuser name. When it is <see langword="null"/>, the name is <c>admin</c>.</param>
    /// <param name="password">
    /// The parameter for the superuser password. When it is <see langword="null"/>, Aspire generates a password.
    /// The password must not contain a single quote.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<CamusDBServerResource> WithAuthentication(
        this IResourceBuilder<CamusDBServerResource> builder,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        CamusDBServerResource resource = builder.Resource;
        IDistributedApplicationBuilder app = builder.ApplicationBuilder;

        resource.UserNameParameter = userName?.Resource;
        resource.PasswordParameter = password?.Resource
            ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(app, $"{resource.Name}-password", special: false);

        // The server refuses to start with a token key shorter than 32 bytes.
        resource.TokenKeyParameter = ParameterResourceBuilderExtensions.CreateGeneratedParameter(
            app,
            $"{resource.Name}-token-key",
            secret: true,
            new GenerateParameterDefault { MinLength = 64, Special = false });

        // The image entrypoint is a shell command with fixed flags, so it is replaced with the same command
        // plus the TLS flag. The ${...} defaults are the same as in the image.
        return builder
            .WithEnvironment("CAMUSDB_AUTH_ENABLED", "true")
            .WithEnvironment("CAMUSDB_AUTH_TOKEN_KEY", resource.TokenKeyParameter)
            .WithEnvironment("CAMUSDB_BOOTSTRAP_USER", resource.UserNameReference)
            .WithEnvironment("CAMUSDB_BOOTSTRAP_PASSWORD", resource.PasswordParameter)
            .WithEntrypoint("sh")
            .WithArgs(
                "-c",
                "exec dotnet CamusDB.dll --mode=standalone --http-port=${CAMUS_HTTP_PORT:-5095} " +
                "--data-dir=${CAMUS_DATA_DIR:-/data} --initial-cluster-partitions=${CAMUS_INITIAL_PARTITIONS:-4} " +
                "--require-tls-when-auth-enabled=false");
    }

    private static async Task CreateDatabasesAsync(CamusDBServerResource server, ResourceReadyEvent @event, CancellationToken cancellationToken)
    {
        if (server.DatabaseResources.Count == 0)
            return;

        ResourceLoggerService loggers = @event.Services.GetRequiredService<ResourceLoggerService>();
        string? connectionString = await server.ConnectionStringExpression.GetValueAsync(cancellationToken).ConfigureAwait(false);

        foreach (CamusDBDatabaseResource database in server.DatabaseResources)
        {
            ILogger logger = loggers.GetLogger(database);

            try
            {
                CamusConnectionStringBuilder connectionStringBuilder = new($"{connectionString};Database={database.DatabaseName}");
                await CreateDatabaseAsync(connectionStringBuilder, cancellationToken).ConfigureAwait(false);

                database.CreationError = null;
                database.Created = true;
                logger.LogInformation("Database '{DatabaseName}' is ready.", database.DatabaseName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                database.CreationError = ex;
                logger.LogError(ex, "Failed to create database '{DatabaseName}'.", database.DatabaseName);
            }
        }
    }

    // /health answers before the server elects its Raft leaders and seeds the bootstrap superuser, so with
    // authentication on the first login can fail with CADB0516. The retries back off because the server
    // allows 20 logins per account per minute.
    private static async Task CreateDatabaseAsync(CamusConnectionStringBuilder connectionStringBuilder, CancellationToken cancellationToken)
    {
        const string AuthenticationFailed = "CADB0516";

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
