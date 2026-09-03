//-----------------------------------------------------------------------------------------
// <copyright file="PdfTimestamp.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace ZenInk.Core;

/// <summary>
/// Where a timestamp comes from: something that takes an RFC 3161 request and
/// hands back the authority's reply.
///
/// It is an interface because the only part that touches the network is this
/// one. Everything else about a timestamp — building the request, checking the
/// reply belongs to this signature, putting it inside the blob — is arithmetic
/// on bytes, and arithmetic is what can be checked without asking anyone for
/// anything.
/// </summary>
public interface ITimestamper
{
    /// <summary>Who is being asked, for saying so when it fails.</summary>
    string Authority { get; }

    /// <summary>The reply to one request, or a throw explaining why there is none.</summary>
    byte[] Stamp(byte[] request);
}

/// <summary>
/// A timestamp over a signature: proof that it existed at a time somebody else
/// vouches for.
///
/// It is what turns a signature that dies with its certificate into one that
/// survives it. Without it, "was this signed while the certificate was valid?"
/// can only be answered by trusting the clock of whoever signed — which is the
/// signer's own clock, and therefore no answer at all.
/// </summary>
public static class PdfTimestamp
{
    /// <summary>id-aa-signatureTimeStampToken (RFC 3161 §3.3.2).</summary>
    public const string TokenAttribute = "1.2.840.113549.1.9.16.2.14";

    /// <summary>
    /// How much room a timestamp adds to the blob. A token carries the
    /// authority's own certificate chain, which is the same order of size as
    /// the signer's, and the reserve has to be right before the token exists.
    /// </summary>
    public const int ReserveBytes = 12288;

    /// <summary>
    /// Asks <paramref name="authority"/> to timestamp a signature, and puts the
    /// reply inside it.
    ///
    /// The token goes in as an unsigned attribute, which is the only place it
    /// can go: it is made from the signature, so the signature cannot cover it.
    /// That is not a weakness — the token itself is signed, and what it signs
    /// is the hash of the signature it is attached to, so moving it to another
    /// signature makes it stop matching.
    /// </summary>
    public static SignedCms Add(SignedCms signed, ITimestamper authority)
    {
        if (signed.SignerInfos.Count == 0) throw new InvalidOperationException("La firma no tiene firmante.");

        var signer = signed.SignerInfos[0];

        var request = Rfc3161TimestampRequest.CreateFromSignerInfo(
            signer,
            HashAlgorithmName.SHA256,
            requestSignerCertificates: true,
            nonce: Nonce());

        byte[] reply = authority.Stamp(request.Encode());

        // ProcessResponse is what checks that the reply is for this request:
        // the nonce, the hash, the policy. A token that does not match is not a
        // timestamp for this signature, and a signature carrying somebody
        // else's timestamp would be worse than one carrying none.
        var token = request.ProcessResponse(reply, out _);

        signer.AddUnsignedAttribute(new AsnEncodedData(new Oid(TokenAttribute), token.AsSignedCms().Encode()));
        return signed;
    }

    /// <summary>
    /// The timestamp inside a signature, or null when it carries none.
    ///
    /// What comes back is what the authority said, not what the signer's clock
    /// said — which is the whole point of asking one.
    /// </summary>
    public static PdfTimestampInfo? Read(SignedCms signed)
    {
        if (signed.SignerInfos.Count == 0) return null;

        foreach (var attribute in signed.SignerInfos[0].UnsignedAttributes)
        {
            if (attribute.Oid?.Value != TokenAttribute) continue;

            foreach (var value in attribute.Values)
            {
                if (!Rfc3161TimestampToken.TryDecode(value.RawData, out var token, out _)) continue;

                var info = token.TokenInfo;
                var stamped = token.AsSignedCms();

                string authority = stamped.SignerInfos.Count > 0
                    ? stamped.SignerInfos[0].Certificate?.GetNameInfo(X509NameType.SimpleName, false) ?? ""
                    : "";

                // Whether it actually covers this signature, which is what the
                // token is for. A mismatch means the token was moved here from
                // somewhere else.
                bool covers;
                try
                {
                    covers = info.GetMessageHash().Span.SequenceEqual(
                        SignatureHash(signed.SignerInfos[0], info.HashAlgorithmId));
                }
                catch (CryptographicException)
                {
                    covers = false;
                }

                return new PdfTimestampInfo(info.Timestamp, authority, covers);
            }
        }

        return null;
    }

    /// <summary>The hash of the signature the token should be over, by the algorithm the token used.</summary>
    private static byte[] SignatureHash(SignerInfo signer, Oid algorithm)
    {
        var name = algorithm.Value switch
        {
            "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
            "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
            "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
            "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
            _ => throw new CryptographicException($"El sello usa un resumen que no se conoce: {algorithm.Value}."),
        };

        return CryptographicOperations.HashData(name, signer.GetSignature());
    }

    /// <summary>
    /// A nonce, so a reply cannot be one the authority gave earlier. It is read
    /// as an INTEGER, so the top bit is cleared to keep it positive.
    /// </summary>
    private static ReadOnlyMemory<byte> Nonce()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        bytes[0] &= 0x7F;
        return bytes;
    }
}

/// <summary>What a timestamp says: when, by whom, and whether it is this signature's.</summary>
public sealed record PdfTimestampInfo(DateTimeOffset Stamped, string Authority, bool CoversSignature);

/// <summary>
/// An authority reached over HTTP, which is how every public one works: one
/// POST of the request, one reply, no session and nothing kept.
///
/// This is the only part of signing that leaves the machine, and it says so.
/// What travels is a hash of the signature and nothing else — not the drawing,
/// not the certificate, not who is signing — which is what makes asking a
/// stranger for the time a reasonable thing to do at all.
/// </summary>
public sealed class HttpTimestamper(string url, TimeSpan? timeout = null) : ITimestamper
{
    private const string RequestType = "application/timestamp-query";

    private const string ReplyType = "application/timestamp-reply";

    public string Authority => url;

    public byte[] Stamp(byte[] request)
    {
        using var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(20) };

        using var body = new ByteArrayContent(request);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(RequestType);

        HttpResponseMessage reply;
        try
        {
            reply = client.PostAsync(url, body).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Said plainly, because the reader chose to timestamp and is about
            // to be told the signing did not happen: they need to know it was
            // the authority and not their drawing.
            throw new InvalidOperationException(
                $"No se pudo pedir el sello de tiempo a «{url}»: {ex.Message}", ex);
        }

        using (reply)
        {
            if (!reply.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"La autoridad de sellado «{url}» respondió {(int)reply.StatusCode} {reply.ReasonPhrase}.");
            }

            string? type = reply.Content.Headers.ContentType?.MediaType;
            if (type is not null && !type.Equals(ReplyType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"«{url}» contestó {type}, que no es una respuesta de sellado.");
            }

            return reply.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
    }
}
