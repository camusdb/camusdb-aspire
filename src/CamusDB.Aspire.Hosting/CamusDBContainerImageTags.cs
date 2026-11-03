/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Aspire.Hosting;

/// <summary>The container image that <see cref="CamusDBBuilderExtensions.AddCamusDB"/> runs.</summary>
internal static class CamusDBContainerImageTags
{
    /// <remarks>docker.io</remarks>
    public const string Registry = "docker.io";

    /// <remarks>camusdb/camusdb</remarks>
    public const string Image = "camusdb/camusdb";

    /// <remarks>0.17.0</remarks>
    public const string Tag = "0.17.0";
}
