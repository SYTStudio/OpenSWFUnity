using System.Collections.Generic;
using UnityEngine;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Tags;

namespace OpenSWFUnity.Runtime.Renderer
{
    public class SwfRasterFillRenderer
    {
        private readonly Transform poolRoot;
        private readonly float stageWidth;
        private readonly float stageHeight;
        private readonly List<RasterInstance> instances = new List<RasterInstance>();

        private static Material sharedMaterial;
        private static readonly Dictionary<int, Material> stencilMaterials =
            new Dictionary<int, Material>();
        private static readonly int MainTextureId = Shader.PropertyToID("_MainTex");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int StencilRefId = Shader.PropertyToID("_StencilRef");
        private static readonly int StencilCompId = Shader.PropertyToID("_StencilComp");
        private static readonly int StencilPassId = Shader.PropertyToID("_StencilPass");
        private static readonly int ColorMaskId = Shader.PropertyToID("_ColorMask");

        private int usedInstanceCount;
        private int drawOrder;
        private float activeRasterScale;
        private int stencilMode;
        private int stencilReference;
        private readonly Stack<int> stencilStateStack = new Stack<int>();

        private static readonly Dictionary<string, Texture2D> rasterTextureCache = new Dictionary<string, Texture2D>();

        private const float PixelsPerUnit = 50f;

        // Higher value = sharper texture, but slower generation.
        public static float RasterScale = 2f;

        public SwfRasterFillRenderer(Transform root, float stageWidth = 600f, float stageHeight = 400f)
        {
            this.stageWidth = stageWidth;
            this.stageHeight = stageHeight;
            RasterScale = GetRasterScale();
            activeRasterScale = RasterScale;

            Transform existingPool = root.Find("__SWF_RasterPool");

            if (existingPool != null)
            {
                poolRoot = existingPool;
            }
            else
            {
                GameObject poolObject = new GameObject("__SWF_RasterPool");
                poolObject.transform.SetParent(root, false);
                poolRoot = poolObject.transform;
            }

            EnsureSharedMaterial();
        }

        public void BeginFrame()
        {
            float requestedScale = GetRasterScale();

            if (!Mathf.Approximately(activeRasterScale, requestedScale))
            {
                ClearTextureCache();
                activeRasterScale = requestedScale;
            }

            RasterScale = requestedScale;
            usedInstanceCount = 0;
            drawOrder = 0;
            stencilMode = 0;
            stencilReference = 0;
            stencilStateStack.Clear();
        }

        public void BeginMaskWrite(int reference)
        {
            stencilStateStack.Push((stencilMode << 8) | stencilReference);
            stencilMode = 1;
            stencilReference = Mathf.Clamp(reference, 1, 255);
        }

        public void BeginMaskedContent(int reference)
        {
            stencilMode = 2;
            stencilReference = Mathf.Clamp(reference, 1, 255);
        }

        public void EndStencil()
        {
            if (stencilStateStack.Count > 0)
            {
                int state = stencilStateStack.Pop();
                stencilMode = (state >> 8) & 0xff;
                stencilReference = state & 0xff;
            }
            else
            {
                stencilMode = 0;
                stencilReference = 0;
            }
        }

        private static void ClearTextureCache()
        {
            foreach (Texture2D texture in rasterTextureCache.Values)
            {
                if (texture != null)
                    Object.Destroy(texture);
            }

            rasterTextureCache.Clear();
        }

