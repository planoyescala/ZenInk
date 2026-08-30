using PDFiumCore;

namespace ZenInk.Signing;

/// <summary>
/// Renders a page so the drawing can be compared before and after a library
/// rewrites the file. A page count that matches proves nothing: what matters is
/// whether the same ink lands in the same places.
/// </summary>
public static class Ink
{
    public static void Start() => fpdfview.FPDF_InitLibrary();
    public static void Stop() => fpdfview.FPDF_DestroyLibrary();

    public sealed record Shape(int Pages, string Sizes, int Annotations, int Objects);

    public static Shape Describe(string path)
    {
        var document = fpdfview.FPDF_LoadDocument(path, null)
            ?? throw new InvalidOperationException($"PDFium no abre {path} (error {fpdfview.FPDF_GetLastError()}).");
        try
        {
            int pages = fpdfview.FPDF_GetPageCount(document);
            var sizes = new List<string>();
            int annotations = 0;
            int objects = 0;

            for (int i = 0; i < pages; i++)
            {
                var page = fpdfview.FPDF_LoadPage(document, i)!;
                sizes.Add($"{fpdfview.FPDF_GetPageWidthF(page):0}x{fpdfview.FPDF_GetPageHeightF(page):0}(giro {fpdf_edit.FPDFPageGetRotation(page)})");
                annotations += fpdf_annot.FPDFPageGetAnnotCount(page);
                objects += fpdf_edit.FPDFPageCountObjects(page);
                fpdfview.FPDF_ClosePage(page);
            }

            return new Shape(pages, string.Join(" ", sizes), annotations, objects);
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(document);
        }
    }

    /// <summary>
    /// How many signatures PDFium finds, and what it calls them.
    ///
    /// A second opinion, and one worth having: the checker in this tool reads
    /// the raw bytes the same way it wrote them, so it could agree with itself
    /// about a file no other reader would accept. PDFium parses the document
    /// properly and has never seen our writer.
    /// </summary>
    public static List<string> Signatures(string path)
    {
        var document = fpdfview.FPDF_LoadDocument(path, null)
            ?? throw new InvalidOperationException($"PDFium no abre {path}.");
        try
        {
            var found = new List<string>();
            for (int i = 0; i < fpdf_signature.FPDF_GetSignatureCount(document); i++)
            {
                var signature = fpdf_signature.FPDF_GetSignatureObject(document, i);
                if (signature is null) { found.Add("?"); continue; }

                found.Add(SubFilter(signature));
            }
            return found;
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(document);
        }
    }

    /// <summary>PDFium hands the /SubFilter back as raw bytes, so this goes through a pointer.</summary>
    private static unsafe string SubFilter(FpdfSignatureT signature)
    {
        ulong size = fpdf_signature.FPDFSignatureObjGetSubFilter(signature, null, 0);
        if (size == 0) return "—";

        var buffer = new sbyte[size];
        fixed (sbyte* into = buffer)
        {
            fpdf_signature.FPDFSignatureObjGetSubFilter(signature, into, size);
            return new string(into, 0, (int)size).TrimEnd('\0');
        }
    }

    /// <summary>Renders page zero into a grey buffer about <paramref name="across"/> pixels wide.</summary>
    public static byte[] Page(string path, int across = 2400)
    {
        var document = fpdfview.FPDF_LoadDocument(path, null)
            ?? throw new InvalidOperationException($"PDFium no abre {path}.");
        try
        {
            var page = fpdfview.FPDF_LoadPage(document, 0)!;
            double scale = across / fpdfview.FPDF_GetPageWidthF(page);
            int width = across;
            int height = Math.Max(1, (int)(fpdfview.FPDF_GetPageHeightF(page) * scale));

            var bitmap = fpdfview.FPDFBitmapCreateEx(width, height, (int)FPDFBitmapFormat.BGRA, IntPtr.Zero, width * 4)!;
            try
            {
                fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFFUL);
                fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0);

                int stride = fpdfview.FPDFBitmapGetStride(bitmap);
                IntPtr buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
                var row = new byte[stride];
                var grey = new byte[width * height];

                for (int y = 0; y < height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(buffer + y * stride, row, 0, stride);
                    for (int x = 0; x < width; x++)
                    {
                        grey[y * width + x] = (byte)((row[x * 4] + row[x * 4 + 1] + row[x * 4 + 2]) / 3);
                    }
                }
                return grey;
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

    /// <summary>Fraction of pixels that differ by more than a rounding wobble.</summary>
    public static double Difference(byte[] before, byte[] after)
    {
        if (before.Length != after.Length) return 1.0;

        int differing = 0;
        for (int i = 0; i < before.Length; i++)
        {
            if (Math.Abs(before[i] - after[i]) > 8) differing++;
        }
        return (double)differing / before.Length;
    }
}
