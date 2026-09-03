//-----------------------------------------------------------------------------------------
// <copyright file="PdfSigner.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace ZenInk.Core;

/// <summary>
/// Turns the stretch of a PDF that a signature covers into the blob that goes
/// inside it.
///
/// It is an interface for one reason: what produces the blob is the only part
/// that has to change to sign with something other than a certificate file —
/// a card, a remote signing service. Everything about building the PDF around
/// it stays the same.
/// </summary>
public interface IPdfSigner
{
    /// <summary>Who is signing, as it goes into the signature's /Name.</summary>
    string Name { get; }

    /// <summary>
    /// How much room to leave for the blob. The PDF reserves it before the blob
    /// exists, because the signature covers the file it is written into, so
    /// writing it for real would move the very offsets it describes. Too small
    /// and the write fails; too large only wastes bytes.
    /// </summary>
    int ReserveBytes { get; }

    byte[] Sign(Stream covered);
}

/// <summary>
/// Signs with a certificate, producing the detached CAdES-BES blob that a PAdES
/// signature carries.
///
/// The private key is never asked for. <see cref="SignedCms"/> signs through
/// the certificate's key handle, so Windows routes the work to whichever
/// provider owns the key and the key material never comes into this process.
/// That is what makes a certificate installed as non-exportable — which is how
/// the FNMT's installer leaves it — usable at all.
/// </summary>
public sealed class CertificateSigner(
    X509Certificate2 certificate,
    X509Certificate2Collection? chain = null,
    ITimestamper? timestamper = null)
    : IPdfSigner
{
    private readonly X509Certificate2Collection _chain = chain ?? [];

    /// <summary>SHA-256, as OID. Anything weaker is refused by verifiers that matter.</summary>
    private static readonly Oid Sha256 = new("2.16.840.1.101.3.4.2.1");

    /// <summary>id-aa-signingCertificateV2 (RFC 5035).</summary>
    private static readonly Oid SigningCertificate = new("1.2.840.113549.1.9.16.2.47");

    public string Name => certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

    /// <summary>
    /// The certificates dominate the blob; the rest is signed attributes and one
    /// key's worth of signature. The slack is deliberately generous — a reserve
    /// that is a few kilobytes too big costs a few kilobytes, and one that is a
    /// byte too small costs the whole write.
    /// </summary>
    public int ReserveBytes
    {
        get
        {
            int certificates = certificate.RawData.Length;
            foreach (var extra in _chain) certificates += extra.RawData.Length;

            // A timestamp brings the authority's own chain with it, so the room
            // for it is reserved before anyone has been asked for one.
            return certificates + 8192 + (timestamper is null ? 0 : PdfTimestamp.ReserveBytes);
        }
    }

    public byte[] Sign(Stream covered)
    {
        var content = new ContentInfo(ReadAll(covered));
        var signed = new SignedCms(content, detached: true);

        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = Sha256,
            IncludeOption = X509IncludeOption.WholeChain,
        };
        foreach (var extra in _chain) signer.Certificates.Add(extra);

        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        // What separates CAdES from a bare PKCS#7: the signer binds its own
        // certificate into the signed attributes, so swapping the certificate
        // inside the blob cannot go unnoticed.
        signer.SignedAttributes.Add(SigningCertificateV2(certificate));

        signed.ComputeSignature(signer, silent: false);

        // The timestamp is asked for after the signature exists, because it is
        // over the signature. If the authority cannot be reached the throw
        // travels: a signature quietly written without the timestamp the reader
        // asked for is one they would believe they had.
        if (timestamper is { } authority)
        {
            PdfTimestamp.Add(signed, authority);
        }

        return signed.Encode();
    }

    /// <summary>
    /// Reads the whole covered range. It is the file minus a few kilobytes, so on
    /// a 50 MB drawing this is 50 MB in memory — <see cref="SignedCms"/> wants the
    /// content as one array and will not take it in pieces.
    /// </summary>
    private static byte[] ReadAll(Stream covered)
    {
        covered.Position = 0;
        var buffer = new byte[covered.Length];

        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = covered.Read(buffer, filled, buffer.Length - filled);
            if (read <= 0) throw new IOException($"El rango a firmar se cortó en {filled} de {buffer.Length} bytes.");
            filled += read;
        }
        return buffer;
    }

    /// <summary>
    /// Both optional fields are left out: the hash algorithm defaults to SHA-256,
    /// and issuerSerial says nothing an IssuerAndSerialNumber signer identifier
    /// has not already said.
    /// </summary>
    private static AsnEncodedData SigningCertificateV2(X509Certificate2 certificate)
    {
        byte[] hash = SHA256.HashData(certificate.RawData);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())        // SigningCertificateV2
        using (writer.PushSequence())        // certs
        using (writer.PushSequence())        // ESSCertIDv2
        {
            writer.WriteOctetString(hash);   // certHash
        }

        return new AsnEncodedData(SigningCertificate, writer.Encode());
    }
}
