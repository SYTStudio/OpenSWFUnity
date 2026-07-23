using System.Collections.Generic;
using UnityEngine;
using OpenSWFUnity.Runtime.Tags;

namespace OpenSWFUnity.Runtime.Renderer
{
    // Turns a fill group's flattened contours (polygon loops, possibly with
    // holes) into a triangle mesh in local shape space, so shapes render as
    // real GPU-rasterized vector geometry instead of a baked raster bitmap.
    internal static class SwfPolygonTriangulator
    {
        public static void Triangulate(
            List<SwfFillContour> contours,
            List<Vector2> outVertices,
            List<int> outTriangles,
            int characterId = 0,
            int groupIndex = 0
        )
        {
            outVertices.Clear();
            outTriangles.Clear();

            List<List<Vector2>> loops = new List<List<Vector2>>();

            for (int i = 0; i < contours.Count; i++)
            {
                SwfFillContour contour = contours[i];

                if (contour?.Points == null || contour.Points.Count < 3)
                    continue;

                loops.Add(contour.Points);
            }

            if (loops.Count == 0)
                return;

            int count = loops.Count;
            float[] area = new float[count];
            Vector2[] centers = new Vector2[count];
            int[] depth = new int[count];

            for (int i = 0; i < count; i++)
            {
                area[i] = Mathf.Abs(SignedArea(loops[i]));
                centers[i] = Centroid(loops[i]);
            }

            // Containment depth under the even-odd rule: a loop nested inside an
            // even number of others is solid fill, an odd number makes it a hole.
            // This handles arbitrary nesting (e.g. an island sitting inside a hole
            // inside an outer shape), not just a single hole level.
            for (int i = 0; i < count; i++)
            {
                int contained = 0;

                for (int j = 0; j < count; j++)
                {
                    if (i == j || area[j] <= area[i])
                        continue;

                    if (PointInPolygon(centers[i], loops[j]))
                        contained++;
                }

                depth[i] = contained;
            }

            for (int i = 0; i < count; i++)
            {
                if ((depth[i] & 1) != 0)
                    continue; // holes are merged into their owning solid below

                List<Vector2> outer = EnsureWinding(loops[i], true);

                for (int j = 0; j < count; j++)
                {
                    if ((depth[j] & 1) == 0 || !PointInPolygon(centers[j], loops[i]))
                        continue;

                    if (OwningSolidIndex(j, count, area, centers, loops, depth) != i)
                        continue;

                    List<Vector2> hole = EnsureWinding(loops[j], false);
                    outer = MergeHole(outer, hole);
                }

                EarClip(outer, outVertices, outTriangles, characterId, groupIndex);
            }
        }

        // The solid a hole cuts into is the smallest-area solid (even depth) loop
        // that still contains the hole; that keeps a hole from being punched out of
        // an ancestor when a nearer solid island is what actually owns it.
        private static int OwningSolidIndex(
            int hole,
            int count,
            float[] area,
            Vector2[] centers,
            List<List<Vector2>> loops,
            int[] depth
        )
        {
            int best = -1;
            float bestArea = float.MaxValue;

            for (int s = 0; s < count; s++)
            {
                if ((depth[s] & 1) != 0 || area[s] <= area[hole])
                    continue;

                if (!PointInPolygon(centers[hole], loops[s]))
                    continue;

                if (area[s] < bestArea)
                {
                    bestArea = area[s];
                    best = s;
                }
            }

            return best;
        }

        private static float SignedArea(List<Vector2> points)
        {
            float sum = 0f;

            for (int i = 0; i < points.Count; i++)
            {
                Vector2 a = points[i];
                Vector2 b = points[(i + 1) % points.Count];
                sum += a.x * b.y - b.x * a.y;
            }

            return sum * 0.5f;
        }

        private static Vector2 Centroid(List<Vector2> points)
        {
            Vector2 sum = Vector2.zero;

            for (int i = 0; i < points.Count; i++)
                sum += points[i];

            return sum / points.Count;
        }

        private static bool PointInPolygon(Vector2 point, List<Vector2> polygon)
        {
            bool inside = false;

            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 pi = polygon[i];
                Vector2 pj = polygon[j];

                bool intersects =
                    (pi.y > point.y) != (pj.y > point.y) &&
                    point.x < (pj.x - pi.x) * (point.y - pi.y) / (pj.y - pi.y + 1e-6f) + pi.x;

                if (intersects)
                    inside = !inside;
            }

