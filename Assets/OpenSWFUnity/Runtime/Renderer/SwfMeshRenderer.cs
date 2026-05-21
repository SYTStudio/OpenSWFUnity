using System.Collections.Generic;
using UnityEngine;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Tags;
using System.IO;

namespace OpenSWFUnity.Runtime.Renderer
{
    public class SwfMeshRenderer
    {
        private readonly Transform root;
        private readonly Material normalMaterial;
        private readonly Material maskedFillMaterial;
        private readonly Material stencilHoleMaterial;

        private const float StageWidth = 600f;
        private const float StageHeight = 400f;
        private const float PixelsPerUnit = 50f;
        private int sortingOrderCounter = 0;
        private int debugStencilContourIndex = 1;

        public SwfMeshRenderer(Transform root)
        {
            this.root = root;

            Shader normalShader = Shader.Find("Sprites/Default");
            Shader maskedShader = Shader.Find("OpenSWFUnity/SWF Fill Masked");
            Shader holeShader = Shader.Find("OpenSWFUnity/SWF Stencil Hole");

            normalMaterial = new Material(normalShader);

            if (maskedShader != null)
            {
                maskedFillMaterial = new Material(maskedShader);
            }
            else
            {
                Debug.LogWarning("Masked fill shader not found. Falling back to Sprites/Default.");
                maskedFillMaterial = new Material(normalShader);
            }

            if (holeShader != null)
            {
                stencilHoleMaterial = new Material(holeShader);
            }
            else
            {
                Debug.LogWarning("Stencil hole shader not found. Falling back to Sprites/Default.");
                stencilHoleMaterial = new Material(normalShader);
            }
        }

        public void DrawShapeFill(DefineShapeTag shape, SwfMatrix matrix, string name, bool enableStencilHoles)
        {
            if (shape == null || shape.ShapeData == null || shape.ShapeData.FillEdgeGroups == null)
                return;

            int stencilRef = GetStencilRefForShape(shape);

            for (int g = 0; g < shape.ShapeData.FillEdgeGroups.Count; g++)
            {
                SwfFillEdgeGroup group = shape.ShapeData.FillEdgeGroups[g];

                if (group == null || group.Contours == null || group.Contours.Count == 0)
                    continue;

                Color fillColor = GetFillColor(shape, group.FillStyleIndex);

                if (fillColor.a <= 0f)
                    continue;

                // Normal mode: render all contours.
                // Do not try to detect holes when stencil masking is disabled.
                if (!enableStencilHoles)
                {
                    for (int c = 0; c < group.Contours.Count; c++)
                    {
                        SwfFillContour contour = group.Contours[c];

                        if (contour == null || contour.Points == null || contour.Points.Count < 3)
                            continue;

                        DrawSingleContourMesh(
                            contour,
                            matrix,
                            name + "_FillGroup_" + g + "_Contour_" + c,
                            fillColor,
                            false,
                            stencilRef
                        );
                    }

                    continue;
                }

                // Stencil mode: use main contour + holes only for dominant fill group.
                bool isDominantGroup = IsDominantFillGroup(shape, group);
                SwfFillContour mainContour = GetLargestContour(group);

                if (mainContour == null || mainContour.Points == null || mainContour.Points.Count < 3)
                    continue;

                List<SwfFillContour> holes = GetValidHolesForMainContour(group, mainContour);

                bool useStencilMask =
                    isDominantGroup &&
                    holes.Count > 0;

                if (useStencilMask)
                {
                    DrawStencilHoles(
                        shape,
                        group,
                        mainContour,
                        holes,
                        matrix,
                        name + "_FillGroup_" + g,
                        stencilRef
                    );

                    DrawSingleContourMesh(
                        mainContour,
                        matrix,
                        name + "_FillGroup_" + g + "_MainContour",
                        fillColor,
                        true,
                        stencilRef
                    );
                }
                else
                {
                    // Non-dominant groups like shadow/details should render normally.
                    for (int c = 0; c < group.Contours.Count; c++)
                    {
                        SwfFillContour contour = group.Contours[c];

                        if (contour == null || contour.Points == null || contour.Points.Count < 3)
                            continue;

                        DrawSingleContourMesh(
                            contour,
                            matrix,
                            name + "_FillGroup_" + g + "_Contour_" + c,
                            fillColor,
                            false,
                            stencilRef
                        );
                    }
                }
            }
        }

