using UnityEngine;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Renderer;
using OpenSWFUnity.Runtime.Tags;
using Unity.VisualScripting;
using UnityEngine.Assertions.Must;
using System.Collections.Generic;

namespace OpenSWFUnity.Runtime
{
    public class SwfPlayer : MonoBehaviour
    {

        public static SwfPlayer Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        [Header("SWF File")]
        public TextAsset swfFile;
        public enum RenderQuality
        {
            Low = 1,
            Medium = 2,
            High = 4,
            Ultra = 8
        }

        [Header("Render Options")]

        [Tooltip("Quality level for software raster fills. Higher quality may be slower to render. This is not a full SWF fill renderer yet.")]
        public RenderQuality renderQuality = RenderQuality.High;

        [Tooltip("Applies the SWF background color to the main camera if available.")]
        public bool applyBackgroundColor = true;

        [Tooltip("Draws SWF fills using a software rasterizer with even-odd fill rule.")]
        private bool enableRasterFills = true;

        [Tooltip("Draws parsed SWF ShapeRecords as outline lines.")]
        public bool enableShapeOutlines = true;

        [Header("Timeline")]

        [Tooltip("Plays parsed SWF timeline frames.")]
        public bool enableTimelinePlayback = true;

        [Tooltip("Current root frame used for timeline debug.")]
        public int debugFrame = 0;

        [Tooltip("Automatically advances debug frames in Play Mode.")]
        public bool autoPlayTimeline = true;

        [Tooltip("Loops sprite timelines. Disable this to hold sprites on their final frame.")]
        public bool loopSpriteTimelines = false;

        [Header("Debug")]

        [Tooltip("Enable detailed Log parsing information to the console. This can be very verbose for complex SWFs.")]
        public bool verboseLogging = false;

        [Tooltip("Draws debug rectangle bounds around parsed SWF shapes.")]
        public bool enableDebugLines = true;

        [Tooltip("If greater than 0, only this SWF character ID will be rendered.")]
        public int debugOnlyCharacterId = 0;

        [Tooltip("Draws fill contour outlines for debugging holes and contour order.")]
        public bool enableFillContourDebug = false;

        [Tooltip("Draws detected hole contours using the background color for visual testing only.")]
        public bool enableHolePreview = false;

        [Tooltip("Draws parsed SWF text using Unity TextMesh for debugging.")]
        public bool enableTextMeshDebug = true;

        [Tooltip("TextMesh character size multiplier for debug text rendering.")]
        public float textMeshCharacterSize = 0.055f;

        [Tooltip("Divides TextHeight to correct Unity TextMesh baseline.")]
        public float textMeshBaselineDivisor = 22f;

        [Header("Timeline Debug")]
        [Tooltip("Uses parsed sprite frames instead of rendering all sprite control tags.")]
        public bool enableSpriteTimelineDebug = true;

        [Tooltip("Frame index used for sprite timeline debug rendering.")]
        public int debugTimelineFrame = 0;


        private SwfParser runtimeParser;
        private SwfDebugRenderer runtimeDebugRenderer;
        private float timelineTimer;
        private int currentTimelineFrame;
        private float swfFrameRate = 30f;


        private void Update()
        {
            if (!Application.isPlaying)
                return;

            if (!enableTimelinePlayback || !autoPlayTimeline)
                return;

            if (runtimeParser == null)
                return;

            timelineTimer += Time.deltaTime;

            float frameDuration = 1f / Mathf.Max(1f, swfFrameRate);

            if (timelineTimer >= frameDuration)
            {
                timelineTimer -= frameDuration;

                currentTimelineFrame++;
                RenderCurrentFrame();
            }
        }

