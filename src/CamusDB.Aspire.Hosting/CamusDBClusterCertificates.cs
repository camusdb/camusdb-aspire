/*
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aspire.Hosting;

/// <summary>
/// The TLS material of one CamusDB cluster for one AppHost run: a certificate authority (CA), and one
/// certificate for each node that the CA signs.
/// </summary>
/// <remarks>
/// <para>
/// The nodes connect to each other over TLS on the Raft port, and each node checks that the certificate of
/// a peer names the host that it dialed. Each node certificate thus names the container host name of the
/// node. Each node trusts the CA through <c>SSL_CERT_FILE</c>, and with mutual TLS on, it also accepts
/// only peer certificates that the CA signed (<c>raft_peer_ca</c>).
/// </para>
/// <para>
/// The keys are made again on each AppHost run and are never stored with the data. The node certificates
/// are written to a temporary folder because the server loads them from a PFX file, and the folder is
/// deleted when the AppHost stops.
/// </para>
/// </remarks>
internal sealed class CamusDBClusterCertificates : IDisposable
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(30);

    private readonly Dictionary<string, string> nodeCertificatePaths = new(StringComparer.Ordinal);

    private CamusDBClusterCertificates(string folder, string caPem)
    {
        Folder = folder;
        CaPem = caPem;
    }

    /// <summary>Gets the temporary folder that holds the PFX files of the nodes.</summary>
    public string Folder { get; }

    /// <summary>Gets the CA certificate, in PEM format.</summary>
    public string CaPem { get; }

    /// <summary>Gets the path of the PFX file (certificate and private key, no password) of a node.</summary>
    public string GetNodeCertificatePath(string nodeHostName) => nodeCertificatePaths[nodeHostName];

    /// <summary>Makes a CA and one certificate for each node host name.</summary>
    public static CamusDBClusterCertificates Create(string clusterName, IReadOnlyList<string> nodeHostNames)
    {
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        DateTimeOffset notAfter = notBefore.Add(Validity);

        using ECDsa caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest caRequest = new($"CN={clusterName} CamusDB Aspire development CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, critical: false));

        using X509Certificate2 ca = caRequest.CreateSelfSigned(notBefore, notAfter);

        string folder = Path.Combine(Path.GetTempPath(), "camusdb-aspire", $"{clusterName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        CamusDBClusterCertificates certificates = new(folder, ca.ExportCertificatePem());

        try
        {
            foreach (string hostName in nodeHostNames)
            {
                string path = Path.Combine(folder, $"{hostName}.pfx");
                File.WriteAllBytes(path, CreateNodeCertificate(ca, hostName, notBefore, notAfter));
                certificates.nodeCertificatePaths[hostName] = path;
            }
        }
        catch
        {
            certificates.Dispose();
            throw;
        }

        return certificates;
    }

    // A node presents the same certificate as a server (to the peers that dial it) and as a client (when
    // it dials a peer with mutual TLS on), so it needs both extended key usages.
    private static byte[] CreateNodeCertificate(X509Certificate2 ca, string hostName, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={hostName}", key, HashAlgorithmName.SHA256);

        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName(hostName);
        names.AddDnsName($"{hostName}.dev.internal");
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")],
            critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        byte[] serialNumber = RandomNumberGenerator.GetBytes(16);
        serialNumber[0] &= 0x7F;

        using X509Certificate2 certificate = request.Create(ca, notBefore, notAfter, serialNumber);
        using X509Certificate2 certificateWithKey = certificate.CopyWithPrivateKey(key);
        return certificateWithKey.Export(X509ContentType.Pkcs12);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
