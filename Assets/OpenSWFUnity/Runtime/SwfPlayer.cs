using UnityEngine;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Renderer;
using OpenSWFUnity.Runtime.Tags;
using OpenSWFUnity.Runtime.Audio;
using OpenSWFUnity.Runtime.AVM1;
using System.Collections.Generic;
using UnityEngine.InputSystem;

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
        public SwfAsset swfFile;

        [Tooltip("Shows the optional console-style library before starting the movie.")]
        public bool showLibraryOnStart = false;
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
        public bool enableShapeOutlines = false;

        [Header("Timeline")]

        [Tooltip("Plays parsed SWF timeline frames.")]
        public bool enableTimelinePlayback = true;

        [Tooltip("Current root frame used for timeline debug.")]
        public int debugFrame = 0;

        // Runtime playback state, not a setting. A Flash movie's root timeline
        // always starts playing; only the movie's own stop()/play() may change
        // that. Exposing it in the Inspector let a saved scene value freeze the
        // root at frame 1 and stall the whole movie, so it is hidden and always
        // starts true.
        [System.NonSerialized]
        public bool autoPlayTimeline = true;

        [Tooltip("Loops sprite timelines. Disable this to hold sprites on their final frame.")]
        public bool loopSpriteTimelines = true;

        [Header("Debug")]

        [Tooltip("Enable detailed Log parsing information to the console. This can be very verbose for complex SWFs.")]
        public bool verboseLogging = false;

        [Tooltip("Draws debug rectangle bounds around parsed SWF shapes.")]
        public bool enableDebugLines = false;

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

        [Tooltip("Uses local age for placed child sprites. More accurate Flash behavior, but may break some exported animations.")]
        public bool usePlacedObjectLocalAge = true;

        [Tooltip("Allows pointer input to reach parsed Flash buttons.")]
        public bool inputEnabled = true;

        [Header("ActionScript 1/2 (AVM1)")]
        [Tooltip("Executes ActionScript 1 and ActionScript 2 bytecode through the AVM1 runtime.")]
        public bool enableAvm1 = true;


        private SwfParser runtimeParser;
        private SwfDebugRenderer runtimeDebugRenderer;
        private SwfRasterFillRenderer runtimeRasterRenderer;
        private SwfTextRenderer runtimeTextRenderer;
        private float timelineTimer;
        private int currentTimelineFrame;
        private float swfFrameRate = 30f;
        private readonly List<ActiveButtonInstance> activeButtons = new List<ActiveButtonInstance>();
        private string hoveredButtonPath;
        private string pressedButtonPath;
        private bool pressedButtonIsOutDown;
        private SwfAudioRuntime runtimeAudio;
        private Avm1Runtime runtimeAvm1;
        private readonly Dictionary<string, int> avm1LastExecutedFrames =
            new Dictionary<string, int>();
        private readonly List<DynamicMovieClip> dynamicMovieClips =
            new List<DynamicMovieClip>();
        private readonly Dictionary<Avm1Object, DynamicMovieClip> dynamicMovieClipsByObject =
            new Dictionary<Avm1Object, DynamicMovieClip>();
        // Display lists are pure functions of (timeline, frame), so they are built
        // once and reused instead of replaying control tags every rendered frame.
        private readonly Dictionary<List<SwfFrame>, Dictionary<int, List<PlaceObject2Tag>>> displayListCache =
            new Dictionary<List<SwfFrame>, Dictionary<int, List<PlaceObject2Tag>>>();

        private readonly Dictionary<string, StaticDisplayInstance> staticDisplayInstances =
            new Dictionary<string, StaticDisplayInstance>();
        private readonly Dictionary<Avm1Object, StaticDisplayInstance> staticDisplayInstancesByObject =
            new Dictionary<Avm1Object, StaticDisplayInstance>();
        private int nextMaskStencilReference = 1;

        public event System.Action<DefineButton2Tag, SwfButtonCondAction> ButtonActionTriggered;

        public bool HasLoadedMovie => runtimeParser != null;
        public bool IsPlaying => enableTimelinePlayback && autoPlayTimeline;
        public int CurrentFrame => currentTimelineFrame;
        public float FrameRate => swfFrameRate;
        public SwfHeader LoadedHeader => runtimeParser != null ? runtimeParser.Header : null;

        public void Play()
        {
            enableTimelinePlayback = true;
            autoPlayTimeline = true;
        }

        public void Pause()
        {
            autoPlayTimeline = false;
        }

        public void Restart()
        {
            timelineTimer = 0f;
            currentTimelineFrame = 0;
            debugFrame = 0;
            debugTimelineFrame = 0;

            if (runtimeParser != null)
            {
                InitializeAvm1(runtimeParser);
                ExecuteCurrentTimelineActions();
                RenderCurrentFrame();
            }
        }


        private void Update()
        {
            if (!Application.isPlaying)
                return;

            if (runtimeParser == null)
                return;

            if (inputEnabled)
            {
                HandlePointerInput();
                HandleKeyboardInput();
            }

            if (!enableTimelinePlayback)
                return;

            timelineTimer += Time.deltaTime;

            float frameDuration = 1f / Mathf.Max(1f, swfFrameRate);

            if (timelineTimer >= frameDuration)
            {
                timelineTimer -= frameDuration;

                if (autoPlayTimeline)
                    currentTimelineFrame++;

                AdvanceStaticMovieClips();
                AdvanceDynamicMovieClips();
                ExecuteCurrentTimelineActions();
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
                if (verboseLogging)
                    Debug.Log(header.ToString());

                parser.ParseTags();

                runtimeParser = parser;
                runtimeDebugRenderer = new SwfDebugRenderer(
                    transform,
                    header.StageWidth,
                    header.StageHeight
                );
                runtimeRasterRenderer = new SwfRasterFillRenderer(
                    transform,
                    header.StageWidth,
                    header.StageHeight
                );

                // Lets bitmap fills resolve their character id to a decoded
                // texture; textures are built on first use, not at parse time.
                runtimeRasterRenderer.BitmapProvider = bitmapId =>
                    parser.FindBitmapById(bitmapId)?.GetTexture();
                runtimeTextRenderer = new SwfTextRenderer(
                    transform,
                    header.StageWidth,
                    header.StageHeight
                );
                swfFrameRate = parser.Header.FrameRate;

                runtimeAudio = GetComponent<SwfAudioRuntime>();

                if (runtimeAudio == null)
                    runtimeAudio = gameObject.AddComponent<SwfAudioRuntime>();

                runtimeAudio.Initialize(parser);
                InitializeAvm1(parser);
                ExecuteCurrentTimelineActions();

                if (verboseLogging)
                {
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
                }

                if (applyBackgroundColor && parser.BackgroundColor != null && Camera.main != null)
                {
                    Camera.main.backgroundColor = parser.BackgroundColor.ToUnityColor();
                }

                if (verboseLogging)
                {
                    Debug.Log(
                        "SWF Parse Summary\n" +
                        "Shapes: " + parser.Shapes.Count + "\n" +
                        "Sprites: " + parser.Sprites.Count + "\n" +
                        "PlacedObjects: " + parser.PlacedObjects.Count + "\n" +
                        "Background: " + parser.BackgroundColor
                    );
                }

                if (verboseLogging && parser.Shapes.Count > 0 && parser.Shapes[0].ShapeData != null)
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

                if (verboseLogging)
                {
                    Debug.Log(
                        "Morph Summary\n" +
                        "DefineMorphShape=" + parser.DefineMorphShapeCount + "\n" +
                        "DefineMorphShape2=" + parser.DefineMorphShape2Count
                    );
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

            runtimeRasterRenderer?.BeginFrame();
            runtimeTextRenderer?.BeginFrame();
            nextMaskStencilReference = 1;

            try
            {
                ClearDebugLines();
                activeButtons.Clear();

                debugTimelineFrame = currentTimelineFrame;

                RenderTopLevelDebug(runtimeParser);
            }
            finally
            {
                runtimeRasterRenderer?.EndFrame();
                runtimeTextRenderer?.EndFrame();
            }
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
            float stageWidth = parser.Header != null ? parser.Header.StageWidth : 600f;
            float stageHeight = parser.Header != null ? parser.Header.StageHeight : 400f;

            SwfDebugRenderer renderer = runtimeDebugRenderer ?? new SwfDebugRenderer(
                transform,
                stageWidth,
                stageHeight
            );

            List<PlaceObject2Tag> places;
            int rootFrame = 0;

            if (parser.RootFrames != null && parser.RootFrames.Count > 0)
            {
                rootFrame = currentTimelineFrame % parser.RootFrames.Count;
                places = BuildDisplayListForFrame(parser, parser.RootFrames, rootFrame);
            }
            else
            {
                places = new List<PlaceObject2Tag>(parser.PlacedObjects);
            }

            debugFrame = rootFrame;
            Stack<int> timelineMaskDepths = new Stack<int>();

            foreach (PlaceObject2Tag placed in places)
            {
                while (timelineMaskDepths.Count > 0 && placed.Depth > timelineMaskDepths.Peek())
                {
                    runtimeRasterRenderer?.EndStencil();
                    timelineMaskDepths.Pop();
                }

                if (!placed.HasCharacter || placed.CharacterId == 0)
                    continue;

                float alpha = 1f;

                if (placed.HasColorTransform && placed.ColorTransform != null)
                {
                    alpha *= placed.ColorTransform.AlphaMultiplier01;
                }

                int childFrame = usePlacedObjectLocalAge
                    ? Mathf.Max(0, currentTimelineFrame - placed.TimelineStartFrame)
                    : currentTimelineFrame;
                bool isSprite = parser.FindSpriteById(placed.CharacterId) != null;
                string timelinePath = "_root/depth" + placed.Depth +
                    (isSprite ? ":sprite" : ":char") + placed.CharacterId;
                StaticDisplayInstance instance = runtimeAvm1 != null
                    ? GetOrCreateStaticDisplayInstance(
                        timelinePath,
                        placed,
                        runtimeAvm1.RootObject
                    )
                    : null;
                SwfMatrix effectiveMatrix = instance != null
                    ? BuildDynamicMovieClipMatrix(instance.ScriptObject)
                    : placed.Matrix;

                // Timeline-placed clips honour _visible exactly like dynamically
                // created ones. Content routinely parks a pile of clips on frame 1
                // and hides them from script; ignoring _visible here drew every one
                // of them on top of the real scene.
                if (instance != null &&
                    !Avm1Boolean(instance.ScriptObject.Get("_visible"), true))
                {
                    continue;
                }

                if (placed.HasVisible && placed.Visible == 0)
                    continue;

                if (isSprite && instance != null)
                    childFrame = instance.CurrentFrame;

                bool beginsTimelineMask = placed.HasClipDepth && placed.ClipDepth > placed.Depth;

                if (beginsTimelineMask)
                    runtimeRasterRenderer?.BeginMaskWrite(nextMaskStencilReference++);

                RenderCharacterDebug(
                    parser,
                    renderer,
                    placed.CharacterId,
                    effectiveMatrix,
                    "Depth_" + placed.Depth,
                    alpha,
                    childFrame,
                    placed.Ratio,
                    placed.HasRatio,
                    instance != null ? instance.ScriptObject : null,
                    timelinePath
                );

                if (beginsTimelineMask)
                {
                    int reference = nextMaskStencilReference - 1;
                    runtimeRasterRenderer?.BeginMaskedContent(reference);
                    timelineMaskDepths.Push(placed.ClipDepth);
                }
            }

            while (timelineMaskDepths.Count > 0)
            {
                runtimeRasterRenderer?.EndStencil();
                timelineMaskDepths.Pop();
            }

            RenderDynamicMovieClips(parser, renderer, null, SwfMatrix.Identity, 1f, 0);
        }

        private void RenderCharacterDebug(
    SwfParser parser,
    SwfDebugRenderer renderer,
    ushort characterId,
    SwfMatrix worldMatrix,
    string path,
    float alpha = 1f,
    int localFrame = 0,
    ushort ratio = 0,
    bool hasRatio = false,
    Avm1Object scriptObject = null,
    string timelinePath = null,
    bool bypassAssignedMask = false,
    bool renderAttachedChildren = true
)
        {
            if (!bypassAssignedMask && TryGetAssignedMask(scriptObject, out Avm1Object maskObject))
            {
                int stencilReference = nextMaskStencilReference++;

                if (nextMaskStencilReference > 255)
                    nextMaskStencilReference = 1;

                runtimeRasterRenderer?.BeginMaskWrite(stencilReference);
                RenderMaskObject(
                    parser,
                    renderer,
                    scriptObject,
                    maskObject,
                    worldMatrix,
                    path + "_Mask"
                );
                runtimeRasterRenderer?.BeginMaskedContent(stencilReference);
                RenderCharacterDebug(
                    parser,
                    renderer,
                    characterId,
                    worldMatrix,
                    path,
                    alpha,
                    localFrame,
                    ratio,
                    hasRatio,
                    scriptObject,
                    timelinePath,
                    true,
                    renderAttachedChildren
                );
                runtimeRasterRenderer?.EndStencil();
                return;
            }

            DefineShapeTag shape = parser.FindShapeById(characterId);

            if (shape != null)
            {
                if (debugOnlyCharacterId > 0 && characterId != debugOnlyCharacterId)
                {
                    return;
                }

                if (enableRasterFills)
                {
                    runtimeRasterRenderer?.DrawShapeRasterFill(
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
    localFrame,
    scriptObject,
    timelinePath,
    renderAttachedChildren
);

                return;
            }

            DefineTextTag text = parser.FindTextById(characterId);

            if (text != null)
            {
                if (enableTextMeshDebug)
                {
                    for (int i = 0; i < text.Records.Count; i++)
                    {
                        SwfTextRecord record = text.Records[i];
                        string decoded = parser.DecodeTextRecordPublic(record);

                        runtimeTextRenderer?.DrawText(
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

            DefineButton2Tag button = parser.FindButton2ById(characterId);

            if (button != null)
            {
                activeButtons.Add(new ActiveButtonInstance
                {
                    Button = button,
                    WorldMatrix = worldMatrix,
                    Path = path,
                    ScriptObject = scriptObject != null
                        ? scriptObject.Get("_parent") as Avm1Object
                        : runtimeAvm1?.RootObject
                });

                RenderButtonState(
                    parser,
                    renderer,
                    button,
                    worldMatrix,
                    path,
                    alpha,
                    localFrame
                );

                return;
            }

            if (verboseLogging)
            {
                Debug.LogWarning("Unknown CharacterId: " + characterId + " at " + path);
            }
        }

        private void RenderButtonState(
            SwfParser parser,
            SwfDebugRenderer renderer,
            DefineButton2Tag button,
            SwfMatrix parentMatrix,
            string path,
            float parentAlpha,
            int localFrame
        )
        {
            bool isHovered = string.Equals(hoveredButtonPath, path, System.StringComparison.Ordinal);
            bool wantsDown = isHovered &&
                string.Equals(pressedButtonPath, path, System.StringComparison.Ordinal);
            bool wantsOver = isHovered && !wantsDown;

            bool hasRequestedState = false;

            for (int i = 0; i < button.Records.Count; i++)
            {
                SwfButtonRecord record = button.Records[i];

                if ((wantsDown && record.StateDown) ||
                    (wantsOver && record.StateOver) ||
                    (!wantsDown && !wantsOver && record.StateUp))
                {
                    hasRequestedState = true;
                    break;
                }
            }

            for (int i = 0; i < button.Records.Count; i++)
            {
                SwfButtonRecord record = button.Records[i];
                bool shouldRender;

                if (!hasRequestedState)
                {
                    shouldRender = record.StateUp;
                }
                else if (wantsDown)
                {
                    shouldRender = record.StateDown;
                }
                else if (wantsOver)
                {
                    shouldRender = record.StateOver;
                }
                else
                {
                    shouldRender = record.StateUp;
                }

                if (!shouldRender)
                    continue;

                float childAlpha = parentAlpha;

                if (record.ColorTransform != null)
                {
                    childAlpha *= record.ColorTransform.AlphaMultiplier01;
                }

                SwfMatrix combinedMatrix = SwfMatrix.Combine(parentMatrix, record.Matrix);

                RenderCharacterDebug(
                    parser,
                    renderer,
                    record.CharacterId,
                    combinedMatrix,
                    path + "_Button_" + button.ButtonId + "_Depth_" + record.PlaceDepth,
                    childAlpha,
                    localFrame
                );
            }
        }

        private void RenderSpriteDebug(
    SwfParser parser,
    SwfDebugRenderer renderer,
    DefineSpriteTag sprite,
    SwfMatrix parentMatrix,
    string path,
    float parentAlpha,
    int spriteFrame,
    Avm1Object spriteObject,
    string timelinePath,
    bool renderAttachedChildren
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
                places = BuildDisplayListForFrame(parser, sprite.Frames, localFrame);
            }
            else
            {
                foreach (SwfTag innerTag in sprite.ControlTags)
                {
                    if (innerTag.Code != 26 && innerTag.Code != 70)
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

            Stack<int> timelineMaskDepths = new Stack<int>();

            for (int i = 0; i < places.Count; i++)
            {
                PlaceObject2Tag innerPlace = places[i];

                if (innerPlace == null)
                    continue;

                while (timelineMaskDepths.Count > 0 && innerPlace.Depth > timelineMaskDepths.Peek())
                {
                    runtimeRasterRenderer?.EndStencil();
                    timelineMaskDepths.Pop();
                }

                if (!innerPlace.HasCharacter || innerPlace.CharacterId == 0)
                    continue;

                float childAlpha = parentAlpha;

                if (innerPlace.HasColorTransform && innerPlace.ColorTransform != null)
                {
                    childAlpha *= innerPlace.ColorTransform.AlphaMultiplier01;
                }

                if (childAlpha <= 0.01f)
                    continue;

                bool childIsSprite = parser.FindSpriteById(innerPlace.CharacterId) != null;
                string childTimelinePath = (timelinePath ?? path) + "/depth" +
                    innerPlace.Depth + (childIsSprite ? ":sprite" : ":char") +
                    innerPlace.CharacterId;
                StaticDisplayInstance childInstance = runtimeAvm1 != null
                    ? GetOrCreateStaticDisplayInstance(
                        childTimelinePath,
                        innerPlace,
                        spriteObject ?? runtimeAvm1.RootObject
                    )
                    : null;
                SwfMatrix localMatrix = childInstance != null
                    ? BuildDynamicMovieClipMatrix(childInstance.ScriptObject)
                    : innerPlace.Matrix;

                // Children inside a sprite honour _visible too; hiding a parent
                // clip must not leave its contents drawn.
                if (childInstance != null &&
                    !Avm1Boolean(childInstance.ScriptObject.Get("_visible"), true))
                {
                    continue;
                }

                if (innerPlace.HasVisible && innerPlace.Visible == 0)
                    continue;

                SwfMatrix combinedMatrix = SwfMatrix.Combine(parentMatrix, localMatrix);

                int childFrame = usePlacedObjectLocalAge
                    ? Mathf.Max(0, localFrame - innerPlace.TimelineStartFrame)
                    : localFrame;

                if (childIsSprite && childInstance != null)
                    childFrame = childInstance.CurrentFrame;

                bool beginsTimelineMask = innerPlace.HasClipDepth &&
                    innerPlace.ClipDepth > innerPlace.Depth;

                if (beginsTimelineMask)
                    runtimeRasterRenderer?.BeginMaskWrite(nextMaskStencilReference++);

                RenderCharacterDebug(
                    parser,
                    renderer,
                    innerPlace.CharacterId,
                    combinedMatrix,
                    path + "_Frame_" + localFrame + "_Depth_" + innerPlace.Depth,
                    childAlpha,
                    childFrame,
                    innerPlace.Ratio,
                    innerPlace.HasRatio,
                    childInstance != null ? childInstance.ScriptObject : null,
                    childTimelinePath
                );

                if (beginsTimelineMask)
                {
                    int reference = nextMaskStencilReference - 1;
                    runtimeRasterRenderer?.BeginMaskedContent(reference);
                    timelineMaskDepths.Push(innerPlace.ClipDepth);
                }
            }

            while (timelineMaskDepths.Count > 0)
            {
                runtimeRasterRenderer?.EndStencil();
                timelineMaskDepths.Pop();
            }

            if (spriteObject != null && renderAttachedChildren)
            {
                RenderDynamicMovieClips(
                    parser,
                    renderer,
                    spriteObject,
                    parentMatrix,
                    parentAlpha,
                    0
                );
            }
        }

        private bool TryGetAssignedMask(Avm1Object scriptObject, out Avm1Object maskObject)
        {
            maskObject = null;

            if (scriptObject == null)
                return false;

            if (dynamicMovieClipsByObject.TryGetValue(scriptObject, out DynamicMovieClip dynamicClip))
                maskObject = dynamicClip.MaskObject;
            else if (staticDisplayInstancesByObject.TryGetValue(scriptObject, out StaticDisplayInstance staticClip))
                maskObject = staticClip.MaskObject;

            return maskObject != null;
        }

        private bool IsObjectUsedAsMask(Avm1Object scriptObject)
        {
            if (scriptObject == null)
                return false;

            for (int i = 0; i < dynamicMovieClips.Count; i++)
            {
                DynamicMovieClip clip = dynamicMovieClips[i];

                if (!clip.Removed && clip.MaskObject == scriptObject)
                    return true;
            }

            foreach (StaticDisplayInstance clip in staticDisplayInstances.Values)
                if (clip != null && clip.MaskObject == scriptObject)
                    return true;

            return false;
        }

        private void RenderMaskObject(
            SwfParser parser,
            SwfDebugRenderer renderer,
            Avm1Object maskedObject,
            Avm1Object maskObject,
            SwfMatrix maskedWorldMatrix,
            string path
        )
        {
            ushort characterId = 0;
            int currentFrame = 0;
            SwfMatrix maskWorldMatrix = maskedWorldMatrix;

            if (dynamicMovieClipsByObject.TryGetValue(maskObject, out DynamicMovieClip dynamicMask))
            {
                characterId = dynamicMask.CharacterId;
                currentFrame = dynamicMask.CurrentFrame;
                maskWorldMatrix = dynamicMask.ParentObject == maskedObject
                    ? SwfMatrix.Combine(maskedWorldMatrix, BuildDynamicMovieClipMatrix(maskObject))
                    : ResolveDisplayObjectWorldMatrix(maskObject);
            }
            else if (staticDisplayInstancesByObject.TryGetValue(maskObject, out StaticDisplayInstance staticMask))
            {
                characterId = staticMask.CharacterId;
                currentFrame = staticMask.CurrentFrame;
                maskWorldMatrix = staticMask.ParentObject == maskedObject
                    ? SwfMatrix.Combine(maskedWorldMatrix, BuildDynamicMovieClipMatrix(maskObject))
                    : ResolveDisplayObjectWorldMatrix(maskObject);
            }

            if (characterId == 0)
                return;

            RenderCharacterDebug(
                parser,
                renderer,
                characterId,
                maskWorldMatrix,
                path,
                1f,
                currentFrame,
                0,
                false,
                maskObject,
                path,
                true,
                true
            );
        }

        private SwfMatrix ResolveDisplayObjectWorldMatrix(Avm1Object scriptObject)
        {
            Dictionary<Avm1Object, SwfMatrix> cache = new Dictionary<Avm1Object, SwfMatrix>
            {
                [runtimeAvm1.RootObject] = SwfMatrix.Identity
            };
            return TryGetDisplayObjectWorldMatrix(scriptObject, cache, 0, out SwfMatrix world)
                ? world
                : SwfMatrix.Identity;
        }

        private void HandlePointerInput()
        {
            if (Mouse.current == null)
                return;

            if (!TryGetPointerFlashPosition(out Vector2 flashPoint))
                return;

            UpdateAvm1MouseCoordinates(flashPoint);

            if (Mouse.current.delta.ReadValue().sqrMagnitude > 0.0001f)
                runtimeAvm1?.Broadcast("Mouse", "onMouseMove");

            if (Mouse.current.leftButton.wasPressedThisFrame)
                runtimeAvm1?.Broadcast("Mouse", "onMouseDown");
            else if (Mouse.current.leftButton.wasReleasedThisFrame)
                runtimeAvm1?.Broadcast("Mouse", "onMouseUp");

            if (activeButtons.Count == 0)
                return;

            ActiveButtonInstance hit = FindTopButtonAt(flashPoint);
            string previousHoveredPath = hoveredButtonPath;
            string newHoveredPath = hit != null ? hit.Path : null;
            ActiveButtonInstance previousHovered = FindButtonByPath(previousHoveredPath);
            ActiveButtonInstance pressed = FindButtonByPath(pressedButtonPath);
            bool visualStateChanged = !string.Equals(
                previousHoveredPath,
                newHoveredPath,
                System.StringComparison.Ordinal
            );

            if (Mouse.current.leftButton.wasPressedThisFrame)
            {
                if (hit != null)
                {
                    ushort transition = string.Equals(
                        previousHoveredPath,
                        newHoveredPath,
                        System.StringComparison.Ordinal
                    ) ? (ushort)0x0004 : (ushort)0x0080;

                    if (previousHovered != null && transition == 0x0080)
                        TriggerButtonTransition(previousHovered, 0x0002);

                    TriggerButtonTransition(hit, transition);
                    pressedButtonPath = hit.Path;
                    pressedButtonIsOutDown = false;
                }
                else
                {
                    if (previousHovered != null)
                        TriggerButtonTransition(previousHovered, 0x0002);

                    pressedButtonPath = null;
                    pressedButtonIsOutDown = false;
                }

                visualStateChanged = true;
            }
            else if (Mouse.current.leftButton.wasReleasedThisFrame)
            {
                if (pressed != null)
                {
                    bool releasedInside = string.Equals(
                        pressed.Path,
                        newHoveredPath,
                        System.StringComparison.Ordinal
                    );
                    ushort transition = releasedInside
                        ? (ushort)0x0008
                        : pressedButtonIsOutDown ? (ushort)0x0040 : (ushort)0x0100;
                    TriggerButtonTransition(pressed, transition);

                    if (!releasedInside && hit != null)
                        TriggerButtonTransition(hit, 0x0001);
                }
                else if (hit != null && previousHovered == null)
                {
                    TriggerButtonTransition(hit, 0x0001);
                }

                pressedButtonPath = null;
                pressedButtonIsOutDown = false;
                visualStateChanged = true;
            }
            else if (Mouse.current.leftButton.isPressed)
            {
                if (pressed != null)
                {
                    bool isInsidePressed = string.Equals(
                        pressed.Path,
                        newHoveredPath,
                        System.StringComparison.Ordinal
                    );

                    if (!isInsidePressed && !pressedButtonIsOutDown)
                    {
                        TriggerButtonTransition(pressed, 0x0010);
                        pressedButtonIsOutDown = true;
                        visualStateChanged = true;
                    }
                    else if (isInsidePressed && pressedButtonIsOutDown)
                    {
                        TriggerButtonTransition(pressed, 0x0020);
                        pressedButtonIsOutDown = false;
                        visualStateChanged = true;
                    }
                }
                else if (hit != null)
                {
                    TriggerButtonTransition(hit, 0x0080);
                    pressedButtonPath = hit.Path;
                    pressedButtonIsOutDown = false;
                    visualStateChanged = true;
                }
            }
            else if (visualStateChanged)
            {
                if (previousHovered != null)
                    TriggerButtonTransition(previousHovered, 0x0002);

                if (hit != null)
                    TriggerButtonTransition(hit, 0x0001);
            }

            hoveredButtonPath = newHoveredPath;

            if (visualStateChanged)
                RenderCurrentFrame();
        }

        private void HandleKeyboardInput()
        {
            if (runtimeAvm1 == null || Keyboard.current == null)
                return;

            var keys = Keyboard.current.allKeys;

            for (int i = 0; i < keys.Count; i++)
            {
                var keyControl = keys[i];

                if (!keyControl.wasPressedThisFrame && !keyControl.wasReleasedThisFrame)
                    continue;

                int flashCode = ToFlashKeyCode(keyControl.keyCode);

                if (flashCode == 0)
                    continue;

                int asciiCode = ToFlashAsciiCode(keyControl.keyCode, flashCode);
                bool pressed = keyControl.wasPressedThisFrame;
                runtimeAvm1.SetKeyState(flashCode, asciiCode, pressed);
                runtimeAvm1.Broadcast("Key", pressed ? "onKeyDown" : "onKeyUp");
                List<StaticDisplayInstance> instances =
                    new List<StaticDisplayInstance>(staticDisplayInstances.Values);

                for (int instanceIndex = 0; instanceIndex < instances.Count; instanceIndex++)
                {
                    TriggerClipActions(
                        instances[instanceIndex],
                        pressed ? 0x00000002u : 0x00000001u
                    );

                    if (pressed)
                        TriggerClipActions(instances[instanceIndex], 0x00020000u, (byte)flashCode);
                }
            }
        }

        private static int ToFlashKeyCode(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
                return 65 + (key - Key.A);
            if (key >= Key.Digit0 && key <= Key.Digit9)
                return 48 + (key - Key.Digit0);
            if (key >= Key.F1 && key <= Key.F12)
                return 112 + (key - Key.F1);

            switch (key)
            {
                case Key.Backspace: return 8;
                case Key.Tab: return 9;
                case Key.Enter:
                case Key.NumpadEnter: return 13;
                case Key.LeftShift:
                case Key.RightShift: return 16;
                case Key.LeftCtrl:
                case Key.RightCtrl: return 17;
                case Key.LeftAlt:
                case Key.RightAlt: return 18;
                case Key.CapsLock: return 20;
                case Key.Escape: return 27;
                case Key.Space: return 32;
                case Key.PageUp: return 33;
                case Key.PageDown: return 34;
                case Key.End: return 35;
                case Key.Home: return 36;
                case Key.LeftArrow: return 37;
                case Key.UpArrow: return 38;
                case Key.RightArrow: return 39;
                case Key.DownArrow: return 40;
                case Key.Insert: return 45;
                case Key.Delete: return 46;
                default: return 0;
            }
        }

        private static int ToFlashAsciiCode(Key key, int flashCode)
        {
            if ((key >= Key.A && key <= Key.Z) ||
                (key >= Key.Digit0 && key <= Key.Digit9) || key == Key.Space)
            {
                return flashCode;
            }

            return key == Key.Enter || key == Key.NumpadEnter || key == Key.Tab ||
                key == Key.Backspace || key == Key.Escape
                    ? flashCode
                    : 0;
        }

        private void UpdateAvm1MouseCoordinates(Vector2 stagePoint)
        {
            if (runtimeAvm1 == null)
                return;

            runtimeAvm1.RootObject.Set("_xmouse", stagePoint.x);
            runtimeAvm1.RootObject.Set("_ymouse", stagePoint.y);
            Dictionary<Avm1Object, SwfMatrix> worldMatrices =
                new Dictionary<Avm1Object, SwfMatrix>();
            worldMatrices[runtimeAvm1.RootObject] = SwfMatrix.Identity;

            foreach (StaticDisplayInstance instance in staticDisplayInstances.Values)
            {
                if (instance?.ScriptObject == null)
                    continue;

                if (TryGetDisplayObjectWorldMatrix(instance.ScriptObject, worldMatrices, 0, out SwfMatrix world))
                    SetLocalMousePosition(instance.ScriptObject, world, stagePoint);
            }

            for (int i = 0; i < dynamicMovieClips.Count; i++)
            {
                DynamicMovieClip clip = dynamicMovieClips[i];

                if (clip == null || clip.Removed || clip.ScriptObject == null)
                    continue;

                if (TryGetDisplayObjectWorldMatrix(clip.ScriptObject, worldMatrices, 0, out SwfMatrix world))
                    SetLocalMousePosition(clip.ScriptObject, world, stagePoint);
            }
        }

        private bool TryGetDisplayObjectWorldMatrix(
            Avm1Object scriptObject,
            Dictionary<Avm1Object, SwfMatrix> cache,
            int depth,
            out SwfMatrix world
        )
        {
            world = SwfMatrix.Identity;

            if (scriptObject == null || depth > 128)
                return false;

            if (cache.TryGetValue(scriptObject, out world))
                return true;

            Avm1Object parent = null;

            if (staticDisplayInstancesByObject.TryGetValue(scriptObject, out StaticDisplayInstance staticInstance))
                parent = staticInstance.ParentObject;
            else if (dynamicMovieClipsByObject.TryGetValue(scriptObject, out DynamicMovieClip dynamicClip))
                parent = dynamicClip.ParentObject;
            else
                parent = scriptObject.Get("_parent") as Avm1Object;

            SwfMatrix parentWorld = SwfMatrix.Identity;

            if (parent != null && parent != runtimeAvm1.RootObject &&
                !TryGetDisplayObjectWorldMatrix(parent, cache, depth + 1, out parentWorld))
            {
                return false;
            }

            world = SwfMatrix.Combine(parentWorld, BuildDynamicMovieClipMatrix(scriptObject));
            cache[scriptObject] = world;
            return true;
        }

        private static void SetLocalMousePosition(
            Avm1Object scriptObject,
            SwfMatrix world,
            Vector2 stagePoint
        )
        {
            float determinant = world.ScaleX * world.ScaleY -
                world.RotateSkew1 * world.RotateSkew0;

            if (Mathf.Abs(determinant) < 0.000001f || float.IsNaN(determinant))
                return;

            float translatedX = stagePoint.x - world.TranslateX;
            float translatedY = stagePoint.y - world.TranslateY;
            float localX = (world.ScaleY * translatedX - world.RotateSkew1 * translatedY) /
                determinant;
            float localY = (-world.RotateSkew0 * translatedX + world.ScaleX * translatedY) /
                determinant;

            if (float.IsNaN(localX) || float.IsInfinity(localX) ||
                float.IsNaN(localY) || float.IsInfinity(localY))
            {
                return;
            }

            scriptObject.Set("_xmouse", localX);
            scriptObject.Set("_ymouse", localY);
        }

        private ActiveButtonInstance FindButtonByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            for (int i = 0; i < activeButtons.Count; i++)
            {
                if (string.Equals(activeButtons[i].Path, path, System.StringComparison.Ordinal))
                    return activeButtons[i];
            }

            return null;
        }

        private void TriggerButtonTransition(
            ActiveButtonInstance instance,
            ushort transitionFlag
        )
        {
            DefineButton2Tag button = instance != null ? instance.Button : null;

            if (button == null || button.Actions == null)
                return;

            for (int i = 0; i < button.Actions.Count; i++)
            {
                SwfButtonCondAction action = button.Actions[i];

                if (action == null || !action.MatchesTransition(transitionFlag))
                    continue;

                ButtonActionTriggered?.Invoke(button, action);

                if (runtimeAvm1 != null)
                    runtimeAvm1.Execute(
                        action.ActionBytes,
                        instance.ScriptObject ?? runtimeAvm1.RootObject
                    );

                if (verboseLogging)
                {
                    Debug.Log(
                        "Flash button transition ButtonId=" + button.ButtonId +
                        " Path=" + instance.Path +
                        " Flag=0x" + transitionFlag.ToString("X4") +
                        " ActionBytes=" + (action.ActionBytes != null ? action.ActionBytes.Length : 0)
                    );
                }
            }
        }

        private bool TryGetPointerFlashPosition(out Vector2 flashPoint)
        {
            flashPoint = Vector2.zero;

            Camera camera = Camera.main;

            if (camera == null || runtimeParser == null || runtimeParser.Header == null)
                return false;

            Vector2 screenPoint = Mouse.current.position.ReadValue();
            float cameraDistance = Mathf.Abs(camera.transform.position.z - transform.position.z);
            Vector3 worldPoint = camera.ScreenToWorldPoint(
                new Vector3(screenPoint.x, screenPoint.y, cameraDistance)
            );
            Vector3 localPoint = transform.InverseTransformPoint(worldPoint);

            const float pixelsPerUnit = 50f;

            flashPoint = new Vector2(
                localPoint.x * pixelsPerUnit + runtimeParser.Header.StageWidth * 0.5f,
                runtimeParser.Header.StageHeight * 0.5f - localPoint.y * pixelsPerUnit
            );

            return true;
        }

        private ActiveButtonInstance FindTopButtonAt(Vector2 flashPoint)
        {
            for (int i = activeButtons.Count - 1; i >= 0; i--)
            {
                ActiveButtonInstance instance = activeButtons[i];

                if (IsPointInsideButton(instance, flashPoint))
                    return instance;
            }

            return null;
        }

        private bool IsPointInsideButton(ActiveButtonInstance instance, Vector2 flashPoint)
        {
            if (instance == null || instance.Button == null)
                return false;

            for (int r = 0; r < instance.Button.Records.Count; r++)
            {
                SwfButtonRecord record = instance.Button.Records[r];

                if (!record.StateHitTest)
                    continue;

                DefineShapeTag shape = runtimeParser.FindShapeById(record.CharacterId);

                if (shape == null || shape.ShapeData == null || shape.ShapeData.FillEdgeGroups == null)
                    continue;

                SwfMatrix hitMatrix = SwfMatrix.Combine(instance.WorldMatrix, record.Matrix);

                for (int g = 0; g < shape.ShapeData.FillEdgeGroups.Count; g++)
                {
                    SwfFillEdgeGroup group = shape.ShapeData.FillEdgeGroups[g];

                    if (group == null || group.Contours == null)
                        continue;

                    int containingContours = 0;

                    for (int c = 0; c < group.Contours.Count; c++)
                    {
                        SwfFillContour contour = group.Contours[c];

                        if (contour == null || contour.Points == null || contour.Points.Count < 3)
                            continue;

                        if (PointInTransformedPolygon(flashPoint, contour.Points, hitMatrix))
                        {
                            containingContours++;
                        }
                    }

                    if ((containingContours & 1) == 1)
                        return true;
                }
            }

            return false;
        }

        private bool PointInTransformedPolygon(
            Vector2 point,
            List<Vector2> polygon,
            SwfMatrix matrix
        )
        {
            bool inside = false;

            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 a = matrix.TransformPoint(polygon[i]);
                Vector2 b = matrix.TransformPoint(polygon[j]);

                bool intersects =
                    ((a.y > point.y) != (b.y > point.y)) &&
                    (point.x < (b.x - a.x) * (point.y - a.y) / ((b.y - a.y) + 0.00001f) + a.x);

                if (intersects)
                    inside = !inside;
            }

            return inside;
        }

        private void InitializeAvm1(SwfParser parser)
        {
            runtimeAvm1 = null;
            avm1LastExecutedFrames.Clear();
            dynamicMovieClips.Clear();
            dynamicMovieClipsByObject.Clear();
            staticDisplayInstances.Clear();
            staticDisplayInstancesByObject.Clear();
            displayListCache.Clear();

            if (!enableAvm1 || parser == null)
                return;

            runtimeAvm1 = new Avm1Runtime(parser.Header.Version >= 7)
            {
                VerboseLogging = verboseLogging,
                ExternalMethod = HandleAvm1ExternalMethod,
                Trace = message => Debug.Log("[AVM1] " + message)
            };

            runtimeAvm1.SetVariable("MOVIE_WIDTH", parser.Header.StageWidth);
            runtimeAvm1.SetVariable("MOVIE_HEIGHT", parser.Header.StageHeight);
            runtimeAvm1.RootObject.Set("_width", parser.Header.StageWidth);
            runtimeAvm1.RootObject.Set("_height", parser.Header.StageHeight);
            runtimeAvm1.RootObject.Set("_totalframes", parser.Header.FrameCount);

            if (runtimeAvm1.GetVariable("Stage") is Avm1Object stageObject)
            {
                stageObject.Set("width", parser.Header.StageWidth);
                stageObject.Set("height", parser.Header.StageHeight);
            }

            for (int i = 0; i < parser.Tags.Count; i++)
            {
                SwfTag tag = parser.Tags[i];

                if (tag.Code == 12)
                    runtimeAvm1.RegisterFunctions(parser.CopyTagData(tag));
                else if (tag.Code == 59)
                    runtimeAvm1.RegisterFunctions(parser.CopyTagData(tag, 2));
            }

            for (int i = 0; i < parser.Tags.Count; i++)
            {
                SwfTag tag = parser.Tags[i];

                if (tag.Code == 59)
                    runtimeAvm1.Execute(parser.CopyTagData(tag, 2));
            }

            if (verboseLogging)
                Debug.Log("AVM1 initialized. Functions=" + runtimeAvm1.DefinedFunctionCount);
        }

        private void ExecuteCurrentTimelineActions()
        {
            if (runtimeAvm1 == null || runtimeParser == null)
                return;

            if (runtimeParser.RootFrames != null && runtimeParser.RootFrames.Count > 0)
            {
                int rootFrame = currentTimelineFrame % runtimeParser.RootFrames.Count;
                SwfFrame frame = runtimeParser.RootFrames[rootFrame];
                List<PlaceObject2Tag> places = BuildDisplayListForFrame(
                    runtimeParser,
                    runtimeParser.RootFrames,
                    rootFrame
                );

                for (int i = 0; i < places.Count; i++)
                {
                    PlaceObject2Tag place = places[i];

                    if (place == null || !place.HasCharacter)
                        continue;

                    bool isSprite = runtimeParser.FindSpriteById(place.CharacterId) != null;
                    string path = "_root/depth" + place.Depth +
                        (isSprite ? ":sprite" : ":char") + place.CharacterId;
                    GetOrCreateStaticDisplayInstance(path, place, runtimeAvm1.RootObject);
                }

                ExecuteFrameActionsOnce(
                    "_root",
                    rootFrame,
                    frame.ControlTags,
                    runtimeAvm1.RootObject
                );

                ExecuteVisibleSpriteActions(
                    places,
                    currentTimelineFrame,
                    "_root",
                    runtimeAvm1.RootObject,
                    0
                );
            }

            runtimeAvm1.TryCallFunction("onEnterFrame", new object[0], out _);

            foreach (StaticDisplayInstance instance in staticDisplayInstances.Values)
            {
                TriggerClipActions(instance, 0x00000040u);
                runtimeAvm1.TryCallMethod(instance.ScriptObject, "onEnterFrame", new object[0], out _);
            }

            for (int i = 0; i < dynamicMovieClips.Count; i++)
            {
                DynamicMovieClip clip = dynamicMovieClips[i];

                if (!clip.Removed)
                    runtimeAvm1.TryCallMethod(clip.ScriptObject, "onEnterFrame", new object[0], out _);
            }
        }

        private void ExecuteVisibleSpriteActions(
            List<PlaceObject2Tag> places,
            int parentFrame,
            string parentPath,
            Avm1Object parentObject,
            int recursionDepth
        )
        {
            if (places == null || recursionDepth > 64)
                return;

            for (int i = 0; i < places.Count; i++)
            {
                PlaceObject2Tag place = places[i];

                if (place == null || !place.HasCharacter)
                    continue;

                DefineSpriteTag sprite = runtimeParser.FindSpriteById(place.CharacterId);

                if (sprite == null || sprite.Frames == null || sprite.Frames.Count == 0)
                    continue;

                string path = parentPath + "/depth" + place.Depth + ":sprite" + sprite.SpriteId;
                StaticDisplayInstance instance = GetOrCreateStaticDisplayInstance(
                    path,
                    place,
                    parentObject
                );
                int localFrame = Mathf.Clamp(instance.CurrentFrame, 0, sprite.Frames.Count - 1);
                instance.ScriptObject.Set("_currentframe", localFrame + 1);
                SwfFrame frame = sprite.Frames[localFrame];
                List<PlaceObject2Tag> childPlaces = BuildDisplayListForFrame(
                    runtimeParser,
                    sprite.Frames,
                    localFrame
                );

                for (int childIndex = 0; childIndex < childPlaces.Count; childIndex++)
                {
                    PlaceObject2Tag child = childPlaces[childIndex];

                    if (child == null || !child.HasCharacter)
                        continue;

                    bool childIsSprite = runtimeParser.FindSpriteById(child.CharacterId) != null;
                    string childPath = path + "/depth" + child.Depth +
                        (childIsSprite ? ":sprite" : ":char") + child.CharacterId;
                    GetOrCreateStaticDisplayInstance(childPath, child, instance.ScriptObject);
                }

                ExecuteFrameActionsOnce(path, localFrame, frame.ControlTags, instance.ScriptObject);

                ExecuteVisibleSpriteActions(
                    childPlaces,
                    localFrame,
                    path,
                    instance.ScriptObject,
                    recursionDepth + 1
                );
            }
        }

        private void ExecuteFrameActionsOnce(
            string timelinePath,
            int frameIndex,
            List<SwfTag> controlTags,
            Avm1Object thisObject
        )
        {
            if (controlTags == null)
                return;

            if (avm1LastExecutedFrames.TryGetValue(timelinePath, out int previousFrame) &&
                previousFrame == frameIndex)
            {
                return;
            }

            avm1LastExecutedFrames[timelinePath] = frameIndex;

            for (int i = 0; i < controlTags.Count; i++)
            {
                SwfTag tag = controlTags[i];

                ExecuteTimelineControlTag(tag, thisObject);
            }
        }

        private void ExecuteTimelineControlTag(SwfTag tag, Avm1Object thisObject)
        {
            if (tag == null)
                return;

            if (tag.Code == 12)
            {
                runtimeAvm1?.Execute(runtimeParser.CopyTagData(tag), thisObject);
                return;
            }

            if (tag.Code == 15)
            {
                byte[] startSound = runtimeParser.CopyTagData(tag);

                if (startSound.Length < 2)
                    return;

                ushort soundId = (ushort)(startSound[0] | (startSound[1] << 8));
                bool syncStop = startSound.Length > 2 && (startSound[2] & 0x20) != 0;

                if (syncStop)
                    runtimeAudio?.StopSound(soundId);
                else
                    runtimeAudio?.PlaySound(soundId);

                return;
            }

            if (tag.Code == 89)
            {
                byte[] startSound2 = runtimeParser.CopyTagData(tag);
                int terminator = 0;

                while (terminator < startSound2.Length && startSound2[terminator] != 0)
                    terminator++;

                if (terminator > 0)
                {
                    string exportName = System.Text.Encoding.UTF8.GetString(
                        startSound2,
                        0,
                        terminator
                    );
                    runtimeAudio?.PlayExported(exportName);
                }
            }
        }

        private StaticDisplayInstance GetOrCreateStaticDisplayInstance(
            string path,
            PlaceObject2Tag place,
            Avm1Object parentObject
        )
        {
            if (staticDisplayInstances.TryGetValue(path, out StaticDisplayInstance existing))
            {
                existing.ClipActions = place.ClipActions;
                return existing;
            }

            Avm1Object scriptObject = runtimeAvm1.CreateObject();
            string instanceName = place.HasName && !string.IsNullOrEmpty(place.Name)
                ? place.Name
                : string.Empty;
            StaticDisplayInstance instance = new StaticDisplayInstance
            {
                ScriptObject = scriptObject,
                ParentObject = parentObject ?? runtimeAvm1.RootObject,
                CharacterId = place.CharacterId,
                InstanceName = instanceName,
                Path = path,
                Depth = place.Depth,
                CurrentFrame = 0,
                IsPlaying = true,
                ClipActions = place.ClipActions
            };

            InitializeDisplayObjectProperties(
                scriptObject,
                instance.ParentObject,
                instanceName,
                path,
                place.Depth,
                place.CharacterId,
                place.Matrix,
                !place.HasVisible || place.Visible != 0
            );

            staticDisplayInstances[path] = instance;
            staticDisplayInstancesByObject[scriptObject] = instance;

            if (!string.IsNullOrEmpty(instanceName))
                instance.ParentObject.Set(instanceName, scriptObject);

            string registeredLinkage = !string.IsNullOrEmpty(place.ClassName)
                ? place.ClassName
                : FindExportName(place.CharacterId);
            runtimeAvm1.ApplyRegisteredClass(registeredLinkage, scriptObject);

            TriggerClipActions(instance, 0x00004000u); // Initialize
            TriggerClipActions(instance, 0x00000080u); // Load

            return instance;
        }

        private void TriggerClipActions(
            StaticDisplayInstance instance,
            uint eventFlag,
            byte keyCode = 0
        )
        {
            if (instance == null || runtimeAvm1 == null || instance.ClipActions == null)
                return;

            for (int i = 0; i < instance.ClipActions.Count; i++)
            {
                SwfClipActionRecord action = instance.ClipActions[i];

                if (action == null || !action.Matches(eventFlag) ||
                    (eventFlag == 0x00020000u && action.KeyCode != keyCode))
                {
                    continue;
                }

                runtimeAvm1.Execute(action.ActionBytes, instance.ScriptObject);
            }
        }

        private void InitializeDisplayObjectProperties(
            Avm1Object scriptObject,
            Avm1Object parentObject,
            string instanceName,
            string targetPath,
            int depth,
            ushort characterId,
            SwfMatrix matrix,
            bool visible
        )
        {
            float scaleX = Mathf.Sqrt(
                matrix.ScaleX * matrix.ScaleX + matrix.RotateSkew0 * matrix.RotateSkew0
            );
            float scaleY = Mathf.Sqrt(
                matrix.RotateSkew1 * matrix.RotateSkew1 + matrix.ScaleY * matrix.ScaleY
            );
            float rotation = Mathf.Atan2(matrix.RotateSkew0, matrix.ScaleX) * Mathf.Rad2Deg;

            scriptObject.Set("_root", runtimeAvm1.RootObject);
            scriptObject.Set("_parent", parentObject ?? runtimeAvm1.RootObject);
            scriptObject.Set("_name", instanceName ?? string.Empty);
            scriptObject.Set("_target", targetPath);
            scriptObject.Set("_depth", depth);
            scriptObject.Set("_x", matrix.TranslateX);
            scriptObject.Set("_y", matrix.TranslateY);
            scriptObject.Set("_xscale", scaleX * 100f);
            scriptObject.Set("_yscale", scaleY * 100f);
            scriptObject.Set("_rotation", rotation);
            scriptObject.Set("__baseX", matrix.TranslateX);
            scriptObject.Set("__baseY", matrix.TranslateY);
            scriptObject.Set("__baseXScale", scaleX * 100f);
            scriptObject.Set("__baseYScale", scaleY * 100f);
            scriptObject.Set("__baseRotation", rotation);
            scriptObject.Set("_alpha", 100d);
            scriptObject.Set("_visible", visible);
            scriptObject.Set("_currentframe", 1);
            scriptObject.Set("_totalframes", GetCharacterFrameCount(characterId));
            scriptObject.Set("__characterId", characterId);
        }

        private Avm1Object CreateDynamicMovieClip(
            Avm1Object requestedParent,
            string linkageName,
            ushort explicitCharacterId,
            string instanceName,
            int depth,
            Avm1Object initObject,
            bool allowEmpty
        )
        {
            if (runtimeAvm1 == null || runtimeParser == null)
                return null;

            Avm1Object parentObject = requestedParent ?? runtimeAvm1.RootObject;
            ushort characterId = explicitCharacterId;

            if (characterId == 0 && !string.IsNullOrEmpty(linkageName))
                runtimeParser.ExportedAssets.TryGetValue(linkageName, out characterId);

            if (characterId == 0 && !allowEmpty)
                return null;

            for (int i = dynamicMovieClips.Count - 1; i >= 0; i--)
            {
                DynamicMovieClip existing = dynamicMovieClips[i];

                if (!existing.Removed && existing.ParentObject == parentObject &&
                    (existing.Depth == depth ||
                     (!string.IsNullOrEmpty(instanceName) && existing.InstanceName == instanceName)))
                {
                    RemoveDynamicMovieClip(existing.ScriptObject);
                }
            }

            Avm1Object scriptObject = runtimeAvm1.CreateObject();
            DynamicMovieClip clip = new DynamicMovieClip
            {
                ScriptObject = scriptObject,
                ParentObject = parentObject,
                CharacterId = characterId,
                LinkageName = linkageName,
                InstanceName = instanceName,
                Depth = depth,
                CurrentFrame = 0,
                IsPlaying = true
            };

            scriptObject.Set("_root", runtimeAvm1.RootObject);
            scriptObject.Set("_parent", parentObject);
            scriptObject.Set("_name", instanceName ?? string.Empty);
            scriptObject.Set("_depth", depth);
            scriptObject.Set("_x", 0d);
            scriptObject.Set("_y", 0d);
            scriptObject.Set("_xscale", 100d);
            scriptObject.Set("_yscale", 100d);
            scriptObject.Set("_rotation", 0d);
            scriptObject.Set("_alpha", 100d);
            scriptObject.Set("_visible", true);
            scriptObject.Set("_currentframe", 1);
            scriptObject.Set("_totalframes", GetCharacterFrameCount(characterId));
            scriptObject.Set("__linkage", linkageName ?? string.Empty);
            scriptObject.Set("__characterId", characterId);

            initObject?.CopyMembersTo(scriptObject);

            dynamicMovieClips.Add(clip);
            dynamicMovieClipsByObject[scriptObject] = clip;

            if (!string.IsNullOrEmpty(instanceName))
                parentObject.Set(instanceName, scriptObject);

            runtimeAvm1.ApplyRegisteredClass(linkageName, scriptObject);

            ExecuteDynamicClipFrameActions(clip);
            return scriptObject;
        }

        private string FindExportName(ushort characterId)
        {
            if (runtimeParser == null || characterId == 0)
                return null;

            foreach (KeyValuePair<string, ushort> asset in runtimeParser.ExportedAssets)
                if (asset.Value == characterId) return asset.Key;

            return null;
        }

        private void RemoveDynamicMovieClip(Avm1Object scriptObject)
        {
            if (scriptObject == null ||
                !dynamicMovieClipsByObject.TryGetValue(scriptObject, out DynamicMovieClip clip) ||
                clip.Removed)
            {
                return;
            }

            clip.Removed = true;

            if (!string.IsNullOrEmpty(clip.InstanceName))
                clip.ParentObject?.Remove(clip.InstanceName);

            for (int i = 0; i < dynamicMovieClips.Count; i++)
            {
                DynamicMovieClip child = dynamicMovieClips[i];

                if (!child.Removed && child.ParentObject == scriptObject)
                    RemoveDynamicMovieClip(child.ScriptObject);
            }
        }

        private void AdvanceDynamicMovieClips()
        {
            int initialCount = dynamicMovieClips.Count;

            for (int i = 0; i < initialCount; i++)
            {
                DynamicMovieClip clip = dynamicMovieClips[i];

                if (clip.Removed || !clip.IsPlaying)
                    continue;

                int frameCount = GetCharacterFrameCount(clip.CharacterId);

                if (frameCount <= 1)
                    continue;

                clip.CurrentFrame = (clip.CurrentFrame + 1) % frameCount;
                clip.ScriptObject.Set("_currentframe", clip.CurrentFrame + 1);
                ExecuteDynamicClipFrameActions(clip);
            }
        }

        private void AdvanceStaticMovieClips()
        {
            if (runtimeParser == null || staticDisplayInstances.Count == 0)
                return;

            foreach (StaticDisplayInstance instance in staticDisplayInstances.Values)
            {
                if (instance == null || !instance.IsPlaying)
                    continue;

                DefineSpriteTag sprite = runtimeParser.FindSpriteById(instance.CharacterId);

                if (sprite == null || sprite.Frames == null || sprite.Frames.Count <= 1)
                    continue;

                instance.CurrentFrame++;

                if (instance.CurrentFrame >= sprite.Frames.Count)
                {
                    instance.CurrentFrame = loopSpriteTimelines
                        ? 0
                        : sprite.Frames.Count - 1;

                    if (!loopSpriteTimelines)
                        instance.IsPlaying = false;
                }

                instance.ScriptObject.Set("_currentframe", instance.CurrentFrame + 1);
            }
        }

        private void ExecuteDynamicClipFrameActions(DynamicMovieClip clip)
        {
            if (clip == null || clip.Removed || runtimeAvm1 == null)
                return;

            DefineSpriteTag sprite = runtimeParser.FindSpriteById(clip.CharacterId);

            if (sprite == null || sprite.Frames == null || sprite.Frames.Count == 0)
                return;

            int frameIndex = Mathf.Clamp(clip.CurrentFrame, 0, sprite.Frames.Count - 1);
            List<SwfTag> tags = sprite.Frames[frameIndex].ControlTags;

            for (int i = 0; i < tags.Count; i++)
                ExecuteTimelineControlTag(tags[i], clip.ScriptObject);
        }

        private void RenderDynamicMovieClips(
            SwfParser parser,
            SwfDebugRenderer renderer,
            Avm1Object parentObject,
            SwfMatrix parentMatrix,
            float parentAlpha,
            int recursionDepth
        )
        {
            if (runtimeAvm1 == null || recursionDepth > 64)
                return;

            Avm1Object resolvedParent = parentObject ?? runtimeAvm1.RootObject;
            int lastDepth = int.MinValue;

            while (true)
            {
                DynamicMovieClip next = null;

                for (int i = 0; i < dynamicMovieClips.Count; i++)
                {
                    DynamicMovieClip candidate = dynamicMovieClips[i];

                    if (candidate.Removed || candidate.ParentObject != resolvedParent ||
                        candidate.Depth <= lastDepth)
                    {
                        continue;
                    }

                    if (next == null || candidate.Depth < next.Depth)
                        next = candidate;
                }

                if (next == null)
                    break;

                lastDepth = next.Depth;

                if (IsObjectUsedAsMask(next.ScriptObject))
                    continue;

                if (!Avm1Boolean(next.ScriptObject.Get("_visible"), true))
                    continue;

                SwfMatrix localMatrix = BuildDynamicMovieClipMatrix(next.ScriptObject);
                SwfMatrix worldMatrix = SwfMatrix.Combine(parentMatrix, localMatrix);
                float alpha = parentAlpha * Mathf.Clamp01(
                    Avm1Float(next.ScriptObject.Get("_alpha"), 100f) / 100f
                );

                if (next.CharacterId != 0 && alpha > 0.001f)
                {
                    RenderCharacterDebug(
                        parser,
                        renderer,
                        next.CharacterId,
                        worldMatrix,
                        "Dynamic_" + next.InstanceName + "_Depth_" + next.Depth,
                        alpha,
                        next.CurrentFrame,
                        0,
                        false,
                        next.ScriptObject,
                        "Dynamic_" + next.InstanceName,
                        false,
                        true
                    );
                }

                if (next.CharacterId == 0 || parser.FindSpriteById(next.CharacterId) == null)
                {
                    RenderDynamicMovieClips(
                        parser,
                        renderer,
                        next.ScriptObject,
                        worldMatrix,
                        alpha,
                        recursionDepth + 1
                    );
                }
            }
        }

        private SwfMatrix BuildDynamicMovieClipMatrix(Avm1Object scriptObject)
        {
            float baseX = Avm1Float(scriptObject.Get("__baseX"), 0f);
            float baseY = Avm1Float(scriptObject.Get("__baseY"), 0f);
            float baseXScale = Avm1Float(scriptObject.Get("__baseXScale"), 100f);
            float baseYScale = Avm1Float(scriptObject.Get("__baseYScale"), 100f);
            float baseRotation = Avm1Float(scriptObject.Get("__baseRotation"), 0f);
            float scaleX = Avm1Float(scriptObject.Get("_xscale"), baseXScale) / 100f;
            float scaleY = Avm1Float(scriptObject.Get("_yscale"), baseYScale) / 100f;
            float rotation = Avm1Float(scriptObject.Get("_rotation"), baseRotation) * Mathf.Deg2Rad;
            float cosine = Mathf.Cos(rotation);
            float sine = Mathf.Sin(rotation);

            return new SwfMatrix
            {
                ScaleX = cosine * scaleX,
                RotateSkew0 = sine * scaleX,
                RotateSkew1 = -sine * scaleY,
                ScaleY = cosine * scaleY,
                TranslateX = Avm1Float(scriptObject.Get("_x"), baseX),
                TranslateY = Avm1Float(scriptObject.Get("_y"), baseY)
            };
        }

        private int GetCharacterFrameCount(ushort characterId)
        {
            DefineSpriteTag sprite = runtimeParser != null
                ? runtimeParser.FindSpriteById(characterId)
                : null;
            return sprite != null && sprite.Frames != null
                ? Mathf.Max(1, sprite.Frames.Count)
                : 1;
        }

        private int GetNextDynamicDepth(object receiver)
        {
            Avm1Object parent = receiver as Avm1Object ?? runtimeAvm1?.RootObject;
            int highestDepth = -1;

            for (int i = 0; i < dynamicMovieClips.Count; i++)
            {
                DynamicMovieClip clip = dynamicMovieClips[i];

                if (!clip.Removed && clip.ParentObject == parent)
                    highestDepth = Mathf.Max(highestDepth, clip.Depth);
            }

            return highestDepth + 1;
        }

        private void SetDynamicClipPlaying(object receiver, bool playing)
        {
            if (receiver == runtimeAvm1?.RootObject)
            {
                autoPlayTimeline = playing;
                return;
            }

            if (receiver is Avm1Object scriptObject &&
                dynamicMovieClipsByObject.TryGetValue(scriptObject, out DynamicMovieClip clip))
            {
                clip.IsPlaying = playing;
                return;
            }

            if (receiver is Avm1Object staticObject &&
                staticDisplayInstancesByObject.TryGetValue(staticObject, out StaticDisplayInstance staticInstance))
            {
                staticInstance.IsPlaying = playing;
            }
        }

        private void StepDynamicClip(object receiver, int direction)
        {
            if (receiver == runtimeAvm1?.RootObject)
            {
                int frameCount = Mathf.Max(1, runtimeParser.RootFrames.Count);
                currentTimelineFrame = (currentTimelineFrame + direction + frameCount) % frameCount;
                return;
            }

            if (receiver is Avm1Object scriptObject &&
                dynamicMovieClipsByObject.TryGetValue(scriptObject, out DynamicMovieClip clip))
            {
                int frameCount = GetCharacterFrameCount(clip.CharacterId);
                clip.CurrentFrame = (clip.CurrentFrame + direction + frameCount) % frameCount;
                clip.ScriptObject.Set("_currentframe", clip.CurrentFrame + 1);
                ExecuteDynamicClipFrameActions(clip);
                return;
            }

            if (receiver is Avm1Object staticObject &&
                staticDisplayInstancesByObject.TryGetValue(staticObject, out StaticDisplayInstance staticInstance))
            {
                int frameCount = GetCharacterFrameCount(staticInstance.CharacterId);
                staticInstance.CurrentFrame =
                    (staticInstance.CurrentFrame + direction + frameCount) % frameCount;
                staticInstance.ScriptObject.Set("_currentframe", staticInstance.CurrentFrame + 1);
            }
        }

        private void SetDynamicClipFrame(
            object receiver,
            IReadOnlyList<object> arguments,
            bool playing
        )
        {
            object requestedValue = arguments.Count > 0 ? arguments[0] : 1;
            int requestedFrame = ResolveTimelineFrame(receiver, requestedValue);

            if (receiver == runtimeAvm1?.RootObject)
            {
                currentTimelineFrame = Mathf.Clamp(
                    requestedFrame,
                    0,
                    Mathf.Max(0, runtimeParser.RootFrames.Count - 1)
                );
                autoPlayTimeline = playing;
                return;
            }

            if (receiver is Avm1Object scriptObject &&
                dynamicMovieClipsByObject.TryGetValue(scriptObject, out DynamicMovieClip clip))
            {
                clip.CurrentFrame = Mathf.Clamp(
                    requestedFrame,
                    0,
                    GetCharacterFrameCount(clip.CharacterId) - 1
                );
                clip.IsPlaying = playing;
                clip.ScriptObject.Set("_currentframe", clip.CurrentFrame + 1);
                ExecuteDynamicClipFrameActions(clip);
                return;
            }

            if (receiver is Avm1Object staticObject &&
                staticDisplayInstancesByObject.TryGetValue(staticObject, out StaticDisplayInstance staticInstance))
            {
                staticInstance.CurrentFrame = Mathf.Clamp(
                    requestedFrame,
                    0,
                    GetCharacterFrameCount(staticInstance.CharacterId) - 1
                );
                staticInstance.IsPlaying = playing;
                staticInstance.ScriptObject.Set("_currentframe", staticInstance.CurrentFrame + 1);
            }
        }

        private int ResolveTimelineFrame(object receiver, object requestedValue)
        {
            List<SwfFrame> frames = null;

            if (receiver == runtimeAvm1?.RootObject)
            {
                frames = runtimeParser?.RootFrames;
            }
            else if (receiver is Avm1Object scriptObject &&
                dynamicMovieClipsByObject.TryGetValue(scriptObject, out DynamicMovieClip dynamicClip))
            {
                frames = runtimeParser.FindSpriteById(dynamicClip.CharacterId)?.Frames;
            }
            else if (receiver is Avm1Object staticObject &&
                staticDisplayInstancesByObject.TryGetValue(staticObject, out StaticDisplayInstance staticClip))
            {
                frames = runtimeParser.FindSpriteById(staticClip.CharacterId)?.Frames;
            }

            if (requestedValue is string label &&
                !int.TryParse(label, out int numericLabel) && frames != null)
            {
                for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    List<SwfTag> tags = frames[frameIndex].ControlTags;

                    for (int i = 0; i < tags.Count; i++)
                    {
                        if (tags[i].Code != 43)
                            continue;

                        byte[] labelData = runtimeParser.CopyTagData(tags[i]);
                        int length = 0;

                        while (length < labelData.Length && labelData[length] != 0)
                            length++;

                        string frameLabel = System.Text.Encoding.UTF8.GetString(labelData, 0, length);

                        if (string.Equals(frameLabel, label, System.StringComparison.Ordinal))
                            return frameIndex;
                    }
                }

                return 0;
            }

            int oneBasedFrame = requestedValue is string numericText &&
                int.TryParse(numericText, out int parsed)
                    ? parsed
                    : Mathf.RoundToInt(Avm1Float(requestedValue, 1f));
            return Mathf.Max(0, oneBasedFrame - 1);
        }

        private void SwapDynamicClipDepth(object receiver, IReadOnlyList<object> arguments)
        {
            if (!(receiver is Avm1Object scriptObject) ||
                !dynamicMovieClipsByObject.TryGetValue(scriptObject, out DynamicMovieClip clip) ||
                arguments.Count == 0)
            {
                return;
            }

            if (arguments[0] is Avm1Object otherObject &&
                dynamicMovieClipsByObject.TryGetValue(otherObject, out DynamicMovieClip otherClip))
            {
                int depth = clip.Depth;
                clip.Depth = otherClip.Depth;
                otherClip.Depth = depth;
                clip.ScriptObject.Set("_depth", clip.Depth);
                otherClip.ScriptObject.Set("_depth", otherClip.Depth);
            }
            else
            {
                clip.Depth = ArgumentInt(arguments, 0, clip.Depth);
                clip.ScriptObject.Set("_depth", clip.Depth);
            }
        }

        private static string ArgumentString(IReadOnlyList<object> arguments, int index)
        {
            return index >= 0 && index < arguments.Count && arguments[index] != null
                ? arguments[index].ToString()
                : string.Empty;
        }

        private static int ArgumentInt(
            IReadOnlyList<object> arguments,
            int index,
            int fallback
        )
        {
            return index >= 0 && index < arguments.Count
                ? Mathf.RoundToInt(Avm1Float(arguments[index], fallback))
                : fallback;
        }

        private static float Avm1Float(object value, float fallback)
        {
            if (value == null)
                return fallback;

            try
            {
                float converted = System.Convert.ToSingle(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture
                );

                return float.IsNaN(converted) || float.IsInfinity(converted)
                    ? fallback
                    : converted;
            }
            catch
            {
                return fallback;
            }
        }

        private static bool Avm1Boolean(object value, bool fallback)
        {
            if (value == null)
                return fallback;
            if (value is bool boolean)
                return boolean;
            if (value is string text)
                return !string.IsNullOrEmpty(text) && text != "0" && text != "false";
            return Mathf.Abs(Avm1Float(value, fallback ? 1f : 0f)) > 0.0001f;
        }

        private object HandleAvm1ExternalMethod(
            object receiver,
            string functionName,
            IReadOnlyList<object> arguments
        )
        {
            string normalized = (functionName ?? string.Empty).ToLowerInvariant();

            switch (normalized)
            {
                case "new:sound":
                {
                    Avm1Object sound = runtimeAvm1.CreateObject();
                    sound.Set("attachSound", new Avm1NativeFunction(args =>
                    {
                        if (args.Count > 0)
                            sound.Set("__exportName", args[0]?.ToString());
                        return true;
                    }));
                    sound.Set("start", new Avm1NativeFunction(args =>
                    {
                        string exportName = sound.Get("__exportName") as string;
                        return runtimeAudio != null && runtimeAudio.PlayExported(exportName);
                    }));
                    sound.Set("stop", new Avm1NativeFunction(args =>
                    {
                        runtimeAudio?.StopAll();
                        return true;
                    }));
                    return sound;
                }

                case "attachmovie":
                {
                    string linkage = ArgumentString(arguments, 0);
                    string instanceName = ArgumentString(arguments, 1);
                    int depth = ArgumentInt(arguments, 2, GetNextDynamicDepth(receiver));
                    Avm1Object initObject = arguments.Count > 3 ? arguments[3] as Avm1Object : null;
                    Avm1Object attached = CreateDynamicMovieClip(
                        receiver as Avm1Object,
                        linkage,
                        0,
                        instanceName,
                        depth,
                        initObject,
                        false
                    );

                    // A silently failing attachMovie looks identical on screen to
                    // a rendering bug, so say which one it is.
                    if (attached == null)
                    {
                        Debug.LogWarning(
                            "attachMovie('" + linkage + "') failed: no exported asset with that " +
                            "linkage name. Exported names: " +
                            string.Join(", ", runtimeParser.ExportedAssets.Keys)
                        );
                    }
                    else if (verboseLogging)
                    {
                        Debug.Log(
                            "attachMovie('" + linkage + "') -> depth " + depth +
                            " parentIsRoot=" + (receiver == runtimeAvm1.RootObject) +
                            " totalDynamicClips=" + dynamicMovieClips.Count
                        );
                    }

                    return attached;
                }

                case "createemptymovieclip":
                    return CreateDynamicMovieClip(
                        receiver as Avm1Object,
                        null,
                        0,
                        ArgumentString(arguments, 0),
                        ArgumentInt(arguments, 1, GetNextDynamicDepth(receiver)),
                        null,
                        true
                    );

                case "duplicatemovieclip":
                {
                    if (!(receiver is Avm1Object receiverObject))
                    {
                        return null;
                    }

                    string instanceName = ArgumentString(arguments, 0);
                    Avm1Object initObject = arguments.Count > 2 ? arguments[2] as Avm1Object : null;

                    if (dynamicMovieClipsByObject.TryGetValue(receiverObject, out DynamicMovieClip source))
                    {
                        int depth = ArgumentInt(arguments, 1, GetNextDynamicDepth(source.ParentObject));
                        return CreateDynamicMovieClip(
                            source.ParentObject,
                            source.LinkageName,
                            source.CharacterId,
                            instanceName,
                            depth,
                            initObject,
                            source.CharacterId == 0
                        );
                    }

                    if (staticDisplayInstancesByObject.TryGetValue(receiverObject, out StaticDisplayInstance staticSource))
                    {
                        int depth = ArgumentInt(arguments, 1, GetNextDynamicDepth(staticSource.ParentObject));
                        return CreateDynamicMovieClip(
                            staticSource.ParentObject,
                            null,
                            staticSource.CharacterId,
                            instanceName,
                            depth,
                            initObject,
                            false
                        );
                    }

                    return null;
                }

                case "getnexthighestdepth":
                    return GetNextDynamicDepth(receiver);

                case "gettimer":
                    return Mathf.RoundToInt(Time.realtimeSinceStartup * 1000f);

                case "random":
                {
                    // AVM1 coerces the argument to a number rather than failing on
                    // it: random(undefined) is 0 in Flash, not an exception. A raw
                    // Convert.ToSingle threw InvalidCastException on undefined and
                    // object arguments, which a game calling random() in a loop
                    // turns into thousands of errors and a stalled script.
                    int maximum = Mathf.Max(0, ArgumentInt(arguments, 0, 0));
                    return maximum > 0 ? UnityEngine.Random.Range(0, maximum) : 0;
                }

                case "trace":
                    if (arguments.Count > 0)
                        Debug.Log("[AVM1] " + arguments[0]);
                    return null;

                case "stopsounds":
                    runtimeAudio?.StopAll();
                    return true;

                case "play":
                    SetDynamicClipPlaying(receiver, true);
                    return true;

                case "stop":
                    SetDynamicClipPlaying(receiver, false);
                    return true;

                case "nextframe":
                    StepDynamicClip(receiver, 1);
                    return true;

                case "prevframe":
                    StepDynamicClip(receiver, -1);
                    return true;

                case "setmask":
                    if (receiver is Avm1Object maskedObject)
                    {
                        Avm1Object assignedMask = arguments.Count > 0
                            ? arguments[0] as Avm1Object
                            : null;

                        if (dynamicMovieClipsByObject.TryGetValue(maskedObject, out DynamicMovieClip maskedClip))
                            maskedClip.MaskObject = assignedMask;

                        if (staticDisplayInstancesByObject.TryGetValue(maskedObject, out StaticDisplayInstance staticMaskedClip))
                            staticMaskedClip.MaskObject = assignedMask;
                    }
                    return true;

                case "removemovieclip":
                    RemoveDynamicMovieClip(receiver as Avm1Object);
                    return true;

                case "gotoandplay":
                    SetDynamicClipFrame(receiver, arguments, true);
                    return true;

                case "gotoandstop":
                    SetDynamicClipFrame(receiver, arguments, false);
                    return true;

                case "gotoframe":
                {
                    bool isPlaying = receiver == runtimeAvm1?.RootObject
                        ? autoPlayTimeline
                        : receiver is Avm1Object frameObject &&
                          dynamicMovieClipsByObject.TryGetValue(frameObject, out DynamicMovieClip frameClip) &&
                          frameClip.IsPlaying;
                    SetDynamicClipFrame(receiver, arguments, isPlaying);
                    return true;
                }

                case "swapdepths":
                    SwapDynamicClipDepth(receiver, arguments);
                    return true;

                case "gotolabel":
                {
                    bool isPlaying = receiver == runtimeAvm1?.RootObject
                        ? autoPlayTimeline
                        : receiver is Avm1Object labelObject &&
                          dynamicMovieClipsByObject.TryGetValue(labelObject, out DynamicMovieClip labelClip) &&
                          labelClip.IsPlaying;
                    SetDynamicClipFrame(receiver, arguments, isPlaying);
                    return true;
                }
                case "call":
                case "startdrag":
                case "stopdrag":
                    return true;

                default:
                    return null;
            }
        }

        private List<PlaceObject2Tag> BuildDisplayListForFrame(
            SwfParser parser,
            List<SwfFrame> frames,
            int frameIndex
        )
        {
            Dictionary<ushort, PlaceObject2Tag> activeByDepth =
                new Dictionary<ushort, PlaceObject2Tag>();

            if (frames == null || frames.Count == 0)
                return new List<PlaceObject2Tag>();

            int maxFrame = Mathf.Clamp(frameIndex, 0, frames.Count - 1);

            // Building a display list replays every control tag from frame 1 up to
            // this frame, re-parsing each one straight out of the SWF byte buffer.
            // That runs once per sprite per rendered frame, so on content with
            // thousands of sprites it dominates the frame time even though the
            // result only depends on (timeline, frame) and never changes. Cache it.
            if (!displayListCache.TryGetValue(frames, out Dictionary<int, List<PlaceObject2Tag>> framesCache))
            {
                framesCache = new Dictionary<int, List<PlaceObject2Tag>>();
                displayListCache[frames] = framesCache;
            }

            if (framesCache.TryGetValue(maxFrame, out List<PlaceObject2Tag> cached))
                return cached;

            for (int f = 0; f <= maxFrame; f++)
            {
                SwfFrame frame = frames[f];

                if (frame == null || frame.ControlTags == null)
                    continue;

                for (int t = 0; t < frame.ControlTags.Count; t++)
                {
                    SwfTag tag = frame.ControlTags[t];

                    if (tag == null)
                        continue;

                    if (tag.Code == 26 || tag.Code == 70) // PlaceObject2 / PlaceObject3
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
                                place.TimelineStartFrame = existing.TimelineStartFrame;

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

                                if (!place.HasRatio)
                                {
                                    place.Ratio = existing.Ratio;
                                    place.HasRatio = existing.HasRatio;
                                }

                                if (!place.HasName)
                                {
                                    place.Name = existing.Name;
                                    place.HasName = existing.HasName;
                                }

                                if (!place.HasClipDepth)
                                {
                                    place.ClipDepth = existing.ClipDepth;
                                    place.HasClipDepth = existing.HasClipDepth;
                                }

                                if (!place.HasClipActions)
                                {
                                    place.ClipActions = existing.ClipActions;
                                    place.HasClipActions = existing.HasClipActions;
                                }

                                if (!place.HasVisible)
                                {
                                    place.Visible = existing.Visible;
                                    place.HasVisible = existing.HasVisible;
                                }

                                if (!place.HasBlendMode)
                                {
                                    place.BlendMode = existing.BlendMode;
                                    place.HasBlendMode = existing.HasBlendMode;
                                }

                                if (!place.HasClassName)
                                {
                                    place.ClassName = existing.ClassName;
                                    place.HasClassName = existing.HasClassName;
                                }
                            }
                            else
                            {
                                place.TimelineStartFrame = f;
                            }

                            // IMPORTANT:
                            // Even if this is a new/replaced character, Flash may omit Matrix.
                            // In that case, keep previous depth matrix instead of using default/identity.
                            // if (!place.HasMatrix)
                            // {
                            //     place.Matrix = existing.Matrix;
                            //     place.HasMatrix = existing.HasMatrix;
                            // }

                            // Do NOT inherit ColorTransform for a new character.
                            // Only move/update tags inherit it above.
                        }
                        else
                        {
                            place.TimelineStartFrame = f;
                        }

                        activeByDepth[place.Depth] = place;
                    }
                    else if (tag.Code == 5) // RemoveObject
                    {
                        ushort depth = parser.ParseRemoveObjectDepthFromTag(tag);

                        if (activeByDepth.ContainsKey(depth))
                            activeByDepth.Remove(depth);
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

            framesCache[maxFrame] = result;
            return result;
        }

        private class ActiveButtonInstance
        {
            public DefineButton2Tag Button;
            public SwfMatrix WorldMatrix;
            public string Path;
            public Avm1Object ScriptObject;
        }

        private sealed class StaticDisplayInstance
        {
            public Avm1Object ScriptObject;
            public Avm1Object ParentObject;
            public Avm1Object MaskObject;
            public ushort CharacterId;
            public string InstanceName;
            public string Path;
            public int Depth;
            public int CurrentFrame;
            public bool IsPlaying;
            public List<SwfClipActionRecord> ClipActions;
        }

        private sealed class DynamicMovieClip
        {
            public Avm1Object ScriptObject;
            public Avm1Object ParentObject;
            public Avm1Object MaskObject;
            public ushort CharacterId;
            public string LinkageName;
            public string InstanceName;
            public int Depth;
            public int CurrentFrame;
            public bool IsPlaying;
            public bool Removed;
        }
    }
}