            return inside;
        }

        private static List<Vector2> EnsureWinding(List<Vector2> points, bool positiveArea)
        {
            List<Vector2> result = new List<Vector2>(points);

            if (result.Count > 1 && Vector2.Distance(result[0], result[result.Count - 1]) < 0.01f)
                result.RemoveAt(result.Count - 1);

            bool isPositive = SignedArea(result) > 0f;

            if (isPositive != positiveArea)
                result.Reverse();

            return result;
        }

        // Standard "keyhole" bridge: connects the hole into the outer loop via
        // a zero-area double edge so a single ear-clip pass can consume both.
        private static List<Vector2> MergeHole(List<Vector2> outer, List<Vector2> hole)
        {
            if (hole.Count < 3)
                return outer;

            int holeStart = 0;
            float maxX = hole[0].x;

            for (int i = 1; i < hole.Count; i++)
            {
                if (hole[i].x > maxX)
                {
                    maxX = hole[i].x;
                    holeStart = i;
                }
            }

            Vector2 holePoint = hole[holeStart];
            int bestOuterIndex = 0;
            float bestDistanceSq = float.MaxValue;
            bool foundClearBridge = false;

            for (int i = 0; i < outer.Count; i++)
            {
                Vector2 candidate = outer[i];
                float distanceSq = (candidate - holePoint).sqrMagnitude;

                if (distanceSq >= bestDistanceSq)
                    continue;

                if (SegmentCrossesPolygon(holePoint, candidate, outer) ||
                    SegmentCrossesPolygon(holePoint, candidate, hole))
                {
                    continue;
                }

                bestDistanceSq = distanceSq;
                bestOuterIndex = i;
                foundClearBridge = true;
            }

            if (!foundClearBridge)
            {
                // Fall back to the nearest vertex even if the bridge may graze
                // an edge; a rare cosmetic seam beats dropping the hole entirely.
                bestDistanceSq = float.MaxValue;

                for (int i = 0; i < outer.Count; i++)
                {
                    float distanceSq = (outer[i] - holePoint).sqrMagnitude;

                    if (distanceSq < bestDistanceSq)
                    {
                        bestDistanceSq = distanceSq;
                        bestOuterIndex = i;
                    }
                }
            }

            List<Vector2> merged = new List<Vector2>(outer.Count + hole.Count + 2);

            for (int i = 0; i <= bestOuterIndex; i++)
                merged.Add(outer[i]);

            for (int i = 0; i < hole.Count; i++)
                merged.Add(hole[(holeStart + i) % hole.Count]);

            merged.Add(hole[holeStart]);
            merged.Add(outer[bestOuterIndex]);

            for (int i = bestOuterIndex + 1; i < outer.Count; i++)
                merged.Add(outer[i]);

            return merged;
        }

        private static bool SegmentCrossesPolygon(Vector2 a, Vector2 b, List<Vector2> polygon)
        {
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 c = polygon[i];
                Vector2 d = polygon[(i + 1) % polygon.Count];

                if (c == a || c == b || d == a || d == b)
                    continue;

                if (SegmentsIntersect(a, b, c, d))
                    return true;
            }

