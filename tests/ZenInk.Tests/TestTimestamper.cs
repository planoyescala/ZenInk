using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using ZenInk.Core;

namespace ZenInk.Tests;

/// <summary>
/// A timestamping authority that lives in this process.
///
/// It exists so the timestamp path can be checked without asking anybody on the
/// internet for anything. That matters twice over: a check that needs a network
/// is a check that fails on a train, and a real authority is somebody else's
/// service to be polite about. What it does is exactly what RFC 3161 says an
/// authority does — read the request, sign a TSTInfo over the hash it carries,
/// and hand back the reply — so the code under test cannot tell the difference.
/// </summary>
public sealed class TestTimestamper : IDisposable, ITimestamper
{
    /// <summary>id-ct-TSTInfo: what the token's content is.</summary>
    private const string TstInfo = "1.2.840.113549.1.9.16.1.4";

    /// <summary>The made-up policy this authority stamps under.</summary>
    private static readonly Oid Policy = new("1.3.6.1.4.1.99999.1.1");

    private readonly X509Certificate2 _certificate = TsaCertificate();

    /// <summary>The time this authority swears by, so a check can compare against something known.</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 3, 4, 9, 30, 0, TimeSpan.Zero);

    /// <summary>Set to have the authority stamp a different hash — a token that belongs to nobody here.</summary>
    public byte[]? StampInstead { get; set; }

    public string Authority => "TSA de pruebas";

    public string Name => _certificate.GetNameInfo(X509NameType.SimpleName, false);

    public byte[] Stamp(byte[] request)
    {
        if (!Rfc3161TimestampRequest.TryDecode(request, out var asked, out _))
        {
            throw new CryptographicException("La petición de sello no es RFC 3161.");
        }

        // The serial goes out as an INTEGER, so it has to read positive: eight
        // random bytes with the top bit set is a negative serial and a TSTInfo
        // nothing will parse.
        byte[] serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        serial[0] |= 0x01;

        var info = new Rfc3161TimestampTokenInfo(
            Policy,
            asked.HashAlgorithmId,
            StampInstead ?? asked.GetMessageHash().ToArray(),
            serial,
            Now,
            nonce: asked.GetNonce());

        var content = new ContentInfo(new Oid(TstInfo), info.Encode());
        var token = new SignedCms(content, detached: false);

        var signer = new CmsSigner(_certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };

        // An authority binds its own certificate into what it signs. RFC 3161
        // asks for it, and a token without it is refused by the reader — which
        // is how a real authority's token differs from one thrown together.
        signer.SignedAttributes.Add(SigningCertificateV2(_certificate));

        token.ComputeSignature(signer);

        // The authority checks its own work: a token this cannot decode is a
        // fault here, not in what is being tested, and finding that out from
        // "the response was not understood" costs an afternoon. It cost one:
        // a token whose time falls outside its own certificate's validity is
        // refused, and every other thing about it can be right.
        if (!Rfc3161TimestampToken.TryDecode(token.Encode(), out _, out _))
        {
            throw new CryptographicException(
                "El sello que fabrica la TSA de pruebas no se puede leer. Empieza por la hora: "
                + $"puso {Now:u}, y su certificado vale de {_certificate.NotBefore:u} a {_certificate.NotAfter:u}.");
        }

        // TimeStampResp ::= SEQUENCE { status PKIStatusInfo, timeStampToken OPTIONAL }
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteInteger(0);      // granted
            }
            writer.WriteEncodedValue(token.Encode());
        }

        return writer.Encode();
    }

    public void Dispose() => _certificate.Dispose();

    /// <summary>
    /// The authority's own name, as a GeneralName [4] directoryName — which is
    /// the shape TSTInfo's optional tsa field takes.
    /// </summary>
    private static byte[] TsaName(X509Certificate2 certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true)))
        {
            writer.WriteEncodedValue(certificate.SubjectName.RawData);
        }
        return writer.Encode();
    }

    /// <summary>id-aa-signingCertificateV2, the same one the signer's own blob carries.</summary>
    private static AsnEncodedData SigningCertificateV2(X509Certificate2 certificate)
    {
        byte[] hash = SHA256.HashData(certificate.RawData);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            writer.WriteOctetString(hash);
        }

        return new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), writer.Encode());
    }

    /// <summary>
    /// A certificate that may timestamp. The extended key usage is not
    /// decoration: a token whose signer cannot claim id-kp-timeStamping is
    /// refused, which is the point of the field.
    /// </summary>
    private static X509Certificate2 TsaCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Sellado de tiempo de pruebas, O=plano y escala, C=ES",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.8")], critical: true));

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddYears(-5), DateTimeOffset.UtcNow.AddYears(5));

        const string password = "zenink";
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pkcs12, password), password, X509KeyStorageFlags.Exportable);
    }
}
