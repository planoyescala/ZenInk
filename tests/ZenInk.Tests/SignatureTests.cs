using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PDFiumCore;
using ZenInk.Core;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>
/// Guards the property the whole signing approach rests on: a signature is
/// <em>appended</em>, so the drawing that was signed is still in the file, byte
/// for byte, and a signature that was already there still holds.
///
/// The checks read the signed file off its raw bytes rather than asking the
/// writer whether it was happy. A writer agreeing with itself proves nothing.
/// </summary>
public static class SignatureTests
{
    public static void Run()
    {
        Section("Signing — reading a PDF's skeleton");

        CheckLexer();
        CheckMap(TestPdf.WriteCornerMark(0), "tabla xref clásica", expectedRoot: 1);
        CheckMap(TestPdf.WriteModern("zenink-firma-moderno"), "flujo xref con objetos comprimidos", expectedRoot: 2);

        Section("Signing — appending a signature");

        using var identity = Certificate();

        CheckSigning(TestPdf.WriteCornerMark(0), "hoja con tabla xref", identity);
        CheckSigning(TestPdf.WriteModern("zenink-firma-moderno-2"), "hoja con flujo xref", identity);
        CheckStacking(identity);
        CheckForeignAnnotationSurvives(identity);
        CheckTamperingShows(identity);
        CheckReserveIsEnough(identity);

        Section("Signing — the stamp a visible signature leaves");

        CheckStampFits();
        CheckVisibleSignature(identity);
    }

    // --- the visible stamp ---------------------------------------------------

    private static void CheckStampFits()
    {
        var lines = PdfSignatureStamp.Lines(
            new PdfSignatureAppearance("MONTERO MORANO MANUEL", "48979704P"),
            reason: "Conforme", location: "", when: DateTimeOffset.Now);

        Check("el sello dice quién firma", lines.Any(l => l.Text.Contains("MONTERO")));
        Check("y lo destaca sobre lo demás", lines.Single(l => l.Strong).Text.Contains("MONTERO"));
        Check("el DNI sale con su etiqueta", lines.Any(l => l.Text == "DNI: 48979704P"));
        Check("y el motivo también", lines.Any(l => l.Text == "Motivo: Conforme"));

        // A box dragged out by hand comes in any size, and the type has to fit
        // the box rather than the box having to fit the type.
        string wide = PdfSignatureStamp.Draw(lines, 260f, 90f);
        string cramped = PdfSignatureStamp.Draw(lines, 90f, 26f);

        Check("una caja holgada lleva texto", wide.Contains("MONTERO"), wide);
        Check("una caja pequeña también, encogido", cramped.Contains("MONTERO"));
        Check("y en la pequeña la letra es menor",
            SizeIn(cramped) < SizeIn(wide), $"{SizeIn(cramped)} vs {SizeIn(wide)}");

        // The line that broke this first: a surname in bold capitals is much
        // wider per letter than an average would say, and it ran off its frame.
        foreach (float boxWidth in new[] { 90f, 140f, 200f, 260f, 400f })
        {
            var fitted = PdfSignatureStamp.Lines(
                new PdfSignatureAppearance("MONTERO MORANO MANUEL", "48979704P"),
                "Conforme", "", DateTimeOffset.Now);

            float size = PdfSignatureStamp.FitSize(fitted, boxWidth, 200f);
            float widest = fitted.Max(l => Helvetica.Width(l.Text, l.Strong) * size);

            Check($"a {boxWidth:0} pt de ancho, la línea más larga cabe",
                widest <= boxWidth - 8f + 0.01f, $"{widest:0.#} pt de texto en {boxWidth - 8f:0.#} pt de hueco");
        }

        // What is left blank does not appear: the field is the switch, so there
        // is nothing that can disagree with it.
        Check("un DNI en blanco no sale en el sello",
            !PdfSignatureStamp.Lines(
                new PdfSignatureAppearance("X", ""), "", "", DateTimeOffset.Now)
                .Any(l => l.Text.StartsWith("DNI")));

        Check("un encabezado en blanco tampoco",
            !PdfSignatureStamp.Lines(
                new PdfSignatureAppearance("X", "111", Heading: ""), "", "", DateTimeOffset.Now)
                .Any(l => l.Text.Contains("Firmado")));

        Check("y el encabezado se puede cambiar",
            PdfSignatureStamp.Lines(
                new PdfSignatureAppearance("X", "", Heading: "Visado por"), "", "", DateTimeOffset.Now)
                .Any(l => l.Text == "Visado por"));

        Check("la fecha sí lleva interruptor, porque no tiene campo",
            !PdfSignatureStamp.Lines(
                new PdfSignatureAppearance("X", "111", ShowDate: false), "", "", DateTimeOffset.Now)
                .Any(l => l.Text.StartsWith("Fecha")));

        // Below a few points the text is a smear, so it is left out and only the
        // frame remains: an honest empty box beats illegible ink.
        string tiny = PdfSignatureStamp.Draw(lines, 20f, 8f);
        Check("una caja diminuta se queda en el marco, sin texto ilegible", !tiny.Contains("Tj"));

        Check("el sello se gira contra el /Rotate de la hoja",
            PdfSignatureStamp.Matrix(1) == "0 -1 1 0 0 0" && PdfSignatureStamp.Matrix(0) == "1 0 0 1 0 0");

        var (w, h) = PdfSignatureStamp.LayoutBox(200f, 60f, 1);
        Check("y en un cuarto de vuelta la caja de texto intercambia sus lados", w == 60f && h == 200f);
    }

