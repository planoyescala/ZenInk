//-----------------------------------------------------------------------------------------
// <copyright file="PdfRevocation.cs" company="plano y escala">
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
using System.Security.Cryptography.X509Certificates;

namespace ZenInk.Core;

/// <summary>
/// Where the proof that a certificate was good comes from.
///
/// Behind an interface for the same reason the timestamp is: this is the part
/// that leaves the machine. Everything else — building the question, putting
/// the answer into the file — is bytes, and bytes can be checked here.
/// </summary>
public interface IRevocationSource
{
    /// <summary>
    /// What the authority says about one certificate, or null when it will not
    /// say — no address to ask, or an answer that is not one.
    /// </summary>
    byte[]? Ask(X509Certificate2 certificate, X509Certificate2 issuer);
}

/// <summary>
/// The question an OCSP responder answers: "is this certificate still good?"
///
/// Written by hand because .NET has no OCSP client, and because the question is
/// small: which certificate, named the way the responder names it — by hashes
/// of its issuer rather than by the issuer itself, so the question gives away
/// nothing the responder does not already have.
/// </summary>
public static class OcspRequest
{
    /// <summary>SHA-1, which is what the CertID uses. Not a signature: a name.</summary>
    private static readonly string Sha1 = "1.3.14.3.2.26";

    public static byte[] Build(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        byte[] nameHash = SHA1.HashData(certificate.IssuerName.RawData);
        byte[] keyHash = SHA1.HashData(issuer.PublicKey.EncodedKeyValue.RawData);

        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence())                        // OCSPRequest
        using (writer.PushSequence())                        // tbsRequest
        using (writer.PushSequence())                        // requestList
        using (writer.PushSequence())                        // Request
        using (writer.PushSequence())                        // CertID
        {
            using (writer.PushSequence())                    // hashAlgorithm
            {
                writer.WriteObjectIdentifier(Sha1);
                writer.WriteNull();
            }

            writer.WriteOctetString(nameHash);
            writer.WriteOctetString(keyHash);
            writer.WriteInteger(certificate.SerialNumberBytes.Span);
        }

        return writer.Encode();
    }

    /// <summary>
    /// Whether a reply is an answer at all. The first field says so, and
    /// anything but "successful" is a refusal to be reported rather than stored
    /// in the drawing as if it were proof.
    /// </summary>
    public static bool IsSuccessful(byte[] response)
    {
        try
        {
            var reader = new AsnReader(response, AsnEncodingRules.DER).ReadSequence();
            return reader.ReadEnumeratedBytes().Span is [0];
        }
        catch (AsnContentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Asks the certificate's own responder, over HTTP, the way every OCSP client
/// does: one POST, one answer, nothing kept.
///
/// The address comes out of the certificate itself — the authority information
/// access extension — so nothing here decides who to ask. A certificate with no
/// responder gets no answer, which is a fact about the certificate rather than
/// a failure.
/// </summary>
public sealed class HttpRevocationSource(TimeSpan? timeout = null) : IRevocationSource
{
    private const string RequestType = "application/ocsp-request";

    /// <summary>Addresses that were asked and what they said, so a chain is not asked twice.</summary>
    private readonly Dictionary<string, byte[]?> _asked = new();

    public byte[]? Ask(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        string? url = ResponderFor(certificate);
        if (url is null) return null;

        string key = $"{url}|{certificate.Thumbprint}";
        if (_asked.TryGetValue(key, out var already)) return already;

        byte[]? answer = null;
        try
        {
            using var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
            using var body = new ByteArrayContent(OcspRequest.Build(certificate, issuer));
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(RequestType);

            using var reply = client.PostAsync(url, body).GetAwaiter().GetResult();
            if (reply.IsSuccessStatusCode)
            {
                byte[] bytes = reply.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (OcspRequest.IsSuccessful(bytes)) answer = bytes;
            }
        }
        catch (Exception)
        {
            // A responder that cannot be reached leaves the drawing without
            // that proof, which is a lesser thing than a signature that failed
            // to be written at all.
            answer = null;
        }

        _asked[key] = answer;
        return answer;
    }

    /// <summary>The responder the certificate names, or none.</summary>
    private static string? ResponderFor(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509AuthorityInformationAccessExtension access) continue;

            foreach (string uri in access.EnumerateOcspUris())
            {
                if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return uri;
            }
        }

        return null;
    }
}
