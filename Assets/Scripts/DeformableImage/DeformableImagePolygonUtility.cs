using System;
using System.Collections.Generic;
using Clipper2Lib;
using LibTessDotNet;
using UnityEngine;

namespace DeformableImage
{
    internal static class DeformableImagePolygonUtility
    {
        private const double CoordinateScale = 1000000.0;

        internal sealed class TriangulatedPolygon
        {
            public Vector2[] Vertices = Array.Empty<Vector2>();
            public int[] Triangles = Array.Empty<int>();
        }

        public static bool TryTriangulateClosedPath(
            IList<Vector2> input,
            out TriangulatedPolygon triangulation)
        {
            triangulation = null;
            if (input == null || input.Count < 3)
            {
                return false;
            }

            Path64 sourcePath = ToPath64(input);
            Paths64 normalized = Clipper.Union(new Paths64 { sourcePath }, FillRule.EvenOdd);
            Paths64 clipped = Clipper.Intersect(normalized, UnitSquare(), FillRule.EvenOdd);
            if (clipped.Count == 0)
            {
                return false;
            }

            return TryTriangulatePath64(clipped, out triangulation);
        }

        public static bool TryNormalizePaths(
            IList<Vector2[]> input,
            out List<Vector2[]> normalized)
        {
            normalized = new List<Vector2[]>();
            Paths64 source = ToPaths64(input);
            if (source.Count == 0)
            {
                return false;
            }

            Paths64 result = Clipper.Union(source, FillRule.EvenOdd);
            normalized = FromPaths64(result);
            return normalized.Count > 0;
        }

        public static bool TryBooleanDifference(
            IList<Vector2[]> subject,
            IList<Vector2> cutter,
            out List<Vector2[]> retained,
            out List<Vector2[]> removed)
        {
            retained = new List<Vector2[]>();
            removed = new List<Vector2[]>();
            Paths64 subjectPaths = Clipper.Union(ToPaths64(subject), FillRule.EvenOdd);
            if (subjectPaths.Count == 0 || cutter == null || cutter.Count < 3)
            {
                return false;
            }

            Paths64 cutterPaths = Clipper.Union(
                new Paths64 { ToPath64(cutter) },
                FillRule.EvenOdd);
            if (cutterPaths.Count == 0)
            {
                return false;
            }

            Paths64 retainedPaths = Clipper.Difference(
                subjectPaths,
                cutterPaths,
                FillRule.EvenOdd);
            Paths64 removedPaths = Clipper.Intersect(
                subjectPaths,
                cutterPaths,
                FillRule.EvenOdd);
            if (retainedPaths.Count == 0 || removedPaths.Count == 0)
            {
                return false;
            }

            retained = FromPaths64(retainedPaths);
            removed = FromPaths64(removedPaths);
            return retained.Count > 0 && removed.Count > 0;
        }

        public static bool TryBooleanDifference(
            IList<Vector2[]> subject,
            IList<Vector2[]> cutters,
            out List<Vector2[]> retained)
        {
            retained = new List<Vector2[]>();
            Paths64 subjectPaths = Clipper.Union(ToPaths64(subject), FillRule.EvenOdd);
            Paths64 cutterPaths = Clipper.Union(ToPaths64(cutters), FillRule.EvenOdd);
            if (subjectPaths.Count == 0 || cutterPaths.Count == 0)
            {
                return false;
            }

            Paths64 result = Clipper.Difference(
                subjectPaths,
                cutterPaths,
                FillRule.EvenOdd);
            retained = FromPaths64(result);
            return true;
        }

        public static bool TryTriangulatePaths(
            IList<Vector2[]> paths,
            out TriangulatedPolygon triangulation)
        {
            triangulation = null;
            Paths64 normalized = Clipper.Union(ToPaths64(paths), FillRule.EvenOdd);
            return normalized.Count > 0
                && TryTriangulatePath64(normalized, out triangulation);
        }

