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
using System.Drawing.Drawing2D;

// Looks at the PNGs the app takes of itself with RenderTargetBitmap. Reasoning
// about the interface from the code has failed more than once; these two answer
// questions the eye cannot at 100 %.
//
//   ampliar   crops a region and blows it up with nearest-neighbour, so an
//             18px icon can be judged. Two files stack for a before/after.
//   costuras  averages a strip and reports positions whose brightness jumps
//             away from their neighbours — a tile seam over a flat fill, in
//             numbers rather than impressions.

if (args.Length == 0)
{
    Console.Error.WriteLine("Uso: ampliar <png> [png2] <x> <y> <ancho> <alto> [factor]");
    Console.Error.WriteLine("     costuras <png> filas|columnas <desde> <grosor> [inicio] [fin]");
    return 1;
}

switch (args[0].ToLowerInvariant())
{
    case "ampliar": return Magnify(args[1..]);
    case "costuras": return Seams(args[1..]);
    default:
        Console.Error.WriteLine($"No conozco «{args[0]}».");
        return 1;
}

static int Magnify(string[] args)
{
    bool pair = args.Length >= 6 && !int.TryParse(args[1], out _);
    string first = args[0];
    string? second = pair ? args[1] : null;
    string[] rest = pair ? args[2..] : args[1..];

    var box = new Rectangle(
        int.Parse(rest[0]), int.Parse(rest[1]), int.Parse(rest[2]), int.Parse(rest[3]));
    int factor = rest.Length > 4 ? int.Parse(rest[4]) : 4;

    using var a = new Bitmap(first);
    using var sliceA = a.Clone(box, a.PixelFormat);
    using var sliceB = second is null ? null : Crop(second, box);

    int w = box.Width * factor, h = box.Height * factor;
    using var big = new Bitmap(w, sliceB is null ? h : h * 2 + 30);
    using (var g = Graphics.FromImage(big))
    {
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(sliceA, 0, 0, w, h);

        if (sliceB is not null)
        {
            g.DrawImage(sliceB, 0, h + 30, w, h);
            using var font = new Font("Segoe UI", 14);
            g.DrawString("ANTES ↑   DESPUÉS ↓", font, Brushes.Black, 6, h + 4);
        }
    }

    string output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(first))!, "ampliacion.png");
    big.Save(output);
    Console.WriteLine(output);
    return 0;
}

static Bitmap Crop(string path, Rectangle box)
{
    using var image = new Bitmap(path);
    return image.Clone(box, image.PixelFormat);
}

static int Seams(string[] args)
{
    string path = args[0];
    bool rows = args[1].StartsWith("fila", StringComparison.OrdinalIgnoreCase);
    int from = int.Parse(args[2]), thickness = int.Parse(args[3]);
    int start = args.Length > 4 ? int.Parse(args[4]) : 0;
    int end = args.Length > 5 ? int.Parse(args[5]) : int.MaxValue;

    using var image = new Bitmap(path);
    int length = rows ? image.Height : image.Width;
    end = Math.Min(end, length - 3);

    var line = new double[length];
    for (int i = 0; i < length; i++)
    {
        double sum = 0;
        for (int j = from; j < from + thickness; j++)
        {
            var p = rows ? image.GetPixel(j, i) : image.GetPixel(i, j);
            sum += (p.R + p.G + p.B) / 3.0;
        }
        line[i] = sum / thickness;
    }

    double worst = 0;
    int count = 0;
    for (int i = Math.Max(2, start); i < end; i++)
    {
        // Compared against neighbours two away, not one: a seam is often two
        // pixels wide and would hide behind its own smear otherwise.
        double neighbours = (line[i - 2] + line[i + 2]) / 2.0;
        double delta = Math.Abs(line[i] - neighbours);
        if (delta <= 6) continue;

        count++;
        worst = Math.Max(worst, delta);
        Console.WriteLine($"  {(rows ? "y" : "x")}={i,5}  {line[i],7:0.0} frente a {neighbours,7:0.0}   desvío {delta,6:0.0}");
    }

    Console.WriteLine($"{Path.GetFileName(path)}: {count} posiciones, desvío máximo {worst:0.0}");
    return 0;
}
