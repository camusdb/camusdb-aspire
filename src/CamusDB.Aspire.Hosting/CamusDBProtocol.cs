/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Aspire.Hosting.ApplicationModel;

/// <summary>The wire protocol that the connection string of a CamusDB resource selects.</summary>
public enum CamusDBProtocol
{
    /// <summary>The JSON/REST API on the <c>http</c> endpoint. This is the default.</summary>
    Rest,

    /// <summary>The gRPC API on the <c>grpc</c> endpoint, as plaintext HTTP/2.</summary>
    Grpc,
}
