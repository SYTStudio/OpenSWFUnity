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

        // Per-renderer, not static: a static cache outlived the movie that filled
        // it, so loading a second SWF whose character ids overlapped would serve the
        // first movie's geometry. Entries are dropped wholesale when the quality
        // revision moves, which is the only thing besides a reload that can change
        // what a mesh should contain.
        private readonly Dictionary<MeshCacheKey, LocalMeshData> meshCache =
            new Dictionary<MeshCacheKey, LocalMeshData>();

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
                meshCache.Clear();
                stencilMaterials.Clear();
                ApplyQualityToMaterials();
            }

            ClearBatchBuffers();
            usedBatchCount = 0;
            stencilMode = 0;
            stencilReference = 0;
            stencilReadMask = 255;
            stencilWriteMask = 255;
            stencilStateStack.Clear();
        }

        public void EndFrame()
        {
            FlushBatch();

            for (int i = usedBatchCount; i < batches.Count; i++)
            {
                if (batches[i].GameObject.activeSelf)
                    batches[i].GameObject.SetActive(false);
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

                    Color fillColor = style.ToUnityColor();
                    Texture texture = ResolveBitmapTexture(style);

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
                    else if (fillColor.a <= 0.004f)
                    {
                        continue;
                    }

                    fillColor.a *= alpha;
                    AppendFill(shape, g, group, matrix, fillColor, style, texture);
                }
            }
        }

        private Texture ResolveBitmapTexture(SwfFillStyle style)
        {
            if (style == null || !style.IsBitmap || BitmapProvider == null)
                return null;

            return BitmapProvider(style.BitmapId);
        }

        // Set by SwfPlayer so the renderer can resolve a fill's bitmap id into a
        // decoded texture without taking a hard dependency on the parser.
        public System.Func<ushort, Texture> BitmapProvider { get; set; }

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
                meshData = BuildLocalMesh(group, shape.CharacterId);
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
                if (!style.BitmapMatrix.TryInvert(out inverseFill))
                    textured = false;

                texelWidth = Mathf.Max(1, texture.width);
                texelHeight = Mathf.Max(1, texture.height);
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

                if (textured)
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

            mesh.Clear();
            mesh.SetVertices(batchVertices);
            mesh.SetColors(batchColors);
            mesh.SetUVs(0, batchUvs);
            mesh.SetTriangles(batchTriangles, 0, false);
            mesh.RecalculateBounds();

            batch.Renderer.sharedMaterial = GetActiveMaterial();
            batch.Renderer.sortingOrder = usedBatchCount;

            batch.PropertyBlock.Clear();
            batch.PropertyBlock.SetTexture(MainTextureId, batchTexture != null ? batchTexture : whitePixel);
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

        private LocalMeshData BuildLocalMesh(SwfFillEdgeGroup group, int characterId)
        {
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
    }
}
