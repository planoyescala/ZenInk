namespace ZenInk.Core;

/// <summary>
/// Maps a continuous render scale (device pixels per PDF point) to a discrete
/// level so tiles can be cached and reused across small zoom changes, the way
/// map tile servers quantize zoom. Levels are half-octaves: each step is a
/// factor of sqrt(2).
///
/// <see cref="LevelForScale"/> rounds *up* on purpose. A tile rasterized above
/// the on-screen scale gets downsampled when drawn, which stays crisp;
/// rounding to the nearest level would upsample up to 41% of the time, and
/// upsampling is exactly what reads as a blurry raster image.
/// </summary>
public static class ZoomLevels
{
    public const int TileSize = 512;

    /// <summary>Floor on how coarse a cached fallback level may get.</summary>
    public const int MinLevel = -12;

    public static int LevelForScale(double scale)
    {
        double clamped = Math.Max(scale, 1e-6);
        return (int)Math.Ceiling(Math.Log2(clamped) * 2.0);
    }

    public static double ScaleForLevel(int level) => Math.Pow(2.0, level / 2.0);
}
