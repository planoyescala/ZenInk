//-----------------------------------------------------------------------------------------
// <copyright file="Program.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Drawing;
using System.Drawing.Imaging;

// Builds the whole icon set from the master in design/, which is the part
// nothing else writes down: how the set is made, not just that it exists.
//
// Two things run through everything here, and the second is what makes an icon
// look sharp rather than smeared:
//
//   Every resize averages in premultiplied alpha. The master carries a
//   leftover white underneath its transparent pixels, and any resampler that
//   ignores alpha drags it into the edges as a pale fringe. It shows first at
//   16 px, which is the one size nobody checks.
//
//   Windows is given every size it can ask for, rather than a few and the job
//   of stretching them. The taskbar asks for 24 effective pixels; at 150 %
//   scaling that is 36 real ones, and with only a 24 px asset on hand the
//   shell blows it up by half and the result is visibly soft. Scaling a
//   bitmap up is the one thing that cannot be undone later, so the ladder
//   below covers 100, 125, 150, 175, 200, 300 and 400 per cent.

string root = FindRepositoryRoot();
string design = Path.Combine(root, "design");
string assets = Path.Combine(root, "src", "ZenInk.App", "Assets");

var mark = Image.Load(Path.Combine(design, "LogoZenInk.png")).Trim();

Console.WriteLine($"maestro: {mark.Width}x{mark.Height}");
Console.WriteLine();

// --- el icono de la aplicación --------------------------------------------
//
// Taskbar, Start, Alt+Tab, "Abrir con". The shell asks for these by target
// size, and prefers the unplated form wherever it draws the icon on its own
// background — the taskbar above all. Both forms are written from the same
// pixels: this mark is a badge with its own shape, so it needs no plate.
//
// 36 and 42 look like odd sizes and are the important ones: they are 24 at
// 150 % and 175 %, which is what most laptops run at.
int[] targetSizes = [16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256];

foreach (int size in targetSizes)
{
    Image icon = mark.Boxed(size, size);
    Write(icon, $"Square44x44Logo.targetsize-{size}.png");
    Write(icon, $"Square44x44Logo.targetsize-{size}_altform-unplated.png");
}

// The scaled family, for the places that ask by scale instead of by size.
WriteScales(mark, "Square44x44Logo", 44, 44);
WriteScales(mark, "StoreLogo", 50, 50);
WriteScales(mark, "LockScreenLogo", 24, 24);

// The mark on the tab strip is drawn at 18 points; 72 leaves pixels to spare
// at 400 %, which is as far as Windows scales.
Write(mark.Boxed(72, 72), "AppMark.png");

// Written as DIB rather than embedded PNG: it is what the taskbar, Explorer
// and the older tooling read without arguing. This is the icon an unpackaged
// run gets, and the one on the window itself.
int[] icoSizes = [16, 20, 24, 30, 32, 36, 40, 48, 64, 96, 128, 256];
Icon.Write(Path.Combine(assets, "AppIcon.ico"), [.. icoSizes.Select(s => mark.Boxed(s, s))]);
Console.WriteLine($"AppIcon.ico ({string.Join(", ", icoSizes)})");

// --- las superficies grandes ----------------------------------------------
//
// The tile guidance wants the mark at two thirds of the tile and the splash at
// half of its height. Keeping those proportions is what stops the tile from
// looking like a sticker filling its own square.
WriteScales(mark, "Square150x150Logo", 150, 150, 2.0 / 3);
WriteScales(mark, "Wide310x150Logo", 310, 150, 2.0 / 3);
WriteScales(mark, "SplashScreen", 620, 300, 0.5);

// --- el icono del tipo de archivo -----------------------------------------
//
// What the Explorer draws on a PDF. The same drawing under its own name: the
// Explorer asks for sizes the application icon is never asked for, and keeping
// them apart means one can change without dragging the other along. It carried
// a "PDF" label for a while and no longer does — at the sizes the Explorer
// actually draws it, three letters are not letters, they are a grey smear
// across the sheet.
foreach (int size in targetSizes)
{
    Write(mark.Boxed(size, size), $"PdfFileType.targetsize-{size}.png");
}

WriteScales(mark, "PdfFileType", 44, 44);

Console.WriteLine();
Console.WriteLine($"Escrito en {assets}");
return 0;