        public void EndFrame()
        {
            for (int i = usedInstanceCount; i < instances.Count; i++)
            {
                if (instances[i].GameObject.activeSelf)
                    instances[i].GameObject.SetActive(false);
            }
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

            List<int> groupOrder = new List<int>();

            for (int i = 0; i < shape.ShapeData.FillEdgeGroups.Count; i++)
            {
                groupOrder.Add(i);
            }

            // Draw secondary/details first, primary fill last.
            // This makes shadows/details go behind the main fill instead of covering it.
            groupOrder.Sort((a, b) =>
            {
                SwfFillEdgeGroup ga = shape.ShapeData.FillEdgeGroups[a];
                SwfFillEdgeGroup gb = shape.ShapeData.FillEdgeGroups[b];

                int fa = ga != null ? ga.FillStyleIndex : 0;
                int fb = gb != null ? gb.FillStyleIndex : 0;

                // Higher fill style index first, FillStyle 1 last.
                int fillCompare = fb.CompareTo(fa);

                if (fillCompare != 0)
                    return fillCompare;

                return a.CompareTo(b);
            });

            for (int orderIndex = 0; orderIndex < groupOrder.Count; orderIndex++)
            {
                int g = groupOrder[orderIndex];

                SwfFillEdgeGroup group = shape.ShapeData.FillEdgeGroups[g];

                if (group == null || group.Contours == null || group.Contours.Count == 0)
                    continue;

                Color fillColor = GetFillColor(shape, group.FillStyleIndex);

                if (fillColor.a <= 0.01f || alpha <= 0.01f)
                    continue;

                Color textureColor = fillColor;
                textureColor.a = 1f;

                Rect bounds = GetGroupBounds(group);

                if (bounds.width <= 0.01f || bounds.height <= 0.01f)
                    continue;

                bounds = ExpandRect(bounds, 1.5f);

                string cacheKey =
                    shape.CharacterId +
                    "_group_" + g +
                    "_scale_" + RasterScale +
                    "_rgb_" + textureColor.r + "_" + textureColor.g + "_" + textureColor.b;

                if (!rasterTextureCache.TryGetValue(cacheKey, out Texture2D texture) || texture == null)
                {
                    texture = BuildRasterTexture(group, bounds, textureColor);
                    rasterTextureCache[cacheKey] = texture;
                }

                if (texture == null)
                    continue;

                RasterInstance instance = AcquireInstance(name + "_RasterFill_Group_" + g);
                instance.Renderer.sortingOrder = drawOrder++;
                instance.Renderer.sharedMaterial = GetActiveMaterial();

                Color materialColor = new Color(1f, 1f, 1f, alpha);
                instance.PropertyBlock.Clear();
                instance.PropertyBlock.SetTexture(MainTextureId, texture);
                instance.PropertyBlock.SetColor(ColorId, materialColor);
                instance.Renderer.SetPropertyBlock(instance.PropertyBlock);

                UpdateTexturedQuad(instance, bounds, matrix);
            }
        }

        private static void EnsureSharedMaterial()
        {
            if (sharedMaterial != null)
                return;

            Shader shader = Shader.Find("OpenSWFUnity/SWF Raster Stencil");

            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            sharedMaterial = new Material(shader)
            {
                name = "OpenSWF Shared Raster Material",
                hideFlags = HideFlags.HideAndDontSave
            };

            if (sharedMaterial.HasProperty(StencilRefId))
            {
                sharedMaterial.SetFloat(StencilRefId, 0f);
                sharedMaterial.SetFloat(StencilCompId, 8f); // Always
                sharedMaterial.SetFloat(StencilPassId, 0f); // Keep
                sharedMaterial.SetFloat(ColorMaskId, 15f);
            }
        }

        private Material GetActiveMaterial()
        {
            if (stencilMode == 0 || sharedMaterial == null ||
                !sharedMaterial.HasProperty(StencilRefId))
            {
                return sharedMaterial;
            }

            int key = (stencilMode << 8) | stencilReference;

            if (stencilMaterials.TryGetValue(key, out Material material) && material != null)
                return material;

            material = new Material(sharedMaterial)
            {
                name = stencilMode == 1
                    ? "OpenSWF Mask Writer " + stencilReference
                    : "OpenSWF Masked Content " + stencilReference,
                hideFlags = HideFlags.HideAndDontSave
            };
            material.SetFloat(StencilRefId, stencilReference);
            material.SetFloat(StencilCompId, stencilMode == 1 ? 8f : 3f); // Always / Equal
            material.SetFloat(StencilPassId, stencilMode == 1 ? 2f : 0f); // Replace / Keep
            material.SetFloat(ColorMaskId, stencilMode == 1 ? 0f : 15f);
            stencilMaterials[key] = material;
            return material;
        }

