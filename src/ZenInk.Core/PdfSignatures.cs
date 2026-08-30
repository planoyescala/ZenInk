using System.Formats.Asn1;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace ZenInk.Core;

/// <summary>
/// What a visible signature writes inside its box. The certificate already
/// carries the name, but the box is what a person reads, so it is stated here
/// rather than dug out of a distinguished name at drawing time.
/// </summary>
/// <param name="Heading">
/// The line above the name. Editable rather than fixed, because «Firmado
/// digitalmente por» is not what everyone writes: some sign as «Visado por»,
/// some want nothing there at all.
/// </param>
/// <remarks>
/// An empty field does not appear. There is no switch for each line because the
/// field is the switch: clearing the DNI is how the DNI leaves the stamp, and
/// that is one thing to understand instead of two that can disagree.
/// </remarks>
public sealed record PdfSignatureAppearance(
    string Name,
    string Id = "",
    string Heading = "Firmado digitalmente por",
    bool ShowDate = true);

/// <summary>What a signature says about itself, beyond the certificate.</summary>
/// <param name="Rectangle">
/// Where the signature shows, <em>in PDF page space</em> — not sheet space. It
/// is the file's own coordinates because that is what a /Rect is; the caller
/// converts, which on a page with a /Rotate or an offset crop box is a job for
/// <see cref="PdfRenderQueue.ToPdfRectAsync"/> and not for arithmetic here.
/// Null leaves the signature invisible.
/// </param>
/// <param name="PageQuarterTurns">
/// The page's own /Rotate, in quarter turns. The stamp is turned against it so
/// it reads level on a sheet that is displayed turned.
/// </param>
public sealed record PdfSignatureOptions(
    int PageIndex = 0,
    string Reason = "",
    string Location = "",
    RectPt? Rectangle = null,
    int PageQuarterTurns = 0,
    PdfSignatureAppearance? Appearance = null);

/// <summary>
/// One signature found in a file, and whether it holds up.
/// </summary>
/// <param name="CoversWholeFile">
/// True for the newest signature only. An earlier one covers the document as it
/// stood when it was signed; anything appended afterwards is legitimately
/// outside its range, and that is how a PDF carries more than one signature at
/// all. A false here is not a fault — <see cref="DigestMatches"/> is.
/// </param>
public sealed record PdfSignatureInfo(
    string SubFilter,
    string Signer,
    DateTimeOffset? SignedAt,
    bool DigestMatches,
    bool CoversWholeFile,
    long[] ByteRange,
    string? Problem)
{
    /// <summary>True when this signature is CAdES, as PAdES requires.</summary>
    public bool IsPades => SubFilter is "ETSI.CAdES.detached";
}

/// <summary>
/// Signing a drawing, and reading back what is signed.
///
/// The whole approach in one line: PDFium keeps writing the PDF, and a
/// signature is appended on top of whatever it wrote. See
/// <see cref="PdfSignatureWriter"/> for why that is not negotiable.
/// </summary>
public static class PdfSignatures
{
    /// <summary>
    /// Writes a signed copy of <paramref name="sourcePath"/>, then reopens it and
    /// checks the signature before handing it back.
    ///
    /// The check is not ceremony. A signature that does not verify is worse than
    /// no signature: it says the drawing was tampered with. If it fails here the
    /// copy is deleted and nothing is returned, the same discipline the save path
    /// already follows.
    /// </summary>
    public static void Sign(
        string sourcePath,
        string targetPath,
        IPdfSigner signer,
        PdfSignatureOptions? options = null)
    {
        PdfSignatureWriter.Append(sourcePath, targetPath, signer, options ?? new PdfSignatureOptions());

        try
        {
            var found = Read(targetPath);
            if (found.Count == 0)
            {
                throw new InvalidDataException("El archivo firmado no lleva ninguna firma.");
            }
            if (found[^1] is { DigestMatches: false } bad)
            {
                throw new InvalidDataException($"La firma recién escrita no cuadra: {bad.Problem ?? "no verifica"}.");
            }
        }
        catch
        {
            TryDelete(targetPath);
            throw;
        }
    }

    /// <summary>Signs with a certificate, which is the ordinary case.</summary>
    public static void Sign(
        string sourcePath,
        string targetPath,
        X509Certificate2 certificate,
        PdfSignatureOptions? options = null) =>
        Sign(sourcePath, targetPath, new CertificateSigner(certificate), options);

    /// <summary>
    /// The certificates on this machine that can sign a document, newest last.
    ///
    /// This is where an FNMT certificate turns up once its installer has run.
    /// Only the user's own store is read, and only ever read.
    /// </summary>
    public static IReadOnlyList<X509Certificate2> AvailableCertificates()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        var usable = new List<X509Certificate2>();
        foreach (var certificate in store.Certificates)
        {
            if (!certificate.HasPrivateKey) continue;
            if (DateTime.Now > certificate.NotAfter || DateTime.Now < certificate.NotBefore) continue;

            // A certificate that says what it is for has to say it signs. One
            // that says nothing is allowed: plenty of working certificates omit
            // the extension entirely.
            bool signs = certificate.Extensions
                .OfType<X509KeyUsageExtension>()
                .All(usage => usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)
                              || usage.KeyUsages.HasFlag(X509KeyUsageFlags.NonRepudiation));

