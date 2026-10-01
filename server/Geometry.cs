#nullable enable
using Maps.Graphics;
using Maps.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

// Portable map geometry (sector clip paths, border paths, hex edges), used by both the
// data model and rendering. No Windows-only System.Drawing types.

namespace Maps.Graphics
{
    internal class AbstractPath
    {
        public PointF[] Points { get; set; }
        public byte[] Types { get; set; }

        public AbstractPath(PointF[] points, byte[] types)
        {
            Points = points;
            Types = types;
        }
    }

    /// <summary>
    /// Point type flags for AbstractPath.Types; same values as GDI+ PathPointType.
    /// </summary>
    internal static class PathPointTypes
    {
        public const byte Start = 0;
        public const byte Line = 1;
        public const byte Bezier = 3;
        public const byte PathTypeMask = 0x07;
        public const byte CloseSubpath = 0x80;
    }
}

namespace Maps.Rendering
{
    // BorderPath is a render-ready representation of a Border.
    // It contains three separate chunks of data:
    // * Pair of (points, types) arrays for "straight" borders to draw/clip
    // * List of curves (open or closed plus array of control points) for "curved" borders
    // Straight borders are always complete closed polygons, since they are rendered
    // clipped against the sector hex bounds.
    // Curved borders must be segmented since simple clipping against sector bounds
    // is insufficient (they weave against the hexes).
    internal class BorderPath
    {
        public class CurveSegment
        {
            public CurveSegment(IEnumerable<PointF> points, bool closed)
            {
                this.points = points.ToArray();
                this.closed = closed;
            }
            public readonly PointF[] points;
            public readonly bool closed;
        }

        public readonly PointF[] points;
        public readonly byte[] types;
        public readonly List<CurveSegment> curves;

        public BorderPath(Border border, Sector sector, PathUtil.PathType type)
        {
            PathUtil.HexEdges(type, out float[] edgeX, out float[] edgeY);

            int lengthEstimate = border.Path.Count() * 3;

            List<PointF> points = new List<PointF>(lengthEstimate);
            List<byte> types = new List<byte>(lengthEstimate);
            LinkedList<LinkedList<PointF>> segments = new LinkedList<LinkedList<PointF>>();
            LinkedList<PointF> currentSegment = new LinkedList<PointF>();

            // Based on http://dotclue.org/t20/sec2pdf - J Greely rocks my world.

            int checkFirst = 0;
            int checkLast = 5;

            Hex startHex = Hex.Empty;
            bool startHexVisited = false;

            foreach (Hex hex in border.Path)
            {
                checkLast = checkFirst + 5;

                if (startHexVisited && hex == startHex)
                {
                    // I'm in the starting hex, and I've been
                    // there before, so stop testing at neighbor
                    // 5, no matter what
                    checkLast = 5;

                    // degenerate case, entering for third time
                    if (checkFirst < 3)
                        break;
                }
                else if (!startHexVisited)
                {
                    startHex = hex;
                    startHexVisited = true;

                    // PERF: This seems costly... analyze it!
                    PointF newPoint = Astrometrics.HexToCenter(Astrometrics.LocationToCoordinates(new Location(sector.Location, hex)));
                    newPoint.X += edgeX[0];
                    newPoint.Y += edgeY[0];

                    // MOVETO
                    points.Add(newPoint);
                    types.Add(PathPointTypes.Start);

                    // MOVETO
                    currentSegment.AddLast(newPoint);
                }

                PointF pt = Astrometrics.HexToCenter(Astrometrics.LocationToCoordinates(new Location(sector.Location, hex)));

                int i = checkFirst;
                for (int check = checkFirst; check <= checkLast; check++)
                {
                    i = check;
                    Hex neighbor = Astrometrics.HexNeighbor(hex, i % 6);

                    if (border.Path.Contains(neighbor)) // TODO: Consider a hash here
                        break;

                    PointF newPoint = new PointF(pt.X + edgeX[(i + 1) % 6], pt.Y + edgeY[(i + 1) % 6]);

                    // LINETO
                    points.Add(newPoint);
                    types.Add(PathPointTypes.Line);

                    if (hex.IsValid)
                    {
                        // MOVETO
                        currentSegment.AddLast(newPoint);
                    }
                    else
                    {
                        // LINETO
                        if (currentSegment.Count > 1)
                            segments.AddLast(currentSegment);
                        currentSegment = new LinkedList<PointF>();
                        currentSegment.AddLast(newPoint);
                    }

                }
                i %= 6;
                // i is the direction to the next border hex,
                // and when we get there, we'll have come from
                // i + 3, so we start checking with i + 4.
                checkFirst = (i + 4) % 6;
            }

            if (points.First() == points.Last())
            {
                int c = points.Count;
                points.RemoveAt(c - 1);
                types.RemoveAt(c - 1);
            }

            types[types.Count - 1] |= PathPointTypes.CloseSubpath;

            if (currentSegment.Count > 1)
                segments.AddLast(currentSegment);

            this.points = points.ToArray();
            this.types = types.ToArray();

            // If last curve segment connects to first curve segment, merge them.
            // Example: Imperial border in Verge.
            if (segments.Count >= 2 && segments.First().First() == segments.Last().Last())
            {
                var first = segments.First();
                var last = segments.Last();
                segments.RemoveFirst();
                first.RemoveFirst();
                foreach (var point in first)
                    last.AddLast(point);
            }

            curves = segments.Select(c =>
            {
                if (c.First() == c.Last())
                {
                    c.RemoveLast();
                    return new CurveSegment(c, true);
                }
                else
                {
                    return new CurveSegment(c, false);
                }
            }).ToList();
        }
    }