            return false;
        }

        private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            float d1 = Cross(p4 - p3, p1 - p3);
            float d2 = Cross(p4 - p3, p2 - p3);
            float d3 = Cross(p2 - p1, p3 - p1);
            float d4 = Cross(p2 - p1, p4 - p1);

            return (d1 > 0f && d2 < 0f || d1 < 0f && d2 > 0f) &&
                   (d3 > 0f && d4 < 0f || d3 < 0f && d4 > 0f);
        }

        // Real vector art (a full game, not a tiny demo) can produce contours
        // with thousands of points. Naive ear clipping re-tests every remaining
        // vertex against every ear candidate (O(n^3) worst case) which is what
        // froze the Editor. Two fixes: (1) only reflex vertices can ever lie
        // inside a candidate ear - a standard ear-clipping theorem - so we test
        // against a much smaller reflex-only list instead of all vertices, and
        // (2) a hard iteration ceiling independent of n so runtime is always
        // bounded; any leftover un-clipped remainder is closed with a cheap
        // fan so the shape never just disappears.
        private const int MaxEarClipIterations = 20000;

        private static void EarClip(
            List<Vector2> polygon,
            List<Vector2> outVertices,
            List<int> outTriangles,
            int characterId,
            int groupIndex
        )
        {
            int n = polygon.Count;

            if (n < 3)
                return;

            int baseIndex = outVertices.Count;
            outVertices.AddRange(polygon);

            List<int> indices = new List<int>(n);

            for (int i = 0; i < n; i++)
                indices.Add(i);

            // Force a consistent winding for the convexity test below.
            if (SignedArea(polygon) < 0f)
                indices.Reverse();

            List<int> reflex = new List<int>();
            RebuildReflexList(polygon, indices, reflex);

            int guard = 0;

            while (indices.Count > 3 && guard++ < MaxEarClipIterations)
            {
                bool clipped = false;

                for (int i = 0; i < indices.Count; i++)
                {
                    int iPrev = indices[(i - 1 + indices.Count) % indices.Count];
                    int iCurr = indices[i];
                    int iNext = indices[(i + 1) % indices.Count];

                    Vector2 a = polygon[iPrev];
                    Vector2 b = polygon[iCurr];
                    Vector2 c = polygon[iNext];

                    if (Cross(b - a, c - b) <= 0f)
                        continue; // reflex vertex, not an ear

                    bool anyInside = false;

                    for (int k = 0; k < reflex.Count; k++)
                    {
                        int idx = reflex[k];

                        if (idx == iPrev || idx == iCurr || idx == iNext)
                            continue;

                        if (PointInTriangle(polygon[idx], a, b, c))
                        {
                            anyInside = true;
                            break;
                        }
                    }

                    if (anyInside)
                        continue;

                    outTriangles.Add(baseIndex + iPrev);
                    outTriangles.Add(baseIndex + iCurr);
                    outTriangles.Add(baseIndex + iNext);

                    indices.RemoveAt(i);
                    clipped = true;

                    // The vertex list changed shape only around iPrev/iNext's
                    // new neighbours, but recomputing the (cheap) reflex list
                    // from scratch here is still far less work than the O(n)
                    // per-candidate scan this replaced.
                    RebuildReflexList(polygon, indices, reflex);
                    break;
                }

                if (!clipped)
                    break; // degenerate polygon; stop rather than looping forever
            }

            // Reaching here with more than a triangle left means ear clipping could
            // not consume the polygon: either the guard tripped or the outline is
            // self-intersecting. Never fan that remainder. A fan joins distant points
            // across a broken outline and creates the huge black/grey wedges seen in
            // complex SWFs. Keeping the valid ears is preferable to covering the
            // stage with geometry that was never present in the movie.
            if (indices.Count > 3)
            {
                SwfRenderDiagnostics.Report(
                    SwfRenderProblem.TriangulationFailed,
                    characterId,
                    groupIndex,
                    "ear clipping left " + indices.Count + " of " + n +
                    " vertices unconsumed in fill group " + groupIndex +
                    "; the unsafe triangle-fan remainder was discarded");
                return;
            }

            if (indices.Count == 3)
            {
                outTriangles.Add(baseIndex + indices[0]);
                outTriangles.Add(baseIndex + indices[1]);
                outTriangles.Add(baseIndex + indices[2]);
            }
        }

        private static void RebuildReflexList(List<Vector2> polygon, List<int> indices, List<int> reflex)
        {
            reflex.Clear();
            int count = indices.Count;

            for (int i = 0; i < count; i++)
            {
                int iPrev = indices[(i - 1 + count) % count];
                int iCurr = indices[i];
                int iNext = indices[(i + 1) % count];

                Vector2 a = polygon[iPrev];
                Vector2 b = polygon[iCurr];
                Vector2 c = polygon[iNext];

                if (Cross(b - a, c - b) <= 0f)
                    reflex.Add(iCurr);
            }
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p - a, b - a);
            float d2 = Cross(p - b, c - b);
            float d3 = Cross(p - c, a - c);

            bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;

            return !(hasNeg && hasPos);
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }
    }
}