        private RasterInstance AcquireInstance(string instanceName)
        {
            RasterInstance instance;

            if (usedInstanceCount < instances.Count)
            {
                instance = instances[usedInstanceCount];
            }
            else
            {
                GameObject go = new GameObject(instanceName);
                go.transform.SetParent(poolRoot, false);

                Mesh mesh = new Mesh
                {
                    name = "SWF Raster Fill Quad"
                };
                mesh.MarkDynamic();
                mesh.vertices = new Vector3[4];
                mesh.uv = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, 1f)
                };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };

                MeshFilter meshFilter = go.AddComponent<MeshFilter>();
                MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();
                meshFilter.sharedMesh = mesh;
                meshRenderer.sharedMaterial = sharedMaterial;
                meshRenderer.sortingLayerName = "Default";

                instance = new RasterInstance
                {
                    GameObject = go,
                    Mesh = mesh,
                    Renderer = meshRenderer,
                    PropertyBlock = new MaterialPropertyBlock(),
                    Vertices = new Vector3[4]
                };

                instances.Add(instance);
            }

            usedInstanceCount++;
            instance.GameObject.name = instanceName;

            if (!instance.GameObject.activeSelf)
                instance.GameObject.SetActive(true);

            return instance;
        }

        private Rect ExpandRect(Rect rect, float amount)
        {
            return Rect.MinMaxRect(
                rect.xMin - amount,
                rect.yMin - amount,
                rect.xMax + amount,
                rect.yMax + amount
            );
        }

        private Texture2D BuildRasterTexture(SwfFillEdgeGroup group, Rect bounds, Color fillColor)
        {
            bool useEvenOdd = HasRealHoles(group);

            int width = Mathf.Clamp(Mathf.CeilToInt(bounds.width * RasterScale), 1, 2048);
            int height = Mathf.Clamp(Mathf.CeilToInt(bounds.height * RasterScale), 1, 2048);

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            Color32 fill = fillColor;
            Color32[] pixels = new Color32[width * height];

            if (useEvenOdd)
            {
                RasterizeEvenOdd(group.Contours, bounds, width, height, pixels, fill);
            }
            else
            {
                RasterizeContourUnion(group.Contours, bounds, width, height, pixels, fill);
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            return texture;
        }

        private void RasterizeEvenOdd(
            List<SwfFillContour> contours,
            Rect bounds,
            int width,
            int height,
            Color32[] pixels,
            Color32 fill
        )
        {
            List<float> intersections = new List<float>(64);

            for (int y = 0; y < height; y++)
            {
                float flashY = bounds.yMax - (y + 0.5f) / RasterScale;
                intersections.Clear();

                for (int c = 0; c < contours.Count; c++)
                {
                    SwfFillContour contour = contours[c];

                    if (contour == null || !contour.IsClosed)
                        continue;

                    AddScanlineIntersections(contour, flashY, intersections);
                }

                FillIntersectionPairs(intersections, bounds, width, y, pixels, fill);
            }
        }

        private void RasterizeContourUnion(
            List<SwfFillContour> contours,
            Rect bounds,
            int width,
            int height,
            Color32[] pixels,
            Color32 fill
        )
        {
            List<float> intersections = new List<float>(64);

            for (int y = 0; y < height; y++)
            {
                float flashY = bounds.yMax - (y + 0.5f) / RasterScale;

                for (int c = 0; c < contours.Count; c++)
                {
                    SwfFillContour contour = contours[c];

                    if (contour == null)
                        continue;

                    intersections.Clear();
                    AddScanlineIntersections(contour, flashY, intersections);
                    FillIntersectionPairs(intersections, bounds, width, y, pixels, fill);
                }
            }
        }

        private void AddScanlineIntersections(
            SwfFillContour contour,
            float y,
            List<float> intersections
        )
        {
            List<Vector2> points = contour.Points;

            if (points == null || points.Count < 3)
                return;

            int edgeCount = contour.IsClosed ? points.Count : points.Count - 1;

            for (int i = 0; i < edgeCount; i++)
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
                intersections.Add(a.x + t * (b.x - a.x));
            }
        }

        private void FillIntersectionPairs(
            List<float> intersections,
            Rect bounds,
            int width,
            int y,
            Color32[] pixels,
            Color32 fill
        )
        {
            if (intersections.Count < 2)
                return;

            intersections.Sort();

            for (int i = 0; i + 1 < intersections.Count; i += 2)
            {
                float left = intersections[i];
                float right = intersections[i + 1];
                int startX = Mathf.CeilToInt((left - bounds.xMin) * RasterScale - 0.5f);
                int endX = Mathf.FloorToInt((right - bounds.xMin) * RasterScale - 0.5f);

                startX = Mathf.Clamp(startX, 0, width - 1);
                endX = Mathf.Clamp(endX, 0, width - 1);

                int rowOffset = y * width;

                for (int x = startX; x <= endX; x++)
                    pixels[rowOffset + x] = fill;
            }
        }

        private bool IsInsideByContourScanlineAllowOpen(Vector2 point, SwfFillEdgeGroup group)
        {
            if (group == null || group.Contours == null || group.Contours.Count == 0)
                return false;

            for (int c = 0; c < group.Contours.Count; c++)
            {
                SwfFillContour contour = group.Contours[c];

                if (contour == null || contour.Points == null || contour.Points.Count < 3)
                    continue;

                if (IsInsideSingleContourScanline(point, contour))
                    return true;
            }

            return false;
        }

        private bool IsInsideSingleContourScanline(Vector2 point, SwfFillContour contour)
        {
            List<Vector2> points = contour.Points;

            if (points == null || points.Count < 3)
                return false;

            List<float> intersections = new List<float>();
            float y = point.y;

            // Closed contours use last -> first.
            // Open contours use only real consecutive edges.
            int edgeCount = contour.IsClosed ? points.Count : points.Count - 1;

            for (int i = 0; i < edgeCount; i++)
            {
                Vector2 a = points[i];
                Vector2 b = points[(i + 1) % points.Count];

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

        private bool IsInsideEvenOddClosedContours(Vector2 point, List<SwfFillContour> contours)
        {
            int crossings = 0;

            for (int i = 0; i < contours.Count; i++)
            {
                SwfFillContour contour = contours[i];

                if (contour == null || contour.Points == null || contour.Points.Count < 3)
                    continue;

                if (!contour.IsClosed)
                    continue;

                if (PointInPolygon(point, contour.Points))
                {
                    crossings++;
                }
            }

            return (crossings % 2) == 1;
        }

        private void UpdateTexturedQuad(RasterInstance instance, Rect bounds, SwfMatrix matrix)
        {
            instance.Vertices[0] = FlashToUnityPoint(bounds.xMin, bounds.yMax, matrix);
            instance.Vertices[1] = FlashToUnityPoint(bounds.xMax, bounds.yMax, matrix);
            instance.Vertices[2] = FlashToUnityPoint(bounds.xMax, bounds.yMin, matrix);
            instance.Vertices[3] = FlashToUnityPoint(bounds.xMin, bounds.yMin, matrix);

            for (int i = 0; i < instance.Vertices.Length; i++)
            {
                if (!IsFinite(instance.Vertices[i]))
                {
                    instance.GameObject.SetActive(false);
                    return;
                }
            }

            instance.Mesh.vertices = instance.Vertices;
            instance.Mesh.RecalculateBounds();
        }

        private static bool IsFinite(Vector3 value)
        {
            return
                !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                !float.IsNaN(value.z) && !float.IsInfinity(value.z);
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
            Vector2 flashPoint = matrix.TransformPoint(x, y);

            float unityX = flashPoint.x - stageWidth / 2f;
            float unityY = stageHeight / 2f - flashPoint.y;

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

        private sealed class RasterInstance
        {
            public GameObject GameObject;
            public Mesh Mesh;
            public MeshRenderer Renderer;
            public MaterialPropertyBlock PropertyBlock;
            public Vector3[] Vertices;
        }
    }
}
