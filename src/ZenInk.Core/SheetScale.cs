using System.Globalization;
using System.Numerics;

namespace ZenInk.Core;

/// <summary>
/// The units a drawing is measured in. Metric first because that is what the
/// drawings are in, with feet and inches kept for a set that arrives from
/// somewhere that works in them.
/// </summary>
public enum MeasureUnit
{
    Millimetre,
    Centimetre,
    Metre,
    Kilometre,
    Inch,
    Foot,
}

/// <summary>
/// What one PDF point of a sheet is worth in the real world.
///
/// One number, not two: a drawing is plotted to a single scale, and a plot
/// stretched differently along each axis is a broken plot rather than something
/// to measure. That is also what makes this survive the reader turning the
/// sheet — the marks are kept in sheet space, where a turn is only a way of
/// looking, and a scalar has no direction to turn.
///
/// It belongs to a sheet and travels with it: a sheet brought in from another
/// drawing arrives with its own scale or with none, never with its neighbour's.
/// </summary>
public readonly record struct SheetScale(double UnitsPerPoint, MeasureUnit Unit)
{
    /// <summary>
    /// Shorter than this, a calibration drag is a click with a wobble in it.
    /// Ten points is about three millimetres of paper, and calibrating off
    /// three millimetres would put the error of the drag into every measurement
    /// on the sheet.
    /// </summary>
    public const float ShortestCalibrationPt = 10f;

    private const double MillimetresPerPoint = 25.4 / 72.0;

    /// <summary>
    /// The scale a drag over a known distance gives. Null when there is nothing
    /// to divide by — too short a drag, or a length that is not a length.
    /// </summary>
    public static SheetScale? From(double paperPt, double realLength, MeasureUnit unit) =>
        paperPt >= ShortestCalibrationPt && realLength > 0 && double.IsFinite(realLength)
            ? new SheetScale(realLength / paperPt, unit)
            : null;

    /// <summary>
    /// The scale from a drawing ratio, for the usual case: the reader knows the
    /// sheet is 1:100 and there is nothing to measure to find that out.
    /// </summary>
    public static SheetScale? FromRatio(double ratio, MeasureUnit unit) =>
        ratio > 0 && double.IsFinite(ratio)
            ? new SheetScale(ratio * MillimetresPerPoint / MillimetresPer(unit), unit)
            : null;

    /// <summary>The real length of a distance on the paper.</summary>
    public double Length(double paperPt) => paperPt * UnitsPerPoint;

    /// <summary>The real area of an area on the paper. Two lengths, so the scale goes in twice.</summary>
    public double Area(double paperPtSquared) => paperPtSquared * UnitsPerPoint * UnitsPerPoint;

    /// <summary>
    /// The drawing's ratio: how many millimetres of building one millimetre of
    /// paper stands for. What the print dialog needs to be able to offer
    /// 1:100, and what a reader recognises a drawing by.
    /// </summary>
    public double Ratio => UnitsPerPoint * MillimetresPer(Unit) / MillimetresPerPoint;

    /// <summary>The ratio as it is written on a drawing, rounded to something a reader would recognise.</summary>
    public string RatioLabel
    {
        get
        {
            double ratio = Ratio;
            if (!double.IsFinite(ratio) || ratio <= 0) return "—";

            // Under one is a drawing enlarged rather than reduced — a detail at
            // 2:1 — and it is written the other way round.
            return ratio < 1
                ? $"{Round(1.0 / ratio).ToString("0.##", CultureInfo.CurrentCulture)}:1"
                : $"1:{Round(ratio).ToString("0.##", CultureInfo.CurrentCulture)}";
        }
    }

    public string Suffix => Unit switch
    {
        MeasureUnit.Millimetre => "mm",
        MeasureUnit.Centimetre => "cm",
        MeasureUnit.Metre => "m",
        MeasureUnit.Kilometre => "km",
        MeasureUnit.Inch => "in",
        _ => "ft",
    };

    /// <summary>A length as the reader reads it, in the sheet's own unit.</summary>
    public string FormatLength(double paperPt, IFormatProvider? culture = null) =>
        Length(paperPt).ToString(Places(Unit), culture ?? CultureInfo.CurrentCulture) + " " + Suffix;

    /// <summary>An area, in the square of the sheet's own unit.</summary>
    public string FormatArea(double paperPtSquared, IFormatProvider? culture = null) =>
        Area(paperPtSquared).ToString(Places(Unit), culture ?? CultureInfo.CurrentCulture) + " " + Suffix + "²";

    /// <summary>An angle. It has no scale in it — the paper and the building agree about angles.</summary>
    public static string FormatAngle(double degrees, IFormatProvider? culture = null) =>
        degrees.ToString("0.#", culture ?? CultureInfo.CurrentCulture) + "°";

    /// <summary>
    /// How many decimals a unit is written to. Millimetres of building do not
    /// need a fraction; metres do, or every room comes out the same size.
    /// </summary>
    private static string Places(MeasureUnit unit) => unit switch
    {
        MeasureUnit.Millimetre => "0",
        MeasureUnit.Centimetre => "0.#",
        MeasureUnit.Kilometre => "0.###",
        MeasureUnit.Inch => "0.#",
        _ => "0.##",
    };

    private static double MillimetresPer(MeasureUnit unit) => unit switch
    {
        MeasureUnit.Millimetre => 1.0,
        MeasureUnit.Centimetre => 10.0,
        MeasureUnit.Metre => 1000.0,
        MeasureUnit.Kilometre => 1_000_000.0,
        MeasureUnit.Inch => 25.4,
        _ => 304.8,
    };

    /// <summary>
    /// Pulls a ratio onto the round number it is obviously meant to be. A
    /// calibration drag over a scale bar lands on 99,4 rather than 100, and a
    /// drawing labelled 1:99,4 reads as an error in the program.
    /// </summary>
    private static double Round(double ratio)
    {
        double[] usual = [1, 2, 2.5, 5, 10, 20, 25, 50, 100, 125, 200, 250, 500, 1000, 1250, 2000, 2500, 5000, 10000];

        foreach (double near in usual)
        {
            if (Math.Abs(ratio - near) <= near * 0.02) return near;
        }

        return ratio >= 100 ? Math.Round(ratio) : Math.Round(ratio, 1);
    }
}

