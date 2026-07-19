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
        private static readonly int ColorMaskId = Shader.PropertyToID("_ColorMask");

        private static readonly Dictionary<string, LocalMeshData> meshCache =
            new Dictionary<string, LocalMeshData>();

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
        private readonly Stack<int> stencilStateStack = new Stack<int>();

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
            ClearBatchBuffers();
            usedBatchCount = 0;
            stencilMode = 0;
            stencilReference = 0;
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
            stencilStateStack.Push((stencilMode << 8) | stencilReference);
            stencilMode = 1;
            stencilReference = Mathf.Clamp(reference, 1, 255);
        }

        public void BeginMaskedContent(int reference)
        {
            FlushBatch();
            stencilMode = 2;
            stencilReference = Mathf.Clamp(reference, 1, 255);
        }

        public void EndStencil()
        {
            FlushBatch();

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

        public void DrawShapeRasterFill(DefineShapeTag shape, SwfMatrix matrix, string name, float alpha)
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

                    if (style.IsBitmap)
                    {
                        // A bitmap fill whose character cannot be resolved draws
                        // nothing in Flash. Content commonly carries fills bound to
                        // id 65535 (the "no bitmap" sentinel); painting those as an
                        // opaque quad covered the whole stage.
                        if (texture == null)
                            continue;

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
            string cacheKey = shape.CharacterId + "_" + groupIndex;

            if (!meshCache.TryGetValue(cacheKey, out LocalMeshData meshData))
            {
                meshData = BuildLocalMesh(group);
                meshCache[cacheKey] = meshData;
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
                    return; // degenerate transform; skip this fill entirely

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

        private static LocalMeshData BuildLocalMesh(SwfFillEdgeGroup group)
        {
            List<Vector2> vertices = new List<Vector2>();
            List<int> triangles = new List<int>();

            SwfPolygonTriangulator.Triangulate(group.Contours, vertices, triangles);

            return new LocalMeshData
            {
                Vertices = vertices.ToArray(),
                Triangles = triangles.ToArray()
            };
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

        private sealed class BatchInstance
        {
            public GameObject GameObject;
            public Mesh Mesh;
            public MeshRenderer Renderer;
            public MaterialPropertyBlock PropertyBlock;
        }
    }
}