    internal static class PathUtil
    {
        /*
         * Hex edge offset: the horizontal distance a hex vertex extends past the
         * cell boundary, in parsecs (hexes are 1 parsec wide, scaled by ParsecScaleX).
         */
        public static readonly float HEX_EDGE = (float)(Math.Tan(Math.PI / 6) / 4 / Astrometrics.ParsecScaleX);

        private static readonly float[] HexEdgesX = { -0.5f + HEX_EDGE, -0.5f - HEX_EDGE, -0.5f + HEX_EDGE, 0.5f - HEX_EDGE, 0.5f + HEX_EDGE, 0.5f - HEX_EDGE };
        private static readonly float[] HexEdgesY = { 0.5f, 0f, -0.5f, -0.5f, 0, 0.5f };

        private static readonly float[] SquareEdgesX = { -0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f };
        private static readonly float[] SquareEdgesY = { 0.5f, 0f, -0.5f, -0.5f, 0, 0.5f };

        public static void HexEdges(PathType type, out float[] edgeX, out float[] edgeY)
        {
            edgeX = (type == PathType.Hex) ? HexEdgesX : SquareEdgesX;
            edgeY = (type == PathType.Hex) ? HexEdgesY : SquareEdgesY;
        }

        public enum PathType : int
        {
            Hex = 0,
            Square = 1,
            TypeCount = 2
        };

        public static RectangleF Bounds(AbstractPath path)
        {
            RectangleF rect = new RectangleF();

            PointF[] points = path.Points;

            rect.X = points[0].X;
            rect.Y = points[0].Y;

            for (int i = 1; i < points.Length; ++i)
            {
                PointF pt = points[i];
                if (pt.X < rect.X)
                {
                    float d = rect.X - pt.X;
                    rect.X = pt.X;
                    rect.Width += d;
                }
                if (pt.Y < rect.Y)
                {
                    float d = rect.Y - pt.Y;
                    rect.Y = pt.Y;
                    rect.Height += d;
                }

                if (pt.X > rect.Right)
                    rect.Width = pt.X - rect.X;
                if (pt.Y > rect.Bottom)
                    rect.Height = pt.Y - rect.Y;
            }

            return rect;
        }