/// <summary>
/// One asset at every scaling Windows uses. The names carry the percentage and
/// the pixels follow from it, so a 150 % screen gets a bitmap made for 150 %
/// instead of one stretched to it.
/// </summary>
void WriteScales(Image source, string name, int width, int height, double fill = 1.0)
{
    foreach (int scale in (int[])[100, 125, 150, 200, 400])
    {
        int w = (int)Math.Round(width * scale / 100.0);
        int h = (int)Math.Round(height * scale / 100.0);
        Write(source.Boxed(w, h, fill), $"{name}.scale-{scale}.png");
    }
}

void Write(Image image, string name)
{
    image.Save(Path.Combine(assets, name));
    Console.WriteLine($"  {name} ({image.Width}x{image.Height})");
}

static string FindRepositoryRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ZenInk.sln")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? throw new InvalidOperationException("No encuentro la raíz del repositorio.");
}

/// <summary>
/// A bitmap as straight-alpha BGRA bytes, which is what both GDI+ and the ICO
/// format want, and the only representation the resampler here needs.
/// </summary>
sealed class Image(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Pixels { get; } = pixels;

    public static Image Load(string path)
    {
        using var source = new Bitmap(path);
        using var argb = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(argb))
        {
            g.DrawImageUnscaled(source, 0, 0);
        }

        var data = argb.LockBits(
            new Rectangle(0, 0, argb.Width, argb.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = new byte[argb.Width * argb.Height * 4];
            for (int y = 0; y < argb.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + (y * data.Stride), pixels, y * argb.Width * 4, argb.Width * 4);
            }

            return new Image(argb.Width, argb.Height, pixels);
        }
        finally
        {
            argb.UnlockBits(data);
        }
    }

    public void Save(string path)
    {
        using var bitmap = ToBitmap();
        bitmap.Save(path, ImageFormat.Png);
    }

    public Bitmap ToBitmap()
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    Pixels, y * Width * 4, data.Scan0 + (y * data.Stride), Width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    /// <summary>
    /// Drops the transparent margin. The masters come out of the drawing tool
    /// with an arbitrary amount of it, and it differs between the two — left
    /// in, the same nominal size would give two marks of different weight.
    /// </summary>
    public Image Trim()
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (Pixels[(((y * Width) + x) * 4) + 3] <= 12) continue;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0) return this;

        int w = maxX - minX + 1, h = maxY - minY + 1;
        byte[] cropped = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            Array.Copy(Pixels, (((y + minY) * Width) + minX) * 4, cropped, y * w * 4, w * 4);
        }

        return new Image(w, h, cropped);
    }

    /// <summary>
    /// Scales the mark to fill <paramref name="fill"/> of the box's height,
    /// keeping its proportions, and centres it on a transparent canvas of the
    /// given size. A fill of 1 is edge to edge, which is what every small icon
    /// wants: at 16 px a margin is most of the icon.
    /// </summary>
    public Image Boxed(int boxWidth, int boxHeight, double fill = 1.0)
    {
        // Edge to edge means edge to edge. The masters are some 3 % taller
        // than wide, and keeping that would leave a column of the icon empty —
        // at 24 px a spare column is a twenty-fourth of the mark, and far more
        // visible than three per cent of squash on a rounded square.
        if (fill >= 1.0) return Resample(boxWidth, boxHeight);

        int height = (int)Math.Round(boxHeight * fill);
        int width = (int)Math.Round(height * (double)Width / Height);
        if (width > boxWidth)
        {
            width = boxWidth;
            height = (int)Math.Round(width * (double)Height / Width);
        }

        Image scaled = Resample(width, height);
        if (width == boxWidth && height == boxHeight) return scaled;

        byte[] canvas = new byte[boxWidth * boxHeight * 4];
        int left = (boxWidth - width) / 2, top = (boxHeight - height) / 2;
        for (int y = 0; y < height; y++)
        {
            Array.Copy(scaled.Pixels, y * width * 4, canvas, (((y + top) * boxWidth) + left) * 4, width * 4);
        }

        return new Image(boxWidth, boxHeight, canvas);
    }

    /// <summary>
    /// Area average in premultiplied alpha. Every target here is far smaller
    /// than the master, so averaging the whole source rectangle beats any
    /// interpolation — and weighting the colour by alpha is what keeps the
    /// colour hiding under the transparent pixels out of the edges.
    /// </summary>
    private Image Resample(int width, int height)
    {
        byte[] output = new byte[width * height * 4];
        double sx = (double)Width / width, sy = (double)Height / height;

        for (int y = 0; y < height; y++)
        {
            double top = y * sy, bottom = (y + 1) * sy;
            int y0 = (int)Math.Floor(top), y1 = Math.Min((int)Math.Ceiling(bottom), Height);

            for (int x = 0; x < width; x++)
            {
                double left = x * sx, right = (x + 1) * sx;
                int x0 = (int)Math.Floor(left), x1 = Math.Min((int)Math.Ceiling(right), Width);

                double weight = 0, alpha = 0, b = 0, g = 0, r = 0;
                for (int j = y0; j < y1; j++)
                {
                    double wy = Math.Min(bottom, j + 1) - Math.Max(top, j);
                    for (int i = x0; i < x1; i++)
                    {
                        double w = wy * (Math.Min(right, i + 1) - Math.Max(left, i));
                        if (w <= 0) continue;

                        int p = (((j * Width) + i) * 4);
                        double a = Pixels[p + 3];
                        weight += w;
                        alpha += w * a;
                        b += w * a * Pixels[p];
                        g += w * a * Pixels[p + 1];
                        r += w * a * Pixels[p + 2];
                    }
                }

                int o = (((y * width) + x) * 4);
                if (weight <= 0 || alpha <= 0) continue;

                output[o] = Round(b / alpha);
                output[o + 1] = Round(g / alpha);
                output[o + 2] = Round(r / alpha);
                output[o + 3] = Round(alpha / weight);
            }
        }

        return new Image(width, height, output);
    }

    private static byte Round(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}

