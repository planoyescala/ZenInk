using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ZenInk.Core;
using ZenInk.Signing;

// Drives the engine's signing against real drawings — the ones that cannot live
// in the repository because they are clients'. The test suite covers the shapes
// it can build by hand; this covers a fifty-megabyte A0 out of Revit, which it
// cannot.
//
//   dotnet run --project tools/ZenInk.Signing -- [plano.pdf ...]   mide y firma
//   dotnet run --project tools/ZenInk.Signing -- certificados      qué hay en el almacén
//   dotnet run --project tools/ZenInk.Signing -- firmar in.pdf out.pdf [huella]
//   dotnet run --project tools/ZenInk.Signing -- marcar orig.pdf marcado.pdf
//
// With no arguments it uses whatever tools/ZenInk.Fixtures has left in %TEMP%.

// Writing marks runs on its own, because it drives the engine's queue and
// PDFium's library init is a process-wide global with no reference count.
if (args is ["marcar", var markSource, var markTarget])
{
    return await Marks.WriteAsync(markSource, markTarget);
}

if (args is ["certificados"])
{
    return Store.List();
}

// A visibly stamped copy signed by nobody, for looking at. The stamp is the one
// part of signing that has to be judged with eyes rather than with checks.
//
// The box is given in sheet points — measured from the top-left corner of the
// sheet as it is seen — and converted through the queue, exactly as the app
// does it. Handing a raw box straight to /Rect works on a fixture whose page
// starts at the origin and quietly misses on a drawing whose page does not.
if (args is ["sello", var stampFrom, var stampTo, ..])
{
    using var throwaway = Throwaway();
    var asked = args.Length > 3 && args[3].Split(',') is [var x, var y, var w, var h]
        ? new RectPt(float.Parse(x), float.Parse(y), float.Parse(w), float.Parse(h))
        : new RectPt(60, 60, 260, 90);

    var queue = PdfRenderQueue.Shared;
    var opened = await queue.OpenDocumentAsync(stampFrom);
    var (box, turns) = await queue.ToPdfRectAsync(opened.DocumentId, 0, asked);
    await queue.CloseDocumentAsync(opened.DocumentId);

    PdfSignatures.Sign(stampFrom, stampTo, throwaway, new PdfSignatureOptions(
        Reason: "Conforme",
        Rectangle: box,
        PageQuarterTurns: turns,
        Appearance: new PdfSignatureAppearance("MONTERO MORANO MANUEL", "48979704P")));

    Console.WriteLine(
        $"Hoja {asked.X},{asked.Y} {asked.Width}x{asked.Height} → "
        + $"PDF {box.X:0},{box.Y:0} {box.Width:0}x{box.Height:0} (giro {turns}) → {stampTo}");
    Report(stampTo, "  ");
    return 0;
}

// Signing for real, with a certificate out of the store: the one thing that
// cannot be checked without the user's own certificate installed.
if (args is ["firmar", var from, var to, ..])
{
    var chosen = Store.Pick(args.Length > 3 ? args[3] : null);
    if (chosen is null) return 1;

    PdfSignatures.Sign(from, to, chosen, new PdfSignatureOptions(Reason: "Conforme"));
    Console.WriteLine($"Firmado con «{chosen.GetNameInfo(X509NameType.SimpleName, false)}» → {to}");
    Report(to, "  ");
    return 0;
}

var inputs = args.Length > 0 ? args : Directory.GetFiles(Path.GetTempPath(), "zenink-*.pdf");
if (inputs.Length == 0)
{
    Console.Error.WriteLine("""
        No hay planos que medir. Genera uno primero:
            dotnet run --project tools/ZenInk.Fixtures denso
        o pasa la ruta de un PDF como argumento.
        """);
    return 1;
}

Ink.Start();
using var identity = Throwaway();

Console.WriteLine($"Certificado de prueba: {identity.Subject}");
Console.WriteLine();

int failures = 0;
foreach (string input in inputs.Where(File.Exists).OrderBy(p => p))
{
    failures += Measure(input, identity) ? 0 : 1;
    Console.WriteLine();
}

Ink.Stop();
return failures == 0 ? 0 : 1;