        private Rect GetContourBounds(SwfFillContour contour)
        {
            if (contour == null || contour.Points == null || contour.Points.Count == 0)
                return new Rect();

            float minX = contour.Points[0].x;
            float maxX = contour.Points[0].x;
            float minY = contour.Points[0].y;
            float maxY = contour.Points[0].y;

            for (int i = 1; i < contour.Points.Count; i++)
            {
                Vector2 p = contour.Points[i];

                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y);
                maxY = Mathf.Max(maxY, p.y);
            }

            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private bool IsRealHoleForMainContour(
    SwfFillContour candidate,
    SwfFillContour mainContour
)
        {
            if (
                candidate == null ||
                mainContour == null ||
                candidate.Points == null ||
                mainContour.Points == null ||
                candidate.Points.Count < 3 ||
                mainContour.Points.Count < 3
            )
            {
                return false;
            }

            if (!candidate.IsClosed)
                return false;

            float rawCandidateArea = candidate.Area;
            float rawMainArea = mainContour.Area;

            float candidateArea = Mathf.Abs(rawCandidateArea);
            float mainArea = Mathf.Abs(rawMainArea);

            bool oppositeWinding =
                Mathf.Sign(rawCandidateArea) != Mathf.Sign(rawMainArea);

            if (!oppositeWinding)
                return false;

            if (candidateArea <= 0.01f || mainArea <= 0.01f)
                return false;

            float areaRatio = candidateArea / mainArea;

            // Keep this loose. Area alone is not enough.
            if (areaRatio > 0.20f)
                return false;

            // Center must be inside.
            Vector2 center = GetContourCenter(candidate);

            if (!PointInPolygon(center, mainContour.Points))
                return false;

            // Stronger check: every point must be inside main contour.
            if (!AllPointsInsidePolygon(candidate.Points, mainContour.Points))
                return false;

            // Candidate must not touch the outer boundary.
            // Fake U/shadow contours usually touch or sit very close to the outer contour.
            float minBoundaryDistance = float.MaxValue;

            for (int i = 0; i < candidate.Points.Count; i++)
            {
                float distance = DistancePointToPolygonBoundary(
                    candidate.Points[i],
                    mainContour.Points
                );

                if (distance < minBoundaryDistance)
                {
                    minBoundaryDistance = distance;
                }
            }

            // SWF units are pixels here. Tune between 1.0f and 3.0f.
            if (minBoundaryDistance < 3.0f)
                return false;

            Rect mainBounds = GetContourBounds(mainContour);
            Rect candidateBounds = GetContourBounds(candidate);

            float margin = 0.5f;

            bool fullyInsideBounds =
                candidateBounds.xMin > mainBounds.xMin + margin &&
                candidateBounds.xMax < mainBounds.xMax - margin &&
                candidateBounds.yMin > mainBounds.yMin + margin &&
                candidateBounds.yMax < mainBounds.yMax - margin;

            if (!fullyInsideBounds)
                return false;

            Debug.Log(
    "Accepted hole candidate. " +
    "CandidateArea=" + rawCandidateArea +
    " MainArea=" + rawMainArea +
    " Ratio=" + areaRatio
);
            return true;
        }

        private float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;

            float abLengthSquared = Vector2.Dot(ab, ab);

            if (abLengthSquared <= 0.00001f)
                return Vector2.Distance(p, a);

            float t = Vector2.Dot(p - a, ab) / abLengthSquared;
            t = Mathf.Clamp01(t);