        public static void ComputeBorderPath(IEnumerable<Point> clip, float[] edgeX, float[] edgeY, out PointF[] clipPathPointCoords, out byte[] clipPathPointTypes)
        {
            // TODO: Consolidate this with border path generation (which is very sector/hex-centric, alas)

            List<PointF> clipPathPoints = new List<PointF>(clip.Count() * 3);
            List<byte> clipPathTypes = new List<byte>(clip.Count() * 3);

            // Algorithm based on http://dotclue.org/t20/sec2pdf - J Greely rocks my world.

            int checkFirst = 0;
            Point startHex = Point.Empty;
            bool startHexVisited = false;

            foreach (Point hex in clip)
            {
                int checkLast = checkFirst + 5;
                if (startHexVisited && hex == startHex)
                {
                    // I'm in the starting hex, and I've been
                    // there before, so stop testing at neighbor
                    // 5, no matter what
                    checkLast = 5;

                    // degenerate case, entering for third time
                    if (checkFirst < 3)
                        break;
                }
                else if (!startHexVisited)
                {
                    startHex = hex;
                    startHexVisited = true;

                    // PERF: This seems costly... analyze it!
                    PointF newPoint = Astrometrics.HexToCenter(hex);
                    newPoint.X += edgeX[0];
                    newPoint.Y += edgeY[0];

                    // MOVETO
                    clipPathPoints.Add(newPoint);
                    clipPathTypes.Add(PathPointTypes.Start);
                }

                PointF pt = Astrometrics.HexToCenter(hex);

                int i = checkFirst;
                for (int check = checkFirst; check <= checkLast; check++)
                {
                    i = check;
                    Point neighbor = Astrometrics.HexNeighbor(hex, i % 6);

                    if (clip.Contains(neighbor))
                        break;

                    PointF newPoint = new PointF(pt.X + edgeX[(i + 1) % 6], pt.Y + edgeY[(i + 1) % 6]);

                    // LINETO
                    clipPathPoints.Add(newPoint);
                    clipPathTypes.Add(PathPointTypes.Line);
                }
                i %= 6;

                // i is the direction to the next border hex,
                // and when we get there, we'll have come from
                // i + 3, so we start checking with i + 4.
                checkFirst = (i + 4) % 6;
            }

            clipPathPointCoords = clipPathPoints.ToArray();
            clipPathPointTypes = clipPathTypes.ToArray();
            clipPathPointTypes[clipPathPointTypes.Length - 1] |= PathPointTypes.CloseSubpath;
        }
    }
    internal class ClipPath
    {
        public readonly PointF[] clipPathPoints;
        public readonly byte[] clipPathPointTypes;
        public readonly RectangleF bounds;

        public ClipPath(Rectangle bounds, PathUtil.PathType borderPathType)
        {
            PathUtil.HexEdges(borderPathType, out float[] edgex, out float[] edgey);

            IEnumerable<Hex> hexes =
                Util.Sequence(1, Astrometrics.SectorWidth).Select(x => new Hex((byte)x, 1))
                .Concat(Util.Sequence(2, Astrometrics.SectorHeight).Select(y => new Hex(Astrometrics.SectorWidth, (byte)y)))
                .Concat(Util.Sequence(Astrometrics.SectorWidth - 1, 1).Select(x => new Hex((byte)x, Astrometrics.SectorHeight)))
                .Concat(Util.Sequence(Astrometrics.SectorHeight - 1, 1).Select(y => new Hex(1, (byte)y)));

            IEnumerable<Point> points = (from hex in hexes select new Point(hex.X + bounds.X, hex.Y + bounds.Y)).ToList();
            PathUtil.ComputeBorderPath(points, edgex, edgey, out clipPathPoints, out clipPathPointTypes);

            PointF min = clipPathPoints[0];
            PointF max = clipPathPoints[0];
            for (int i = 1; i < clipPathPoints.Length; ++i)
            {
                PointF pt = clipPathPoints[i];
                if (pt.X < min.X)
                    min.X = pt.X;
                if (pt.Y < min.Y)
                    min.Y = pt.Y;
                if (pt.X > max.X)
                    max.X = pt.X;
                if (pt.Y > max.Y)
                    max.Y = pt.Y;
            }
            this.bounds = new RectangleF(min, new SizeF(max.X - min.X, max.Y - min.Y));
        }
    }
}