static bool Measure(string input, X509Certificate2 identity)
{
    string name = Path.GetFileName(input);
    var original = new FileInfo(input);
    Console.WriteLine($"── {name}  ({original.Length / 1024:N0} KB)");

    Ink.Shape before;
    byte[] inkBefore;
    try
    {
        before = Ink.Describe(input);
        inkBefore = Ink.Page(input);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   PDFium no lo lee: {ex.Message}");
        return false;
    }
    Console.WriteLine($"   antes: {before.Pages} hoja(s) {before.Sizes} pt · "
                      + $"{before.Objects:N0} objeto(s) de dibujo · {before.Annotations} marca(s)");

    bool ok = true;
    string once = Path.Combine(Path.GetTempPath(), $"firma-1-{name}");
    string twice = Path.Combine(Path.GetTempPath(), $"firma-2-{name}");

    try
    {
        var clock = Stopwatch.StartNew();
        PdfSignatures.Sign(input, once, identity, new PdfSignatureOptions(Reason: "Conforme"));
        clock.Stop();

        byte[] source = File.ReadAllBytes(input);
        byte[] signed = File.ReadAllBytes(once);
        bool untouched = signed.AsSpan(0, source.Length).SequenceEqual(source);

        Console.WriteLine($"   firma: {clock.ElapsedMilliseconds:N0} ms · "
                          + $"+{signed.LongLength - source.LongLength:N0} B · "
                          + $"bytes originales intactos: {(untouched ? "sí" : "NO")}");
        ok &= untouched;
        ok &= Report(once, "   ");

        var shape = Ink.Describe(once);
        double moved = Ink.Difference(inkBefore, Ink.Page(once));
        bool same = shape.Pages == before.Pages && shape.Objects == before.Objects && moved < 0.001;
        ok &= same;
        Console.WriteLine($"   PDFium lo lee: {shape.Pages} hoja(s) · {shape.Objects:N0} objeto(s) · "
                          + $"{shape.Annotations} marca(s) · tinta distinta {moved:P3} → {(same ? "intacto" : "NO INTACTO")}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   firma: falla — {ex.GetType().Name}: {ex.Message}");
        return false;
    }

    // A second signer on top of the first: the case the whole approach exists for.
    try
    {
        PdfSignatures.Sign(once, twice, identity, new PdfSignatureOptions(Reason: "Segunda firma"));

        byte[] first = File.ReadAllBytes(once);
        byte[] second = File.ReadAllBytes(twice);
        bool untouched = second.AsSpan(0, first.Length).SequenceEqual(first);

        Console.WriteLine($"   segunda firma: la primera queda intacta: {(untouched ? "sí" : "NO")}");
        ok &= untouched;
        ok &= Report(twice, "   ");

        var seen = Ink.Signatures(twice);
        ok &= seen.Count == 2;
        Console.WriteLine($"   PDFium ve {seen.Count} firma(s): {string.Join(", ", seen)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   segunda firma: falla — {ex.GetType().Name}: {ex.Message}");
        ok = false;
    }

    Console.WriteLine($"   → {(ok ? "SIRVE" : "NO SIRVE")}");
    return ok;
}

static bool Report(string path, string indent)
{
    var found = PdfSignatures.Read(path);
    if (found.Count == 0)
    {
        Console.WriteLine($"{indent}verificación: no hay ninguna firma en el archivo.");
        return false;
    }

    // Only the newest signature covers the whole file; an earlier one covers the
    // document as it stood when it was signed.
    bool ok = found.Count(f => f.CoversWholeFile) == 1;

    foreach (var signature in found)
    {
        ok &= signature.DigestMatches;
        Console.WriteLine(
            $"{indent}verificación: /{signature.SubFilter} · "
            + $"{(signature.CoversWholeFile ? "cubre todo el archivo" : "cubre hasta su propia firma")} · "
            + $"{signature.SignedAt:yyyy-MM-dd HH:mm} · "
            + $"cuadra: {(signature.DigestMatches ? $"sí ({signature.Signer})" : "NO")}"
            + (signature.Problem is null ? "" : $" — {signature.Problem}"));
    }
    return ok;
}

/// <summary>
/// A certificate that exists for one run and is nobody's. It goes out to PKCS#12
/// and back in because the CMS layer signs through a key handle, which a freshly
/// built request has no container to hand out.
/// </summary>
static X509Certificate2 Throwaway()
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest(
        "CN=ZenInk prueba de firma, O=plano y escala, C=ES", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    request.CertificateExtensions.Add(new X509KeyUsageExtension(
        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));

    using var ephemeral = request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    const string password = "zenink";
    return X509CertificateLoader.LoadPkcs12(
        ephemeral.Export(X509ContentType.Pkcs12, password), password, X509KeyStorageFlags.Exportable);
}
