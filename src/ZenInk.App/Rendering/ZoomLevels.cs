namespace ZenInk_App.Rendering;

/// <summary>
/// Maps a continuous view scale (device px per PDF point) to a discrete render
/// level so tiles can be cached and reused across small zoom changes, the same
/// way map tile servers quantize zoom. Levels are half-octaves: each step is a
/// factor of sqrt(2), giving smoother fallback-to-sharp transitions than whole
/// octaves without exploding the number of cached buckets.
/// </summary>
public static class ZoomLevels
{
    public const int TileSize = 512;

    public static int LevelForScale(double scale)
    {
        double clamped = Math.Max(scale, 1e-6);
        return (int)Math.Round(Math.Log2(clamped) * 2.0);
    }

    public static double ScaleForLevel(int level) => Math.Pow(2.0, level / 2.0);
}