        private void Start()
        {
            if (swfFile == null)
            {
                Debug.LogError("No SWF file assigned.");
                return;
            }

            try
            {
                SwfParser parser = new SwfParser(swfFile.bytes);
                parser.VerboseLogging = verboseLogging;

                SwfHeader header = parser.ParseHeader();
                Debug.Log(header.ToString());

                parser.ParseTags();

                runtimeParser = parser;
                runtimeDebugRenderer = new SwfDebugRenderer(transform);
                swfFrameRate = parser.Header.FrameRate;

                Debug.Log(
                    "Text/Font Summary\n" +
                    "DefineFont=" + parser.DefineFontCount + "\n" +
                    "DefineFont2=" + parser.DefineFont2Count + "\n" +
                    "DefineFont3=" + parser.DefineFont3Count + "\n" +
                    "DefineText=" + parser.DefineTextCount + "\n" +
                    "DefineText2=" + parser.DefineText2Count + "\n" +
                    "DefineEditText=" + parser.DefineEditTextCount + "\n" +
                    "CSMTextSettings=" + parser.CsmTextSettingsCount + "\n" +
                    "FontAlignZones=" + parser.FontAlignZonesCount
                );

                if (applyBackgroundColor && parser.BackgroundColor != null && Camera.main != null)
                {
                    Camera.main.backgroundColor = parser.BackgroundColor.ToUnityColor();
                }

                Debug.Log(
                    "SWF Parse Summary\n" +
                    "Shapes: " + parser.Shapes.Count + "\n" +
                    "Sprites: " + parser.Sprites.Count + "\n" +
                    "PlacedObjects: " + parser.PlacedObjects.Count + "\n" +
                    "Background: " + parser.BackgroundColor
                );

                if (parser.Shapes.Count > 0 && parser.Shapes[0].ShapeData != null)
                {
                    Debug.Log("First ShapeData: " + parser.Shapes[0].ShapeData);

                    if (parser.Shapes[0].ShapeData.FillStyles.Count > 0)
                    {
                        Debug.Log("First Shape FillStyle 0: " + parser.Shapes[0].ShapeData.FillStyles[0]);
                    }

                    Debug.Log("First Shape Edges: " + parser.Shapes[0].ShapeData.Edges.Count);
                    Debug.Log("First Shape FillGroups: " + parser.Shapes[0].ShapeData.FillEdgeGroups.Count);

                    for (int i = 0; i < parser.Shapes[0].ShapeData.FillEdgeGroups.Count; i++)
                    {
                        var group = parser.Shapes[0].ShapeData.FillEdgeGroups[i];

                        Debug.Log("First Shape FillGroup " + i + ": " + group);

                        for (int c = 0; c < group.Contours.Count; c++)
                        {
                            Debug.Log("First Shape Contour " + c + ": " + group.Contours[c]);
                        }
                    }

                    for (int i = 0; i < parser.Shapes.Count; i++)
                    {
                        var shape = parser.Shapes[i];

                        if (shape.ShapeData == null || shape.ShapeData.FillStyles == null)
                            continue;

                        for (int f = 0; f < shape.ShapeData.FillStyles.Count; f++)
                        {
                            Debug.Log(
                                "Shape " + shape.CharacterId +
                                " FillStyle " + f +
                                ": " + shape.ShapeData.FillStyles[f]
                            );
                        }
                    }

                    for (int i = 0; i < parser.Shapes[0].ShapeData.Paths.Count; i++)
                    {
                        Debug.Log("First Shape Path " + i + ": " + parser.Shapes[0].ShapeData.Paths[i]);
                    }

                    for (int i = 0; i < parser.Shapes.Count; i++)
                    {
                        var shape = parser.Shapes[i];

                        if (shape.ShapeData == null)
                            continue;

                        Debug.Log(
                            "Shape " + shape.CharacterId +
                            " Paths=" + shape.ShapeData.Paths.Count +
                            " Edges=" + shape.ShapeData.Edges.Count +
                            " Groups=" + shape.ShapeData.FillEdgeGroups.Count +
                            " Contours=" + CountContours(shape.ShapeData)
                        );
                    }
                }

                ClearDebugLines();

                if (enableDebugLines || enableShapeOutlines || enableRasterFills || enableFillContourDebug || enableTextMeshDebug)
                {
                    RenderCurrentFrame();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("Failed to parse SWF: " + e.Message);
            }
        }

        private void RenderCurrentFrame()
        {
            if (runtimeParser == null)
                return;

            ClearDebugLines();

            debugTimelineFrame = currentTimelineFrame;

            RenderTopLevelDebug(runtimeParser);
        }

        private int CountContours(SwfShapeData data)
        {
            int count = 0;

            if (data == null || data.FillEdgeGroups == null)
                return 0;

            for (int i = 0; i < data.FillEdgeGroups.Count; i++)
            {
                if (data.FillEdgeGroups[i].Contours != null)
                    count += data.FillEdgeGroups[i].Contours.Count;
            }

            return count;
        }

        private void ClearDebugLines()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);

