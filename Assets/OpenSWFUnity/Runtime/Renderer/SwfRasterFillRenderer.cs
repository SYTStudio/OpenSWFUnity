using System.Collections.Generic;
using UnityEngine;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Tags;
using Unity.Mathematics;

namespace OpenSWFUnity.Runtime.Renderer
{
    public class SwfRasterFillRenderer
    {
        private readonly Transform root;
        private readonly Material material;

        private const float StageWidth = 600f;
        private const float StageHeight = 400f;
        private const float PixelsPerUnit = 50f;

        // Higher value = sharper texture, but slower generation.
        public static float RasterScale = 2f;

        public SwfRasterFillRenderer(Transform root)
        {
            this.root = root;
            RasterScale = GetRasterScale();

            Shader shader = Shader.Find("Sprites/Default");
            material = new Material(shader);
        }

        private float GetRasterScale()
        {
            if (SwfPlayer.Instance != null)
            {
                float qualityScale = (float)SwfPlayer.Instance.renderQuality;
                return qualityScale;
            }

            return 4f;
        }

        public void DrawShapeRasterFill(DefineShapeTag shape, SwfMatrix matrix, string name, float alpha)
        {
            if (shape == null || shape.ShapeData == null || shape.ShapeData.FillEdgeGroups == null)
                return;

            for (int g = 0; g < shape.ShapeData.FillEdgeGroups.Count; g++)
            {
                SwfFillEdgeGroup group = shape.ShapeData.FillEdgeGroups[g];

                if (group == null || group.Contours == null || group.Contours.Count == 0)
                    continue;

                Color fillColor = GetFillColor(shape, group.FillStyleIndex);
                fillColor.a *= alpha;

                if (fillColor.a <= 0f)
                    continue;

                Rect bounds = GetGroupBounds(group);

                if (bounds.width <= 0.01f || bounds.height <= 0.01f)
                    continue;

                Texture2D texture = BuildRasterTexture(group, bounds, fillColor);

                if (texture == null)
                    continue;

                GameObject go = new GameObject(name + "_RasterFill_Group_" + g);
                go.transform.SetParent(root, false);

                MeshFilter meshFilter = go.AddComponent<MeshFilter>();
                MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();

                meshRenderer.sortingLayerName = "Default";
                meshRenderer.sortingOrder = g;

                Material instanceMaterial = new Material(material);
                instanceMaterial.mainTexture = texture;
                instanceMaterial.color = Color.white;

                meshRenderer.material = instanceMaterial;
                meshFilter.mesh = BuildTexturedQuad(bounds, matrix);
            }
        }

        private Texture2D BuildRasterTexture(SwfFillEdgeGroup group, Rect bounds, Color fillColor)
        {
            bool useEvenOdd = HasRealHoles(group);

            int width = Mathf.Clamp(Mathf.CeilToInt(bounds.width * RasterScale), 1, 2048);
            int height = Mathf.Clamp(Mathf.CeilToInt(bounds.height * RasterScale), 1, 2048);

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Point;

            Color32 transparent = new Color32(0, 0, 0, 0);
            Color32 fill = fillColor;

            Color32[] pixels = new Color32[width * height];

            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = transparent;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float flashX = bounds.xMin + (x + 0.5f) / RasterScale;

                    // Texture Y goes bottom to top, Flash Y goes top to bottom.
                    float flashY = bounds.yMax - (y + 0.5f) / RasterScale;

                    Vector2 point = new Vector2(flashX, flashY);

                    if (IsInsideByContourScanline(point, group))
                    {
                        pixels[y * width + x] = fill;
                    }
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            return texture;
        }

        private Mesh BuildTexturedQuad(Rect bounds, SwfMatrix matrix)
        {
            Vector3 bottomLeft = FlashToUnityPoint(bounds.xMin, bounds.yMax, matrix);
            Vector3 bottomRight = FlashToUnityPoint(bounds.xMax, bounds.yMax, matrix);
            Vector3 topRight = FlashToUnityPoint(bounds.xMax, bounds.yMin, matrix);
            Vector3 topLeft = FlashToUnityPoint(bounds.xMin, bounds.yMin, matrix);

            Mesh mesh = new Mesh();
            mesh.name = "SWF Raster Fill Quad";

            mesh.vertices = new Vector3[]
            {
                bottomLeft,
                bottomRight,
                topRight,
                topLeft
            };

            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f)
            };