    /// <summary>The first /Fn size in a content stream, which is the type size the stamp settled on.</summary>
    private static float SizeIn(string content)
    {
        var match = System.Text.RegularExpressions.Regex.Match(content, @"/F\d ([\d.]+) Tf");
        return match.Success
            ? float.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0f;
    }

    /// <summary>
    /// The stamp has to be drawn by a reader that knows nothing of ZenInk, which
    /// means it has to live in the field's appearance stream and not in some
    /// private corner. Pixels are the only honest proof of that.
    /// </summary>
    private static void CheckVisibleSignature(X509Certificate2 identity)
    {
        string source = TestPdf.WriteCornerMark(0);
        string signed = Path.Combine(Path.GetTempPath(), $"zenink-firma-visible-{Guid.NewGuid():N}.pdf");

        // Low on the page, where the corner mark leaves the paper blank.
        var box = new RectPt(40f, 40f, 240f, 80f);

        try
        {
            PdfSignatures.Sign(source, signed, identity, new PdfSignatureOptions(
                Reason: "Conforme",
                Rectangle: box,
                Appearance: new PdfSignatureAppearance("MONTERO MORANO MANUEL", "48979704P")));
        }
        catch (Exception ex)
        {
            Check("firma visible: se escribe", false, ex.Message);
            return;
        }

        var found = PdfSignatures.Read(signed);
        Check("firma visible: la firma sigue cuadrando", found.Count == 1 && found[0].DigestMatches,
            found.Count == 1 ? found[0].Problem : $"{found.Count} firmas");

        string raw = File.ReadAllText(signed, System.Text.Encoding.Latin1);
        Check("firma visible: el campo lleva apariencia", raw.Contains("/AP<</N "));
        Check("firma visible: la caja va donde se pidió", raw.Contains("/Rect[40 40 280 120]"), Around(raw, "/Rect["));

        int inkBefore = InkIn(source, box);
        int inkAfter = InkIn(signed, box);

        // Rendered with annotations on, the way any reader shows a form field.
        Check("firma visible: el papel estaba limpio ahí antes", inkBefore < 50, $"{inkBefore} píxeles");
        Check("firma visible: y ahora un lector cualquiera dibuja el sello",
            inkAfter > inkBefore + 200, $"{inkBefore} → {inkAfter} píxeles");

        TryDelete(signed);
    }