            if (signs) usable.Add(certificate);
        }

        return [.. usable.OrderBy(c => c.NotAfter)];
    }

    private static readonly Regex ByteRangePattern = new(
        @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]", RegexOptions.Compiled);

    /// <summary>
    /// Every signature in a file, read off the raw bytes.
    ///
    /// Deliberately independent of the writer: the /ByteRange either describes
    /// the real file or it does not, and the blob either verifies against those
    /// bytes or it does not. Asking the code that wrote a file whether the file
    /// is right proves nothing.
    /// </summary>
    public static IReadOnlyList<PdfSignatureInfo> Read(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        // Latin-1 maps every byte to one char, so regex offsets are byte offsets.
        string text = Encoding.Latin1.GetString(file);

        var found = new List<PdfSignatureInfo>();
        foreach (Match match in ByteRangePattern.Matches(text))
        {
            long[] range =
            [
                long.Parse(match.Groups[1].Value), long.Parse(match.Groups[2].Value),
                long.Parse(match.Groups[3].Value), long.Parse(match.Groups[4].Value),
            ];
            found.Add(CheckOne(file, text, match.Index, range));
        }
        return found;
    }

    private static PdfSignatureInfo CheckOne(byte[] file, string text, int dictionaryAt, long[] range)
    {
        string subFilter = NameNear(text, dictionaryAt, "/SubFilter");
        var signedAt = TimeNear(text, dictionaryAt);

        long gapStart = range[0] + range[1];
        long gapEnd = range[2];
        bool wholeFile = range[0] == 0 && range[2] + range[3] == file.LongLength;

        if (gapStart >= gapEnd || gapEnd > file.LongLength)
        {
            return new(subFilter, "", signedAt, false, wholeFile, range,
                "El /ByteRange no describe este archivo.");
        }

        // The hole the byte range leaves is the hex string holding the blob.
        string gap = text[(int)gapStart..(int)gapEnd];
        int open = gap.IndexOf('<'), close = gap.LastIndexOf('>');
        if (open < 0 || close < open)
        {
            return new(subFilter, "", signedAt, false, wholeFile, range,
                "El hueco del /ByteRange no contiene una firma.");
        }

        byte[] padded;
        try
        {
            padded = Convert.FromHexString(gap[(open + 1)..close].Trim());
        }
        catch (FormatException ex)
        {
            return new(subFilter, "", signedAt, false, wholeFile, range, ex.Message);
        }

        // /Contents is padded with zeros to the reserved size; the signature is
        // the first DER value in it and nothing else.
        byte[] blob;
        try
        {
            blob = new AsnReader(padded, AsnEncodingRules.BER).PeekEncodedValue().ToArray();
        }
        catch (AsnContentException ex)
        {
            return new(subFilter, "", signedAt, false, wholeFile, range, ex.Message);
        }

        var signed = new byte[range[1] + range[3]];
        Array.Copy(file, range[0], signed, 0, range[1]);
        Array.Copy(file, range[2], signed, range[1], range[3]);

        try
        {
            var cms = new SignedCms(new ContentInfo(signed), detached: true);
            cms.Decode(blob);
            // Whether the certificate is trusted is a separate question, and one
            // for the reader's own chain. What is settled here is that these
            // bytes were signed by that key and have not moved since.
            cms.CheckSignature(verifySignatureOnly: true);

            string who = cms.SignerInfos.Count > 0
                ? cms.SignerInfos[0].Certificate?.GetNameInfo(X509NameType.SimpleName, false) ?? "?"
                : "?";

            return new(subFilter, who, signedAt, true, wholeFile, range, null);
        }
        catch (Exception ex)
        {
            return new(subFilter, "", signedAt, false, wholeFile, range, ex.Message);
        }
    }

    /// <summary>
    /// Reads a name value out of the dictionary the /ByteRange sits in. The
    /// window has to be wide: the reserved /Contents pushes /SubFilter tens of
    /// kilobytes away from the /ByteRange inside the same dictionary.
    /// </summary>
    private static string NameNear(string text, int at, string key)
    {
        string pattern = Regex.Escape(key) + @"\s*/([A-Za-z0-9.\-_]+)";

        int from = Math.Max(0, at - (1 << 20));
        var behind = Regex.Matches(text[from..at], pattern);
        if (behind.Count > 0) return behind[^1].Groups[1].Value;

        int to = Math.Min(text.Length, at + 4096);
        var ahead = Regex.Match(text[at..to], pattern);
        return ahead.Success ? ahead.Groups[1].Value : "";
    }

    private static DateTimeOffset? TimeNear(string text, int at)
    {
        int from = Math.Max(0, at - (1 << 20));
        var matches = Regex.Matches(text[from..at], @"/M\s*\(D:(\d{14})([+\-Z])?(\d{2})?'?(\d{2})?'?\)");
        if (matches.Count == 0) return null;

        var match = matches[^1];
        if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var when))
        {
            return null;
        }

        if (match.Groups[2].Value is not ("+" or "-")) return new DateTimeOffset(when, TimeSpan.Zero);

        int hours = int.TryParse(match.Groups[3].Value, out int h) ? h : 0;
        int minutes = int.TryParse(match.Groups[4].Value, out int m) ? m : 0;
        var offset = new TimeSpan(hours, minutes, 0);

        return new DateTimeOffset(when, match.Groups[2].Value == "-" ? -offset : offset);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