        public static TriangulatedPolygon SubdivideTriangulation(
            TriangulatedPolygon source,
            int subdivisionLevels)
        {
            if (source == null || subdivisionLevels <= 0)
            {
                return source;
            }

            Vector2[] vertices = source.Vertices;
            int[] triangles = source.Triangles;
            int levels = Mathf.Clamp(subdivisionLevels, 0, 3);
            for (int level = 0; level < levels; level++)
            {
                List<Vector2> refinedVertices = new List<Vector2>(vertices);
                List<int> refinedTriangles = new List<int>(triangles.Length * 4);
                Dictionary<ulong, int> midpointLookup = new Dictionary<ulong, int>();
                for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
                {
                    int a = triangles[triangle];
                    int b = triangles[triangle + 1];
                    int c = triangles[triangle + 2];
                    int ab = GetOrCreateMidpoint(a, b, vertices, refinedVertices, midpointLookup);
                    int bc = GetOrCreateMidpoint(b, c, vertices, refinedVertices, midpointLookup);
                    int ca = GetOrCreateMidpoint(c, a, vertices, refinedVertices, midpointLookup);

                    refinedTriangles.Add(a);
                    refinedTriangles.Add(ab);
                    refinedTriangles.Add(ca);
                    refinedTriangles.Add(ab);
                    refinedTriangles.Add(b);
                    refinedTriangles.Add(bc);
                    refinedTriangles.Add(ca);
                    refinedTriangles.Add(bc);
                    refinedTriangles.Add(c);
                    refinedTriangles.Add(ab);
                    refinedTriangles.Add(bc);
                    refinedTriangles.Add(ca);
                }

                vertices = refinedVertices.ToArray();
                triangles = refinedTriangles.ToArray();
            }

            return new TriangulatedPolygon
            {
                Vertices = vertices,
                Triangles = triangles
            };
        }

        private static int GetOrCreateMidpoint(
            int first,
            int second,
            Vector2[] sourceVertices,
            ICollection<Vector2> destinationVertices,
            IDictionary<ulong, int> midpointLookup)
        {
            int minimum = Mathf.Min(first, second);
            int maximum = Mathf.Max(first, second);
            ulong key = ((ulong)(uint)minimum << 32) | (uint)maximum;
            if (midpointLookup.TryGetValue(key, out int midpoint))
            {
                return midpoint;
            }

            midpoint = destinationVertices.Count;
            destinationVertices.Add((sourceVertices[first] + sourceVertices[second]) * 0.5f);
            midpointLookup.Add(key, midpoint);
            return midpoint;
        }

        private static bool TryTriangulatePath64(
            Paths64 paths,
            out TriangulatedPolygon triangulation)
        {
            triangulation = null;

            Tess tess = new Tess();
            for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
            {
                Path64 path = paths[pathIndex];
                if (path.Count < 3)
                {
                    continue;
                }

                ContourVertex[] contour = new ContourVertex[path.Count];
                for (int pointIndex = 0; pointIndex < path.Count; pointIndex++)
                {
                    Vector2 point = ToVector2(path[pointIndex]);
                    contour[pointIndex].Position = new Vec3(point.x, point.y, 0f);
                }

                tess.AddContour(contour, ContourOrientation.Original);
            }

            tess.Tessellate(WindingRule.EvenOdd, ElementType.Polygons, 3);
            if (tess.ElementCount == 0 || tess.VertexCount == 0)
            {
                return false;
            }

            Vector2[] vertices = new Vector2[tess.VertexCount];
            for (int index = 0; index < vertices.Length; index++)
            {
                Vec3 position = tess.Vertices[index].Position;
                vertices[index] = new Vector2(position.X, position.Y);
            }

            List<int> triangles = new List<int>(tess.ElementCount * 3);
            for (int element = 0; element < tess.ElementCount; element++)
            {
                int offset = element * 3;
                int a = tess.Elements[offset];
                int b = tess.Elements[offset + 1];
                int c = tess.Elements[offset + 2];
                if (a >= 0 && b >= 0 && c >= 0)
                {
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                }
            }

            if (triangles.Count == 0)
            {
                return false;
            }

            triangulation = new TriangulatedPolygon
            {
                Vertices = vertices,
                Triangles = triangles.ToArray()
            };
            return true;
        }