/// <summary>
/// What a measurement says. The number is worked out from the mark's own points
/// and the sheet's scale and never stored apart from them, so a measurement
/// cannot come to disagree with the line it is drawn on.
/// </summary>
public static class Measures
{
    /// <summary>The kinds whose whole point is the number they carry.</summary>
    public static bool Is(AnnotationKind kind) =>
        kind is AnnotationKind.Distance or AnnotationKind.Perimeter
            or AnnotationKind.Area or AnnotationKind.Angle;

    /// <summary>
    /// The label a measurement reads. Empty when the sheet has no scale: a
    /// number without a calibration behind it would be a guess with a unit
    /// stuck on it.
    /// </summary>
    public static string Label(AnnotationKind kind, IReadOnlyList<Vector2> points, SheetScale? scale)
    {
        if (kind == AnnotationKind.Angle)
        {
            return points.Count < 3 ? "" : SheetScale.FormatAngle(AnnotationGeometry.CornerAngle(points[0], points[1], points[2]));
        }

        if (scale is not { } sheet || points.Count < 2) return "";

        return kind switch
        {
            AnnotationKind.Distance => sheet.FormatLength(AnnotationGeometry.TotalLength(points)),

            // Around the shape and back to the start, which is what makes it a
            // perimeter rather than the length of a run of segments.
            AnnotationKind.Perimeter => sheet.FormatLength(AnnotationGeometry.TotalLength(points, closed: true)),

            AnnotationKind.Area => sheet.FormatArea(AnnotationGeometry.PolygonArea(points)),

            _ => "",
        };
    }

    /// <summary>
    /// Where the number sits: beside the middle of what was measured, and
    /// inside it for an area. Worked out here so that the canvas, the paper and
    /// the file all put it in the same place.
    /// </summary>
    public static Vector2 LabelAnchor(AnnotationKind kind, IReadOnlyList<Vector2> points)
    {
        if (points.Count == 0) return Vector2.Zero;
        if (points.Count == 1) return points[0];

        return kind switch
        {
            // Halfway along the line it measures.
            AnnotationKind.Distance => (points[0] + points[^1]) / 2f,

            // At the corner it is about, which is the only place an angle can
            // be read without counting arms.
            AnnotationKind.Angle => points.Count >= 3 ? points[1] : points[0],

            _ => AnnotationGeometry.Centre(points),
        };
    }
}
