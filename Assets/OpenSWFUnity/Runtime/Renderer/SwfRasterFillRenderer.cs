using System.Collections.Generic;
using UnityEngine;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Tags;

namespace OpenSWFUnity.Runtime.Renderer
{
    // Batched vector fill renderer.
    //
    // Shapes are triangulated once into local space (cached per shape+group),
    // then every fill drawn this frame is appended into shared vertex/index/
    // colour buffers. Buffers are flushed into a real Mesh only when the stencil
    // state changes or the frame ends, so a scene with thousands of shapes costs
    // a handful of draws instead of one GameObject per fill per frame - that
    // GameObject churn is what made a full game freeze the Editor.
    public class SwfRasterFillRenderer
    {
        private readonly Transform poolRoot;
        private readonly float stageWidth;
        private readonly float stageHeight;

        private static Material sharedMaterial;
        private static readonly Dictionary<int, Material> stencilMaterials =
            new Dictionary<int, Material>();
        private static readonly int MainTextureId = Shader.PropertyToID("_MainTex");
        private static readonly int StencilRefId = Shader.PropertyToID("_StencilRef");
        private static readonly int StencilCompId = Shader.PropertyToID("_StencilComp");
        private static readonly int StencilPassId = Shader.PropertyToID("_StencilPass");
        private static readonly int StencilReadMaskId = Shader.PropertyToID("_StencilReadMask");
        private static readonly int StencilWriteMaskId = Shader.PropertyToID("_StencilWriteMask");
        private static readonly int ColorMaskId = Shader.PropertyToID("_ColorMask");
        private static readonly int TintColorId = Shader.PropertyToID("_Color");
        private static readonly int UseSwfMatrixId = Shader.PropertyToID("_UseSwfMatrix");
        private static readonly int SwfMatrix0Id = Shader.PropertyToID("_SwfMatrix0");
        private static readonly int SwfMatrix1Id = Shader.PropertyToID("_SwfMatrix1");

        // Per-renderer, not static: a static cache outlived the movie that filled
        // it, so loading a second SWF whose character ids overlapped would serve the
        // first movie's geometry. Entries are dropped wholesale when the quality
        // revision moves, which is the only thing besides a reload that can change
        // what a mesh should contain.
        private readonly Dictionary<MeshCacheKey, LocalMeshData> meshCache =
            new Dictionary<MeshCacheKey, LocalMeshData>();
        private readonly Dictionary<SwfFillStyle, Texture2D> gradientTextures =
            new Dictionary<SwfFillStyle, Texture2D>();
        private readonly Dictionary<ushort, RasterizedShape> rasterShapeCache =
            new Dictionary<ushort, RasterizedShape>();
        private readonly Dictionary<Texture2D, Color32[]> readableTexturePixels =
            new Dictionary<Texture2D, Color32[]>();
        private readonly List<RasterAtlas> rasterAtlases = new List<RasterAtlas>();
        private long rasterShapeCachePixels;

        private readonly List<Vector2> simplifyBuffer = new List<Vector2>(256);
        private readonly List<SwfFillContour> simplifiedContours = new List<SwfFillContour>(16);

        private int cachedQualityRevision = -1;

        public int MeshCacheCount => meshCache.Count;
        public int MeshBuildCount { get; private set; }

        private readonly struct MeshCacheKey : System.IEquatable<MeshCacheKey>
        {
            private readonly int characterId;
            private readonly int groupIndex;

            public MeshCacheKey(int characterId, int groupIndex)
            {
                this.characterId = characterId;
                this.groupIndex = groupIndex;
            }

            public bool Equals(MeshCacheKey other)
            {
                return characterId == other.characterId && groupIndex == other.groupIndex;
            }

            public override bool Equals(object obj) => obj is MeshCacheKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked { return (characterId * 397) ^ groupIndex; }
            }
        }

        // A single Unity mesh cannot exceed 65535 vertices with a 16-bit index
        // buffer; flush before crossing it rather than losing geometry.
        private const int MaxBatchVertices = 60000;
        private const float PixelsPerUnit = 50f;
        private const float TwipsPerPixel = 20f;

        private readonly List<Vector3> batchVertices = new List<Vector3>(8192);
        private readonly List<int> batchTriangles = new List<int>(16384);
        private readonly List<Color32> batchColors = new List<Color32>(8192);
        private readonly List<Vector2> batchUvs = new List<Vector2>(8192);

        private readonly List<BatchInstance> batches = new List<BatchInstance>();
        private int usedBatchCount;
        private readonly List<RetainedInstance> retainedInstances =
            new List<RetainedInstance>();
        private int usedRetainedCount;
        private Mesh retainedStencilQuad;

        // Large Flash movies are faster when their triangulated local meshes remain
        // resident on the GPU and only the small instance matrix changes per frame.
        // The legacy CPU batcher remains available for comparison and fallback.
        public bool UseRetainedGpuRendering { get; set; } = false;

        // Geometry batches only as long as it shares a texture, so the active
        // texture is part of the batch key alongside the stencil state.
        private Texture batchTexture;
        private static Texture2D whitePixel;

        private int stencilMode;
        private int stencilReference;
        private int stencilReadMask = 255;
        private int stencilWriteMask = 255;
        private readonly Stack<StencilState> stencilStateStack = new Stack<StencilState>();

        public SwfRasterFillRenderer(Transform root, float stageWidth = 600f, float stageHeight = 400f)
        {
            this.stageWidth = stageWidth;
            this.stageHeight = stageHeight;

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
            // Geometry is a pure function of the shape and the quality settings, so
            // the cache only needs discarding when those settings actually change.
            if (cachedQualityRevision != SwfRenderQuality.Revision)
            {
                cachedQualityRevision = SwfRenderQuality.Revision;
                ClearMeshCache();
                ClearRasterShapeCache();
                stencilMaterials.Clear();
                ClearGradientTextures();
                ApplyQualityToMaterials();
            }

            ClearBatchBuffers();
            usedBatchCount = 0;
            usedRetainedCount = 0;
            stencilMode = 0;
            stencilReference = 0;
            stencilReadMask = 255;
            stencilWriteMask = 255;
            stencilStateStack.Clear();
        }

        public void EndFrame()
        {
            if (!UseRetainedGpuRendering)
                FlushBatch();

            for (int i = usedBatchCount; i < batches.Count; i++)
            {
                if (batches[i].GameObject.activeSelf)
                    batches[i].GameObject.SetActive(false);
            }

            for (int i = usedRetainedCount; i < retainedInstances.Count; i++)
            {
                if (retainedInstances[i].GameObject.activeSelf)
                    retainedInstances[i].GameObject.SetActive(false);
            }
        }

        // Stencil state is baked into the material, so a change forces the
        // current geometry out as its own draw before the new state begins.
        public void BeginMaskWrite(int reference)
        {
            FlushBatch();
            StencilState previous = new StencilState
            {
                Mode = stencilMode,
                Reference = stencilReference,
                ReadMask = stencilReadMask,
                WriteMask = stencilWriteMask
            };
            stencilStateStack.Push(previous);

            // With stencil masking off, the mask shape must still be hidden - it is
            // a clipping region, not artwork - so mode 3 writes no colour and no
            // stencil. The content it would have clipped then draws unclipped.
            if (!SwfRenderQuality.Settings.StencilMasks)
            {
                stencilMode = 3;
                stencilReference = 0;
                stencilReadMask = 255;
                stencilWriteMask = 0;
                return;
            }

            // One stencil bit represents one active mask depth. Reusing references
            // with Replace made an inner setMask overwrite its parent's value, so
            // everything rendered after the inner mask failed the parent test. A bit
            // per depth preserves the complete mask intersection.
            int depth = stencilStateStack.Count - 1;
            int parentMask = previous.Mode == 2 ? previous.Reference : 0;

            // The hardware stencil has eight bits. At a deeper nesting level keep
            // the already-active parent mask and draw the extra level unclipped;
            // corrupting bit 7 would make the parent and all later siblings vanish.
            if (depth >= 8)
            {
                stencilMode = 3;
                stencilReference = parentMask;
                stencilReadMask = parentMask;
                stencilWriteMask = 0;
                return;
            }

            int bit = 1 << depth;

            // Sibling masks reuse the same depth bit. Clear it before writing the new
            // shape or pixels left by an earlier sibling would leak into this mask.
            ClearStencilBit(bit);

            stencilMode = 1;
            stencilReference = parentMask | bit;
            stencilReadMask = parentMask;
            stencilWriteMask = bit;
        }