                if (
    child.name.StartsWith("Shape_") ||
    child.name.StartsWith("Debug_") ||
    child.name.StartsWith("Outline_") ||
    child.name.StartsWith("Fill_") ||
    child.name.StartsWith("RasterFill_") ||
    child.name.StartsWith("FillContour_") ||
    child.name.StartsWith("Text_")
)
                {
                    Destroy(child.gameObject);
                }
            }
        }

        private void RenderTopLevelDebug(SwfParser parser)
        {
            SwfDebugRenderer renderer = new SwfDebugRenderer(transform);

            foreach (PlaceObject2Tag placed in parser.PlacedObjects)
            {
                if (!placed.HasCharacter || placed.CharacterId == 0)
                    continue;

                RenderCharacterDebug(
                    parser,
                    renderer,
                    placed.CharacterId,
                    placed.Matrix,
                    "Depth_" + placed.Depth,
                    1f,
                    debugTimelineFrame
                );
            }
        }

        private void RenderCharacterDebug(
    SwfParser parser,
    SwfDebugRenderer renderer,
    ushort characterId,
    SwfMatrix worldMatrix,
    string path,
    float alpha = 1f,
    int localFrame = 0
)
        {
            DefineShapeTag shape = parser.FindShapeById(characterId);

            if (shape != null)
            {
                if (debugOnlyCharacterId > 0 && characterId != debugOnlyCharacterId)
                {
                    return;
                }

                if (enableRasterFills)
                {
                    SwfRasterFillRenderer rasterRenderer = new SwfRasterFillRenderer(transform);

                    rasterRenderer.DrawShapeRasterFill(
                        shape,
                        worldMatrix,
                        "RasterFill_" + characterId + "_" + path,
                        alpha
                    );
                }

                if (enableFillContourDebug)
                {
                    renderer.DrawFillContours(
                        shape,
                        worldMatrix,
                        "FillContour_" + characterId + "_" + path
                    );
                }

                if (enableDebugLines)
                {
                    renderer.DrawShapeBounds(
                        shape,
                        worldMatrix,
                        "Shape_" + characterId + "_" + path
                    );
                }

                if (enableShapeOutlines)
                {
                    renderer.DrawShapeOutline(
                        shape,
                        worldMatrix,
                        "Outline_" + characterId + "_" + path
                    );
                }

                return;
            }

            DefineSpriteTag sprite = parser.FindSpriteById(characterId);

            if (sprite != null)
            {
                if (verboseLogging)
                {
                    Debug.Log("Entering Sprite " + sprite.SpriteId + " at " + path);
                }

                RenderSpriteDebug(
    parser,
    renderer,
    sprite,
    worldMatrix,
    path + "_Sprite_" + sprite.SpriteId,
    alpha,
    localFrame
);

                return;
            }

            DefineTextTag text = parser.FindTextById(characterId);

            if (text != null)
            {
                if (enableTextMeshDebug)
                {
                    SwfTextRenderer textRenderer = new SwfTextRenderer(transform);

                    for (int i = 0; i < text.Records.Count; i++)
                    {
                        SwfTextRecord record = text.Records[i];
                        string decoded = parser.DecodeTextRecordPublic(record);

                        textRenderer.DrawText(
                            text,
                            record,
                            decoded,
                            worldMatrix,
                            "Text_" + characterId + "_" + path + "_Record_" + i,
                            textMeshCharacterSize,
                            textMeshBaselineDivisor,
                            alpha
                        );
                    }
                }

                return;
            }

            if (verboseLogging)
            {
                Debug.LogWarning("Unknown CharacterId: " + characterId + " at " + path);
            }
        }

        private void RenderSpriteDebug(
    SwfParser parser,
    SwfDebugRenderer renderer,
    DefineSpriteTag sprite,
    SwfMatrix parentMatrix,
    string path,
    float parentAlpha,
    int spriteFrame
)
        {
            List<PlaceObject2Tag> places = new List<PlaceObject2Tag>();

            int localFrame = 0;

            if (sprite.Frames != null && sprite.Frames.Count > 0)
            {
                if (loopSpriteTimelines)
                    localFrame = spriteFrame % sprite.Frames.Count;
                else
                    localFrame = Mathf.Clamp(spriteFrame, 0, sprite.Frames.Count - 1);
            }

            if (enableSpriteTimelineDebug && sprite.Frames != null && sprite.Frames.Count > 0)
            {
                places = BuildSpriteDisplayListForFrame(parser, sprite, localFrame);
            }
            else
            {
                foreach (SwfTag innerTag in sprite.ControlTags)
                {
                    if (innerTag.Code != 26)
                        continue;

                    try
                    {
                        PlaceObject2Tag innerPlace = parser.ParsePlaceObject2FromTag(innerTag);

                        if (innerPlace != null)
                            places.Add(innerPlace);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning(
                            "Failed inner PlaceObject2 parse in sprite " +
                            sprite.SpriteId + ": " + e.Message
                        );
                    }
                }
            }

            for (int i = 0; i < places.Count; i++)
            {
                PlaceObject2Tag innerPlace = places[i];

                if (innerPlace == null)
                    continue;

                if (!innerPlace.HasCharacter || innerPlace.CharacterId == 0)
                    continue;

                float childAlpha = parentAlpha;

                if (innerPlace.HasColorTransform && innerPlace.ColorTransform != null)
                {
                    childAlpha *= innerPlace.ColorTransform.AlphaMultiplier01;
                }

                if (childAlpha <= 0.01f)
                    continue;

                SwfMatrix combinedMatrix = SwfMatrix.Combine(
                    parentMatrix,
                    innerPlace.Matrix
                );

                RenderCharacterDebug(
                    parser,
                    renderer,
                    innerPlace.CharacterId,
                    combinedMatrix,
                    path + "_Frame_" + localFrame + "_Depth_" + innerPlace.Depth,
                    childAlpha,
                    localFrame
                );
            }
        }

        private List<PlaceObject2Tag> BuildSpriteDisplayListForFrame(
    SwfParser parser,
    DefineSpriteTag sprite,
    int frameIndex
)
        {
            Dictionary<ushort, PlaceObject2Tag> activeByDepth =
                new Dictionary<ushort, PlaceObject2Tag>();

            if (sprite == null || sprite.Frames == null || sprite.Frames.Count == 0)
                return new List<PlaceObject2Tag>();

            int maxFrame = Mathf.Clamp(frameIndex, 0, sprite.Frames.Count - 1);

            for (int f = 0; f <= maxFrame; f++)
            {
                SwfFrame frame = sprite.Frames[f];

                if (frame == null || frame.ControlTags == null)
                    continue;

                for (int t = 0; t < frame.ControlTags.Count; t++)
                {
                    SwfTag tag = frame.ControlTags[t];

                    if (tag == null)
                        continue;

                    if (tag.Code == 26) // PlaceObject2
                    {
                        PlaceObject2Tag place = parser.ParsePlaceObject2FromTag(tag);

                        if (place == null)
                            continue;

                        if (activeByDepth.TryGetValue(place.Depth, out PlaceObject2Tag existing))
                        {
                            bool isMoveUpdate =
                                !place.HasCharacter ||
                                place.CharacterId == 0;

                            if (isMoveUpdate)
                            {
                                place.CharacterId = existing.CharacterId;
                                place.HasCharacter = existing.HasCharacter;

                                if (!place.HasMatrix)
                                {
                                    place.Matrix = existing.Matrix;
                                    place.HasMatrix = existing.HasMatrix;
                                }

                                if (!place.HasColorTransform)
                                {
                                    place.ColorTransform = existing.ColorTransform;
                                    place.HasColorTransform = existing.HasColorTransform;
                                }
                            }
                        }

                        activeByDepth[place.Depth] = place;
                    }
                    else if (tag.Code == 28) // RemoveObject2
                    {
                        ushort depth = parser.ParseRemoveObject2DepthFromTag(tag);

                        if (activeByDepth.ContainsKey(depth))
                            activeByDepth.Remove(depth);
                    }
                }
            }

            List<PlaceObject2Tag> result = new List<PlaceObject2Tag>(activeByDepth.Values);

            result.Sort((a, b) => a.Depth.CompareTo(b.Depth));

            return result;
        }
    }
}