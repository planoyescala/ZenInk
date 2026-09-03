//-----------------------------------------------------------------------------------------
// <copyright file="TileKey.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

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
///
/// <paramref name="Document"/> is which open document the page belongs to, and
/// it is what keeps a cache honest once sheets can be brought in from another
/// file — page 3 of one drawing is not page 3 of another. It is the document's
/// own number rather than a position in a list, because positions get
/// renumbered and a stale tile drawn under a renumbered one is the wrong sheet
/// on screen with nothing to say so.
/// </summary>
public readonly record struct TileKey(int PageIndex, int Level, int Col, int Row, int Rotation = 0, int Document = 0);