        public void BeginMaskedContent(int reference)
        {
            FlushBatch();

            if (!SwfRenderQuality.Settings.StencilMasks)
            {
                // Keep mode 3 until EndStencil so geometry belonging to the mask
                // remains hidden even when clipping is disabled at Low quality.
                StencilState parent = stencilStateStack.Count > 0
                    ? stencilStateStack.Peek()
                    : default;
                stencilMode = parent.Mode;
                stencilReference = parent.Reference;
                stencilReadMask = parent.ReadMask == 0 ? 255 : parent.ReadMask;
                stencilWriteMask = parent.WriteMask;
                return;
            }

            stencilMode = 2;
            stencilReadMask = stencilReference;
            stencilWriteMask = 0;
        }

        public void EndStencil()
        {
            FlushBatch();

            if (stencilStateStack.Count > 0)
            {
                StencilState state = stencilStateStack.Pop();
                stencilMode = state.Mode;
                stencilReference = state.Reference;
                stencilReadMask = state.ReadMask;
                stencilWriteMask = state.WriteMask;
            }
            else
            {
                stencilMode = 0;
                stencilReference = 0;
                stencilReadMask = 255;
                stencilWriteMask = 255;
            }
        }

        private void ClearStencilBit(int bit)
        {
            StencilState writer = new StencilState
            {
                Mode = stencilMode,
                Reference = stencilReference,
                ReadMask = stencilReadMask,
                WriteMask = stencilWriteMask
            };

            stencilMode = 4;
            stencilReference = 0;
            stencilReadMask = 255;
            stencilWriteMask = bit;

            if (UseRetainedGpuRendering)
            {
                DrawRetainedStencilClear();
                stencilMode = writer.Mode;
                stencilReference = writer.Reference;
                stencilReadMask = writer.ReadMask;
                stencilWriteMask = writer.WriteMask;
                return;
            }

            batchTexture = whitePixel;

            float halfWidth = stageWidth / (PixelsPerUnit * 2f);
            float halfHeight = stageHeight / (PixelsPerUnit * 2f);
            batchVertices.Add(new Vector3(-halfWidth, -halfHeight, -0.02f));
            batchVertices.Add(new Vector3(-halfWidth, halfHeight, -0.02f));
            batchVertices.Add(new Vector3(halfWidth, halfHeight, -0.02f));
            batchVertices.Add(new Vector3(halfWidth, -halfHeight, -0.02f));

            for (int i = 0; i < 4; i++)
            {
                // The shader alpha-clips before the stencil operation. Keep these
                // fragments opaque; ColorMask=0 still guarantees no colour output.
                batchColors.Add(new Color32(255, 255, 255, 255));
                batchUvs.Add(Vector2.zero);
            }

            batchTriangles.Add(0);
            batchTriangles.Add(1);
            batchTriangles.Add(2);
            batchTriangles.Add(0);
            batchTriangles.Add(2);
            batchTriangles.Add(3);
            FlushBatch();

            stencilMode = writer.Mode;
            stencilReference = writer.Reference;
            stencilReadMask = writer.ReadMask;
            stencilWriteMask = writer.WriteMask;
        }

        public void DrawShapeRasterFill(DefineShapeTag shape, SwfMatrix matrix, float alpha)
        {
            if (shape == null || shape.ShapeData == null || shape.ShapeData.FillEdgeGroups == null)
                return;

            if (alpha <= 0.004f)
                return;

            // Complex vector artwork is converted once into a transparent texture
            // directly from its fill-edge graph. This bypasses both the fragile
            // contour stitching/ear clipping path and the per-frame vertex upload.
            // Simple shapes keep the tiny vector mesh path, while bitmap rectangles
            // already use their original decoded image as a four-vertex quad.
            if (!UseRetainedGpuRendering && ShouldRasterizeShape(shape) &&
                TryGetRasterizedShape(shape, out RasterizedShape rasterized))
            {
                AppendRasterizedShape(rasterized, matrix, alpha);
                return;
            }

            List<SwfFillEdgeGroup> groups = shape.ShapeData.FillEdgeGroups;

            // Higher fill style indices are detail/shadow layers that belong
            // behind the primary fill, so emit them first (painter's order).
            for (int pass = 0; pass < 2; pass++)
            {
                for (int g = 0; g < groups.Count; g++)
                {
                    SwfFillEdgeGroup group = groups[g];

                    if (group == null || group.Contours == null || group.Contours.Count == 0)
                        continue;

                    bool isPrimary = group.FillStyleIndex <= 1;

                    if ((pass == 0) == isPrimary)
                        continue;

                    SwfFillStyle style = GetFillStyle(shape, group.FillStyleIndex);

                    if (style == null)
                        continue;

                    // Fill bytes in a SWF are sRGB, while this project's batched
                    // mesh shader consumes vertex colours in working (linear)
                    // space. Without this conversion every solid fill is washed
                    // out, including the logo's orange and its darker cut shadows.
                    Color fillColor = SwfRenderQuality.ToVertexColor(style.ToUnityColor());
                    Texture texture = style.IsGradient
                        ? ResolveGradientTexture(style)
                        : ResolveBitmapTexture(style);

                    // A fill type outside the solid/gradient/bitmap families cannot be
                    // drawn, and drawing it as flat white would misrepresent the art.
                    if (!style.IsBitmap && !style.IsGradient && style.FillType != 0x00)
                    {
                        SwfRenderDiagnostics.Report(
                            SwfRenderProblem.UnsupportedFillStyle,
                            shape.CharacterId,
                            style.FillType,
                            "fill style type 0x" + style.FillType.ToString("X2") +
                            " in fill group " + g + " is not a recognised SWF fill");
                    }

                    if (style.IsBitmap)
                    {
                        // A bitmap fill whose character cannot be resolved draws
                        // nothing in Flash. Content commonly carries fills bound to
                        // id 65535 (the "no bitmap" sentinel); painting those as an
                        // opaque quad covered the whole stage.
                        if (texture == null)
                        {
                            SwfRenderDiagnostics.Report(
                                SwfRenderProblem.MissingBitmap,
                                shape.CharacterId,
                                style.BitmapId,
                                "bitmap character " + style.BitmapId +
                                " could not be resolved for fill group " + g +
                                "; nothing is drawn for it");
                            continue;
                        }

                        // Colour comes from the texture, so the vertex colour stays
                        // neutral and only carries alpha.
                        fillColor = Color.white;
                    }
                    else if (style.IsGradient)
                    {
                        // Gradient colour comes from a generated sRGB ramp. The old
                        // average-colour placeholder erased 196 fills in Marvin
                        // Spectrum and made whole surfaces look missing.
                        if (texture == null)
                            continue;

                        fillColor = Color.white;
                    }
                    else if (fillColor.a <= 0.004f)
                    {
                        continue;
                    }

                    fillColor.a *= alpha;
                    if (UseRetainedGpuRendering)
                    {
                        AppendRetainedFill(
                            shape, g, group, matrix, fillColor, style, texture);
                    }
                    else
                    {
                        AppendFill(shape, g, group, matrix, fillColor, style, texture);
                    }
                }
            }
        }

        private Texture ResolveBitmapTexture(SwfFillStyle style)
        {
            if (style == null || !style.IsBitmap || BitmapProvider == null)
                return null;

            return BitmapProvider(style.BitmapId);
        }