            Vector2 closest = a + t * ab;

            return Vector2.Distance(p, closest);
        }

        private float DistancePointToPolygonBoundary(Vector2 point, List<Vector2> polygon)
        {
            if (polygon == null || polygon.Count < 2)
                return float.MaxValue;

            float bestDistance = float.MaxValue;

            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Count];

                float distance = DistancePointToSegment(point, a, b);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                }
            }

            return bestDistance;
        }

        private bool AllPointsInsidePolygon(List<Vector2> points, List<Vector2> polygon)
        {
            if (points == null || polygon == null)
                return false;

            for (int i = 0; i < points.Count; i++)
            {
                if (!PointInPolygon(points[i], polygon))
                {
                    return false;
                }
            }

            return true;
        }

        private void DrawSingleContourMesh(
    SwfFillContour contour,
    SwfMatrix matrix,
    string objectName,
    Color fillColor,
    bool useStencilMask,
    int stencilRef
)
        {
            Mesh mesh = BuildEarClipMesh(contour.Points, matrix);

            if (mesh == null || mesh.vertexCount == 0)
                return;

            GameObject go = new GameObject(objectName);
            go.transform.SetParent(root, false);

            MeshFilter meshFilter = go.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();

            meshRenderer.sortingLayerName = "Default";
            meshRenderer.sortingOrder = sortingOrderCounter++;

            Material instanceMaterial;

            if (useStencilMask)
            {
                instanceMaterial = new Material(maskedFillMaterial);

                if (instanceMaterial.HasProperty("_StencilRef"))
                {
                    instanceMaterial.SetFloat("_StencilRef", stencilRef);
                }
            }
            else
            {
                instanceMaterial = new Material(normalMaterial);
            }

            if (instanceMaterial.HasProperty("_Color"))
            {
                instanceMaterial.SetColor("_Color", fillColor);
            }

            instanceMaterial.color = fillColor;

            meshRenderer.material = instanceMaterial;
            meshFilter.mesh = mesh;
        }

        private Mesh BuildStencilFanMesh(List<Vector2> points, SwfMatrix matrix)
        {
            if (points == null || points.Count < 3)
                return null;

            List<Vector2> cleanPoints = new List<Vector2>(points);

            // Remove duplicate closing point.
            if (cleanPoints.Count > 1 && Vector2.Distance(cleanPoints[0], cleanPoints[cleanPoints.Count - 1]) < 0.5f)
            {
                cleanPoints.RemoveAt(cleanPoints.Count - 1);
            }

            if (cleanPoints.Count < 3)
                return null;

            Vector2 center = Vector2.zero;

            for (int i = 0; i < cleanPoints.Count; i++)
            {
                center += cleanPoints[i];
            }

            center /= cleanPoints.Count;

            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();

            vertices.Add(FlashToUnityPoint(center.x, center.y, matrix));

            for (int i = 0; i < cleanPoints.Count; i++)
            {
                vertices.Add(FlashToUnityPoint(cleanPoints[i].x, cleanPoints[i].y, matrix));
            }

            for (int i = 1; i < vertices.Count - 1; i++)
            {
                triangles.Add(0);
                triangles.Add(i);
                triangles.Add(i + 1);
            }

            // Close fan.
            triangles.Add(0);
            triangles.Add(vertices.Count - 1);
            triangles.Add(1);

            Mesh mesh = new Mesh();
            mesh.name = "SWF Stencil Hole Fan Mesh";
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();

            return mesh;
        }

        private bool HasHoleCandidates(SwfFillEdgeGroup group)
        {
            if (group == null || group.Contours == null)
                return false;

            for (int i = 0; i < group.Contours.Count; i++)
            {
                SwfFillContour contour = group.Contours[i];

                if (
                    contour != null &&
                    contour.IsHoleCandidate &&
                    contour.Points != null &&
                    contour.Points.Count >= 3
                )
                {
                    return true;
                }
            }

            return false;
        }
        private int GetStencilRefForShape(DefineShapeTag shape)
        {
            // Stencil ref range is 1-255. 0 is avoided.
            return (shape.CharacterId % 254) + 1;
        }

        private void DrawStencilHoles(
    DefineShapeTag shape,
    SwfFillEdgeGroup group,
    SwfFillContour mainContour,
    List<SwfFillContour> holes,
    SwfMatrix matrix,
    string name,
    int stencilRef
)
        {
            if (holes == null || holes.Count == 0)
                return;

            for (int i = 0; i < holes.Count; i++)
            {
                SwfFillContour hole = holes[i];

                if (hole == null || hole.Points == null || hole.Points.Count < 3)
                    continue;

                Mesh mesh = BuildStencilFanMesh(hole.Points, matrix);

                if (mesh == null || mesh.vertexCount == 0)
                    continue;

                GameObject go = new GameObject(name + "_StencilHole_" + i);
                go.transform.SetParent(root, false);

                MeshFilter meshFilter = go.AddComponent<MeshFilter>();
                MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();

                meshRenderer.sortingLayerName = "Default";
                meshRenderer.sortingOrder = sortingOrderCounter++;

                Material instanceMaterial = new Material(stencilHoleMaterial);
                instanceMaterial.SetFloat("_StencilRef", stencilRef);

                meshRenderer.material = instanceMaterial;
                meshFilter.mesh = mesh;

                Debug.Log(
                    "Rendering stencil hole. Shape=" + shape.CharacterId +
                    " FillGroup=" + group.FillStyleIndex +
                    " StencilRef=" + stencilRef +
                    " HolePoints=" + hole.Points.Count
                );
            }
        }

        private bool IsDominantFillGroup(DefineShapeTag shape, SwfFillEdgeGroup targetGroup)
        {
            if (
                shape == null ||
                shape.ShapeData == null ||
                shape.ShapeData.FillEdgeGroups == null ||
                targetGroup == null
            )
            {
                return false;
            }

            float targetArea = GetGroupMainArea(targetGroup);
            float bestArea = 0f;

            for (int i = 0; i < shape.ShapeData.FillEdgeGroups.Count; i++)
            {
                SwfFillEdgeGroup group = shape.ShapeData.FillEdgeGroups[i];
                float area = GetGroupMainArea(group);

                if (area > bestArea)
                {
                    bestArea = area;
                }
            }

            return Mathf.Approximately(targetArea, bestArea);
        }

        private float GetGroupMainArea(SwfFillEdgeGroup group)
        {
            SwfFillContour contour = GetLargestContour(group);

            if (contour == null)
                return 0f;

            return Mathf.Abs(contour.Area);
        }

        private List<SwfFillContour> GetSmallestHoleForMainContour(
    SwfFillEdgeGroup group,
    SwfFillContour mainContour
)
        {
            List<SwfFillContour> result = new List<SwfFillContour>();

            List<SwfFillContour> holes = GetValidHolesForMainContour(group, mainContour);

            if (holes == null || holes.Count == 0)
                return result;

            SwfFillContour bestHole = null;
            float bestArea = 0f;

            for (int i = 0; i < holes.Count; i++)
            {
                SwfFillContour hole = holes[i];

                if (hole == null || hole.Points == null || hole.Points.Count < 3)
                    continue;

                float area = Mathf.Abs(hole.Area);

                if (bestHole == null || area < bestArea)
                {
                    bestHole = hole;
                    bestArea = area;
                }
            }

            if (bestHole != null)
            {
                result.Add(bestHole);
            }

            return result;
        }

        private List<SwfFillContour> GetValidHolesForMainContour(
    SwfFillEdgeGroup group,
    SwfFillContour mainContour
)
        {
            List<SwfFillContour> holes = new List<SwfFillContour>();

            if (group == null || mainContour == null || group.Contours == null)
                return holes;

            for (int i = 0; i < group.Contours.Count; i++)
            {
                SwfFillContour candidate = group.Contours[i];

                if (candidate == mainContour)
                    continue;

                if (IsRealHoleForMainContour(candidate, mainContour))
                {
                    holes.Add(candidate);
                }
            }

            return holes;
        }

        private List<Vector2> BuildSimplePolygonWithHoles(
    SwfFillContour outer,
    List<SwfFillContour> holes
)
        {
            List<Vector2> polygon = new List<Vector2>(outer.Points);

            if (polygon.Count > 1 && Vector2.Distance(polygon[0], polygon[polygon.Count - 1]) < 0.5f)
            {
                polygon.RemoveAt(polygon.Count - 1);
            }

            if (holes == null || holes.Count == 0)
                return polygon;

            for (int i = 0; i < holes.Count; i++)
            {
                SwfFillContour hole = holes[i];

                if (hole == null || hole.Points == null || hole.Points.Count < 3)
                    continue;

                List<Vector2> holePoints = new List<Vector2>(hole.Points);

                if (holePoints.Count > 1 && Vector2.Distance(holePoints[0], holePoints[holePoints.Count - 1]) < 0.5f)
                {
                    holePoints.RemoveAt(holePoints.Count - 1);
                }

                BridgeHoleSafely(polygon, holePoints);
            }

            return polygon;
        }

        private void BridgeHoleSafely(List<Vector2> outer, List<Vector2> hole)
        {
            if (outer == null || outer.Count < 3 || hole == null || hole.Count < 3)
                return;

            int holeIndex = GetRightMostPointIndex(hole);
            Vector2 holePoint = hole[holeIndex];

            int outerIndex = GetBestOuterBridgePoint(outer, holePoint);

            if (outerIndex < 0)
                return;

            List<Vector2> combined = new List<Vector2>();

            for (int i = 0; i <= outerIndex; i++)
            {
                combined.Add(outer[i]);
            }

            combined.Add(holePoint);

            // Reverse hole winding while inserting.
            for (int i = 0; i < hole.Count; i++)
            {
                int index = (holeIndex - i + hole.Count) % hole.Count;
                combined.Add(hole[index]);
            }

            combined.Add(holePoint);
            combined.Add(outer[outerIndex]);

            for (int i = outerIndex + 1; i < outer.Count; i++)
            {
                combined.Add(outer[i]);
            }

            outer.Clear();
            outer.AddRange(combined);
        }

        private int GetBestOuterBridgePoint(List<Vector2> outer, Vector2 holePoint)
        {
            int bestIndex = -1;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < outer.Count; i++)
            {
                Vector2 p = outer[i];

                // Prefer points to the right of the hole point.
                if (p.x < holePoint.x)
                    continue;

                float distance = Vector2.Distance(p, holePoint);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            // Fallback: closest point.
            if (bestIndex < 0)
            {
                bestIndex = GetClosestPointIndex(outer, holePoint);
            }

            return bestIndex;
        }

        private void LogHoleCandidatesForMainContour(
    DefineShapeTag shape,
    SwfFillEdgeGroup group,
    SwfFillContour mainContour
)
        {
            if (shape == null || group == null || mainContour == null)
                return;

            Debug.Log(
                "Main contour check. Shape=" + shape.CharacterId +
                " FillGroup=" + group.FillStyleIndex +
                " MainPoints=" + mainContour.Points.Count +
                " MainArea=" + mainContour.Area +
                " TotalContours=" + group.Contours.Count
            );

            for (int i = 0; i < group.Contours.Count; i++)
            {
                SwfFillContour candidate = group.Contours[i];

                if (candidate == null || candidate == mainContour || candidate.Points == null)
                    continue;

                Vector2 center = GetContourCenter(candidate);
                bool insideMain = PointInPolygon(center, mainContour.Points);
                bool smaller = Mathf.Abs(candidate.Area) < Mathf.Abs(mainContour.Area);

                Debug.Log(
                    "  Candidate contour=" + i +
                    " Points=" + candidate.Points.Count +
                    " Area=" + candidate.Area +
                    " InsideMain=" + insideMain +
                    " Smaller=" + smaller +
                    " HoleCandidate=" + candidate.IsHoleCandidate
                );
            }
        }

        private Vector2 GetContourCenter(SwfFillContour contour)
        {
            Vector2 sum = Vector2.zero;

            if (contour == null || contour.Points == null || contour.Points.Count == 0)
                return sum;

            for (int i = 0; i < contour.Points.Count; i++)
            {
                sum += contour.Points[i];
            }

            return sum / contour.Points.Count;
        }

        private bool PointInPolygon(Vector2 point, List<Vector2> polygon)
        {
            if (polygon == null || polygon.Count < 3)
                return false;

            bool inside = false;

            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 pi = polygon[i];
                Vector2 pj = polygon[j];

                bool intersects =
                    ((pi.y > point.y) != (pj.y > point.y)) &&
                    (point.x < (pj.x - pi.x) * (point.y - pi.y) / ((pj.y - pi.y) + 0.00001f) + pi.x);

                if (intersects)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private SwfFillContour GetLargestContour(SwfFillEdgeGroup group)
        {
            if (group == null || group.Contours == null || group.Contours.Count == 0)
                return null;

            SwfFillContour best = null;
            float bestArea = 0f;

            for (int i = 0; i < group.Contours.Count; i++)
            {
                SwfFillContour contour = group.Contours[i];

                if (contour == null || contour.Points == null || contour.Points.Count < 3)
                    continue;

                float area = Mathf.Abs(contour.Area);

                if (best == null || area > bestArea)
                {
                    best = contour;
                    bestArea = area;
                }
            }

            return best;
        }

        private Color GetFillColor(DefineShapeTag shape, int fillStyleIndex)
        {
            if (
                shape == null ||
                shape.ShapeData == null ||
                shape.ShapeData.FillStyles == null
            )
            {
                return Color.clear;
            }

            // SWF fill style index is 1-based.
            int listIndex = fillStyleIndex - 1;

            if (listIndex < 0 || listIndex >= shape.ShapeData.FillStyles.Count)
                return Color.clear;

            return shape.ShapeData.FillStyles[listIndex].ToUnityColor();
        }

        private Mesh BuildEarClipMesh(List<Vector2> sourcePoints, SwfMatrix matrix)
        {
            List<Vector2> points = new List<Vector2>(sourcePoints);

            // Remove duplicated closing point.
            if (points.Count > 1 && Vector2.Distance(points[0], points[points.Count - 1]) < 0.5f)
            {
                points.RemoveAt(points.Count - 1);
            }

            if (points.Count < 3)
                return null;

            List<Vector3> vertices = new List<Vector3>();

            for (int i = 0; i < points.Count; i++)
            {
                vertices.Add(FlashToUnityPoint(points[i].x, points[i].y, matrix));
            }

            List<int> triangles = TriangulateEarClipping(points);

            if (triangles.Count < 3)
                return null;

            Mesh mesh = new Mesh();
            mesh.name = "SWF Fill Contour Mesh";
            mesh.SetVertices(vertices);

            FlipTriangleWinding(triangles);

            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();

            return mesh;
        }

        private void FlipTriangleWinding(List<int> triangles)
        {
            for (int i = 0; i < triangles.Count; i += 3)
            {
                int temp = triangles[i + 1];
                triangles[i + 1] = triangles[i + 2];
                triangles[i + 2] = temp;
            }
        }

        private List<int> TriangulateEarClipping(List<Vector2> points)
        {
            List<int> triangles = new List<int>();

            if (points == null || points.Count < 3)
                return triangles;

            List<int> indices = new List<int>();
            for (int i = 0; i < points.Count; i++)
            {
                indices.Add(i);
            }

            // Ensure counter-clockwise order.
            if (PolygonArea(points) < 0f)
            {
                indices.Reverse();
            }

            int guard = 0;

            while (indices.Count > 3 && guard < 10000)
            {
                guard++;

                bool earFound = false;

                for (int i = 0; i < indices.Count; i++)
                {
                    int prevIndex = indices[(i - 1 + indices.Count) % indices.Count];
                    int currIndex = indices[i];
                    int nextIndex = indices[(i + 1) % indices.Count];

                    Vector2 prev = points[prevIndex];
                    Vector2 curr = points[currIndex];
                    Vector2 next = points[nextIndex];

                    if (!IsConvex(prev, curr, next))
                        continue;

                    bool hasPointInside = false;

                    for (int j = 0; j < indices.Count; j++)
                    {
                        int testIndex = indices[j];

                        if (testIndex == prevIndex || testIndex == currIndex || testIndex == nextIndex)
                            continue;

                        if (PointInTriangle(points[testIndex], prev, curr, next))
                        {
                            hasPointInside = true;
                            break;
                        }
                    }

                    if (hasPointInside)
                        continue;

                    triangles.Add(prevIndex);
                    triangles.Add(currIndex);
                    triangles.Add(nextIndex);

                    indices.RemoveAt(i);
                    earFound = true;
                    break;
                }

                if (!earFound)
                {
                    break;
                }
            }

            if (indices.Count == 3)
            {
                triangles.Add(indices[0]);
                triangles.Add(indices[1]);
                triangles.Add(indices[2]);
            }

            return triangles;
        }

        private float PolygonArea(List<Vector2> points)
        {
            float area = 0f;

            for (int i = 0; i < points.Count; i++)
            {
                Vector2 a = points[i];
                Vector2 b = points[(i + 1) % points.Count];

                area += a.x * b.y - b.x * a.y;
            }

            return area * 0.5f;
        }

        private bool IsConvex(Vector2 a, Vector2 b, Vector2 c)
        {
            float cross =
                (b.x - a.x) * (c.y - a.y) -
                (b.y - a.y) * (c.x - a.x);

            return cross > 0f;
        }

        private bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float area = Mathf.Abs(Cross(a, b, c));
            float area1 = Mathf.Abs(Cross(p, b, c));
            float area2 = Mathf.Abs(Cross(a, p, c));
            float area3 = Mathf.Abs(Cross(a, b, p));

            return Mathf.Abs(area - (area1 + area2 + area3)) < 0.01f;
        }

        private float Cross(Vector2 a, Vector2 b, Vector2 c)
        {
            return
                (b.x - a.x) * (c.y - a.y) -
                (b.y - a.y) * (c.x - a.x);
        }

        private Vector3 FlashToUnityPoint(float x, float y, SwfMatrix matrix)
        {
            float flashX = x * matrix.ScaleX + matrix.TranslateX;
            float flashY = y * matrix.ScaleY + matrix.TranslateY;

            float unityX = flashX - StageWidth / 2f;
            float unityY = StageHeight / 2f - flashY;

            return new Vector3(
                unityX / PixelsPerUnit,
                unityY / PixelsPerUnit,
                -0.01f
            );
        }

        private int GetRightMostPointIndex(List<Vector2> points)
        {
            if (points == null || points.Count == 0)
                return -1;

            int bestIndex = 0;
            float bestX = points[0].x;

            for (int i = 1; i < points.Count; i++)
            {
                if (points[i].x > bestX)
                {
                    bestX = points[i].x;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private int GetClosestPointIndex(List<Vector2> points, Vector2 target)
        {
            if (points == null || points.Count == 0)
                return -1;

            int bestIndex = 0;
            float bestDistance = Vector2.Distance(points[0], target);

            for (int i = 1; i < points.Count; i++)
            {
                float distance = Vector2.Distance(points[i], target);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }
    }
}