            mesh.triangles = new int[]
            {
                0, 1, 2,
                0, 2, 3
            };

            mesh.RecalculateBounds();
            return mesh;
        }

        private bool IsInsideByContourScanline(Vector2 point, SwfFillEdgeGroup group)
        {
            if (group == null || group.Contours == null || group.Contours.Count == 0)
                return false;

            List<float> intersections = new List<float>();
            float y = point.y;

            for (int c = 0; c < group.Contours.Count; c++)
            {
                SwfFillContour contour = group.Contours[c];

                if (contour == null || contour.Points == null || contour.Points.Count < 3)
                    continue;

                if (!contour.IsClosed)
                    continue;

                List<Vector2> points = contour.Points;

                for (int i = 0; i < points.Count; i++)
                {
                    Vector2 a = points[i];
                    Vector2 b = points[(i + 1) % points.Count];

                    if (Mathf.Abs(a.y - b.y) < 0.0001f)
                        continue;

                    float minY = Mathf.Min(a.y, b.y);
                    float maxY = Mathf.Max(a.y, b.y);

                    if (y < minY || y >= maxY)
                        continue;

                    float t = (y - a.y) / (b.y - a.y);
                    float x = a.x + t * (b.x - a.x);

                    intersections.Add(x);
                }
            }

            if (intersections.Count < 2)
                return false;

            intersections.Sort();

            for (int i = 0; i < intersections.Count - 1; i += 2)
            {
                float x0 = intersections[i];
                float x1 = intersections[i + 1];

                if (point.x >= x0 && point.x <= x1)
                    return true;
            }

            return false;
        }

        private bool IsInsideEvenOdd(Vector2 point, List<SwfFillContour> contours)
        {
            int crossings = 0;

            for (int i = 0; i < contours.Count; i++)
            {
                SwfFillContour contour = contours[i];

                if (contour == null || contour.Points == null || contour.Points.Count < 3)
                    continue;

                if (PointInPolygon(point, contour.Points))
                {
                    crossings++;
                }
            }

            return (crossings % 2) == 1;
        }

        private bool PointInPolygon(Vector2 point, List<Vector2> polygon)
        {
            bool inside = false;

            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 pi = polygon[i];
                Vector2 pj = polygon[j];

                bool intersects =
                    ((pi.y > point.y) != (pj.y > point.y)) &&
                    (point.x < (pj.x - pi.x) * (point.y - pi.y) / ((pj.y - pi.y) + 0.00001f) + pi.x);

                if (intersects)
                    inside = !inside;
            }

            return inside;
        }

        private Rect GetGroupBounds(SwfFillEdgeGroup group)
        {
            bool hasPoint = false;

            float minX = 0f;
            float minY = 0f;
            float maxX = 0f;
            float maxY = 0f;

            for (int c = 0; c < group.Contours.Count; c++)
            {
                SwfFillContour contour = group.Contours[c];

                if (contour == null || contour.Points == null)
                    continue;

                for (int p = 0; p < contour.Points.Count; p++)
                {
                    Vector2 point = contour.Points[p];

                    if (!hasPoint)
                    {
                        minX = maxX = point.x;
                        minY = maxY = point.y;
                        hasPoint = true;
                    }
                    else
                    {
                        minX = Mathf.Min(minX, point.x);
                        minY = Mathf.Min(minY, point.y);
                        maxX = Mathf.Max(maxX, point.x);
                        maxY = Mathf.Max(maxY, point.y);
                    }
                }
            }

            if (!hasPoint)
                return new Rect();

            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private Color GetFillColor(DefineShapeTag shape, int fillStyleIndex)
        {
            if (shape == null || shape.ShapeData == null || shape.ShapeData.FillStyles == null)
                return Color.clear;

            int listIndex = fillStyleIndex - 1;

            if (listIndex < 0 || listIndex >= shape.ShapeData.FillStyles.Count)
                return Color.clear;

            return shape.ShapeData.FillStyles[listIndex].ToUnityColor();
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
                -0.02f
            );
        }

        private bool HasRealHoles(SwfFillEdgeGroup group)
        {
            if (group == null || group.Contours == null || group.Contours.Count < 2)
                return false;

            SwfFillContour mainContour = GetLargestContour(group);

            if (mainContour == null)
                return false;

            for (int i = 0; i < group.Contours.Count; i++)
            {
                SwfFillContour contour = group.Contours[i];

                if (contour == null || contour == mainContour)
                    continue;

                if (contour.Points == null || contour.Points.Count < 3)
                    continue;

                Vector2 center = GetContourCenter(contour);

                bool insideMain = PointInPolygon(center, mainContour.Points);
                bool smaller = Mathf.Abs(contour.Area) < Mathf.Abs(mainContour.Area);

                if (insideMain && smaller && contour.IsClosed)
                    return true;
            }

            return false;
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

        private bool IsInsideUnion(Vector2 point, List<SwfFillContour> contours)
        {
            for (int i = 0; i < contours.Count; i++)
            {
                SwfFillContour contour = contours[i];

                if (contour == null || contour.Points == null || contour.Points.Count < 3)
                    continue;

                if (PointInPolygon(point, contour.Points))
                    return true;
            }

            return false;
        }

        private bool IsInsideByScanline(Vector2 point, SwfFillEdgeGroup group)
        {
            if (group == null || group.Edges == null || group.Edges.Count == 0)
                return false;

            List<float> intersections = new List<float>();

            float y = point.y;

            for (int i = 0; i < group.Edges.Count; i++)
            {
                SwfShapeEdge edge = group.Edges[i];

                Vector2 a = edge.Start;
                Vector2 b = edge.End;

                // Ignore horizontal edges.
                if (Mathf.Abs(a.y - b.y) < 0.0001f)
                    continue;

                float minY = Mathf.Min(a.y, b.y);
                float maxY = Mathf.Max(a.y, b.y);

                // Half-open interval avoids double-counting vertices.
                if (y < minY || y >= maxY)
                    continue;

                float t = (y - a.y) / (b.y - a.y);
                float x = a.x + t * (b.x - a.x);

                intersections.Add(x);
            }

            if (intersections.Count < 2)
                return false;

            intersections.Sort();

            for (int i = 0; i < intersections.Count - 1; i += 2)
            {
                float x0 = intersections[i];
                float x1 = intersections[i + 1];

                if (point.x >= x0 && point.x <= x1)
                    return true;
            }

            return false;
        }

        private bool IsInsideByWindingScanline(Vector2 point, SwfFillEdgeGroup group)
        {
            if (group == null || group.Edges == null || group.Edges.Count == 0)
                return false;

            int winding = 0;
            float y = point.y;

            for (int i = 0; i < group.Edges.Count; i++)
            {
                SwfShapeEdge edge = group.Edges[i];

                Vector2 a = edge.Start;
                Vector2 b = edge.End;

                // Ignore horizontal edges.
                if (Mathf.Abs(a.y - b.y) < 0.0001f)
                    continue;

                // Check if the edge crosses the horizontal ray to the left of the point.
                bool crossesUp =
                    a.y <= y &&
                    b.y > y;

                bool crossesDown =
                    b.y <= y &&
                    a.y > y;

                if (!crossesUp && !crossesDown)
                    continue;

                float t = (y - a.y) / (b.y - a.y);
                float x = a.x + t * (b.x - a.x);

                // Count only crossings to the left of the pixel.
                if (x > point.x)
                    continue;

                if (crossesUp)
                {
                    winding++;
                }
                else if (crossesDown)
                {
                    winding--;
                }
            }

            return winding != 0;
        }
    }
}