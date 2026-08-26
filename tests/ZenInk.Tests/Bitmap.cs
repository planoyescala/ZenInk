using PDFiumCore;

namespace ZenInk.Tests;

/// <summary>Direct PDFium rendering helpers, used to establish ground truth for the engine's output.</summary>
public static class Bitmap
{
    /// <summary>
    /// Renders a region using the same call the engine makes: the whole scaled
    /// page positioned so that the requested tile lands on the bitmap.
    /// </summary>
    public static byte[] Render(FpdfPageT page, double scale, int startX, int startY, int outWidth, int outHeight)
    {
        int scaledWidth = Math.Max(1, (int)Math.Ceiling(fpdfview.FPDF_GetPageWidthF(page) * scale));
        int scaledHeight = Math.Max(1, (int)Math.Ceiling(fpdfview.FPDF_GetPageHeightF(page) * scale));

        var bitmap = fpdfview.FPDFBitmapCreateEx(outWidth, outHeight, (int)FPDFBitmapFormat.BGRA, IntPtr.Zero, outWidth * 4)
            ?? throw new InvalidOperationException("FPDFBitmap_CreateEx returned null.");
        try
        {
            fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, outWidth, outHeight, 0xFFFFFFFFUL);
            fpdfview.FPDF_RenderPageBitmap(bitmap, page, startX, startY, scaledWidth, scaledHeight, 0, 0);

            int stride = fpdfview.FPDFBitmapGetStride(bitmap);
            IntPtr buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);

            var packed = new byte[outWidth * 4 * outHeight];
            for (int row = 0; row < outHeight; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    buffer + row * stride, packed, row * outWidth * 4, outWidth * 4);
            }
            return packed;
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }

    public static bool IsDark(byte[] bgra, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return bgra[i] < 128 && bgra[i + 1] < 128 && bgra[i + 2] < 128;
    }

    /// <summary>Bounding box of the non-white pixels, or null if the image is blank.</summary>
    public static (int Left, int Top, int Right, int Bottom)? InkBounds(byte[] bgra, int width, int height)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!IsDark(bgra, width, x, y)) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < left ? null : (left, top, right, bottom);
    }
}
