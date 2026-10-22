/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a database on a CamusDB server. The AppHost creates the database when the
/// server becomes ready, because CamusDB does not create a database on first use.
/// </summary>
public class CamusDBDatabaseResource : Resource, IResourceWithParent<CamusDBServerResource>, IResourceWithConnectionString
{
    /// <summary>Initializes a new instance of the <see cref="CamusDBDatabaseResource"/> class.</summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="databaseName">The name of the database on the server.</param>
    /// <param name="parent">The CamusDB server that holds the database.</param>
    public CamusDBDatabaseResource(string name, string databaseName, CamusDBServerResource parent) : base(name)
    {
        ArgumentException.ThrowIfNullOrEmpty(databaseName);
        ArgumentNullException.ThrowIfNull(parent);

        DatabaseName = databaseName;
        Parent = parent;
    }

    /// <summary>Gets the CamusDB server that holds the database.</summary>
    public CamusDBServerResource Parent { get; }

    /// <summary>Gets the name of the database on the server.</summary>
    public string DatabaseName { get; }

    /// <summary>Gets the connection string expression for the database.</summary>
    public ReferenceExpression ConnectionStringExpression => Parent.BuildConnectionString(DatabaseName);

    // Set when the AppHost creates the database; read by the database health check.
    internal volatile bool Created;
    internal volatile Exception? CreationError;

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        Parent.CombineProperties([new("Database", ReferenceExpression.Create($"{DatabaseName}"))]);
}
