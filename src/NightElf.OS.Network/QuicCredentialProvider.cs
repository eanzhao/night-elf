using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NightElf.OS.Network;

public interface IQuicCredentialProvider
{
    X509Certificate2 GetServerCertificate();

    bool IsTrustedPeer(X509Certificate? certificate);
}

public sealed class EphemeralQuicCredentialProvider : IQuicCredentialProvider
{
    private readonly Lazy<X509Certificate2> _serverCertificate = new(CreateCertificate);

    public X509Certificate2 GetServerCertificate()
    {
        return _serverCertificate.Value;
    }

    public bool IsTrustedPeer(X509Certificate? certificate)
    {
        return certificate is not null &&
               string.Equals(
                   certificate.GetCertHashString(),
                   GetServerCertificate().GetCertHashString(),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=nightelf-quic",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx),
            string.Empty,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable,
            loaderLimits: null);
    }
}
