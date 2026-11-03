/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Aspire.Hosting;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a database on a CamusDB cluster. The AppHost creates the database when all
/// the nodes of the cluster are healthy, because CamusDB does not create a database on first use.
/// </summary>
public class CamusDBClusterDatabaseResource : Resource, IResourceWithParent<CamusDBClusterResource>, IResourceWithConnectionString
{
    /// <summary>Initializes a new instance of the <see cref="CamusDBClusterDatabaseResource"/> class.</summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="databaseName">The name of the database on the cluster.</param>
    /// <param name="parent">The CamusDB cluster that holds the database.</param>
    public CamusDBClusterDatabaseResource(string name, string databaseName, CamusDBClusterResource parent) : base(name)
    {
        ArgumentException.ThrowIfNullOrEmpty(databaseName);
        ArgumentNullException.ThrowIfNull(parent);

        DatabaseName = databaseName;
        Parent = parent;
        State = new CamusDBDatabaseState(databaseName);
    }

    /// <summary>Gets the CamusDB cluster that holds the database.</summary>
    public CamusDBClusterResource Parent { get; }

    /// <summary>Gets the name of the database on the cluster.</summary>
    public string DatabaseName { get; }

    /// <summary>Gets the connection string expression for the database.</summary>
    public ReferenceExpression ConnectionStringExpression => Parent.BuildConnectionString(DatabaseName);

    // Set when the AppHost creates the database; read by the database health check.
    internal CamusDBDatabaseState State { get; }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        Parent.CombineProperties([new("Database", ReferenceExpression.Create($"{DatabaseName}"))]);
}
