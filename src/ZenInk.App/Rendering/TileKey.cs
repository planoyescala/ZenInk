namespace ZenInk_App.Rendering;

/// <summary>
/// Identifies a single tile: a fixed-size square of a page, rasterized at a
/// discrete zoom level. Levels are half-octave buckets (see <see cref="ZoomLevels"/>)
/// so nearby zoom values reuse the same cached tiles.
/// </summary>
public readonly record struct TileKey(int PageIndex, int Level, int Col, int Row);