/// <summary>
/// Writes a multi-size .ico by hand. Every image goes in as a 32-bit DIB with
/// its AND mask rather than as an embedded PNG: the taskbar, Explorer and the
/// older tooling all read that without complaint, and some of them read the
/// PNG form badly or not at all.
/// </summary>
static class Icon
{
    public static void Write(string path, IReadOnlyList<Image> images)
    {
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);

        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type: icon
        w.Write((ushort)images.Count);

        byte[][] bodies = [.. images.Select(Dib)];
        int offset = 6 + (16 * images.Count);
        for (int i = 0; i < images.Count; i++)
        {
            // 256 is written as 0 — the field is one byte wide.
            w.Write((byte)(images[i].Width == 256 ? 0 : images[i].Width));
            w.Write((byte)(images[i].Height == 256 ? 0 : images[i].Height));
            w.Write((byte)0);          // palette entries
            w.Write((byte)0);          // reserved
            w.Write((ushort)1);        // colour planes
            w.Write((ushort)32);       // bits per pixel
            w.Write(bodies[i].Length);
            w.Write(offset);
            offset += bodies[i].Length;
        }

        foreach (byte[] body in bodies)
        {
            w.Write(body);
        }
    }

    private static byte[] Dib(Image image)
    {
        int w = image.Width, h = image.Height;
        int maskStride = (((w + 31) / 32) * 4);   // the mask rows are padded to 4 bytes

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write(40);                          // BITMAPINFOHEADER size
        writer.Write(w);
        writer.Write(h * 2);                       // colour rows plus mask rows
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);                           // BI_RGB
        writer.Write((w * h * 4) + (maskStride * h));
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        // Bottom-up, like every DIB.
        for (int y = h - 1; y >= 0; y--)
        {
            writer.Write(image.Pixels, y * w * 4, w * 4);
        }

        // The AND mask is ignored by anything that honours the alpha channel,
        // but it has to be there and it has to be right for the ones that do
        // not — an all-zero mask makes a fully transparent icon show as a
        // black square in the places that still read it.
        byte[] row = new byte[maskStride];
        for (int y = h - 1; y >= 0; y--)
        {
            Array.Clear(row);
            for (int x = 0; x < w; x++)
            {
                if (image.Pixels[(((y * w) + x) * 4) + 3] >= 128) continue;

                row[x / 8] |= (byte)(0x80 >> (x % 8));
            }

            writer.Write(row);
        }

        return buffer.ToArray();
    }
}
