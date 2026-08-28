namespace ZenInk.Core;

/// <summary>
/// Identifies a single tile: a fixed-size square of a page, rasterized at a
/// discrete zoom level. Levels are half-octave buckets (see <see cref="ZoomLevels"/>)
/// so nearby zoom values reuse the same cached tiles.
///
/// <paramref name="Rotation"/> is the viewer's own quarter-turn on top of the
/// page's /Rotate, and belongs in the key: a rotated tile is a different
/// raster, and keeping both means turning a sheet back is instant rather than
/// a re-render.
/// </summary>
public readonly record struct TileKey(int PageIndex, int Level, int Col, int Row, int Rotation = 0);
