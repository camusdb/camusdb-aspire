/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Aspire.Hosting;

/// <summary>
/// The result of the creation of one database. The AppHost writes it when it creates the database, and
/// the database health check reads it.
/// </summary>
internal sealed class CamusDBDatabaseState(string databaseName)
{
    private volatile bool created;
    private volatile Exception? creationError;

    public string DatabaseName { get; } = databaseName;

    public bool Created
    {
        get => created;
        set => created = value;
    }

    public Exception? CreationError
    {
        get => creationError;
        set => creationError = value;
    }
}
