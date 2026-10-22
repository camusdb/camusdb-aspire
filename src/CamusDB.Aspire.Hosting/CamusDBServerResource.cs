/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a CamusDB server container. The container serves the JSON/REST API on the
/// <c>http</c> endpoint and the gRPC API on the <c>grpc</c> endpoint.
/// </summary>
/// <param name="name">The name of the resource.</param>
public class CamusDBServerResource(string name) : ContainerResource(name), IResourceWithConnectionString
{
    internal const string HttpEndpointName = "http";
    internal const string GrpcEndpointName = "grpc";
    internal const int DefaultHttpPort = 5095;
    internal const int DefaultGrpcPort = 5096;
    internal const string DefaultUserName = "admin";
    internal const string DataTargetFolder = "/data";

    private readonly Dictionary<string, string> databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CamusDBDatabaseResource> databaseResources = [];

    /// <summary>Gets the endpoint of the JSON/REST API.</summary>
    public EndpointReference HttpEndpoint => field ??= new(this, HttpEndpointName);

    /// <summary>Gets the endpoint of the gRPC API.</summary>
    public EndpointReference GrpcEndpoint => field ??= new(this, GrpcEndpointName);

    /// <summary>
    /// Gets the wire protocol that the connection string selects. Change it with
    /// <see cref="CamusDBBuilderExtensions.WithProtocol"/>.
    /// </summary>
    public CamusDBProtocol Protocol { get; internal set; } = CamusDBProtocol.Rest;

    /// <summary>
    /// Gets a value that is <see langword="true"/> when the server enforces authentication. Turn it on with
    /// <see cref="CamusDBBuilderExtensions.WithAuthentication"/>.
    /// </summary>
    public bool AuthenticationEnabled => PasswordParameter is not null;

    /// <summary>Gets the parameter that holds the name of the bootstrap superuser, or <see langword="null"/> for the default name.</summary>
    public ParameterResource? UserNameParameter { get; internal set; }

    /// <summary>Gets the parameter that holds the password of the bootstrap superuser. It is <see langword="null"/> when authentication is off.</summary>
    public ParameterResource? PasswordParameter { get; internal set; }

    /// <summary>Gets the parameter that holds the key the server uses to hash access tokens. It is <see langword="null"/> when authentication is off.</summary>
    public ParameterResource? TokenKeyParameter { get; internal set; }

    /// <summary>Gets an expression for the name of the bootstrap superuser.</summary>
    public ReferenceExpression UserNameReference =>
        UserNameParameter is not null
            ? ReferenceExpression.Create($"{UserNameParameter}")
            : ReferenceExpression.Create($"{DefaultUserName}");

    /// <summary>Gets the databases of this server, as a map from resource name to database name.</summary>
    public IReadOnlyDictionary<string, string> Databases => databases;

    internal IReadOnlyList<CamusDBDatabaseResource> DatabaseResources => databaseResources;

    /// <summary>
    /// Gets the connection string expression for the server. It has no <c>Database</c> key, so a
    /// <c>CamusConnection</c> needs one added before it opens. Reference a database resource to get it.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => BuildConnectionString(databaseName: null);

    internal void AddDatabase(CamusDBDatabaseResource database)
    {
        databases.TryAdd(database.Name, database.DatabaseName);
        databaseResources.Add(database);
    }

    // The keys follow CamusDB.Client's CamusConnectionStringBuilder. The password is quoted because a
    // value may hold a semicolon. A container reaches the server by its container host name, which is not
    // loopback, so the client needs AllowInsecureCredentials to send credentials over plaintext HTTP.
    internal ReferenceExpression BuildConnectionString(string? databaseName)
    {
        ReferenceExpressionBuilder builder = new();

        if (Protocol == CamusDBProtocol.Grpc)
            builder.Append($"Endpoint={GrpcEndpoint.Property(EndpointProperty.Url)};Protocol=grpc;BackupEndpoint={HttpEndpoint.Property(EndpointProperty.Url)}");
        else
            builder.Append($"Endpoint={HttpEndpoint.Property(EndpointProperty.Url)}");

        if (databaseName is not null)
            builder.AppendLiteral($";Database={databaseName}");

        if (PasswordParameter is not null)
            builder.Append($";User={UserNameReference};Password='{PasswordParameter}';AllowInsecureCredentials=true");

        return builder.Build();
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Host", ReferenceExpression.Create($"{HttpEndpoint.Property(EndpointProperty.Host)}"));
        yield return new("Port", ReferenceExpression.Create($"{HttpEndpoint.Property(EndpointProperty.Port)}"));
        yield return new("GrpcPort", ReferenceExpression.Create($"{GrpcEndpoint.Property(EndpointProperty.Port)}"));
        yield return new("Uri", ReferenceExpression.Create($"{HttpEndpoint.Property(EndpointProperty.Url)}"));
        yield return new("GrpcUri", ReferenceExpression.Create($"{GrpcEndpoint.Property(EndpointProperty.Url)}"));

        if (PasswordParameter is not null)
        {
            yield return new("Username", UserNameReference);
            yield return new("Password", ReferenceExpression.Create($"{PasswordParameter}"));
        }
    }
}
