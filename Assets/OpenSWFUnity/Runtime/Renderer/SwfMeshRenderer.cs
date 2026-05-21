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
        private readonly Material material;

        private const float StageWidth = 600f;
        private const float StageHeight = 400f;
        private const float PixelsPerUnit = 50f;
        private int sortingOrderCounter = 0;

        public SwfMeshRenderer(Transform root)
        {
            this.root = root;

            Shader shader = Shader.Find("Sprites/Default");
            material = new Material(shader);
        }

        public void DrawShapeFill(DefineShapeTag shape, SwfMatrix matrix, string name)
        {
            if (shape == null || shape.ShapeData == null || shape.ShapeData.FillEdgeGroups == null)
                return;

            for (int g = 0; g < shape.ShapeData.FillEdgeGroups.Count; g++)
            {
                SwfFillEdgeGroup group = shape.ShapeData.FillEdgeGroups[g];

                Color fillColor = GetFillColor(shape, group.FillStyleIndex);

                if (fillColor.a <= 0f)
                    continue;

                for (int c = 0; c < group.Contours.Count; c++)
                {
                    SwfFillContour contour = group.Contours[c];

                    if (contour == null || contour.Points == null || contour.Points.Count < 3)
                        continue;

                    Mesh mesh = BuildEarClipMesh(contour.Points, matrix);

                    if (mesh == null || mesh.vertexCount == 0)
                        continue;

                    GameObject go = new GameObject(name + "_FillGroup_" + g + "_Contour_" + c);
                    go.transform.SetParent(root, false);

                    MeshFilter meshFilter = go.AddComponent<MeshFilter>();
                    MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();
                    meshRenderer.sortingLayerName = "Default";
                    meshRenderer.sortingOrder = sortingOrderCounter++;

                    Material instanceMaterial = new Material(material);
                    instanceMaterial.color = fillColor;
                    meshRenderer.material = instanceMaterial;

                    meshFilter.mesh = mesh;
                }
            }
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

        private Color GetPathFillColor(DefineShapeTag shape, SwfShapePath path)
        {
            Color fillColor = Color.white;

            if (shape == null || shape.ShapeData == null || shape.ShapeData.FillStyles == null)
                return fillColor;

            int fillIndex = path.FillStyle1;

            if (fillIndex <= 0)
                return Color.clear;

            int listIndex = fillIndex - 1;

            if (listIndex >= 0 && listIndex < shape.ShapeData.FillStyles.Count)
            {
                fillColor = shape.ShapeData.FillStyles[listIndex].ToUnityColor();
            }

            return fillColor;
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

        private Mesh BuildFanMesh(List<Vector2> points, SwfMatrix matrix)
        {
            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();

            Vector2 center = GetCenter(points);
            vertices.Add(FlashToUnityPoint(center.x, center.y, matrix));

            for (int i = 0; i < points.Count; i++)
            {
                Vector2 p = points[i];
                vertices.Add(FlashToUnityPoint(p.x, p.y, matrix));
            }

            for (int i = 1; i < vertices.Count - 1; i++)
            {
                triangles.Add(0);
                triangles.Add(i);
                triangles.Add(i + 1);
            }

            // Close the fan
            triangles.Add(0);
            triangles.Add(vertices.Count - 1);
            triangles.Add(1);

            Mesh mesh = new Mesh();
            mesh.name = "SWF Shape Fill Mesh";
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();

            return mesh;
        }

        private Vector2 GetCenter(List<Vector2> points)
        {
            Vector2 sum = Vector2.zero;

            for (int i = 0; i < points.Count; i++)
            {
                sum += points[i];
            }

            return sum / points.Count;
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

        private bool IsClosedPath(List<Vector2> points)
        {
            if (points == null || points.Count < 3)
                return false;

            Vector2 first = points[0];
            Vector2 last = points[points.Count - 1];

            return Vector2.Distance(first, last) < 0.5f;
        }
    }
}