        public static List<Vector2[]> BuildOpenCutPolygons(IList<Vector2> input, float normalizedWidth)
        {
            List<Vector2[]> result = new List<Vector2[]>();
            if (input == null || input.Count < 2 || normalizedWidth <= 0f)
            {
                return result;
            }

            Paths64 expanded = Clipper.InflatePaths(
                new Paths64 { ToPath64(input) },
                normalizedWidth * CoordinateScale * 0.5,
                JoinType.Round,
                EndType.Round);
            Paths64 clipped = Clipper.Intersect(expanded, UnitSquare(), FillRule.NonZero);

            for (int pathIndex = 0; pathIndex < clipped.Count; pathIndex++)
            {
                Path64 path = clipped[pathIndex];
                if (path.Count < 3)
                {
                    continue;
                }

                Vector2[] polygon = new Vector2[path.Count];
                for (int pointIndex = 0; pointIndex < path.Count; pointIndex++)
                {
                    polygon[pointIndex] = ToVector2(path[pointIndex]);
                }

                result.Add(polygon);
            }

            return result;
        }

        public static bool ContainsPoint(TriangulatedPolygon polygon, Vector2 point)
        {
            if (polygon == null)
            {
                return false;
            }

            for (int triangle = 0; triangle < polygon.Triangles.Length; triangle += 3)
            {
                Vector2 a = polygon.Vertices[polygon.Triangles[triangle]];
                Vector2 b = polygon.Vertices[polygon.Triangles[triangle + 1]];
                Vector2 c = polygon.Vertices[polygon.Triangles[triangle + 2]];
                if (PointInTriangle(point, a, b, c))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool ContainsPoint(IList<Vector2[]> polygons, Vector2 point)
        {
            for (int polygonIndex = 0; polygonIndex < polygons.Count; polygonIndex++)
            {
                if (PointInPolygon(point, polygons[polygonIndex]))
                {
                    return true;
                }
            }

            return false;
        }

        private static Path64 ToPath64(IList<Vector2> points)
        {
            Path64 path = new Path64(points.Count);
            for (int index = 0; index < points.Count; index++)
            {
                path.Add(new Point64(
                    points[index].x * CoordinateScale,
                    points[index].y * CoordinateScale));
            }

            return path;
        }

        private static Paths64 ToPaths64(IList<Vector2[]> paths)
        {
            Paths64 result = new Paths64(paths != null ? paths.Count : 0);
            if (paths == null)
            {
                return result;
            }

            for (int index = 0; index < paths.Count; index++)
            {
                Vector2[] path = paths[index];
                if (path != null && path.Length >= 3)
                {
                    result.Add(ToPath64(path));
                }
            }

            return result;
        }

        private static List<Vector2[]> FromPaths64(Paths64 paths)
        {
            List<Vector2[]> result = new List<Vector2[]>(paths.Count);
            for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
            {
                Path64 path = paths[pathIndex];
                if (path.Count < 3)
                {
                    continue;
                }

                Vector2[] points = new Vector2[path.Count];
                for (int pointIndex = 0; pointIndex < path.Count; pointIndex++)
                {
                    points[pointIndex] = ToVector2(path[pointIndex]);
                }

                result.Add(points);
            }

            return result;
        }

        private static Vector2 ToVector2(Point64 point)
        {
            return new Vector2(
                (float)(point.X / CoordinateScale),
                (float)(point.Y / CoordinateScale));
        }

        private static Paths64 UnitSquare()
        {
            return new Paths64
            {
                new Path64
                {
                    new Point64(0, 0),
                    new Point64(CoordinateScale, 0),
                    new Point64(CoordinateScale, CoordinateScale),
                    new Point64(0, CoordinateScale)
                }
            };
        }

        private static bool PointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
        {
            float area1 = Cross(b - a, point - a);
            float area2 = Cross(c - b, point - b);
            float area3 = Cross(a - c, point - c);
            bool hasNegative = area1 < -0.000001f || area2 < -0.000001f || area3 < -0.000001f;
            bool hasPositive = area1 > 0.000001f || area2 > 0.000001f || area3 > 0.000001f;
            return !(hasNegative && hasPositive);
        }

        private static bool PointInPolygon(Vector2 point, IList<Vector2> polygon)
        {
            bool inside = false;
            int previous = polygon.Count - 1;
            for (int current = 0; current < polygon.Count; current++)
            {
                Vector2 a = polygon[current];
                Vector2 b = polygon[previous];
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                {
                    inside = !inside;
                }

                previous = current;
            }

            return inside;
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }
    }
}