    /// <summary>Dark pixels inside a box of the page, with annotations drawn.</summary>
    private static int InkIn(string path, RectPt pdfBox)
    {
        var document = fpdfview.FPDF_LoadDocument(path, null);
        if (document is null) return -1;

        try
        {
            var page = fpdfview.FPDF_LoadPage(document, 0)!;
            int width = TestPdf.PageWidth, height = TestPdf.PageHeight;

            var bitmap = fpdfview.FPDFBitmapCreateEx(
                width, height, (int)FPDFBitmapFormat.BGRA, IntPtr.Zero, width * 4)!;
            try
            {
                fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFFUL);
                fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0x01);

                // A signature's stamp lives in a form field's appearance, and
                // PDFium leaves those out of the page render: they belong to the
                // form layer, which is this second call. The engine's own tiles
                // go through the same pair — see PdfRenderQueue.DrawFormFields.
                var info = new FPDF_FORMFILLINFO { Version = 2 };
                var form = fpdf_formfill.FPDFDOC_InitFormFillEnvironment(document, info);
                if (form is not null)
                {
                    fpdf_formfill.FORM_OnAfterLoadPage(page, form);
                    fpdf_formfill.FPDF_FFLDraw(form, bitmap, page, 0, 0, width, height, 0, 0x01);
                    fpdf_formfill.FORM_OnBeforeClosePage(page, form);
                    fpdf_formfill.FPDFDOC_ExitFormFillEnvironment(form);
                }
                info.Dispose();

                int stride = fpdfview.FPDFBitmapGetStride(bitmap);
                IntPtr buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
                var row = new byte[stride];

                // PDF space is y-up; the bitmap is y-down.
                int top = (int)(height - (pdfBox.Y + pdfBox.Height));
                int bottom = (int)(height - pdfBox.Y);

                int dark = 0;
                for (int y = Math.Max(0, top); y < Math.Min(height, bottom); y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(buffer + y * stride, row, 0, stride);
                    for (int x = (int)pdfBox.X; x < Math.Min(width, (int)(pdfBox.X + pdfBox.Width)); x++)
                    {
                        if (row[x * 4] < 200 && row[x * 4 + 1] < 200 && row[x * 4 + 2] < 200) dark++;
                    }
                }
                return dark;
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(bitmap);
                fpdfview.FPDF_ClosePage(page);
            }
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(document);
        }
    }

    private static string Around(string text, string needle)
    {
        int at = text.IndexOf(needle, StringComparison.Ordinal);
        return at < 0 ? "no está" : text.Substring(at, Math.Min(48, text.Length - at));
    }

    // --- the skeleton readers ------------------------------------------------

    private static void CheckLexer()
    {
        byte[] dictionary = Encoding.Latin1.GetBytes(
            "<</Type/Page/Rect[1 2 3 4]/Nested<</Rect[9 9 9 9]>>/Note(a ) trap)/Parent 7 0 R>>");

        var rect = PdfLexer.Value(dictionary, 0, "/Rect");
        Check("el lexer encuentra un valor de arreglo",
            rect is not null && PdfLexer.Text(dictionary, rect.Value.Start, rect.Value.End) == "[1 2 3 4]");

        var parent = PdfLexer.Value(dictionary, 0, "/Parent");
        Check("una referencia indirecta se lee entera, no solo su primer número",
            parent is not null && PdfLexer.Text(dictionary, parent.Value.Start, parent.Value.End).Trim() == "7 0 R");

        // The nested dictionary also has a /Rect, and a string holds a bracket
        // that would end the dictionary if it were read as syntax.
        Check("las claves de un diccionario anidado no se confunden con las de fuera",
            rect is not null && PdfLexer.Text(dictionary, rect.Value.Start, rect.Value.End) == "[1 2 3 4]");

        var missing = PdfLexer.Value(dictionary, 0, "/Contents");
        Check("una clave que no está devuelve nada", missing is null);
    }

    private static void CheckMap(string path, string what, int expectedRoot)
    {
        var map = PdfFileMap.Read(path);

        Check($"{what}: se lee sin quejarse", map.Unsupported is null, map.Unsupported);
        if (map.Unsupported is not null) return;

        Check($"{what}: encuentra el catálogo", map.Root == expectedRoot, $"esperaba {expectedRoot}, salió {map.Root}");

        string catalogue = map.Body(map.Root);
        Check($"{what}: el catálogo dice ser un catálogo", catalogue.Contains("/Catalog"), catalogue);
        Check($"{what}: el catálogo apunta al árbol de páginas", catalogue.Contains("/Pages"));
    }

    // --- signing -------------------------------------------------------------

    private static void CheckSigning(string source, string what, X509Certificate2 identity)
    {
        byte[] before = File.ReadAllBytes(source);
        var (pages, objects) = Shape(source);

        string signed = Path.Combine(Path.GetTempPath(), $"zenink-firmado-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfSignatures.Sign(source, signed, identity, new PdfSignatureOptions(Reason: "Conforme"));
        }
        catch (Exception ex)
        {
            Check($"{what}: se firma", false, ex.Message);
            return;
        }

        byte[] after = File.ReadAllBytes(signed);
        Check($"{what}: los bytes originales siguen ahí, intactos",
            after.Length > before.Length && after.AsSpan(0, before.Length).SequenceEqual(before));

        var found = PdfSignatures.Read(signed);
        Check($"{what}: hay exactamente una firma", found.Count == 1, $"salieron {found.Count}");
        if (found.Count != 1) { TryDelete(signed); return; }

        Check($"{what}: la firma cuadra con los bytes", found[0].DigestMatches, found[0].Problem);
        Check($"{what}: cubre el archivo entero", found[0].CoversWholeFile);
        Check($"{what}: es PAdES, no PKCS#7 suelto", found[0].IsPades, found[0].SubFilter);
        Check($"{what}: dice quién firma", found[0].Signer.Contains("ZenInk"), found[0].Signer);
        Check($"{what}: dice cuándo", found[0].SignedAt is not null);

        var (pagesAfter, objectsAfter) = Shape(signed);
        Check($"{what}: PDFium lo sigue abriendo con el mismo dibujo",
            pagesAfter == pages && objectsAfter == objects,
            $"{pages}/{objects} → {pagesAfter}/{objectsAfter}");

        TryDelete(signed);
    }

    /// <summary>
    /// Two signers on one drawing. This is the case the whole approach exists
    /// for: the second signature must not disturb the first, and only the newest
    /// one covers the file to its end.
    /// </summary>
    private static void CheckStacking(X509Certificate2 identity)
    {
        string source = TestPdf.WriteCornerMark(0);
        string once = Path.Combine(Path.GetTempPath(), $"zenink-firma1-{Guid.NewGuid():N}.pdf");
        string twice = Path.Combine(Path.GetTempPath(), $"zenink-firma2-{Guid.NewGuid():N}.pdf");

        try
        {
            PdfSignatures.Sign(source, once, identity, new PdfSignatureOptions(Reason: "Primera"));
            PdfSignatures.Sign(once, twice, identity, new PdfSignatureOptions(Reason: "Segunda"));
        }
        catch (Exception ex)
        {
            Check("dos firmas: se pueden apilar", false, ex.Message);
            TryDelete(once);
            TryDelete(twice);
            return;
        }

        byte[] first = File.ReadAllBytes(once);
        byte[] second = File.ReadAllBytes(twice);
        Check("dos firmas: la segunda se añade detrás de la primera, sin tocarla",
            second.AsSpan(0, first.Length).SequenceEqual(first));

        var found = PdfSignatures.Read(twice);
        Check("dos firmas: el archivo lleva dos", found.Count == 2, $"salieron {found.Count}");

        if (found.Count == 2)
        {
            Check("dos firmas: las dos cuadran",
                found[0].DigestMatches && found[1].DigestMatches,
                $"{found[0].Problem} / {found[1].Problem}");

            // The earlier one covers the document as it stood when it was
            // signed; what came after is legitimately outside its range.
            Check("dos firmas: solo la última cubre el archivo entero",
                found.Count(f => f.CoversWholeFile) == 1);
        }

        Check("dos firmas: PDFium ve las dos", SignatureCount(twice) == 2, $"vio {SignatureCount(twice)}");

        TryDelete(once);
        TryDelete(twice);
    }

    /// <summary>
    /// A drawing that arrives with somebody else's review mark on it. Signing
    /// must not disturb it: the mark is part of what is being signed.
    /// </summary>
    private static void CheckForeignAnnotationSurvives(X509Certificate2 identity)
    {
        string source = TestPdf.WriteForeignAnnotation("zenink-firma-ajena");
        string signed = Path.Combine(Path.GetTempPath(), $"zenink-firma-ajena-{Guid.NewGuid():N}.pdf");

        try
        {
            PdfSignatures.Sign(source, signed, identity);
        }
        catch (Exception ex)
        {
            Check("marca ajena: se firma sin perderla", false, ex.Message);
            return;
        }

        // One annotation was there, and signing adds the signature's own field.
        Check("marca ajena: sigue en la hoja, y ahora convive con el campo de firma",
            Annotations(signed) == 2, $"salieron {Annotations(signed)}");

        TryDelete(signed);
    }

    /// <summary>
    /// The point of signing at all: change one byte of the drawing and the
    /// signature stops adding up.
    /// </summary>
    private static void CheckTamperingShows(X509Certificate2 identity)
    {
        string source = TestPdf.WriteCornerMark(0);
        string signed = Path.Combine(Path.GetTempPath(), $"zenink-manipulado-{Guid.NewGuid():N}.pdf");

        PdfSignatures.Sign(source, signed, identity);
        Check("manipulación: recién firmado, cuadra", PdfSignatures.Read(signed)[0].DigestMatches);

        byte[] file = File.ReadAllBytes(signed);
        // Somewhere in the original drawing, well before the signature.
        int at = Encoding.Latin1.GetString(file).IndexOf("0 450 100 150 re f", StringComparison.Ordinal);
        if (at < 0)
        {
            Check("manipulación: se encuentra el trozo a tocar", false);
            TryDelete(signed);
            return;
        }

        file[at] = (byte)'9';
        File.WriteAllBytes(signed, file);

        var found = PdfSignatures.Read(signed);
        Check("manipulación: tocar un byte del dibujo rompe la firma",
            found.Count == 1 && !found[0].DigestMatches);

        TryDelete(signed);
    }

    private static void CheckReserveIsEnough(X509Certificate2 identity)
    {
        var signer = new CertificateSigner(identity);
        byte[] blob = signer.Sign(new MemoryStream(Encoding.ASCII.GetBytes("lo que sea")));

        Check("la reserva de /Contents da de sobra para la firma real",
            blob.Length < signer.ReserveBytes,
            $"{blob.Length} B de firma contra {signer.ReserveBytes} B reservados");

        Check("la reserva no es absurdamente grande",
            signer.ReserveBytes < blob.Length * 20,
            $"{signer.ReserveBytes} B para una firma de {blob.Length} B");
    }

    /// <summary>
    /// The same stamp, seen the way the app sees it: through the render queue's
    /// tiles rather than through a bare PDFium call.
    ///
    /// This is the check that matters. A stamp that a test can coax onto a
    /// bitmap but that never reaches a tile is a signature the reader cannot
    /// see, which is the whole point of asking for a visible one.
    /// </summary>
    public static async Task RunAsync()
    {
        Section("Signing — the stamp as the viewer draws it");

        using var identity = Certificate();

        string source = TestPdf.WriteCornerMark(0);
        string signed = Path.Combine(Path.GetTempPath(), $"zenink-firma-tile-{Guid.NewGuid():N}.pdf");
        var box = new RectPt(40f, 40f, 240f, 80f);

        PdfSignatures.Sign(source, signed, identity, new PdfSignatureOptions(
            Rectangle: box,
            Appearance: new PdfSignatureAppearance("MONTERO MORANO MANUEL", "48979704P")));

        var queue = PdfRenderQueue.Shared;
        var document = await queue.OpenDocumentAsync(signed);

        // One tile covering the whole small page, at scale 1.
        var tile = await queue.RequestTileAsync(
            document.DocumentId, new TileKey(0, ZoomLevels.LevelForScale(1.0), 0, 0), 1024);

        Check("el sello llega al tile", tile is not null);

        if (tile is { } image)
        {
            // PDF space is y-up; the tile is y-down.
            int top = (int)(TestPdf.PageHeight - (box.Y + box.Height));
            int bottom = (int)(TestPdf.PageHeight - box.Y);

            int dark = 0;
            for (int y = top; y < bottom; y++)
            {
                for (int x = (int)box.X; x < (int)(box.X + box.Width); x++)
                {
                    int at = (y * image.Width + x) * 4;
                    if (at + 2 < image.Bgra.Length
                        && image.Bgra[at] < 200 && image.Bgra[at + 1] < 200 && image.Bgra[at + 2] < 200)
                    {
                        dark++;
                    }
                }
            }

            Check("y el visor lo dibuja donde se puso", dark > 200, $"{dark} píxeles en la caja");
        }

        // The viewer almost never asks for the page as one tile: it asks for
        // pieces, and a piece is rendered by placing the whole scaled page at a
        // negative offset. If the form layer ignored that offset the stamp would
        // show on the first tile and nowhere else — which is exactly how it
        // looked in the app.
        var piece = await queue.RequestTileAsync(
            document.DocumentId, new TileKey(0, ZoomLevels.LevelForScale(1.0), 0, 2), 256);

        if (piece is { } corner)
        {
            int dark = 0;
            for (int y = 0; y < corner.Height; y++)
            {
                for (int x = 40; x < 256; x++)
                {
                    int at = (y * corner.Width + x) * 4;
                    if (at + 2 < corner.Bgra.Length
                        && corner.Bgra[at] < 200 && corner.Bgra[at + 1] < 200 && corner.Bgra[at + 2] < 200)
                    {
                        dark++;
                    }
                }
            }

            Check("y también en un tile desplazado, que es como se ve de verdad",
                dark > 100, $"{dark} píxeles en el tile");
        }

        await queue.CloseDocumentAsync(document.DocumentId);
        TryDelete(signed);
    }

    /// <summary>
    /// A locked PDF has to be told apart from a broken one: one of them the
    /// reader can do something about.
    /// </summary>
    public static async Task LockedAsync()
    {
        Section("Documentos protegidos con contraseña");

        var queue = PdfRenderQueue.Shared;
        string locked = TestPdf.WriteEncrypted("zenink-con-clave", "abreme");

        try
        {
            await queue.OpenDocumentAsync(locked);
            Check("sin contraseña no se abre", false, "se abrió sin pedir nada");
        }
        catch (PdfPasswordRequiredException asked)
        {
            Check("sin contraseña, pide una", !asked.WasTried);
        }
        catch (Exception ex)
        {
            Check("sin contraseña, pide una", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            await queue.OpenDocumentAsync(locked, "no es esta");
            Check("con la contraseña equivocada tampoco", false, "se abrió igualmente");
        }
        catch (PdfPasswordRequiredException wrong)
        {
            Check("con la equivocada, dice que ya se intentó", wrong.WasTried);
        }
        catch (Exception ex)
        {
            Check("con la equivocada, dice que ya se intentó", false, ex.Message);
        }

        try
        {
            var opened = await queue.OpenDocumentAsync(locked, "abreme");
            Check("con la buena, se abre", opened.Pages.Count == 1, $"{opened.Pages.Count} páginas");
            await queue.CloseDocumentAsync(opened.DocumentId);
        }
        catch (Exception ex)
        {
            Check("con la buena, se abre", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // Signing would have to encrypt what it appends, and it does not. Better
        // to say so than to write a file that opens and is quietly wrong.
        var map = PdfFileMap.Read(locked);
        Check("y el escritor de firmas ve que está cifrado", map.Encrypted);

        // Left in %TEMP% like every other fixture: it is the only locked PDF
        // around, and the interface has to be tried against one by hand.
    }

    // --- helpers -------------------------------------------------------------

    /// <summary>
    /// A throwaway certificate. It goes out to PKCS#12 and back in because on
    /// Windows the CMS layer signs through a key handle, and the key a freshly
    /// built request holds has no container behind it to hand one out.
    /// </summary>
    private static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ZenInk pruebas, O=plano y escala, C=ES", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        const string password = "zenink";
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pkcs12, password), password, X509KeyStorageFlags.Exportable);
    }

    private static (int Pages, int Objects) Shape(string path)
    {
        var document = fpdfview.FPDF_LoadDocument(path, null);
        if (document is null) return (-1, -1);

        try
        {
            int pages = fpdfview.FPDF_GetPageCount(document);
            int objects = 0;
            for (int i = 0; i < pages; i++)
            {
                var page = fpdfview.FPDF_LoadPage(document, i)!;
                objects += fpdf_edit.FPDFPageCountObjects(page);
                fpdfview.FPDF_ClosePage(page);
            }
            return (pages, objects);
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(document);
        }
    }

    private static int Annotations(string path)
    {
        var document = fpdfview.FPDF_LoadDocument(path, null);
        if (document is null) return -1;

        try
        {
            var page = fpdfview.FPDF_LoadPage(document, 0)!;
            int count = fpdf_annot.FPDFPageGetAnnotCount(page);
            fpdfview.FPDF_ClosePage(page);
            return count;
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(document);
        }
    }

    /// <summary>A second opinion from a reader that has never seen our writer.</summary>
    private static int SignatureCount(string path)
    {
        var document = fpdfview.FPDF_LoadDocument(path, null);
        if (document is null) return -1;

        try
        {
            return fpdf_signature.FPDF_GetSignatureCount(document);
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(document);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