        private Texture2D ResolveGradientTexture(SwfFillStyle style)
        {
            if (style == null || !style.IsGradient || style.GradientStops == null ||
                style.GradientStops.Count == 0)
            {
                return null;
            }

            if (gradientTextures.TryGetValue(style, out Texture2D cached) && cached != null)
                return cached;

            int resolution = Mathf.Clamp(
                Mathf.RoundToInt(128f * SwfRenderQuality.Settings.RasterizationScale),
                64,
                256
            );
            bool radial = style.FillType == 0x12 || style.FillType == 0x13;
            int width = resolution;
            int height = radial ? resolution : 1;
            Color32[] pixels = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                float normalY = height > 1 ? y / (float)(height - 1) * 2f - 1f : 0f;

                for (int x = 0; x < width; x++)
                {
                    float t;

                    if (radial)
                    {
                        float normalX = x / (float)(width - 1) * 2f - 1f;
                        t = Mathf.Sqrt(normalX * normalX + normalY * normalY);
                    }
                    else
                    {
                        t = x / (float)(width - 1);
                    }

                    pixels[y * width + x] = EvaluateGradient(style, Mathf.Clamp01(t));
                }
            }

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "SWF Gradient 0x" + style.FillType.ToString("X2"),
                wrapMode = TextureWrapMode.Clamp,
                filterMode = SwfRenderQuality.Settings.BitmapFilter,
                anisoLevel = 0,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            gradientTextures[style] = texture;
            return texture;
        }

        private static Color32 EvaluateGradient(SwfFillStyle style, float t)
        {
            List<SwfGradientStop> stops = style.GradientStops;

            if (stops.Count == 1 || t * 255f <= stops[0].Ratio)
                return stops[0].Color;

            float ratio = t * 255f;

            for (int i = 1; i < stops.Count; i++)
            {
                SwfGradientStop right = stops[i];

                if (ratio > right.Ratio)
                    continue;

                SwfGradientStop left = stops[i - 1];
                float span = Mathf.Max(1f, right.Ratio - left.Ratio);
                float amount = Mathf.Clamp01((ratio - left.Ratio) / span);
                return Color.LerpUnclamped(left.Color, right.Color, amount);
            }

            return stops[stops.Count - 1].Color;
        }

        private static bool ShouldRasterizeShape(DefineShapeTag shape)
        {
            List<SwfFillEdgeGroup> groups = shape?.ShapeData?.FillEdgeGroups;

            if (groups == null)
                return false;

            // The vector mesh path historically emitted fills only. Rasterizing
            // every shape that owns a real SWF LINESTYLE preserves line-only HUD,
            // text-like artwork and outlines without adding thousands of Unity
            // LineRenderers or draw calls.
            List<SwfShapePath> paths = shape.ShapeData.Paths;

            if (paths != null)
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (paths[i] != null && paths[i].LineStyle > 0 &&
                        paths[i].Points != null && paths[i].Points.Count > 1)
                    {
                        return true;
                    }
                }
            }

            int edgeCount = 0;

            for (int i = 0; i < groups.Count; i++)
            {
                SwfFillEdgeGroup group = groups[i];

                if (group == null)
                    continue;

                edgeCount += group.Edges != null ? group.Edges.Count : 0;

                if (group.Contours == null)
                    continue;

                for (int c = 0; c < group.Contours.Count; c++)
                {
                    if (group.Contours[c] != null && !group.Contours[c].IsClosed)
                        return true;
                }
            }

            // Ear clipping scales poorly and is least reliable on long outlines.
            // A cached scanline image turns these into the same four-vertex path as
            // an ordinary SWF bitmap after a single build.
            return edgeCount > 96;
        }

        private bool TryGetRasterizedShape(
            DefineShapeTag shape,
            out RasterizedShape rasterized)
        {
            if (rasterShapeCache.TryGetValue(shape.CharacterId, out rasterized))
                return rasterized != null;

            rasterized = BuildRasterizedShape(shape);

            if (rasterized?.Texture != null)
            {
                long addedPixels = (long)rasterized.Texture.width * rasterized.Texture.height;
                long pixelBudget = GetRasterCachePixelBudget();

                // Clear as one generation instead of maintaining thousands of LRU
                // nodes on the hot draw path. A scene normally settles far below the
                // budget; this is only a guard against a movie cycling through many
                // full-stage vector images and retaining all of them forever.
                if (rasterShapeCachePixels > 0 &&
                    rasterShapeCachePixels + addedPixels > pixelBudget)
                {
                    ClearRasterShapeCache();
                }

                rasterShapeCachePixels += addedPixels;
                PackRasterizedShape(rasterized);
            }

            rasterShapeCache[shape.CharacterId] = rasterized;
            return rasterized != null;
        }

        private static long GetRasterCachePixelBudget()
        {
            switch (SwfRenderQuality.Level)
            {
                case SwfQualityLevel.Low: return 16L * 1024L * 1024L;    // 64 MiB RGBA
                case SwfQualityLevel.Medium: return 32L * 1024L * 1024L; // 128 MiB
                case SwfQualityLevel.Ultra: return 96L * 1024L * 1024L;  // 384 MiB
                default: return 64L * 1024L * 1024L;                      // 256 MiB
            }
        }

        private RasterizedShape BuildRasterizedShape(DefineShapeTag shape)
        {
            float minX = shape.ShapeBounds.XMinPixels;
            float maxX = shape.ShapeBounds.XMaxPixels;
            float minY = shape.ShapeBounds.YMinPixels;
            float maxY = shape.ShapeBounds.YMaxPixels;
            float boundsWidth = maxX - minX;
            float boundsHeight = maxY - minY;

            if (boundsWidth <= 0.001f || boundsHeight <= 0.001f)
                return null;

            SwfQualitySettings settings = SwfRenderQuality.Settings;
            float requestedScale = Mathf.Max(0.25f, settings.RasterizationScale);
            int sizeCap = Mathf.Max(64, settings.MaxTextureSize);
            float scale = Mathf.Min(
                requestedScale,
                sizeCap / boundsWidth,
                sizeCap / boundsHeight);
            int width = Mathf.Clamp(Mathf.CeilToInt(boundsWidth * scale), 1, sizeCap);
            int height = Mathf.Clamp(Mathf.CeilToInt(boundsHeight * scale), 1, sizeCap);
            float scaleX = width / boundsWidth;
            float scaleY = height / boundsHeight;
            Color32[] output = new Color32[width * height];
            List<float> intersections = new List<float>(256);
            List<SwfFillEdgeGroup> groups = shape.ShapeData.FillEdgeGroups;

            // Preserve the renderer's established fill order while producing one
            // composited texture. Most SWF fill regions do not overlap, but artwork
            // that deliberately layers a shadow/detail fill still behaves the same.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int g = 0; g < groups.Count; g++)
                {
                    SwfFillEdgeGroup group = groups[g];

                    if (group?.Edges == null || group.Edges.Count == 0)
                        continue;

                    bool isPrimary = group.FillStyleIndex <= 1;

                    if ((pass == 0) == isPrimary)
                        continue;

                    SwfFillStyle style = GetFillStyle(shape, group.FillStyleIndex);
                    RasterFillSampler sampler = CreateRasterFillSampler(style);

                    if (sampler == null)
                        continue;

                    for (int py = 0; py < height; py++)
                    {
                        float localY = maxY - (py + 0.5f) / scaleY;
                        intersections.Clear();

                        for (int e = 0; e < group.Edges.Count; e++)
                        {
                            SwfShapeEdge edge = group.Edges[e];
                            Vector2 a = edge.Start;
                            Vector2 b = edge.End;

                            // Half-open crossing rule prevents a shared vertex from
                            // being counted twice and works directly on the unordered
                            // fill graph; no stitched contour is required.
                            if ((a.y > localY) == (b.y > localY))
                                continue;

                            float x = a.x +
                                (localY - a.y) * (b.x - a.x) / (b.y - a.y);

                            if (!float.IsNaN(x) && !float.IsInfinity(x))
                                intersections.Add(x);
                        }

                        if (intersections.Count < 2)
                            continue;

                        intersections.Sort();

                        for (int pair = 0; pair + 1 < intersections.Count; pair += 2)
                        {
                            float left = Mathf.Max(minX, intersections[pair]);
                            float right = Mathf.Min(maxX, intersections[pair + 1]);

                            if (right <= left)
                                continue;

                            int startX = Mathf.Clamp(
                                Mathf.CeilToInt((left - minX) * scaleX - 0.5f),
                                0,
                                width - 1);
                            int endX = Mathf.Clamp(
                                Mathf.FloorToInt((right - minX) * scaleX - 0.5f),
                                0,
                                width - 1);

                            for (int px = startX; px <= endX; px++)
                            {
                                float localX = minX + (px + 0.5f) / scaleX;
                                Color32 source = SampleRasterFill(
                                    sampler,
                                    new Vector2(localX, localY));

                                if (source.a != 0)
                                {
                                    int pixelIndex = py * width + px;
                                    output[pixelIndex] = CompositeOver(output[pixelIndex], source);
                                }
                            }
                        }
                    }
                }
            }

            RasterizeShapeLines(
                shape,
                output,
                width,
                height,
                minX,
                maxY,
                scaleX,
                scaleY);

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "SWF Rasterized Shape " + shape.CharacterId,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = settings.BitmapFilter,
                anisoLevel = settings.BitmapAnisoLevel,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixels32(output);
            texture.Apply(false, true);

            return new RasterizedShape
            {
                Texture = texture,
                OwnsTexture = true,
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY,
                UvMin = Vector2.zero,
                UvMax = Vector2.one
            };
        }

        private void RasterizeShapeLines(
            DefineShapeTag shape,
            Color32[] output,
            int width,
            int height,
            float minX,
            float maxY,
            float scaleX,
            float scaleY)
        {
            List<SwfShapePath> paths = shape?.ShapeData?.Paths;
            List<SwfLineStyle> styles = shape?.ShapeData?.LineStyles;

            if (paths == null || styles == null || styles.Count == 0)
                return;

            float averageScale = Mathf.Max(0.0001f, (scaleX + scaleY) * 0.5f);
            float antiAliasWidth = 0.75f / averageScale;

            for (int p = 0; p < paths.Count; p++)
            {
                SwfShapePath path = paths[p];
                int styleIndex = path != null ? path.LineStyle - 1 : -1;

                if (styleIndex < 0 || styleIndex >= styles.Count ||
                    path.Points == null || path.Points.Count < 2)
                {
                    continue;
                }

                SwfLineStyle line = styles[styleIndex];
                RasterFillSampler fillSampler = line.HasFillStyle
                    ? CreateRasterFillSampler(GetFillStyle(shape, line.FillStyleIndex + 1))
                    : null;
                Color32 solid = (Color32)line.Color;
                float halfWidth = Mathf.Max(line.Width * 0.5f, 0.5f / averageScale);
                float sampleRadius = halfWidth + antiAliasWidth;

                for (int segment = 0; segment + 1 < path.Points.Count; segment++)
                {
                    Vector2 a = path.Points[segment];
                    Vector2 b = path.Points[segment + 1];
                    float segmentLengthSquared = (b - a).sqrMagnitude;

                    if (segmentLengthSquared <= 0.0000001f)
                        continue;

                    int startX = Mathf.Clamp(
                        Mathf.FloorToInt((Mathf.Min(a.x, b.x) - sampleRadius - minX) * scaleX),
                        0,
                        width - 1);
                    int endX = Mathf.Clamp(
                        Mathf.CeilToInt((Mathf.Max(a.x, b.x) + sampleRadius - minX) * scaleX),
                        0,
                        width - 1);
                    int startY = Mathf.Clamp(
                        Mathf.FloorToInt((maxY - Mathf.Max(a.y, b.y) - sampleRadius) * scaleY),
                        0,
                        height - 1);
                    int endY = Mathf.Clamp(
                        Mathf.CeilToInt((maxY - Mathf.Min(a.y, b.y) + sampleRadius) * scaleY),
                        0,
                        height - 1);

                    for (int py = startY; py <= endY; py++)
                    {
                        float localY = maxY - (py + 0.5f) / scaleY;

                        for (int px = startX; px <= endX; px++)
                        {
                            float localX = minX + (px + 0.5f) / scaleX;
                            Vector2 point = new Vector2(localX, localY);
                            float amount = Mathf.Clamp01(
                                Vector2.Dot(point - a, b - a) / segmentLengthSquared);
                            float distance = Vector2.Distance(point, a + (b - a) * amount);
                            float coverage = Mathf.Clamp01(
                                (halfWidth + antiAliasWidth - distance) / antiAliasWidth);

                            if (coverage <= 0f)
                                continue;

                            Color32 source = fillSampler != null
                                ? SampleRasterFill(fillSampler, point)
                                : solid;
                            source.a = (byte)Mathf.Clamp(
                                Mathf.RoundToInt(source.a * coverage), 0, 255);

                            if (source.a != 0)
                            {
                                int pixelIndex = py * width + px;
                                output[pixelIndex] = CompositeOver(output[pixelIndex], source);
                            }
                        }
                    }
                }
            }
        }

        private void PackRasterizedShape(RasterizedShape rasterized)
        {
            Texture2D source = rasterized?.Texture;

            if (source == null)
                return;

            const int padding = 2;
            RasterAtlas selected = null;
            RectInt destination = default;

            for (int i = 0; i < rasterAtlases.Count; i++)
            {
                if (rasterAtlases[i].TryAllocate(
                    source.width + padding * 2,
                    source.height + padding * 2,
                    out destination))
                {
                    selected = rasterAtlases[i];
                    break;
                }
            }

            if (selected == null)
            {
                int requestedSize = SwfRenderQuality.Level == SwfQualityLevel.Low
                    ? 2048
                    : 4096;
                int atlasSize = Mathf.Min(requestedSize, SystemInfo.maxTextureSize);

                if (source.width + padding * 2 > atlasSize ||
                    source.height + padding * 2 > atlasSize)
                {
                    return; // keep the standalone texture
                }

                Texture2D atlasTexture = new Texture2D(
                    atlasSize,
                    atlasSize,
                    TextureFormat.RGBA32,
                    false)
                {
                    name = "SWF Raster Shape Atlas " + rasterAtlases.Count,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = SwfRenderQuality.Settings.BitmapFilter,
                    anisoLevel = SwfRenderQuality.Settings.BitmapAnisoLevel,
                    hideFlags = HideFlags.HideAndDontSave
                };
                atlasTexture.Apply(false, true);
                selected = new RasterAtlas(atlasTexture);
                rasterAtlases.Add(selected);

                if (!selected.TryAllocate(
                    source.width + padding * 2,
                    source.height + padding * 2,
                    out destination))
                {
                    return;
                }
            }

            int copyX = destination.x + padding;
            int copyY = destination.y + padding;

            try
            {
                Graphics.CopyTexture(
                    source,
                    0,
                    0,
                    0,
                    0,
                    source.width,
                    source.height,
                    selected.Texture,
                    0,
                    0,
                    copyX,
                    copyY);
            }
            catch (UnityException)
            {
                return; // platform copy unsupported; standalone texture remains valid
            }

            rasterized.Texture = selected.Texture;
            rasterized.OwnsTexture = false;
            rasterized.UvMin = new Vector2(
                (copyX + 0.5f) / selected.Texture.width,
                (copyY + 0.5f) / selected.Texture.height);
            rasterized.UvMax = new Vector2(
                (copyX + source.width - 0.5f) / selected.Texture.width,
                (copyY + source.height - 0.5f) / selected.Texture.height);
            Object.DestroyImmediate(source);
        }

        private RasterFillSampler CreateRasterFillSampler(SwfFillStyle style)
        {
            if (style == null)
                return null;

            RasterFillSampler sampler = new RasterFillSampler { Style = style };

            if (style.IsBitmap)
            {
                Texture2D texture = ResolveBitmapTexture(style) as Texture2D;

                if (texture == null || !style.BitmapMatrix.TryInvert(out sampler.InverseMatrix))
                    return null;

                if (!readableTexturePixels.TryGetValue(texture, out sampler.Pixels))
                {
                    try
                    {
                        sampler.Pixels = texture.GetPixels32();
                        readableTexturePixels[texture] = sampler.Pixels;
                    }
                    catch (UnityException)
                    {
                        return null;
                    }
                }

                sampler.TextureWidth = texture.width;
                sampler.TextureHeight = texture.height;
                Vector2Int sourceSize = BitmapSizeProvider != null
                    ? BitmapSizeProvider(style.BitmapId)
                    : new Vector2Int(texture.width, texture.height);
                sampler.SourceWidth = Mathf.Max(1, sourceSize.x);
                sampler.SourceHeight = Mathf.Max(1, sourceSize.y);
            }
            else if (style.IsGradient &&
                !style.GradientMatrix.TryInvert(out sampler.InverseMatrix))
            {
                return null;
            }

            return sampler;
        }

        private static Color32 SampleRasterFill(RasterFillSampler sampler, Vector2 local)
        {
            SwfFillStyle style = sampler.Style;

            if (!style.IsBitmap && !style.IsGradient)
                return new Color32(style.R, style.G, style.B, style.A);

            Vector2 fillPoint = sampler.InverseMatrix.TransformPoint(local) * TwipsPerPixel;

            if (style.IsGradient)
            {
                float t;

                if (style.FillType == 0x12 || style.FillType == 0x13)
                {
                    float gx = fillPoint.x / 16384f;
                    float gy = fillPoint.y / 16384f;
                    t = Mathf.Sqrt(gx * gx + gy * gy);
                }
                else
                {
                    t = fillPoint.x / 32768f + 0.5f;
                }

                return EvaluateGradient(style, Mathf.Clamp01(t));
            }

            float u = fillPoint.x / sampler.SourceWidth;
            float v = 1f - fillPoint.y / sampler.SourceHeight;
            bool repeating = style.FillType == 0x40 || style.FillType == 0x42;

            if (repeating)
            {
                u = Mathf.Repeat(u, 1f);
                v = Mathf.Repeat(v, 1f);
            }
            else if (u < 0f || u > 1f || v < 0f || v > 1f)
            {
                return new Color32(0, 0, 0, 0);
            }

            return SampleBilinear(
                sampler.Pixels,
                sampler.TextureWidth,
                sampler.TextureHeight,
                Mathf.Clamp01(u),
                Mathf.Clamp01(v));
        }

        private static Color32 SampleBilinear(
            Color32[] pixels,
            int width,
            int height,
            float u,
            float v)
        {
            float x = u * Mathf.Max(0, width - 1);
            float y = v * Mathf.Max(0, height - 1);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, height - 1);
            int x1 = Mathf.Min(width - 1, x0 + 1);
            int y1 = Mathf.Min(height - 1, y0 + 1);
            float tx = x - x0;
            float ty = y - y0;
            Color c00 = pixels[y0 * width + x0];
            Color c10 = pixels[y0 * width + x1];
            Color c01 = pixels[y1 * width + x0];
            Color c11 = pixels[y1 * width + x1];
            return (Color32)Color.LerpUnclamped(
                Color.LerpUnclamped(c00, c10, tx),
                Color.LerpUnclamped(c01, c11, tx),
                ty);
        }

        private static Color32 CompositeOver(Color32 destination, Color32 source)
        {
            float sourceAlpha = source.a / 255f;
            float destinationAlpha = destination.a / 255f;
            float outputAlpha = sourceAlpha + destinationAlpha * (1f - sourceAlpha);

            if (outputAlpha <= 0.0001f)
                return new Color32(0, 0, 0, 0);

            float destinationWeight = destinationAlpha * (1f - sourceAlpha);
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(
                    (source.r * sourceAlpha + destination.r * destinationWeight) /
                    outputAlpha), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(
                    (source.g * sourceAlpha + destination.g * destinationWeight) /
                    outputAlpha), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(
                    (source.b * sourceAlpha + destination.b * destinationWeight) /
                    outputAlpha), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(outputAlpha * 255f), 0, 255));
        }

        private void AppendRasterizedShape(
            RasterizedShape rasterized,
            SwfMatrix matrix,
            float alpha)
        {
            if (batchTexture != null && batchTexture != rasterized.Texture)
                FlushBatch();

            if (batchVertices.Count + 4 > MaxBatchVertices)
                FlushBatch();

            batchTexture = rasterized.Texture;
            int baseIndex = batchVertices.Count;
            batchVertices.Add(FlashToUnityPoint(rasterized.MinX, rasterized.MinY, matrix));
            batchVertices.Add(FlashToUnityPoint(rasterized.MinX, rasterized.MaxY, matrix));
            batchVertices.Add(FlashToUnityPoint(rasterized.MaxX, rasterized.MaxY, matrix));
            batchVertices.Add(FlashToUnityPoint(rasterized.MaxX, rasterized.MinY, matrix));
            byte packedAlpha = (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255);

            for (int i = 0; i < 4; i++)
                batchColors.Add(new Color32(255, 255, 255, packedAlpha));

            batchUvs.Add(new Vector2(rasterized.UvMin.x, rasterized.UvMax.y));
            batchUvs.Add(new Vector2(rasterized.UvMin.x, rasterized.UvMin.y));
            batchUvs.Add(new Vector2(rasterized.UvMax.x, rasterized.UvMin.y));
            batchUvs.Add(new Vector2(rasterized.UvMax.x, rasterized.UvMax.y));
            batchTriangles.Add(baseIndex);
            batchTriangles.Add(baseIndex + 1);
            batchTriangles.Add(baseIndex + 2);
            batchTriangles.Add(baseIndex);
            batchTriangles.Add(baseIndex + 2);
            batchTriangles.Add(baseIndex + 3);
        }

        public void DrawTextureQuad(
            Texture2D texture,
            float width,
            float height,
            SwfMatrix matrix,
            float alpha)
        {
            if (texture == null || width <= 0f || height <= 0f || alpha <= 0.004f)
                return;

            AppendRasterizedShape(
                new RasterizedShape
                {
                    Texture = texture,
                    MinX = 0f,
                    MinY = 0f,
                    MaxX = width,
                    MaxY = height,
                    UvMin = Vector2.zero,
                    UvMax = Vector2.one
                },
                matrix,
                alpha);
        }

        private void ClearRasterShapeCache()
        {
            foreach (RasterizedShape rasterized in rasterShapeCache.Values)
            {
                if (rasterized?.OwnsTexture == true && rasterized.Texture != null)
                    Object.DestroyImmediate(rasterized.Texture);
            }

            for (int i = 0; i < rasterAtlases.Count; i++)
            {
                if (rasterAtlases[i]?.Texture != null)
                    Object.DestroyImmediate(rasterAtlases[i].Texture);
            }

            rasterShapeCache.Clear();
            rasterAtlases.Clear();
            readableTexturePixels.Clear();
            rasterShapeCachePixels = 0;
        }

        private void ClearGradientTextures()
        {
            foreach (Texture2D texture in gradientTextures.Values)
            {
                if (texture != null)
                    Object.DestroyImmediate(texture);
            }

            gradientTextures.Clear();
        }

        private void ClearMeshCache()
        {
            foreach (LocalMeshData meshData in meshCache.Values)
            {
                if (meshData?.RetainedMesh != null)
                    Object.DestroyImmediate(meshData.RetainedMesh);
            }

            meshCache.Clear();
        }

        // Set by SwfPlayer so the renderer can resolve a fill's bitmap id into a
        // decoded texture without taking a hard dependency on the parser.
        public System.Func<ushort, Texture> BitmapProvider { get; set; }

        // UVs are defined in the source bitmap's coordinate system. Quality may
        // upload a smaller texture, but its normalised 0..1 UV range is unchanged;
        // dividing by the reduced GPU size makes every sample run off the edge and
        // clamp to one grey pixel (most visible on large Isaac backgrounds).
        public System.Func<ushort, Vector2Int> BitmapSizeProvider { get; set; }

        private void AppendRetainedFill(
            DefineShapeTag shape,
            int groupIndex,
            SwfFillEdgeGroup group,
            SwfMatrix matrix,
            Color color,
            SwfFillStyle style,
            Texture texture
        )
        {
            MeshCacheKey cacheKey = new MeshCacheKey(shape.CharacterId, groupIndex);

            if (!meshCache.TryGetValue(cacheKey, out LocalMeshData meshData))
            {
                meshData = BuildLocalMesh(group, shape.CharacterId, style);
                meshCache[cacheKey] = meshData;
                MeshBuildCount++;
            }

            if (meshData == null || meshData.Triangles.Length == 0)
                return;

            if (meshData.RetainedMesh == null)
                meshData.RetainedMesh = BuildRetainedMesh(meshData, style, texture);

            if (meshData.RetainedMesh == null)
                return;

            RetainedInstance instance = AcquireRetainedInstance();
            instance.Filter.sharedMesh = meshData.RetainedMesh;
            instance.Renderer.sharedMaterial = GetActiveMaterial();
            instance.Renderer.sortingOrder = usedRetainedCount - 1;

            instance.PropertyBlock.Clear();
            instance.PropertyBlock.SetTexture(
                MainTextureId,
                texture != null ? texture : whitePixel);
            instance.PropertyBlock.SetVector(TintColorId, color);
            instance.PropertyBlock.SetFloat(UseSwfMatrixId, 1f);
            instance.PropertyBlock.SetVector(
                SwfMatrix0Id,
                new Vector4(
                    matrix.ScaleX / PixelsPerUnit,
                    matrix.RotateSkew1 / PixelsPerUnit,
                    0f,
                    (matrix.TranslateX - stageWidth * 0.5f) / PixelsPerUnit));
            instance.PropertyBlock.SetVector(
                SwfMatrix1Id,
                new Vector4(
                    -matrix.RotateSkew0 / PixelsPerUnit,
                    -matrix.ScaleY / PixelsPerUnit,
                    0f,
                    (stageHeight * 0.5f - matrix.TranslateY) / PixelsPerUnit));
            instance.Renderer.SetPropertyBlock(instance.PropertyBlock);
        }

        private Mesh BuildRetainedMesh(
            LocalMeshData meshData,
            SwfFillStyle style,
            Texture texture
        )
        {
            int count = meshData.Vertices.Length;
            Vector3[] vertices = new Vector3[count];
            Vector2[] uvs = new Vector2[count];
            Color32[] colors = new Color32[count];
            bool textured = texture != null;
            SwfMatrix inverseFill = SwfMatrix.Identity;
            float texelWidth = 1f;
            float texelHeight = 1f;

            if (textured)
            {
                SwfMatrix fillMatrix = style.IsGradient
                    ? style.GradientMatrix
                    : style.BitmapMatrix;

                if (!fillMatrix.TryInvert(out inverseFill))
                    textured = false;

                if (!style.IsGradient)
                {
                    Vector2Int sourceSize = BitmapSizeProvider != null
                        ? BitmapSizeProvider(style.BitmapId)
                        : new Vector2Int(texture.width, texture.height);
                    texelWidth = Mathf.Max(1, sourceSize.x > 0 ? sourceSize.x : texture.width);
                    texelHeight = Mathf.Max(1, sourceSize.y > 0 ? sourceSize.y : texture.height);
                }
            }

            for (int i = 0; i < count; i++)
            {
                Vector2 local = meshData.Vertices[i];
                vertices[i] = new Vector3(local.x, local.y, -0.02f);
                colors[i] = new Color32(255, 255, 255, 255);

                if (textured && style.IsGradient)
                {
                    Vector2 gradient = inverseFill.TransformPoint(local) * TwipsPerPixel;
                    uvs[i] = new Vector2(
                        gradient.x / 32768f + 0.5f,
                        0.5f - gradient.y / 32768f);
                }
                else if (textured)
                {
                    Vector2 texel = inverseFill.TransformPoint(local) * TwipsPerPixel;
                    uvs[i] = new Vector2(
                        texel.x / texelWidth,
                        1f - texel.y / texelHeight);
                }
            }

            Mesh mesh = new Mesh
            {
                name = "SWF Retained Fill Mesh",
                hideFlags = HideFlags.HideAndDontSave
            };

            if (count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.vertices = vertices;
            mesh.colors32 = colors;
            mesh.uv = uvs;
            mesh.triangles = meshData.Triangles;

            // The vertex shader applies the Flash matrix, which Unity's CPU culler
            // cannot see. A stage-sized conservative bound keeps valid instances
            // from disappearing before reaching the GPU.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            mesh.UploadMeshData(true);
            return mesh;
        }

        private RetainedInstance AcquireRetainedInstance()
        {
            RetainedInstance instance;

            if (usedRetainedCount < retainedInstances.Count)
            {
                instance = retainedInstances[usedRetainedCount];
            }
            else
            {
                GameObject go = new GameObject("__SWF_GPU_" + usedRetainedCount);
                go.transform.SetParent(poolRoot, false);
                MeshFilter filter = go.AddComponent<MeshFilter>();
                MeshRenderer renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = sharedMaterial;
                renderer.sortingLayerName = "Default";
                instance = new RetainedInstance
                {
                    GameObject = go,
                    Filter = filter,
                    Renderer = renderer,
                    PropertyBlock = new MaterialPropertyBlock()
                };
                retainedInstances.Add(instance);
            }

            usedRetainedCount++;

            if (!instance.GameObject.activeSelf)
                instance.GameObject.SetActive(true);

            return instance;
        }

        private void DrawRetainedStencilClear()
        {
            if (retainedStencilQuad == null)
            {
                float halfWidth = stageWidth / (PixelsPerUnit * 2f);
                float halfHeight = stageHeight / (PixelsPerUnit * 2f);
                retainedStencilQuad = new Mesh
                {
                    name = "SWF Retained Stencil Clear",
                    hideFlags = HideFlags.HideAndDontSave,
                    vertices = new[]
                    {
                        new Vector3(-halfWidth, -halfHeight, -0.02f),
                        new Vector3(-halfWidth, halfHeight, -0.02f),
                        new Vector3(halfWidth, halfHeight, -0.02f),
                        new Vector3(halfWidth, -halfHeight, -0.02f)
                    },
                    uv = new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero },
                    colors32 = new[]
                    {
                        new Color32(255, 255, 255, 255),
                        new Color32(255, 255, 255, 255),
                        new Color32(255, 255, 255, 255),
                        new Color32(255, 255, 255, 255)
                    },
                    triangles = new[] { 0, 1, 2, 0, 2, 3 }
                };
                retainedStencilQuad.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
                retainedStencilQuad.UploadMeshData(true);
            }

            RetainedInstance instance = AcquireRetainedInstance();
            instance.Filter.sharedMesh = retainedStencilQuad;
            instance.Renderer.sharedMaterial = GetActiveMaterial();
            instance.Renderer.sortingOrder = usedRetainedCount - 1;
            instance.PropertyBlock.Clear();
            instance.PropertyBlock.SetTexture(MainTextureId, whitePixel);
            instance.PropertyBlock.SetVector(TintColorId, Color.white);
            instance.PropertyBlock.SetFloat(UseSwfMatrixId, 0f);
            instance.Renderer.SetPropertyBlock(instance.PropertyBlock);
        }

        private void AppendFill(
            DefineShapeTag shape,
            int groupIndex,
            SwfFillEdgeGroup group,
            SwfMatrix matrix,
            Color color,
            SwfFillStyle style,
            Texture texture
        )
        {
            // A struct key avoids the string concatenation this path performed for
            // every fill of every frame: profiling measured 120k short-lived strings
            // per 60 frames on the sample content.
            MeshCacheKey cacheKey = new MeshCacheKey(shape.CharacterId, groupIndex);

            if (!meshCache.TryGetValue(cacheKey, out LocalMeshData meshData))
            {
                meshData = BuildLocalMesh(group, shape.CharacterId, style);
                meshCache[cacheKey] = meshData;
                MeshBuildCount++;
            }

            if (meshData == null || meshData.Triangles.Length == 0)
                return;

            Texture wanted = texture != null ? texture : whitePixel;

            if (batchTexture != null && wanted != batchTexture)
                FlushBatch();

            if (batchVertices.Count + meshData.Vertices.Length > MaxBatchVertices)
                FlushBatch();

            batchTexture = wanted;

            // For bitmap fills the stored matrix maps texture space into shape
            // space, so the inverse turns each shape-space vertex back into a
            // texel coordinate, then into a normalised UV.
            bool textured = texture != null;
            SwfMatrix inverseFill = SwfMatrix.Identity;
            float texelWidth = 1f;
            float texelHeight = 1f;

            if (textured)
            {
                SwfMatrix fillMatrix = style.IsGradient
                    ? style.GradientMatrix
                    : style.BitmapMatrix;

                if (!fillMatrix.TryInvert(out inverseFill))
                    textured = false;

                if (!style.IsGradient)
                {
                    Vector2Int sourceSize = BitmapSizeProvider != null
                        ? BitmapSizeProvider(style.BitmapId)
                        : new Vector2Int(texture.width, texture.height);
                    texelWidth = Mathf.Max(1, sourceSize.x > 0 ? sourceSize.x : texture.width);
                    texelHeight = Mathf.Max(1, sourceSize.y > 0 ? sourceSize.y : texture.height);
                }
            }

            int baseIndex = batchVertices.Count;
            Color32 packed = color;

            for (int i = 0; i < meshData.Vertices.Length; i++)
            {
                Vector2 local = meshData.Vertices[i];
                Vector3 world = FlashToUnityPoint(local.x, local.y, matrix);

                if (!IsFinite(world))
                {
                    // A non-finite vertex means the concatenated matrix was degenerate
                    // or carried NaN; drawing it would corrupt the whole batch.
                    SwfRenderDiagnostics.Report(
                        SwfRenderProblem.InvalidMatrix,
                        shape.CharacterId,
                        groupIndex,
                        "transform produced a non-finite vertex for fill group " +
                        groupIndex + "; the fill was skipped");
                    return;
                }

                batchVertices.Add(world);
                batchColors.Add(packed);

                if (textured && style.IsGradient)
                {
                    // SWF gradient space spans -16384..16384 twips. Parser matrices
                    // expose shape coordinates in pixels, hence the same x20 unit
                    // correction used by bitmap fills before normalising to 0..1.
                    Vector2 gradient = inverseFill.TransformPoint(local) * TwipsPerPixel;
                    batchUvs.Add(new Vector2(
                        gradient.x / 32768f + 0.5f,
                        0.5f - gradient.y / 32768f
                    ));
                }
                else if (textured)
                {
                    // The inverse runs in the parser's mixed units: shape points are
                    // pixels and the matrix translate is pixels, but its scale still
                    // maps the bitmap's own texel units into twips. That leaves the
                    // result 20x short of a texel coordinate, which magnified every
                    // texture by 20x on screen.
                    Vector2 texel = inverseFill.TransformPoint(local) * TwipsPerPixel;
                    batchUvs.Add(new Vector2(texel.x / texelWidth, 1f - texel.y / texelHeight));
                }
                else
                {
                    batchUvs.Add(Vector2.zero);
                }
            }

            for (int i = 0; i < meshData.Triangles.Length; i++)
                batchTriangles.Add(baseIndex + meshData.Triangles[i]);
        }

        private void FlushBatch()
        {
            if (batchTriangles.Count == 0)
            {
                ClearBatchBuffers();
                return;
            }

            BatchInstance batch = AcquireBatch();
            Mesh mesh = batch.Mesh;

            // Preserve the mesh's native buffers and use a fixed stage bound. Calling
            // RecalculateBounds walked every uploaded vertex every Flash frame; on
            // Isaac's large display lists that alone creates a visible CPU spike.
            mesh.Clear(false);
            mesh.SetVertices(batchVertices);
            mesh.SetColors(batchColors);
            mesh.SetUVs(0, batchUvs);
            mesh.SetTriangles(batchTriangles, 0, false);
            mesh.bounds = new Bounds(
                Vector3.zero,
                new Vector3(stageWidth / PixelsPerUnit * 2f,
                    stageHeight / PixelsPerUnit * 2f, 10f)
            );

            batch.Renderer.sharedMaterial = GetActiveMaterial();
            batch.Renderer.sortingOrder = usedBatchCount;

            batch.PropertyBlock.Clear();
            batch.PropertyBlock.SetTexture(MainTextureId, batchTexture != null ? batchTexture : whitePixel);
            batch.PropertyBlock.SetVector(TintColorId, Color.white);
            batch.PropertyBlock.SetFloat(UseSwfMatrixId, 0f);
            batch.Renderer.SetPropertyBlock(batch.PropertyBlock);

            ClearBatchBuffers();
        }

        private void ClearBatchBuffers()
        {
            batchVertices.Clear();
            batchTriangles.Clear();
            batchColors.Clear();
            batchUvs.Clear();
            batchTexture = null;
        }

        private LocalMeshData BuildLocalMesh(
            SwfFillEdgeGroup group,
            int characterId,
            SwfFillStyle style)
        {
            if (style != null && style.IsBitmap &&
                TryBuildBitmapQuad(group, out LocalMeshData bitmapQuad))
            {
                return bitmapQuad;
            }

            List<Vector2> vertices = new List<Vector2>();
            List<int> triangles = new List<int>();
            List<SwfFillContour> contours = ApplyQualitySimplification(group.Contours);

            SwfPolygonTriangulator.Triangulate(
                contours, vertices, triangles, characterId, group.FillStyleIndex);

            return new LocalMeshData
            {
                Vertices = vertices.ToArray(),
                Triangles = triangles.ToArray()
            };
        }

        private static bool TryBuildBitmapQuad(
            SwfFillEdgeGroup group,
            out LocalMeshData result)
        {
            result = null;

            if (group?.Contours == null || group.Contours.Count != 1)
                return false;

            List<Vector2> points = group.Contours[0]?.Points;

            if (points == null || points.Count < 4)
                return false;

            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            double twiceArea = 0d;

            for (int i = 0; i < points.Count; i++)
            {
                Vector2 point = points[i];
                Vector2 next = points[(i + 1) % points.Count];
                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxY = Mathf.Max(maxY, point.y);
                twiceArea += (double)point.x * next.y - (double)next.x * point.y;
            }

            float width = maxX - minX;
            float height = maxY - minY;

            if (width <= 0.001f || height <= 0.001f)
                return false;

            double polygonArea = System.Math.Abs(twiceArea) * 0.5d;
            double rectangleArea = (double)width * height;

            // Extra points along a rectangular edge are fine. Any inset, cut-out or
            // diagonal clipping changes the area and must keep the real polygon.
            if (System.Math.Abs(polygonArea - rectangleArea) > rectangleArea * 0.002d)
                return false;

            result = new LocalMeshData
            {
                Vertices = new[]
                {
                    new Vector2(minX, minY),
                    new Vector2(minX, maxY),
                    new Vector2(maxX, maxY),
                    new Vector2(maxX, minY)
                },
                Triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            return true;
        }

        // Collapses points closer together than the active tolerance before
        // triangulation. Triangulation cost tracks point count closely - profiling
        // measured roughly 1.9 ms per fill group at full detail, and a 1.5 pixel
        // tolerance removes 57% of the points on the sample content - so this is the
        // setting that actually buys frame time at the lower levels.
        //
        // Only the geometry handed to the triangulator is affected. The parsed
        // contours are left intact, so bounds, hit testing and script-visible
        // positions are identical at every quality level.
        private List<SwfFillContour> ApplyQualitySimplification(List<SwfFillContour> source)
        {
            float tolerance = SwfRenderQuality.Settings.ContourSimplifyTolerance;

            if (tolerance <= 0f || source == null || source.Count == 0)
                return source;

            simplifiedContours.Clear();
            float squared = tolerance * tolerance;

            for (int i = 0; i < source.Count; i++)
            {
                SwfFillContour contour = source[i];
                List<Vector2> points = contour?.Points;

                // Below five points there is nothing to remove without collapsing the
                // polygon, so those pass through untouched.
                if (points == null || points.Count < 5)
                {
                    if (contour != null)
                        simplifiedContours.Add(contour);

                    continue;
                }

                simplifyBuffer.Clear();
                Vector2 last = points[0];
                simplifyBuffer.Add(last);

                for (int p = 1; p < points.Count - 1; p++)
                {
                    if ((points[p] - last).sqrMagnitude < squared)
                        continue;

                    last = points[p];
                    simplifyBuffer.Add(last);
                }

                // The final point is always kept so the loop still closes on itself.
                simplifyBuffer.Add(points[points.Count - 1]);

                if (simplifyBuffer.Count < 3)
                {
                    simplifiedContours.Add(contour);
                    continue;
                }

                simplifiedContours.Add(new SwfFillContour
                {
                    FillStyleIndex = contour.FillStyleIndex,
                    IsHoleCandidate = contour.IsHoleCandidate,
                    Points = new List<Vector2>(simplifyBuffer)
                });
            }

            return simplifiedContours;
        }

        // Filtering and anisotropy are material-side, so they are refreshed whenever
        // the quality revision moves rather than sampled per draw.
        private static void ApplyQualityToMaterials()
        {
            if (sharedMaterial == null)
                return;

            SwfQualitySettings settings = SwfRenderQuality.Settings;

            if (whitePixel != null)
            {
                whitePixel.filterMode = settings.BitmapFilter;
                whitePixel.anisoLevel = settings.BitmapAnisoLevel;
            }
        }

        private static void EnsureSharedMaterial()
        {
            if (whitePixel == null)
            {
                // Solid fills sample this so one shader serves both solid and
                // bitmap fills without a branch or a second material.
                whitePixel = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    name = "OpenSWF White Pixel",
                    hideFlags = HideFlags.HideAndDontSave
                };
                whitePixel.SetPixel(0, 0, Color.white);
                whitePixel.Apply(false, true);
            }

            if (sharedMaterial != null)
                return;

            Shader shader = Shader.Find("OpenSWFUnity/SWF Vector Batched");

            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            sharedMaterial = new Material(shader)
            {
                name = "OpenSWF Batched Vector Material",
                hideFlags = HideFlags.HideAndDontSave
            };

            if (sharedMaterial.HasProperty(StencilRefId))
            {
                sharedMaterial.SetFloat(StencilRefId, 0f);
                sharedMaterial.SetFloat(StencilCompId, 8f); // Always
                sharedMaterial.SetFloat(StencilPassId, 0f); // Keep
                sharedMaterial.SetFloat(StencilReadMaskId, 255f);
                sharedMaterial.SetFloat(StencilWriteMaskId, 255f);
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

            int key = stencilMode |
                (stencilReference << 4) |
                (stencilReadMask << 12) |
                (stencilWriteMask << 20);

            if (stencilMaterials.TryGetValue(key, out Material material) && material != null)
                return material;

            // Mode 1 writes the mask into the stencil buffer without drawing it,
            // mode 2 draws only where that mask was written, and mode 3 is the
            // stencil-free fallback: hide the mask shape and clip nothing.
            bool writesMask = stencilMode == 1;
            bool hideOnly = stencilMode == 3;
            bool clearsMask = stencilMode == 4;

            material = new Material(sharedMaterial)
            {
                name = clearsMask
                    ? "OpenSWF Mask Bit Clear " + stencilWriteMask
                    : writesMask
                    ? "OpenSWF Mask Writer " + stencilReference
                    : hideOnly
                        ? "OpenSWF Mask Hidden"
                        : "OpenSWF Masked Content " + stencilReference,
                hideFlags = HideFlags.HideAndDontSave
            };
            material.SetFloat(StencilRefId, hideOnly ? 0f : stencilReference);
            material.SetFloat(
                StencilCompId,
                writesMask && stencilReadMask != 0 ? 3f : clearsMask || hideOnly ? 8f :
                writesMask ? 8f : 3f
            ); // Equal for nested writer/content; Always for root writer/clear.
            material.SetFloat(StencilPassId, clearsMask ? 1f : writesMask ? 2f : 0f);
            material.SetFloat(StencilReadMaskId, stencilReadMask);
            material.SetFloat(StencilWriteMaskId, stencilWriteMask);
            material.SetFloat(ColorMaskId, writesMask || hideOnly || clearsMask ? 0f : 15f);
            stencilMaterials[key] = material;
            return material;
        }

        private BatchInstance AcquireBatch()
        {
            BatchInstance batch;

            if (usedBatchCount < batches.Count)
            {
                batch = batches[usedBatchCount];
            }
            else
            {
                GameObject go = new GameObject("__SWF_Batch_" + usedBatchCount);
                go.transform.SetParent(poolRoot, false);

                Mesh mesh = new Mesh { name = "SWF Batched Vector Mesh" };
                mesh.MarkDynamic();

                MeshFilter meshFilter = go.AddComponent<MeshFilter>();
                MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();
                meshFilter.sharedMesh = mesh;
                meshRenderer.sharedMaterial = sharedMaterial;
                meshRenderer.sortingLayerName = "Default";

                batch = new BatchInstance
                {
                    GameObject = go,
                    Mesh = mesh,
                    Renderer = meshRenderer,
                    PropertyBlock = new MaterialPropertyBlock()
                };

                batches.Add(batch);
            }

            usedBatchCount++;

            if (!batch.GameObject.activeSelf)
                batch.GameObject.SetActive(true);

            return batch;
        }

        private static bool IsFinite(Vector3 value)
        {
            return
                !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private SwfFillStyle GetFillStyle(DefineShapeTag shape, int fillStyleIndex)
        {
            if (shape == null || shape.ShapeData == null || shape.ShapeData.FillStyles == null)
                return null;

            int listIndex = fillStyleIndex - 1;

            if (listIndex < 0 || listIndex >= shape.ShapeData.FillStyles.Count)
                return null;

            return shape.ShapeData.FillStyles[listIndex];
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

        private sealed class LocalMeshData
        {
            public Vector2[] Vertices;
            public int[] Triangles;
            public Mesh RetainedMesh;
        }

        private sealed class RasterizedShape
        {
            public Texture2D Texture;
            public bool OwnsTexture;
            public float MinX;
            public float MaxX;
            public float MinY;
            public float MaxY;
            public Vector2 UvMin;
            public Vector2 UvMax;
        }

        private sealed class RasterAtlas
        {
            public readonly Texture2D Texture;
            private int cursorX;
            private int cursorY;
            private int rowHeight;

            public RasterAtlas(Texture2D texture)
            {
                Texture = texture;
            }

            public bool TryAllocate(int width, int height, out RectInt rectangle)
            {
                rectangle = default;

                if (Texture == null || width > Texture.width || height > Texture.height)
                    return false;

                if (cursorX + width > Texture.width)
                {
                    cursorX = 0;
                    cursorY += rowHeight;
                    rowHeight = 0;
                }

                if (cursorY + height > Texture.height)
                    return false;

                rectangle = new RectInt(cursorX, cursorY, width, height);
                cursorX += width;
                rowHeight = Mathf.Max(rowHeight, height);
                return true;
            }
        }

        private sealed class RasterFillSampler
        {
            public SwfFillStyle Style;
            public SwfMatrix InverseMatrix;
            public Color32[] Pixels;
            public int TextureWidth;
            public int TextureHeight;
            public int SourceWidth;
            public int SourceHeight;
        }

        private struct StencilState
        {
            public int Mode;
            public int Reference;
            public int ReadMask;
            public int WriteMask;
        }

        private sealed class BatchInstance
        {
            public GameObject GameObject;
            public Mesh Mesh;
            public MeshRenderer Renderer;
            public MaterialPropertyBlock PropertyBlock;
        }

        private sealed class RetainedInstance
        {
            public GameObject GameObject;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public MaterialPropertyBlock PropertyBlock;
        }
    }
}
