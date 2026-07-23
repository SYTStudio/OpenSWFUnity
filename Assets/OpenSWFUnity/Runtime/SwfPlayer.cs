using UnityEngine;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Renderer;
using OpenSWFUnity.Runtime.Tags;
using OpenSWFUnity.Runtime.Audio;
using OpenSWFUnity.Runtime.AVM1;
using OpenSWFUnity.Runtime.AVM2;
using OpenSWFUnity.Runtime.Script;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using Unity.Profiling;

namespace OpenSWFUnity.Runtime
{
    public partial class SwfPlayer : MonoBehaviour
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
        // Kept as the inspector-facing name so existing scenes keep their value.
        // The values line up with SwfQualityLevel, which is what the renderer,
        // parser and texture cache actually read.
        public enum RenderQuality
        {
            Low = 0,
            Medium = 1,
            High = 2,
            Ultra = 3
        }

        [Header("Render Options")]

        [Tooltip("Quality level for software raster fills. Higher quality may be slower to render. This is not a full SWF fill renderer yet.")]
        public RenderQuality renderQuality = RenderQuality.High;

        [Tooltip("Applies the SWF background color to the main camera if available.")]
        public bool applyBackgroundColor = true;

        [Tooltip("Experimental per-fill retained renderer. Keep disabled for large SWFs; the batched GPU renderer uses far fewer GameObjects and draw calls.")]
        public bool useGpuRetainedRendering = false;

        [Tooltip("Centers the SWF stage and fits it inside the active orthographic camera while preserving aspect ratio.")]
        public bool autoFitCamera = true;

        [Tooltip("Pillarboxes/letterboxes to the SWF stage aspect so artwork outside the Flash stage cannot leak into the side bars.")]
        public bool constrainCameraToStageAspect = true;

        [Range(0f, 0.2f)]
        public float cameraFitPadding = 0.02f;

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

        [Tooltip("Writes a small capped trace of play/stop/goto and timeline wraps. Useful for diagnosing scene loops without enabling the very verbose renderer log.")]
        public bool enableTimelineControlDiagnostics = false;

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

        [Header("ActionScript 3 (AVM2)")]
        [Tooltip("Executes ActionScript 3 bytecode through the AVM2 runtime when supported.")]
        public bool enableAvm2 = true;

        private SwfParser runtimeParser;
        private SwfDebugRenderer runtimeDebugRenderer;
        private SwfRasterFillRenderer runtimeRasterRenderer;
        private SwfTextRenderer runtimeTextRenderer;
        private float timelineTimer;
        private int currentTimelineFrame;
        // A goto/next-frame operation enters its destination immediately. The frame's
        // actions must run before a playing timeline advances again; otherwise a
        // gotoAndPlay(2) skips frame 2's stop() and falls through every later scene.
        private bool rootFrameEnteredPending;
        private long timelineTickSerial;
        private bool renderStateDirty = true;
        private float swfFrameRate = 30f;
        // Rebuilt every rendered frame. Entries are reused in place and `activeButtonCount`
        // marks how many are live, so a stage full of buttons allocates nothing per frame.
        private readonly List<ActiveButtonInstance> activeButtons = new List<ActiveButtonInstance>();
        private int activeButtonCount;
        private string hoveredButtonPath;
        private string pressedButtonPath;
        private bool pressedButtonIsOutDown;
        private SwfAudioRuntime runtimeAudio;
        private float lastFittedCameraAspect = -1f;
        private Avm1Runtime runtimeAvm1;
        private Avm2Runtime runtimeAvm2;
        private ISwfScriptRuntime currentScriptRuntime;
        private readonly Dictionary<string, int> avm1LastExecutedFrames =
            new Dictionary<string, int>();
        private readonly List<DynamicMovieClip> dynamicMovieClips =
            new List<DynamicMovieClip>();
        private readonly Dictionary<Avm1Object, DynamicMovieClip> dynamicMovieClipsByObject =
            new Dictionary<Avm1Object, DynamicMovieClip>();
        private readonly Dictionary<Avm1Object, DynamicBitmapData> dynamicBitmapData =
            new Dictionary<Avm1Object, DynamicBitmapData>();
        private readonly Dictionary<Avm1Object, List<AttachedBitmap>> attachedBitmaps =
            new Dictionary<Avm1Object, List<AttachedBitmap>>();
        private long nextDynamicMovieClipSerial;
        // Display lists are pure functions of (timeline, frame), so they are built
        // once and reused instead of replaying control tags every rendered frame.
        private readonly Dictionary<List<SwfFrame>, Dictionary<int, List<PlaceObject2Tag>>> displayListCache =
            new Dictionary<List<SwfFrame>, Dictionary<int, List<PlaceObject2Tag>>>();

        private readonly Dictionary<string, StaticDisplayInstance> staticDisplayInstances =
            new Dictionary<string, StaticDisplayInstance>();
        private readonly Dictionary<Avm1Object, StaticDisplayInstance> staticDisplayInstancesByObject =
            new Dictionary<Avm1Object, StaticDisplayInstance>();
        private long nextStaticDisplayInstanceSerial;
        // Verbose render diagnostics must never write once per sprite per frame.
        // Large games contain thousands of nested sprites; that log flood alone can
        // stall the Editor and make a healthy timeline look frozen.
        private readonly HashSet<ushort> verboseLoggedSpriteIds = new HashSet<ushort>();
        private readonly HashSet<ushort> verboseLoggedUnknownCharacterIds = new HashSet<ushort>();
        private int nextMaskStencilReference = 1;
        private int timelineControlDiagnosticCount;
        private const int MaxTimelineControlDiagnostics = 600;
        private const int TimelineDiagnosticMinFrames = 32;

        // Extracting a control tag's action bytes copies them out of the SWF buffer.
        // The timeline re-enters the same frames on every loop, so the copies are
        // memoised by tag reference (each tag is always read with the same skip) to
        // stop allocating a fresh byte[] every time a frame's script runs.
        private readonly Dictionary<SwfTag, byte[]> actionByteCache =
            new Dictionary<SwfTag, byte[]>();

        // Reused per rendered frame by the top-level traversal instead of
        // allocating a new stack each time. Not shared with the recursive sprite
        // traversal, which needs its own per-call stack.
        private readonly Stack<int> rootTimelineMaskDepths = new Stack<int>();
        private readonly List<int> spriteMaskDepthStack = new List<int>();
        private readonly List<StaticDisplayInstance> enterFrameBuffer =
            new List<StaticDisplayInstance>();
        private readonly List<StaticDisplayInstance> staticRemovalBuffer =
            new List<StaticDisplayInstance>();

        // Reused snapshot buffer for clip key events, so a held key does not
        // allocate a fresh list every frame it fires.
        private readonly List<StaticDisplayInstance> keyEventInstanceBuffer =
            new List<StaticDisplayInstance>();

        // Event callbacks with no arguments share one empty array rather than
        // allocating object[0] on every dispatch.
        private static readonly object[] NoArgs = System.Array.Empty<object>();

        // The timeline may need to advance several logical frames in one Update
        // when a hitch makes deltaTime exceed the frame duration. Capping the
        // catch-up keeps a long stall from turning into a spiral of death while
        // still preventing the movie from running in slow motion.
        private const int MaxTimelineCatchUpSteps = 4;

        // Flash's default "a script is running slowly" timeout. ScriptLimits is
        // expressed relative to this, so it is the baseline used to scale the AVM1
        // instruction budget upward for movies that request more headroom.
        private const int DefaultFlashScriptTimeoutSeconds = 15;

        private static readonly ProfilerMarker updateMarker =
            new ProfilerMarker("SwfPlayer.TimelineUpdate");
        private static readonly ProfilerMarker parseMarker =
            new ProfilerMarker("SwfPlayer.ParseTags");
        private static readonly ProfilerMarker timelineActionsMarker =
            new ProfilerMarker("SwfPlayer.ExecuteTimelineActions");
        private static readonly ProfilerMarker renderMarker =
            new ProfilerMarker("SwfPlayer.RenderCurrentFrame");

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
            rootFrameEnteredPending = false;
            timelineTickSerial++;
            runtimeAudio?.StopAll();
            debugFrame = 0;
            debugTimelineFrame = 0;

            if (runtimeParser != null)
            {
                InitializeScriptRuntime(runtimeParser);
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

            SyncQualityIfChanged();

            float displayAspect = Screen.height > 0
                ? (float)Screen.width / Screen.height
                : (Camera.main != null ? Camera.main.aspect : 1f);

            if (autoFitCamera && Camera.main != null &&
                !Mathf.Approximately(lastFittedCameraAspect, displayAspect))
            {
                FitCameraToStage();
            }

            if (inputEnabled)
            {
                HandlePointerInput();
                HandleKeyboardInput();
            }

            if (!enableTimelinePlayback)
                return;

            using (updateMarker.Auto())
            {
                timelineTimer += Time.deltaTime;

                float frameDuration = 1f / Mathf.Max(1f, swfFrameRate);
                int steps = 0;

                // Advance the timeline logic for each elapsed frame, but render only
                // once at the end: catching up five frames is five timeline steps and
                // one draw, exactly like Flash, not five redundant redraws.
                while (timelineTimer >= frameDuration && steps < MaxTimelineCatchUpSteps)
                {
                    timelineTimer -= frameDuration;
                    steps++;
                    timelineTickSerial++;

                    if (rootFrameEnteredPending)
                    {
                        // Execute the frame selected by goto/nextFrame on this step.
                        // It may contain stop(), another goto, sound control, or setup
                        // that must happen before the playhead is allowed to move on.
                        rootFrameEnteredPending = false;
                    }
                    else if (autoPlayTimeline)
                    {
                        currentTimelineFrame++;
                        renderStateDirty = true;
                    }

                    AdvanceStaticMovieClips();
                    AdvanceDynamicMovieClips();
                    ExecuteCurrentTimelineActions();
                    AdvanceAvm2Frame();

                    // AS3 can mutate display properties and BitmapData directly
                    // during enterFrame. Those writes do not pass through the
                    // static timeline's dirty flags, so an AVM2 movie must present
                    // once after each script tick even when its root playhead is
                    // stopped (software-rendered games rely on exactly this).
                    if (runtimeAvm2 != null)
                        renderStateDirty = true;
                }

                if (steps >= MaxTimelineCatchUpSteps)
                {
                    // A stall longer than the catch-up cap is dropped rather than
                    // repaid, so a momentary freeze cannot snowball into a spiral of
                    // death where each Update owes ever more frames than it can run.
                    timelineTimer = 0f;
                }

                if (steps > 0 && renderStateDirty)
                    RenderCurrentFrame();

                // Streaming audio is positioned from the playhead rather than left to
                // free-run, so a stop, a seek or a dropped catch-up frame cannot let
                // it drift away from the picture.
                runtimeAudio?.SyncStreamToFrame(currentTimelineFrame, autoPlayTimeline);
            }
        }

        private void Start()
        {
            SwfRenderDiagnostics.Reset();
            SwfRenderDiagnostics.Enabled = verboseLogging;

            if (swfFile == null)
            {
                Debug.LogError("No SWF file assigned.");
                return;
            }

            try
            {
                // Before parsing: curve subdivision is baked into the shape records.
                ApplyQualityBeforeLoad();

                SwfParser parser = new SwfParser(swfFile.bytes);
                parser.VerboseLogging = verboseLogging;

                SwfHeader header = parser.ParseHeader();
                if (verboseLogging)
                    Debug.Log(header.ToString());

                using (parseMarker.Auto())
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

                // One MeshRenderer per fill is useful for small animations but is a
                // catastrophic object/draw-call explosion for games such as Isaac.
                // Keep the Inspector switch as an experimental small-movie option,
                // with a hard guard so a saved checkbox cannot crash a build.
                bool retainedRenderingAllowed =
                    useGpuRetainedRendering && parser.Shapes.Count <= 1024;
                runtimeRasterRenderer.UseRetainedGpuRendering = retainedRenderingAllowed;

                // Lets bitmap fills resolve their character id to a decoded
                // texture; textures are built on first use, not at parse time.
                runtimeRasterRenderer.BitmapProvider = bitmapId =>
                    parser.FindBitmapById(bitmapId)?.GetTexture();
                runtimeRasterRenderer.BitmapSizeProvider = bitmapId =>
                {
                    DefineBitmapTag bitmap = parser.FindBitmapById(bitmapId);
                    return bitmap != null
                        ? new Vector2Int(bitmap.Width, bitmap.Height)
                        : Vector2Int.zero;
                };
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

                InitializeScriptRuntime(parser);
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

                FitCameraToStage();

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

            using var _profilerScope = renderMarker.Auto();

            runtimeRasterRenderer?.BeginFrame();
            runtimeTextRenderer?.BeginFrame();
            nextMaskStencilReference = 1;

            try
            {
                ClearDebugLines();
                activeButtonCount = 0;

                debugTimelineFrame = currentTimelineFrame;

                RenderTopLevelDebug(runtimeParser);
            }
            finally
            {
                runtimeRasterRenderer?.EndFrame();
                runtimeTextRenderer?.EndFrame();
                renderStateDirty = false;
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

        private void FitCameraToStage()
        {
            if (!autoFitCamera || runtimeParser?.Header == null)
                return;

            Camera camera = Camera.main;

            if (camera == null || !camera.orthographic)
                return;

            const float pixelsPerUnit = 50f;
            float displayAspect = Screen.height > 0
                ? Mathf.Max(0.01f, (float)Screen.width / Screen.height)
                : Mathf.Max(0.01f, camera.aspect);
            float stageAspect = Mathf.Max(
                0.01f,
                runtimeParser.Header.StageWidth / runtimeParser.Header.StageHeight);

            if (constrainCameraToStageAspect)
            {
                Rect viewport = new Rect(0f, 0f, 1f, 1f);

                if (displayAspect > stageAspect)
                {
                    viewport.width = stageAspect / displayAspect;
                    viewport.x = (1f - viewport.width) * 0.5f;
                }
                else if (displayAspect < stageAspect)
                {
                    viewport.height = displayAspect / stageAspect;
                    viewport.y = (1f - viewport.height) * 0.5f;
                }

                camera.rect = viewport;
            }
            else
            {
                camera.rect = new Rect(0f, 0f, 1f, 1f);
            }

            float aspect = constrainCameraToStageAspect
                ? stageAspect
                : displayAspect;
            float scaleX = Mathf.Abs(transform.lossyScale.x);
            float scaleY = Mathf.Abs(transform.lossyScale.y);
            float halfHeight = runtimeParser.Header.StageHeight * scaleY /
                (pixelsPerUnit * 2f);
            float halfWidthForAspect = runtimeParser.Header.StageWidth * scaleX /
                (pixelsPerUnit * 2f * aspect);
            camera.orthographicSize = Mathf.Max(halfHeight, halfWidthForAspect) *
                (1f + Mathf.Max(0f, cameraFitPadding));

            Vector3 cameraPosition = camera.transform.position;
            cameraPosition.x = transform.position.x;
            cameraPosition.y = transform.position.y;
            camera.transform.position = cameraPosition;
            lastFittedCameraAspect = displayAspect;
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
            rootTimelineMaskDepths.Clear();
            SwfMatrix rootMatrix = runtimeAvm1 != null
                ? BuildDynamicMovieClipMatrix(runtimeAvm1.RootObject)
                : SwfMatrix.Identity;
            float rootAlpha = runtimeAvm1 != null
                ? Mathf.Clamp01(Avm1Float(runtimeAvm1.RootObject.Get("_alpha"), 100f) / 100f)
                : 1f;

            if (runtimeAvm1 != null &&
                !Avm1Boolean(runtimeAvm1.RootObject.Get("_visible"), true))
            {
                return;
            }

            foreach (PlaceObject2Tag placed in places)
            {
                while (rootTimelineMaskDepths.Count > 0 && placed.Depth > rootTimelineMaskDepths.Peek())
                {
                    runtimeRasterRenderer?.EndStencil();
                    rootTimelineMaskDepths.Pop();
                }

                if (!placed.HasCharacter || placed.CharacterId == 0)
                    continue;

                float alpha = rootAlpha;

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
                StaticDisplayInstance instance = runtimeAvm1 != null &&
                    RequiresScriptInstance(parser, placed.CharacterId)
                    ? GetOrCreateStaticDisplayInstance(
                        timelinePath,
                        placed,
                        runtimeAvm1.RootObject
                    )
                    : null;

                // A MovieClip assigned through setMask participates only in the
                // stencil pass. Drawing a timeline/static mask here as ordinary art
                // made it appear twice and cover the clipped object.
                if (instance != null && IsObjectUsedAsMask(instance.ScriptObject))
                    continue;

                SwfMatrix effectiveMatrix = instance != null
                    ? BuildDynamicMovieClipMatrix(instance.ScriptObject)
                    : placed.Matrix;
                effectiveMatrix = SwfMatrix.Combine(rootMatrix, effectiveMatrix);

                // Timeline-placed clips honour _visible exactly like dynamically
                // created ones. Content routinely parks a pile of clips on frame 1
                // and hides them from script; ignoring _visible here drew every one
                // of them on top of the real scene.
                if (instance != null &&
                    !Avm1Boolean(instance.ScriptObject.Get("_visible"), true))
                {
                    continue;
                }

                // _alpha applies to timeline-placed clips exactly as it does to attached
                // ones; without this a script fading a clip in or out had no effect.
                if (instance != null)
                {
                    alpha *= Mathf.Clamp01(
                        Avm1Float(instance.ScriptObject.Get("_alpha"), 100f) / 100f
                    );

                    if (alpha <= 0.001f)
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
                    rootTimelineMaskDepths.Push(placed.ClipDepth);
                }
            }

            while (rootTimelineMaskDepths.Count > 0)
            {
                runtimeRasterRenderer?.EndStencil();
                rootTimelineMaskDepths.Pop();
            }

            RenderDynamicMovieClips(parser, renderer, null, rootMatrix, rootAlpha, 0);
            RenderAvm2DisplayTree(parser, renderer);
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

                try
                {
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
                }
                finally
                {
                    runtimeRasterRenderer?.EndStencil();
                }

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
                if (verboseLogging && verboseLoggedSpriteIds.Add(sprite.SpriteId))
                {
                    Debug.Log("First render of Sprite " + sprite.SpriteId + " at " + path);
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

            DefineEditTextTag editText = parser.FindEditTextById(characterId);

            if (editText != null)
            {
                if (enableTextMeshDebug)
                {
                    runtimeTextRenderer?.DrawEditText(
                        editText,
                        ResolveEditTextValue(editText, scriptObject),
                        worldMatrix,
                        "EditText_" + characterId + "_" + path,
                        textMeshCharacterSize,
                        alpha
                    );
                }

                return;
            }

            DefineButton2Tag button = parser.FindButton2ById(characterId);

            if (button != null)
            {
                ActiveButtonInstance activeButton = AcquireActiveButton();
                activeButton.Button = button;
                activeButton.WorldMatrix = worldMatrix;
                activeButton.Path = path;

                // Button actions run on the timeline that contains the button, which
                // is why the parent - not the button's own object - is the receiver.
                activeButton.ScriptObject = scriptObject != null
                    ? scriptObject.Get("_parent") as Avm1Object
                    : runtimeAvm1?.RootObject;

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

            if (verboseLogging && verboseLoggedUnknownCharacterIds.Add(characterId))
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
            int localFrame = 0;

            if (sprite.Frames != null && sprite.Frames.Count > 0)
            {
                if (loopSpriteTimelines)
                    localFrame = spriteFrame % sprite.Frames.Count;
                else
                    localFrame = Mathf.Clamp(spriteFrame, 0, sprite.Frames.Count - 1);
            }

            List<PlaceObject2Tag> places;

            if (enableSpriteTimelineDebug && sprite.Frames != null && sprite.Frames.Count > 0)
            {
                // Owned by the display-list cache and shared with every other sprite
                // render this frame, so it must be treated as read-only here.
                places = BuildDisplayListForFrame(parser, sprite.Frames, localFrame);
            }
            else
            {
                places = new List<PlaceObject2Tag>();

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

            // Nested sprites share one stack: each call remembers where its own
            // entries begin and unwinds back to that mark, so recursion costs nothing.
            int maskStackBase = spriteMaskDepthStack.Count;

            for (int i = 0; i < places.Count; i++)
            {
                PlaceObject2Tag innerPlace = places[i];

                if (innerPlace == null)
                    continue;

                while (spriteMaskDepthStack.Count > maskStackBase &&
                       innerPlace.Depth > spriteMaskDepthStack[spriteMaskDepthStack.Count - 1])
                {
                    runtimeRasterRenderer?.EndStencil();
                    spriteMaskDepthStack.RemoveAt(spriteMaskDepthStack.Count - 1);
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
                StaticDisplayInstance childInstance = runtimeAvm1 != null &&
                    RequiresScriptInstance(parser, innerPlace.CharacterId)
                    ? GetOrCreateStaticDisplayInstance(
                        childTimelinePath,
                        innerPlace,
                        spriteObject ?? runtimeAvm1.RootObject
                    )
                    : null;

                if (childInstance != null && IsObjectUsedAsMask(childInstance.ScriptObject))
                    continue;

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

                if (childInstance != null)
                {
                    childAlpha *= Mathf.Clamp01(
                        Avm1Float(childInstance.ScriptObject.Get("_alpha"), 100f) / 100f
                    );

                    if (childAlpha <= 0.001f)
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
                    spriteMaskDepthStack.Add(innerPlace.ClipDepth);
                }
            }

            while (spriteMaskDepthStack.Count > maskStackBase)
            {
                runtimeRasterRenderer?.EndStencil();
                spriteMaskDepthStack.RemoveAt(spriteMaskDepthStack.Count - 1);
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

            StaticDisplayInstance staticClip = null;

            if (dynamicMovieClipsByObject.TryGetValue(scriptObject, out DynamicMovieClip dynamicClip))
                maskObject = dynamicClip.MaskObject;
            else if (staticDisplayInstancesByObject.TryGetValue(scriptObject, out staticClip))
                maskObject = staticClip.MaskObject;

            if (maskObject == null)
                return false;

            bool usable =
                dynamicMovieClipsByObject.TryGetValue(maskObject, out DynamicMovieClip dynamicMask)
                    ? !dynamicMask.Removed && dynamicMask.CharacterId != 0
                    : staticDisplayInstancesByObject.TryGetValue(maskObject, out StaticDisplayInstance staticMask) &&
                      staticMask.CharacterId != 0;

            if (usable)
                return true;

            // A removed/replaced mask must stop clipping immediately. Keeping the
            // stale object reference made RenderMaskObject write no stencil and hid
            // the complete target on every later frame.
            if (dynamicMovieClipsByObject.TryGetValue(scriptObject, out dynamicClip))
                dynamicClip.MaskObject = null;
            else if (staticDisplayInstancesByObject.TryGetValue(scriptObject, out staticClip))
                staticClip.MaskObject = null;

            maskObject = null;
            return false;
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
            DispatchAvm2PointerEvents(flashPoint);

            if (Mouse.current.delta.ReadValue().sqrMagnitude > 0.0001f)
                runtimeAvm1?.Broadcast("Mouse", "onMouseMove");

            if (Mouse.current.leftButton.wasPressedThisFrame)
                runtimeAvm1?.Broadcast("Mouse", "onMouseDown");
            else if (Mouse.current.leftButton.wasReleasedThisFrame)
                runtimeAvm1?.Broadcast("Mouse", "onMouseUp");

            if (activeButtonCount == 0)
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
            if (Keyboard.current == null)
                return;

            if (runtimeAvm1 == null)
            {
                DispatchAvm2KeyboardInput();
                return;
            }

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
                DispatchAvm2KeyEvent(pressed, flashCode, asciiCode);
                runtimeAvm1.Broadcast("Key", pressed ? "onKeyDown" : "onKeyUp");

                // Snapshot into a reused buffer because a triggered handler may add
                // or remove display instances while we iterate.
                keyEventInstanceBuffer.Clear();
                keyEventInstanceBuffer.AddRange(staticDisplayInstances.Values);

                for (int instanceIndex = 0; instanceIndex < keyEventInstanceBuffer.Count; instanceIndex++)
                {
                    TriggerClipActions(
                        keyEventInstanceBuffer[instanceIndex],
                        pressed ? 0x00000002u : 0x00000001u
                    );

                    if (pressed)
                        TriggerClipActions(keyEventInstanceBuffer[instanceIndex], 0x00020000u, (byte)flashCode);
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
            SwfMatrix rootWorld = BuildDynamicMovieClipMatrix(runtimeAvm1.RootObject);
            worldMatrices[runtimeAvm1.RootObject] = rootWorld;
            SetLocalMousePosition(runtimeAvm1.RootObject, rootWorld, stagePoint);

            foreach (StaticDisplayInstance instance in staticDisplayInstances.Values)
            {
                if (instance?.ScriptObject == null)
                    continue;

                if (instance.LastSeenTimelineTick < timelineTickSerial - 1)
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

            SwfMatrix parentWorld = parent == runtimeAvm1.RootObject &&
                cache.TryGetValue(runtimeAvm1.RootObject, out SwfMatrix cachedRoot)
                    ? cachedRoot
                    : SwfMatrix.Identity;

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

        private ActiveButtonInstance AcquireActiveButton()
        {
            if (activeButtonCount < activeButtons.Count)
                return activeButtons[activeButtonCount++];

            ActiveButtonInstance created = new ActiveButtonInstance();
            activeButtons.Add(created);
            activeButtonCount++;
            return created;
        }

        private ActiveButtonInstance FindButtonByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            for (int i = 0; i < activeButtonCount; i++)
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

                ReportTimelineControl(
                    "button id=" + button.ButtonId +
                    " transition=0x" + transitionFlag.ToString("X4") +
                    " path=" + instance.Path
                );

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
            for (int i = activeButtonCount - 1; i >= 0; i--)
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

        private void InitializeScriptRuntime(SwfParser parser)
        {
            foreach (DynamicBitmapData bitmap in dynamicBitmapData.Values)
            {
                if (bitmap?.Texture != null)
                    Destroy(bitmap.Texture);
            }

            dynamicBitmapData.Clear();
            attachedBitmaps.Clear();
            runtimeAvm1 = null;
            runtimeAvm2 = null;
            currentScriptRuntime = null;
            rootFrameEnteredPending = false;
            timelineControlDiagnosticCount = 0;
            avm1LastExecutedFrames.Clear();
            dynamicMovieClips.Clear();
            dynamicMovieClipsByObject.Clear();
            nextDynamicMovieClipSerial = 0;
            nextStaticDisplayInstanceSerial = 0;
            staticDisplayInstances.Clear();
            staticDisplayInstancesByObject.Clear();
            displayListCache.Clear();
            actionByteCache.Clear();

            if (parser == null)
                return;

            if (parser.DoAbcBlocks != null && parser.DoAbcBlocks.Count > 0)
            {
                if (enableAvm2)
                {
                    InitializeAvm2(parser);
                    return;
                }

                Debug.LogWarning(
                    "SWF contains DoABC blocks, but AVM2 execution is disabled. " +
                    "ActionScript 3 content will not execute."
                );
            }

            InitializeAvm1(parser);
        }

        private void InitializeAvm2(SwfParser parser)
        {
            runtimeAvm2 = new Avm2Runtime()
            {
                VerboseLogging = verboseLogging,
                ExternalMethod = HandleAvm2ExternalMethod,
                ExternalFunction = HandleAvm2ExternalFunction,
                Trace = message => Debug.Log("[AVM2] " + message),
                Warning = message => Debug.LogWarning("[AVM2] " + message)
            };

            currentScriptRuntime = runtimeAvm2;

            for (int i = 0; i < parser.DoAbcBlocks.Count; i++)
            {
                SwfDoAbcBlock abcBlock = parser.DoAbcBlocks[i];
                runtimeAvm2.RegisterFunctions(abcBlock.AbcData, abcBlock.Name);

                if (verboseLogging)
                {
                    Debug.Log(
                        "Registered DoABC block: " +
                        (string.IsNullOrEmpty(abcBlock.Name) ? "<anonymous>" : abcBlock.Name) +
                        " Size=" + abcBlock.AbcData.Length
                    );
                }
            }

            if (verboseLogging)
            {
                Debug.Log(runtimeAvm2.DescribeDiagnostics());

                // Names the instructions this movie actually needs, which is the input
                // for deciding what an AS3 interpreter would have to cover.
                Debug.Log(runtimeAvm2.DescribeOpCodeUsage());
            }

            InitializeAvm2Display(parser);

            if (parser.DoAbcBlocks.Count > 0)
            {
                Debug.Log(
                    "This SWF is ActionScript 3 (" + parser.DoAbcBlocks.Count + " DoABC block(s)). " +
                    runtimeAvm2.DescribeDiagnostics()
                );

                if (runtimeAvm2.FailedScriptCount > 0)
                {
                    Debug.LogWarning(
                        runtimeAvm2.FailedScriptCount + " of " + parser.DoAbcBlocks.Count +
                        " ActionScript 3 block(s) stopped during initialisation. The display and " +
                        "event core (Sprite, MovieClip, Shape, TextField, EventDispatcher) is " +
                        "available; anything outside it is not, and is named in the warnings " +
                        "above. The timeline still renders regardless."
                    );
                }
            }
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

            characterBoundsCache.Clear();
            characterBoundsInProgress.Clear();

            runtimeAvm1 = new Avm1Runtime(parser.Header.Version >= 7)
            {
                VerboseLogging = verboseLogging,
                ExternalMethod = HandleAvm1ExternalMethod,
                ComputedPropertyGetter = GetDisplayObjectComputedProperty,
                ComputedPropertySetter = SetDisplayObjectComputedProperty,
                MemberChanged = HandleAvm1MemberChanged,
                Trace = message => Debug.Log("[AVM1] " + message)
            };

            currentScriptRuntime = runtimeAvm1;

            // A movie that ships a ScriptLimits tag is explicitly asking for more
            // script headroom than the default. Honour it by scaling the AVM1
            // instruction budget up in proportion to the requested timeout, clamped
            // so it can only ever rise above the default safety cap - never fall
            // below it - so existing content cannot start tripping the guard sooner.
            if (parser.HasScriptLimits &&
                parser.ScriptTimeoutSeconds > DefaultFlashScriptTimeoutSeconds)
            {
                long scaledBudget =
                    (long)Avm1Runtime.DefaultInstructionBudget *
                    parser.ScriptTimeoutSeconds /
                    DefaultFlashScriptTimeoutSeconds;

                runtimeAvm1.InstructionBudget = (int)System.Math.Min(
                    int.MaxValue,
                    System.Math.Max(Avm1Runtime.DefaultInstructionBudget, scaledBudget)
                );

                if (verboseLogging)
                {
                    Debug.Log(
                        "Raised AVM1 instruction budget to " + runtimeAvm1.InstructionBudget +
                        " from ScriptLimits (timeout=" + parser.ScriptTimeoutSeconds + "s)"
                    );
                }
            }

            // Same one-way rule for recursion: the movie may ask for a deeper call
            // stack than the default guard allows, but never a shallower one, since
            // the guard is what stops runaway recursion from overflowing the CLR stack.
            if (parser.HasScriptLimits &&
                parser.ScriptMaxRecursionDepth > Avm1Runtime.DefaultMaxCallDepth)
            {
                runtimeAvm1.MaxCallDepth = parser.ScriptMaxRecursionDepth;

                if (verboseLogging)
                {
                    Debug.Log(
                        "Raised AVM1 call depth limit to " + runtimeAvm1.MaxCallDepth +
                        " from ScriptLimits"
                    );
                }
            }

            runtimeAvm1.SetVariable("MOVIE_WIDTH", parser.Header.StageWidth);
            runtimeAvm1.SetVariable("MOVIE_HEIGHT", parser.Header.StageHeight);
            runtimeAvm1.RootObject.Set("_width", parser.Header.StageWidth);
            runtimeAvm1.RootObject.Set("_height", parser.Header.StageHeight);
            runtimeAvm1.RootObject.Set("_totalframes", parser.Header.FrameCount);
            // The complete local asset is resident before frame 1 executes. Classic
            // AS1/AS2 preloaders commonly stop on their splash frame until
            // _framesloaded == _totalframes (or getBytesLoaded == getBytesTotal).
            // Leaving these properties undefined made every such movie wait forever.
            runtimeAvm1.RootObject.Set("_framesloaded", parser.Header.FrameCount);
            double loadedByteCount = swfFile?.bytes != null ? swfFile.bytes.LongLength : 0d;
            runtimeAvm1.RootObject.Set("_bytesloaded", loadedByteCount);
            runtimeAvm1.RootObject.Set("_bytestotal", loadedByteCount);
            runtimeAvm1.RootObject.Set("_x", 0d);
            runtimeAvm1.RootObject.Set("_y", 0d);
            runtimeAvm1.RootObject.Set("_xscale", 100d);
            runtimeAvm1.RootObject.Set("_yscale", 100d);
            runtimeAvm1.RootObject.Set("_rotation", 0d);
            runtimeAvm1.RootObject.Set("_alpha", 100d);
            runtimeAvm1.RootObject.Set("_visible", true);

            if (runtimeAvm1.GetVariable("Stage") is Avm1Object stageObject)
            {
                stageObject.Set("width", parser.Header.StageWidth);
                stageObject.Set("height", parser.Header.StageHeight);
            }

            ISwfScriptRuntime scriptRuntime = runtimeAvm1;

            for (int i = 0; i < parser.Tags.Count; i++)
            {
                SwfTag tag = parser.Tags[i];

                if (tag.Code == 12)
                    scriptRuntime.RegisterFunctions(parser.CopyTagData(tag));
                else if (tag.Code == 59)
                    scriptRuntime.RegisterFunctions(parser.CopyTagData(tag, 2));
            }

            if (verboseLogging)
                Debug.Log("AVM1 initialized. " + runtimeAvm1.DescribeDiagnostics());
        }

        private void ExecuteInitActionsForCharacter(ushort characterId, Avm1Object targetObject)
        {
            if (runtimeParser == null || runtimeAvm1 == null || targetObject == null)
                return;

            for (int i = 0; i < runtimeParser.InitActions.Count; i++)
            {
                SwfInitAction initAction = runtimeParser.InitActions[i];

                if (initAction == null || initAction.SpriteId != characterId ||
                    initAction.ActionBytes == null || initAction.ActionBytes.Length == 0)
                {
                    continue;
                }

                runtimeAvm1.Execute(initAction.ActionBytes, targetObject);
            }
        }

        private void ExecuteCurrentTimelineActions()
        {
            if (runtimeAvm1 == null || runtimeParser == null)
                return;

            using var _profilerScope = timelineActionsMarker.Auto();
            runtimeAudio?.BeginSpriteStreamFrame();

            if (runtimeParser.RootFrames != null && runtimeParser.RootFrames.Count > 0)
            {
                int rootFrame = currentTimelineFrame % runtimeParser.RootFrames.Count;
                SwfFrame frame = runtimeParser.RootFrames[rootFrame];

                // Kept in step with the root timeline so `_root._currentframe` and the
                // bare `_currentframe` read from a root script report the live frame.
                // Flash frame numbers are 1-based.
                runtimeAvm1.RootObject.Set("_currentframe", rootFrame + 1);

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

                    if (!RequiresScriptInstance(runtimeParser, place.CharacterId))
                        continue;

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

            // attachMovie/createEmptyMovieClip instances are separate timeline roots.
            // Visit them before enterFrame dispatch so every live static descendant is
            // marked for this exact display-list tick. That gives the cleanup below a
            // reliable distinction between a stopped clip and a clip which no longer
            // exists on stage.
            ExecuteDynamicMovieClipSubtreeActions();

            runtimeAvm1.TryCallFunction("onEnterFrame", NoArgs, out _);

            // Dispatched from a snapshot: the handlers below run arbitrary script, and
            // anything that registers a new display instance mid-dispatch would
            // otherwise invalidate the dictionary enumerator. Taking a copy also gives
            // the Flash behaviour that a clip created during this frame first receives
            // enterFrame on the next one. The buffer is reused, so this does not
            // allocate once the scene has settled.
            enterFrameBuffer.Clear();

            foreach (StaticDisplayInstance instance in staticDisplayInstances.Values)
                enterFrameBuffer.Add(instance);

            for (int i = 0; i < enterFrameBuffer.Count; i++)
            {
                StaticDisplayInstance instance = enterFrameBuffer[i];

                if (instance == null ||
                    instance.LastSeenTimelineTick != timelineTickSerial ||
                    runtimeParser.FindSpriteById(instance.CharacterId) == null)
                {
                    continue;
                }

                TriggerClipActions(instance, 0x00000040u);
                runtimeAvm1.TryCallMethod(instance.ScriptObject, "onEnterFrame", NoArgs, out _);
            }

            enterFrameBuffer.Clear();

            int dynamicCount = dynamicMovieClips.Count;

            for (int i = 0; i < dynamicCount; i++)
            {
                DynamicMovieClip clip = dynamicMovieClips[i];

                if (!clip.Removed &&
                    IsDisplayParentActive(clip.ParentObject, timelineTickSerial, 0))
                {
                    runtimeAvm1.TryCallMethod(clip.ScriptObject, "onEnterFrame", NoArgs, out _);
                }
            }

            // enterFrame handlers can seek timelines or attach/remove clips. Traverse
            // once more so the display list being rendered this tick has already run
            // its entry actions, then unload everything no longer reachable.
            ExecuteDynamicMovieClipSubtreeActions();
            RemoveInactiveDisplayObjects();

            runtimeAudio?.EndSpriteStreamFrame();
        }

        private void ExecuteDynamicMovieClipSubtreeActions()
        {
            int dynamicCount = dynamicMovieClips.Count;

            for (int i = 0; i < dynamicCount; i++)
            {
                DynamicMovieClip clip = dynamicMovieClips[i];

                if (clip == null || clip.Removed || clip.CharacterId == 0 ||
                    !IsDisplayParentActive(clip.ParentObject, timelineTickSerial, 0))
                {
                    continue;
                }

                DefineSpriteTag sprite = runtimeParser.FindSpriteById(clip.CharacterId);

                if (sprite == null || sprite.Frames == null || sprite.Frames.Count == 0)
                    continue;

                int localFrame = Mathf.Clamp(clip.CurrentFrame, 0, sprite.Frames.Count - 1);
                runtimeAudio?.SyncSpriteStream(sprite, localFrame, clip.IsPlaying);
                List<PlaceObject2Tag> places = BuildDisplayListForFrame(
                    runtimeParser,
                    sprite.Frames,
                    localFrame
                );

                ExecuteVisibleSpriteActions(
                    places,
                    localFrame,
                    "_dynamic/" + clip.Serial,
                    clip.ScriptObject,
                    0
                );
            }
        }

        private void RemoveInactiveDisplayObjects()
        {
            // A dynamic clip attached below a timeline instance belongs to that
            // instance's display-list lifetime. It must not keep receiving
            // onEnterFrame or playing sounds after its parent disappears.
            for (int i = dynamicMovieClips.Count - 1; i >= 0; i--)
            {
                DynamicMovieClip clip = dynamicMovieClips[i];

                if (clip == null || clip.Removed ||
                    IsDisplayParentActive(clip.ParentObject, timelineTickSerial, 0))
                {
                    continue;
                }

                RemoveDynamicMovieClip(clip.ScriptObject);
            }

            staticRemovalBuffer.Clear();

            foreach (StaticDisplayInstance instance in staticDisplayInstances.Values)
            {
                if (instance != null &&
                    instance.LastSeenTimelineTick != timelineTickSerial)
                {
                    staticRemovalBuffer.Add(instance);
                }
            }

            // Children unload before their parents, matching the display-list tree
            // and ensuring their onUnload handlers can still inspect the parent.
            staticRemovalBuffer.Sort((left, right) =>
                (right.Path?.Length ?? 0).CompareTo(left.Path?.Length ?? 0));

            for (int i = 0; i < staticRemovalBuffer.Count; i++)
                RemoveStaticDisplayInstance(staticRemovalBuffer[i]);

            staticRemovalBuffer.Clear();
        }

        private bool IsDisplayParentActive(
            Avm1Object parentObject,
            long requiredTimelineTick,
            int recursionDepth
        )
        {
            if (parentObject == null || parentObject == runtimeAvm1?.RootObject)
                return true;

            if (recursionDepth > 128)
                return false;

            if (staticDisplayInstancesByObject.TryGetValue(
                parentObject,
                out StaticDisplayInstance staticParent
            ))
            {
                return staticParent.LastSeenTimelineTick == requiredTimelineTick &&
                    IsDisplayParentActive(
                        staticParent.ParentObject,
                        requiredTimelineTick,
                        recursionDepth + 1
                    );
            }

            if (dynamicMovieClipsByObject.TryGetValue(
                parentObject,
                out DynamicMovieClip dynamicParent
            ))
            {
                return !dynamicParent.Removed &&
                    IsDisplayParentActive(
                        dynamicParent.ParentObject,
                        requiredTimelineTick,
                        recursionDepth + 1
                    );
            }

            return false;
        }

        private void RemoveStaticDisplayInstance(StaticDisplayInstance instance)
        {
            if (instance?.ScriptObject == null)
                return;

            Avm1Object scriptObject = instance.ScriptObject;

            // Remove attached dynamic descendants first. RemoveDynamicMovieClip
            // recursively handles deeper dynamic children and their onUnload calls.
            for (int i = dynamicMovieClips.Count - 1; i >= 0; i--)
            {
                DynamicMovieClip child = dynamicMovieClips[i];

                if (child != null && !child.Removed &&
                    child.ParentObject == scriptObject)
                {
                    RemoveDynamicMovieClip(child.ScriptObject);
                }
            }

            TriggerClipActions(instance, 0x00000020u); // Unload
            runtimeAvm1?.TryCallMethod(scriptObject, "onUnload", NoArgs, out _);

            if (!string.IsNullOrEmpty(instance.InstanceName) &&
                instance.ParentObject != null &&
                instance.ParentObject.TryGetOwn(
                    instance.InstanceName,
                    out object currentBinding
                ) &&
                ReferenceEquals(currentBinding, scriptObject))
            {
                instance.ParentObject.Remove(instance.InstanceName);
            }

            foreach (StaticDisplayInstance owner in staticDisplayInstances.Values)
            {
                if (owner != null && owner.MaskObject == scriptObject)
                    owner.MaskObject = null;
            }

            for (int i = 0; i < dynamicMovieClips.Count; i++)
            {
                DynamicMovieClip owner = dynamicMovieClips[i];

                if (owner != null && owner.MaskObject == scriptObject)
                    owner.MaskObject = null;
            }

            attachedBitmaps.Remove(scriptObject);
            avm1LastExecutedFrames.Remove("_static/" + instance.Serial);
            staticDisplayInstancesByObject.Remove(scriptObject);
            staticDisplayInstances.Remove(instance.Path);
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
                runtimeAudio?.SyncSpriteStream(sprite, localFrame, instance.IsPlaying);
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

                    if (!RequiresScriptInstance(runtimeParser, child.CharacterId))
                        continue;

                    string childPath = path + "/depth" + child.Depth +
                        (childIsSprite ? ":sprite" : ":char") + child.CharacterId;
                    GetOrCreateStaticDisplayInstance(childPath, child, instance.ScriptObject);
                }

                bool firstActionPass = !instance.HasExecutedFrameActions;
                int frameBeforeActions = instance.CurrentFrame;
                bool playingBeforeActions = instance.IsPlaying;

                ExecuteFrameActionsOnce(
                    "_static/" + instance.Serial,
                    localFrame,
                    frame.ControlTags,
                    instance.ScriptObject
                );

                if (firstActionPass && sprite.Frames.Count >= TimelineDiagnosticMinFrames)
                {
                    ReportTimelineControl(
                        "static enter char=" + instance.CharacterId +
                        " serial=" + instance.Serial +
                        " sourceFrame=" + (localFrame + 1) +
                        " resultFrame=" + (instance.CurrentFrame + 1) +
                        " playing=" + instance.IsPlaying +
                        " changed=" +
                        (frameBeforeActions != instance.CurrentFrame ||
                         playingBeforeActions != instance.IsPlaying) +
                        " name=" + instance.InstanceName +
                        " path=" + instance.Path
                    );
                }
                // Rendering may discover a newly placed MovieClip between Flash
                // ticks (for example immediately after a button changes scene).
                // Its frame-1 actions must run before AdvanceStaticMovieClips is
                // allowed to move it to frame 2. Otherwise stop(), class setup and
                // input/onEnterFrame registration are skipped and the symbol turns
                // into a slideshow of all of its library frames.
                instance.HasExecutedFrameActions = true;

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

        // Returns the control tag's action payload, copying it out of the SWF
        // buffer only the first time and reusing that array on every subsequent
        // execution of the same tag.
        private byte[] GetActionBytes(SwfTag tag, int skipBytes)
        {
            if (tag == null || runtimeParser == null)
                return System.Array.Empty<byte>();

            if (!actionByteCache.TryGetValue(tag, out byte[] bytes))
            {
                bytes = runtimeParser.CopyTagData(tag, skipBytes);
                actionByteCache[tag] = bytes;
            }

            return bytes;
        }

        private void ExecuteTimelineControlTag(SwfTag tag, Avm1Object thisObject)
        {
            if (tag == null)
                return;

            if (tag.Code == 12)
            {
                currentScriptRuntime?.Execute(GetActionBytes(tag, 0), thisObject);
                return;
            }

            if (tag.Code == 59)
            {
                ushort spriteId = runtimeParser.ParseDoInitActionSpriteId(tag);

                if (spriteId == 0)
                {
                    currentScriptRuntime?.Execute(GetActionBytes(tag, 2), thisObject);
                }
                return;
            }

            if (tag.Code == 15)
            {
                byte[] startSound = GetActionBytes(tag, 0);

                if (startSound.Length < 2)
                    return;

                ushort soundId = (ushort)(startSound[0] | (startSound[1] << 8));

                // The SOUNDINFO record after the id carries stop, no-multiple, loop
                // count, in/out points and the volume envelope.
                Tags.SwfSoundInfo info = runtimeParser.ParseSoundInfo(startSound, 2, out _);
                runtimeAudio?.PlaySound(soundId, info);

                return;
            }

            if (tag.Code == 89)
            {
                byte[] startSound2 = GetActionBytes(tag, 0);
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
                Avm1Object resolvedParent = parentObject ?? runtimeAvm1.RootObject;
                string resolvedName = place.HasName && !string.IsNullOrEmpty(place.Name)
                    ? place.Name
                    : string.Empty;
                existing.ClipActions = place.ClipActions;
                bool placementChanged =
                    existing.TimelineStartFrame != place.TimelineStartFrame;
                bool reenteredTimeline =
                    existing.LastSeenTimelineTick < timelineTickSerial - 1 ||
                    placementChanged;
                bool bindingChanged =
                    existing.ParentObject != resolvedParent ||
                    !string.Equals(
                        existing.InstanceName,
                        resolvedName,
                        System.StringComparison.Ordinal
                    );
                existing.LastSeenTimelineTick = timelineTickSerial;

                // Move/update PlaceObject tags carry the authoritative matrix for
                // this timeline frame. The old implementation initialized it only
                // once, so replaying a timeline kept every object at its first-frame
                // transform (usually off stage).
                if (place.HasMatrix &&
                    (reenteredTimeline || !MatricesApproximately(existing.TimelineMatrix, place.Matrix)))
                {
                    ApplyTimelineTransform(existing.ScriptObject, place.Matrix);
                    existing.TimelineMatrix = place.Matrix;
                }

                bool timelineVisible = !place.HasVisible || place.Visible != 0;

                if (reenteredTimeline)
                {
                    // A timeline removal followed by a later placement creates a new
                    // display-list lifetime in Flash. Reuse the allocation, but reset
                    // the state that must not leak from its previous appearance.
                    existing.CurrentFrame = 0;
                    existing.IsPlaying = true;
                    existing.FrameEnteredPending = false;
                    existing.HasExecutedFrameActions = false;
                    existing.Serial = ++nextStaticDisplayInstanceSerial;
                    existing.MaskObject = null;
                    existing.ScriptObject.Set("_alpha", 100d);
                    existing.ScriptObject.Set("_currentframe", 1);
                }

                if (reenteredTimeline || bindingChanged)
                {
                    // A timeline instance name is also a property on its parent
                    // MovieClip. Static instances are allocation-cached by path, so
                    // a remove/re-place used to revive the rendered object without
                    // restoring that property. ActionScript could then see the clip
                    // on stage while `parent.childName` evaluated to undefined, and
                    // gotoAndStop/stop targeted at it were silently lost.
                    //
                    // Remove only our own old binding: a script is allowed to have
                    // replaced that member while the old timeline lifetime was gone.
                    if (!string.IsNullOrEmpty(existing.InstanceName) &&
                        existing.ParentObject != null &&
                        existing.ParentObject.TryGetOwn(
                            existing.InstanceName,
                            out object oldBinding
                        ) &&
                        ReferenceEquals(oldBinding, existing.ScriptObject))
                    {
                        existing.ParentObject.Remove(existing.InstanceName);
                    }

                    existing.ParentObject = resolvedParent;
                    existing.InstanceName = resolvedName;
                    existing.TimelineStartFrame = place.TimelineStartFrame;
                    existing.ScriptObject.Set("_parent", resolvedParent);
                    existing.ScriptObject.Set("_name", resolvedName);
                    existing.ScriptObject.Set("_target", path);
                    existing.ScriptObject.Set("_depth", place.Depth);
                }

                // Keep the display-list name authoritative while the object is
                // placed. This also repairs a missing binding when the instance was
                // first discovered through a render traversal between script ticks.
                if (!string.IsNullOrEmpty(existing.InstanceName) &&
                    (!existing.ParentObject.TryGetOwn(
                        existing.InstanceName,
                        out object currentBinding
                    ) ||
                    !ReferenceEquals(currentBinding, existing.ScriptObject)))
                {
                    existing.ParentObject.Set(existing.InstanceName, existing.ScriptObject);
                }

                if (reenteredTimeline || existing.TimelineVisible != timelineVisible)
                {
                    existing.TimelineVisible = timelineVisible;
                    existing.ScriptObject.Set("_visible", timelineVisible);
                }

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
                HasExecutedFrameActions = false,
                TimelineMatrix = place.Matrix,
                TimelineVisible = !place.HasVisible || place.Visible != 0,
                TimelineStartFrame = place.TimelineStartFrame,
                LastSeenTimelineTick = timelineTickSerial,
                Serial = ++nextStaticDisplayInstanceSerial,
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

            ExecuteInitActionsForCharacter(place.CharacterId, scriptObject);
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
            scriptObject.Set("_root", runtimeAvm1.RootObject);
            scriptObject.Set("_parent", parentObject ?? runtimeAvm1.RootObject);
            scriptObject.Set("_name", instanceName ?? string.Empty);
            scriptObject.Set("_target", targetPath);
            scriptObject.Set("_depth", depth);
            ApplyTimelineTransform(scriptObject, matrix);
            scriptObject.Set("_alpha", 100d);
            scriptObject.Set("_visible", visible);
            scriptObject.Set("_currentframe", 1);
            int totalFrames = GetCharacterFrameCount(characterId);
            scriptObject.Set("_totalframes", totalFrames);
            scriptObject.Set("_framesloaded", totalFrames);
            scriptObject.Set("__characterId", characterId);
            InitializeEditTextProperties(scriptObject, parentObject, characterId);
        }

        private void InitializeEditTextProperties(
            Avm1Object scriptObject,
            Avm1Object parentObject,
            ushort characterId
        )
        {
            DefineEditTextTag editText = runtimeParser?.FindEditTextById(characterId);

            if (editText == null || scriptObject == null)
                return;

            scriptObject.Set("text", editText.InitialText ?? string.Empty);
            string variable = editText.VariableName ?? string.Empty;

            if (parentObject != null && !string.IsNullOrEmpty(variable) &&
                variable.IndexOf('.') < 0 && variable.IndexOf(':') < 0 &&
                !parentObject.TryGet(variable, out _))
            {
                parentObject.Set(variable, editText.InitialText ?? string.Empty);
            }
        }

        private string ResolveEditTextValue(
            DefineEditTextTag editText,
            Avm1Object scriptObject
        )
        {
            if (editText == null)
                return string.Empty;

            string variable = editText.VariableName ?? string.Empty;
            Avm1Object parentObject = scriptObject?.Get("_parent") as Avm1Object;

            if (!string.IsNullOrEmpty(variable))
            {
                if (parentObject != null && parentObject.TryGet(variable, out object parentValue) &&
                    parentValue != null)
                {
                    return parentValue.ToString();
                }

                object globalValue = runtimeAvm1?.GetVariable(variable);

                if (globalValue != null)
                    return globalValue.ToString();
            }

            object directValue = scriptObject?.Get("text");
            return directValue?.ToString() ?? editText.InitialText ?? string.Empty;
        }

        private static void ApplyTimelineTransform(Avm1Object scriptObject, SwfMatrix matrix)
        {
            if (scriptObject == null)
                return;

            float scaleX = Mathf.Sqrt(
                matrix.ScaleX * matrix.ScaleX + matrix.RotateSkew0 * matrix.RotateSkew0
            );
            float scaleY = Mathf.Sqrt(
                matrix.RotateSkew1 * matrix.RotateSkew1 + matrix.ScaleY * matrix.ScaleY
            );
            float rotation = Mathf.Atan2(matrix.RotateSkew0, matrix.ScaleX) * Mathf.Rad2Deg;

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
        }

        private static bool MatricesApproximately(SwfMatrix a, SwfMatrix b)
        {
            const float epsilon = 0.00001f;
            return
                Mathf.Abs(a.ScaleX - b.ScaleX) <= epsilon &&
                Mathf.Abs(a.ScaleY - b.ScaleY) <= epsilon &&
                Mathf.Abs(a.RotateSkew0 - b.RotateSkew0) <= epsilon &&
                Mathf.Abs(a.RotateSkew1 - b.RotateSkew1) <= epsilon &&
                Mathf.Abs(a.TranslateX - b.TranslateX) <= epsilon &&
                Mathf.Abs(a.TranslateY - b.TranslateY) <= epsilon;
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
                IsPlaying = true,
                Serial = ++nextDynamicMovieClipSerial
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
            int totalFrames = GetCharacterFrameCount(characterId);
            scriptObject.Set("_totalframes", totalFrames);
            scriptObject.Set("_framesloaded", totalFrames);
            scriptObject.Set("__linkage", linkageName ?? string.Empty);
            scriptObject.Set("__characterId", characterId);
            InitializeEditTextProperties(scriptObject, parentObject, characterId);

            initObject?.CopyMembersTo(scriptObject);

            dynamicMovieClips.Add(clip);
            dynamicMovieClipsByObject[scriptObject] = clip;
            renderStateDirty = true;

            if (!string.IsNullOrEmpty(instanceName))
                parentObject.Set(instanceName, scriptObject);

            runtimeAvm1.ApplyRegisteredClass(linkageName, scriptObject);

            ExecuteInitActionsForCharacter(characterId, scriptObject);
            if (ShouldReportTimelineClip(characterId))
            {
                ReportTimelineControl(
                    "dynamic create char=" + characterId +
                    " serial=" + clip.Serial +
                    " frames=" + totalFrames +
                    " name=" + (instanceName ?? string.Empty) +
                    " linkage=" + (linkageName ?? string.Empty)
                );
            }
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

            // Fired before the clip is torn down, matching Flash, so the handler can
            // still read the clip's own properties. Marking Removed first would also
            // make a handler that re-entered removeMovieClip recurse.
            clip.Removed = true;
            renderStateDirty = true;

            // Detach the mask from every owner before the object disappears. Flash
            // does not keep clipping with a MovieClip that is no longer on stage.
            for (int i = 0; i < dynamicMovieClips.Count; i++)
            {
                DynamicMovieClip owner = dynamicMovieClips[i];

                if (owner != null && owner.MaskObject == scriptObject)
                    owner.MaskObject = null;
            }

            foreach (StaticDisplayInstance owner in staticDisplayInstances.Values)
            {
                if (owner != null && owner.MaskObject == scriptObject)
                    owner.MaskObject = null;
            }

            runtimeAvm1?.TryCallMethod(clip.ScriptObject, "onUnload", NoArgs, out _);

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

                if (clip.CurrentFrame == 0 && ShouldReportTimelineClip(clip.CharacterId))
                {
                    ReportTimelineControl("dynamic wrap char=" + clip.CharacterId +
                        " serial=" + clip.Serial + " frames=" + frameCount);
                }

                clip.ScriptObject.Set("_currentframe", clip.CurrentFrame + 1);
                renderStateDirty = true;
                ExecuteDynamicClipFrameActions(clip);
            }
        }

        private void AdvanceStaticMovieClips()
        {
            if (runtimeParser == null || staticDisplayInstances.Count == 0)
                return;

            foreach (StaticDisplayInstance instance in staticDisplayInstances.Values)
            {
                if (instance == null ||
                    instance.LastSeenTimelineTick < timelineTickSerial - 1)
                    continue;

                // A clip can be created by the render traversal after the previous
                // action pass. Keep it on frame 1 until ExecuteVisibleSpriteActions
                // has actually run that frame's scripts once.
                if (!instance.HasExecutedFrameActions)
                    continue;

                if (instance.FrameEnteredPending)
                {
                    instance.FrameEnteredPending = false;
                    continue;
                }

                if (!instance.IsPlaying)
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

                    if (sprite.Frames.Count >= TimelineDiagnosticMinFrames)
                    {
                        ReportTimelineControl("static wrap char=" + instance.CharacterId +
                            " serial=" + instance.Serial + " frames=" + sprite.Frames.Count +
                            " loop=" + loopSpriteTimelines + " path=" + instance.Path);
                    }

                    if (!loopSpriteTimelines)
                        instance.IsPlaying = false;
                }

                instance.ScriptObject.Set("_currentframe", instance.CurrentFrame + 1);
                renderStateDirty = true;
            }
        }

        private static bool RequiresScriptInstance(SwfParser parser, ushort characterId)
        {
            if (parser == null || characterId == 0)
                return false;

            // Raw Shape and DefineText characters are display data, not MovieClips.
            // Giving every one an AVM1 object made large SWFs dispatch enterFrame,
            // mouse transforms and property work to thousands of inert shapes.
            return parser.FindSpriteById(characterId) != null ||
                   parser.FindButton2ById(characterId) != null ||
                   parser.FindEditTextById(characterId) != null;
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

                RenderAttachedBitmaps(next.ScriptObject, worldMatrix, alpha);

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

        private void RenderAttachedBitmaps(
            Avm1Object owner,
            SwfMatrix worldMatrix,
            float alpha)
        {
            if (owner == null || alpha <= 0.001f || runtimeRasterRenderer == null ||
                !attachedBitmaps.TryGetValue(owner, out List<AttachedBitmap> attachments))
            {
                return;
            }

            for (int i = 0; i < attachments.Count; i++)
            {
                DynamicBitmapData bitmap = attachments[i]?.Bitmap;

                if (bitmap == null || bitmap.Disposed || bitmap.Texture == null)
                    continue;

                if (bitmap.Dirty)
                {
                    bitmap.Texture.SetPixels32(bitmap.Pixels);
                    bitmap.Texture.Apply(false, false);
                    bitmap.Dirty = false;
                }

                runtimeRasterRenderer.DrawTextureQuad(
                    bitmap.Texture,
                    bitmap.Width,
                    bitmap.Height,
                    worldMatrix,
                    alpha);
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
            float x = Avm1Float(scriptObject.Get("_x"), baseX);
            float y = Avm1Float(scriptObject.Get("_y"), baseY);

            if (staticDisplayInstancesByObject.TryGetValue(
                    scriptObject,
                    out StaticDisplayInstance staticInstance) &&
                Mathf.Abs(scaleX * 100f - baseXScale) <= 0.0001f &&
                Mathf.Abs(scaleY * 100f - baseYScale) <= 0.0001f &&
                Mathf.Abs(rotation * Mathf.Rad2Deg - baseRotation) <= 0.0001f)
            {
                // Preserve the exact SWF affine matrix, including independent skew.
                // Script-only x/y changes can still move it without flattening that
                // matrix into Unity-style rotation plus scale.
                SwfMatrix exact = staticInstance.TimelineMatrix;
                exact.TranslateX = x;
                exact.TranslateY = y;
                return exact;
            }

            float cosine = Mathf.Cos(rotation);
            float sine = Mathf.Sin(rotation);

            return new SwfMatrix
            {
                ScaleX = cosine * scaleX,
                RotateSkew0 = sine * scaleX,
                RotateSkew1 = -sine * scaleY,
                ScaleY = cosine * scaleY,
                TranslateX = x,
                TranslateY = y
            };
        }

        // Local-space bounds of a character, in pixels, keyed by character id.
        //
        // A sprite's entry is the union of its children's bounds across every frame.
        // Flash measures only the frame currently showing, so a clip whose contents
        // move between frames reads larger here; for the static graphics that scripts
        // actually measure the two agree, and a per-character value stays valid for
        // the life of the movie instead of being recomputed as timelines advance.
        private readonly Dictionary<ushort, Rect> characterBoundsCache =
            new Dictionary<ushort, Rect>();
        private readonly HashSet<ushort> characterBoundsInProgress = new HashSet<ushort>();

        private static readonly Rect EmptyCharacterBounds = new Rect(0f, 0f, -1f, -1f);

        private bool TryGetCharacterBounds(ushort characterId, out Rect bounds)
        {
            if (characterBoundsCache.TryGetValue(characterId, out bounds))
                return bounds.width >= 0f;

            // A sprite that (directly or indirectly) contains itself would otherwise
            // recurse forever; treat the re-entry as contributing nothing.
            if (!characterBoundsInProgress.Add(characterId))
            {
                bounds = EmptyCharacterBounds;
                return false;
            }

            try
            {
                bounds = ComputeCharacterBounds(characterId);
            }
            finally
            {
                characterBoundsInProgress.Remove(characterId);
            }

            characterBoundsCache[characterId] = bounds;
            return bounds.width >= 0f;
        }

        private Rect ComputeCharacterBounds(ushort characterId)
        {
            if (runtimeParser == null)
                return EmptyCharacterBounds;

            DefineShapeTag shape = runtimeParser.FindShapeById(characterId);

            if (shape != null)
            {
                SwfRect r = shape.ShapeBounds;
                return new Rect(
                    r.XMinPixels,
                    r.YMinPixels,
                    Mathf.Max(0f, r.WidthPixels),
                    Mathf.Max(0f, r.HeightPixels)
                );
            }

            DefineTextTag text = runtimeParser.FindTextById(characterId);

            if (text != null)
            {
                SwfRect r = text.TextBounds;
                return new Rect(
                    r.XMinPixels,
                    r.YMinPixels,
                    Mathf.Max(0f, r.WidthPixels),
                    Mathf.Max(0f, r.HeightPixels)
                );
            }

            DefineButton2Tag button = runtimeParser.FindButton2ById(characterId);

            if (button != null)
            {
                Rect result = EmptyCharacterBounds;

                for (int i = 0; i < button.Records.Count; i++)
                {
                    SwfButtonRecord record = button.Records[i];

                    if (record == null || !record.StateUp)
                        continue;

                    if (TryGetCharacterBounds(record.CharacterId, out Rect child))
                        result = UnionBounds(result, TransformBounds(child, record.Matrix));
                }

                return result;
            }

            DefineSpriteTag sprite = runtimeParser.FindSpriteById(characterId);

            if (sprite?.Frames == null || sprite.Frames.Count == 0)
                return EmptyCharacterBounds;

            Rect spriteBounds = EmptyCharacterBounds;

            for (int frame = 0; frame < sprite.Frames.Count; frame++)
            {
                List<PlaceObject2Tag> places =
                    BuildDisplayListForFrame(runtimeParser, sprite.Frames, frame);

                for (int i = 0; i < places.Count; i++)
                {
                    PlaceObject2Tag place = places[i];

                    if (place == null || !place.HasCharacter || place.CharacterId == 0)
                        continue;

                    if (TryGetCharacterBounds(place.CharacterId, out Rect child))
                        spriteBounds = UnionBounds(spriteBounds, TransformBounds(child, place.Matrix));
                }
            }

            return spriteBounds;
        }

        // Axis-aligned bounds of the transformed corners; a rotated child therefore
        // reports the extent of its rotated box, which is what Flash measures.
        private static Rect TransformBounds(Rect local, SwfMatrix matrix)
        {
            Vector2 c0 = matrix.TransformPoint(local.xMin, local.yMin);
            Vector2 c1 = matrix.TransformPoint(local.xMax, local.yMin);
            Vector2 c2 = matrix.TransformPoint(local.xMax, local.yMax);
            Vector2 c3 = matrix.TransformPoint(local.xMin, local.yMax);

            float minX = Mathf.Min(Mathf.Min(c0.x, c1.x), Mathf.Min(c2.x, c3.x));
            float maxX = Mathf.Max(Mathf.Max(c0.x, c1.x), Mathf.Max(c2.x, c3.x));
            float minY = Mathf.Min(Mathf.Min(c0.y, c1.y), Mathf.Min(c2.y, c3.y));
            float maxY = Mathf.Max(Mathf.Max(c0.y, c1.y), Mathf.Max(c2.y, c3.y));

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private static Rect UnionBounds(Rect a, Rect b)
        {
            if (a.width < 0f)
                return b;

            if (b.width < 0f)
                return a;

            float minX = Mathf.Min(a.xMin, b.xMin);
            float minY = Mathf.Min(a.yMin, b.yMin);
            float maxX = Mathf.Max(a.xMax, b.xMax);
            float maxY = Mathf.Max(a.yMax, b.yMax);

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        // _width/_height are the character's bounds scaled by the object's own
        // _xscale/_yscale, i.e. its extent in the parent's coordinate space.
        private object GetDisplayObjectComputedProperty(Avm1Object scriptObject, int propertyId)
        {
            if (scriptObject == null || !TryGetDisplayObjectCharacter(scriptObject, out ushort characterId))
                return null;

            if (!TryGetCharacterBounds(characterId, out Rect bounds))
                return null;

            if (propertyId == Avm1Runtime.ComputedPropertyWidth)
            {
                float scaleX = Avm1Float(scriptObject.Get("_xscale"), 100f) / 100f;
                return (double)(bounds.width * Mathf.Abs(scaleX));
            }

            float scaleY = Avm1Float(scriptObject.Get("_yscale"), 100f) / 100f;
            return (double)(bounds.height * Mathf.Abs(scaleY));
        }

        // Assigning _width/_height is a rescale: Flash converts the requested size into
        // the scale factor that produces it, leaving the character's own geometry alone.
        private bool SetDisplayObjectComputedProperty(
            Avm1Object scriptObject,
            int propertyId,
            object value
        )
        {
            if (scriptObject == null || !TryGetDisplayObjectCharacter(scriptObject, out ushort characterId))
                return false;

            if (!TryGetCharacterBounds(characterId, out Rect bounds))
                return false;

            float requested = Avm1Float(value, float.NaN);

            if (float.IsNaN(requested) || requested < 0f)
                return false;

            bool isWidth = propertyId == Avm1Runtime.ComputedPropertyWidth;
            float extent = isWidth ? bounds.width : bounds.height;

            // A zero-extent character has no size to scale from, so Flash cannot
            // satisfy the assignment either; report it rather than divide by zero.
            if (extent <= 0.0001f)
            {
                if (verboseLogging)
                {
                    Debug.LogWarning(
                        "Cannot set " + (isWidth ? "_width" : "_height") + " on character " +
                        characterId + ": it has no measurable bounds."
                    );
                }

                return false;
            }

            scriptObject.Set(isWidth ? "_xscale" : "_yscale", (double)(requested / extent * 100f));
            renderStateDirty = true;
            return true;
        }

        private void HandleAvm1MemberChanged(Avm1Object scriptObject, string memberName)
        {
            if (scriptObject == null || string.IsNullOrEmpty(memberName))
                return;

            // Ordinary variables do not affect pixels. Marking every assignment dirty
            // defeats retained frames in script-heavy games, so only display properties
            // that the renderer consumes request a new mesh upload.
            switch (memberName.ToLowerInvariant())
            {
                case "_x":
                case "_y":
                case "_xscale":
                case "_yscale":
                case "_rotation":
                case "_alpha":
                case "_visible":
                case "_width":
                case "_height":
                case "text":
                    renderStateDirty = true;
                    break;
            }
        }

        private bool TryGetDisplayObjectCharacter(Avm1Object scriptObject, out ushort characterId)
        {
            object stored = scriptObject.Get("__characterId");

            if (stored != null)
            {
                characterId = (ushort)Mathf.Clamp(Avm1Float(stored, 0f), 0f, ushort.MaxValue);
                return characterId != 0;
            }

            characterId = 0;
            return false;
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
                ReportTimelineControl("root " + (playing ? "play" : "stop") +
                    " frame=" + (currentTimelineFrame + 1));
                return;
            }

            if (receiver is Avm1Object scriptObject &&
                dynamicMovieClipsByObject.TryGetValue(scriptObject, out DynamicMovieClip clip))
            {
                clip.IsPlaying = playing;
                if (ShouldReportTimelineClip(clip.CharacterId))
                {
                    ReportTimelineControl("dynamic " + (playing ? "play" : "stop") +
                        " char=" + clip.CharacterId + " serial=" + clip.Serial +
                        " frame=" + (clip.CurrentFrame + 1));
                }
                return;
            }

            if (receiver is Avm1Object staticObject &&
                staticDisplayInstancesByObject.TryGetValue(staticObject, out StaticDisplayInstance staticInstance))
            {
                staticInstance.IsPlaying = playing;
                if (ShouldReportTimelineClip(staticInstance.CharacterId))
                {
                    ReportTimelineControl("static " + (playing ? "play" : "stop") +
                        " char=" + staticInstance.CharacterId + " serial=" + staticInstance.Serial +
                        " frame=" + (staticInstance.CurrentFrame + 1) +
                        " path=" + staticInstance.Path);
                }
                return;
            }

            ReportTimelineControl("unresolved " + (playing ? "play" : "stop") +
                " receiver=" + DescribeTimelineReceiver(receiver));
        }

        private void StepDynamicClip(object receiver, int direction)
        {
            if (receiver == runtimeAvm1?.RootObject)
            {
                int frameCount = Mathf.Max(1, runtimeParser.RootFrames.Count);
                currentTimelineFrame = (currentTimelineFrame + direction + frameCount) % frameCount;
                rootFrameEnteredPending = true;
                renderStateDirty = true;
                ReportTimelineControl("root step direction=" + direction +
                    " frame=" + (currentTimelineFrame + 1));
                return;
            }

            if (receiver is Avm1Object scriptObject &&
                dynamicMovieClipsByObject.TryGetValue(scriptObject, out DynamicMovieClip clip))
            {
                int frameCount = GetCharacterFrameCount(clip.CharacterId);
                clip.CurrentFrame = (clip.CurrentFrame + direction + frameCount) % frameCount;
                clip.ScriptObject.Set("_currentframe", clip.CurrentFrame + 1);
                renderStateDirty = true;
                ReportTimelineControl("dynamic step direction=" + direction +
                    " char=" + clip.CharacterId + " serial=" + clip.Serial +
                    " frame=" + (clip.CurrentFrame + 1));
                ExecuteDynamicClipFrameActions(clip);
                return;
            }

            if (receiver is Avm1Object staticObject &&
                staticDisplayInstancesByObject.TryGetValue(staticObject, out StaticDisplayInstance staticInstance))
            {
                int frameCount = GetCharacterFrameCount(staticInstance.CharacterId);
                staticInstance.CurrentFrame =
                    (staticInstance.CurrentFrame + direction + frameCount) % frameCount;
                staticInstance.FrameEnteredPending = true;
                staticInstance.ScriptObject.Set("_currentframe", staticInstance.CurrentFrame + 1);
                renderStateDirty = true;
                if (ShouldReportTimelineClip(staticInstance.CharacterId))
                {
                    ReportTimelineControl("static step direction=" + direction +
                        " char=" + staticInstance.CharacterId + " serial=" + staticInstance.Serial +
                        " frame=" + (staticInstance.CurrentFrame + 1) +
                        " path=" + staticInstance.Path);
                }
                return;
            }

            ReportTimelineControl("unresolved step direction=" + direction +
                " receiver=" + DescribeTimelineReceiver(receiver));
        }

        private void ReportTimelineControl(string message)
        {
            if (!enableTimelineControlDiagnostics ||
                timelineControlDiagnosticCount >= MaxTimelineControlDiagnostics)
            {
                return;
            }

            timelineControlDiagnosticCount++;
            Debug.Log("[SWF timeline] " + message);
        }

        private bool ShouldReportTimelineClip(ushort characterId)
        {
            return GetCharacterFrameCount(characterId) >= TimelineDiagnosticMinFrames;
        }

        private static string DescribeTimelineReceiver(object receiver)
        {
            if (receiver == null)
                return "null";

            if (receiver is Avm1Object avm1Object)
            {
                return "Avm1Object target=" + (avm1Object.Get("_target") ?? "<none>") +
                    " name=" + (avm1Object.Get("_name") ?? "<none>") +
                    " char=" + (avm1Object.Get("__characterId") ?? "<none>");
            }

            return receiver.GetType().Name;
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
                rootFrameEnteredPending = true;
                renderStateDirty = true;
                ReportTimelineControl("root goto frame=" + (currentTimelineFrame + 1) +
                    " playing=" + playing);
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
                renderStateDirty = true;
                ReportTimelineControl("dynamic goto char=" + clip.CharacterId +
                    " serial=" + clip.Serial + " frame=" + (clip.CurrentFrame + 1) +
                    " playing=" + playing);
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
                staticInstance.FrameEnteredPending = true;
                staticInstance.ScriptObject.Set("_currentframe", staticInstance.CurrentFrame + 1);
                renderStateDirty = true;
                if (ShouldReportTimelineClip(staticInstance.CharacterId))
                {
                    ReportTimelineControl("static goto char=" + staticInstance.CharacterId +
                        " serial=" + staticInstance.Serial + " frame=" +
                        (staticInstance.CurrentFrame + 1) + " playing=" + playing +
                        " path=" + staticInstance.Path);
                }
                return;
            }

            ReportTimelineControl("unresolved goto requested=" + requestedValue +
                " resolvedFrame=" + (requestedFrame + 1) + " playing=" + playing +
                " receiver=" + DescribeTimelineReceiver(receiver));
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
                if (receiver == runtimeAvm1?.RootObject &&
                    runtimeParser.RootFrameLabels.TryGetValue(label, out int taggedFrame))
                {
                    return Mathf.Clamp(taggedFrame, 0, Mathf.Max(0, frames.Count - 1));
                }

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
                renderStateDirty = true;
            }
            else
            {
                clip.Depth = ArgumentInt(arguments, 0, clip.Depth);
                clip.ScriptObject.Set("_depth", clip.Depth);
                renderStateDirty = true;
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

        private static uint Avm1UInt(object value, uint fallback)
        {
            if (value == null)
                return fallback;

            try
            {
                return System.Convert.ToUInt32(
                    System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)
                );
            }
            catch
            {
                return fallback;
            }
        }

        private Avm1Object CreateGeometryObject(
            IReadOnlyList<object> arguments,
            params string[] members)
        {
            Avm1Object result = runtimeAvm1.CreateObject();

            for (int i = 0; i < members.Length; i++)
                result.Set(members[i], i < arguments.Count ? arguments[i] : 0d);

            return result;
        }

        private Avm1Object CreateMatrixObject(IReadOnlyList<object> arguments)
        {
            Avm1Object matrix = runtimeAvm1.CreateObject();
            matrix.Set("a", arguments.Count > 0 ? arguments[0] : 1d);
            matrix.Set("b", arguments.Count > 1 ? arguments[1] : 0d);
            matrix.Set("c", arguments.Count > 2 ? arguments[2] : 0d);
            matrix.Set("d", arguments.Count > 3 ? arguments[3] : 1d);
            matrix.Set("tx", arguments.Count > 4 ? arguments[4] : 0d);
            matrix.Set("ty", arguments.Count > 5 ? arguments[5] : 0d);
            matrix.Set("translate", new Avm1NativeFunction(args =>
            {
                matrix.Set("tx", Avm1Float(matrix.Get("tx"), 0f) + Avm1Float(args.Count > 0 ? args[0] : null, 0f));
                matrix.Set("ty", Avm1Float(matrix.Get("ty"), 0f) + Avm1Float(args.Count > 1 ? args[1] : null, 0f));
                return matrix;
            }));
            matrix.Set("scale", new Avm1NativeFunction(args =>
            {
                float sx = Avm1Float(args.Count > 0 ? args[0] : null, 1f);
                float sy = Avm1Float(args.Count > 1 ? args[1] : null, sx);
                matrix.Set("a", Avm1Float(matrix.Get("a"), 1f) * sx);
                matrix.Set("b", Avm1Float(matrix.Get("b"), 0f) * sy);
                matrix.Set("c", Avm1Float(matrix.Get("c"), 0f) * sx);
                matrix.Set("d", Avm1Float(matrix.Get("d"), 1f) * sy);
                matrix.Set("tx", Avm1Float(matrix.Get("tx"), 0f) * sx);
                matrix.Set("ty", Avm1Float(matrix.Get("ty"), 0f) * sy);
                return matrix;
            }));
            matrix.Set("identity", new Avm1NativeFunction(_ =>
            {
                matrix.Set("a", 1d); matrix.Set("b", 0d);
                matrix.Set("c", 0d); matrix.Set("d", 1d);
                matrix.Set("tx", 0d); matrix.Set("ty", 0d);
                return matrix;
            }));
            return matrix;
        }

        private static Color32 ArgbToColor(uint argb, bool transparent)
        {
            byte alpha = transparent ? (byte)(argb >> 24) : (byte)255;
            return new Color32(
                (byte)(argb >> 16),
                (byte)(argb >> 8),
                (byte)argb,
                alpha);
        }

        private Avm1Object CreateBitmapDataObject(IReadOnlyList<object> arguments)
        {
            int width = Mathf.Clamp(ArgumentInt(arguments, 0, 1), 1, 4096);
            int height = Mathf.Clamp(ArgumentInt(arguments, 1, 1), 1, 4096);
            bool transparent = arguments.Count <= 2 || Avm1Boolean(arguments[2], true);
            uint fillValue = arguments.Count > 3
                ? Avm1UInt(arguments[3], 0xFFFFFFFFu)
                : 0xFFFFFFFFu;
            Color32 fill = ArgbToColor(fillValue, transparent);
            Color32[] pixels = new Color32[width * height];

            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = fill;

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "AVM1 BitmapData " + width + "x" + height,
                filterMode = SwfRenderQuality.Settings.BitmapFilter,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            Avm1Object scriptObject = runtimeAvm1.CreateObject();
            DynamicBitmapData bitmap = new DynamicBitmapData
            {
                ScriptObject = scriptObject,
                Texture = texture,
                Pixels = pixels,
                Width = width,
                Height = height,
                Transparent = transparent
            };
            dynamicBitmapData[scriptObject] = bitmap;
            scriptObject.Set("width", width);
            scriptObject.Set("height", height);
            scriptObject.Set("transparent", transparent);
            scriptObject.Set("rectangle", CreateGeometryObject(
                new object[] { 0d, 0d, (double)width, (double)height },
                "x", "y", "width", "height"));
            scriptObject.Set("fillRect", new Avm1NativeFunction(args =>
            {
                FillBitmapRect(bitmap, args.Count > 0 ? args[0] as Avm1Object : null,
                    args.Count > 1 ? Avm1UInt(args[1], 0u) : 0u);
                return true;
            }));
            scriptObject.Set("setPixel", new Avm1NativeFunction(args =>
            {
                SetBitmapPixel(bitmap, ArgumentInt(args, 0, 0), ArgumentInt(args, 1, 0),
                    args.Count > 2 ? Avm1UInt(args[2], 0u) | 0xFF000000u : 0xFF000000u);
                return true;
            }));
            scriptObject.Set("setPixel32", new Avm1NativeFunction(args =>
            {
                SetBitmapPixel(bitmap, ArgumentInt(args, 0, 0), ArgumentInt(args, 1, 0),
                    args.Count > 2 ? Avm1UInt(args[2], 0u) : 0u);
                return true;
            }));
            scriptObject.Set("getPixel", new Avm1NativeFunction(args =>
                GetBitmapPixel(bitmap, ArgumentInt(args, 0, 0), ArgumentInt(args, 1, 0), false)));
            scriptObject.Set("getPixel32", new Avm1NativeFunction(args =>
                GetBitmapPixel(bitmap, ArgumentInt(args, 0, 0), ArgumentInt(args, 1, 0), true)));
            scriptObject.Set("copyPixels", new Avm1NativeFunction(args =>
            {
                CopyBitmapPixels(
                    bitmap,
                    args.Count > 0 && args[0] is Avm1Object sourceObject &&
                        dynamicBitmapData.TryGetValue(sourceObject, out DynamicBitmapData source)
                            ? source
                            : null,
                    args.Count > 1 ? args[1] as Avm1Object : null,
                    args.Count > 2 ? args[2] as Avm1Object : null);
                return true;
            }));
            scriptObject.Set("draw", new Avm1NativeFunction(args =>
            {
                if (args.Count > 0 && args[0] is Avm1Object sourceObject &&
                    dynamicBitmapData.TryGetValue(sourceObject, out DynamicBitmapData source))
                {
                    CopyBitmapPixels(bitmap, source, null, null);
                }
                return true;
            }));
            scriptObject.Set("lock", new Avm1NativeFunction(_ => true));
            scriptObject.Set("unlock", new Avm1NativeFunction(_ =>
            {
                bitmap.Dirty = true;
                renderStateDirty = true;
                return true;
            }));
            scriptObject.Set("dispose", new Avm1NativeFunction(_ =>
            {
                bitmap.Disposed = true;
                renderStateDirty = true;
                return true;
            }));
            return scriptObject;
        }

        private static int BitmapIndex(DynamicBitmapData bitmap, int x, int y)
        {
            if (bitmap == null || x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
                return -1;
            return (bitmap.Height - 1 - y) * bitmap.Width + x;
        }

        private void SetBitmapPixel(DynamicBitmapData bitmap, int x, int y, uint argb)
        {
            int index = BitmapIndex(bitmap, x, y);
            if (index < 0) return;
            bitmap.Pixels[index] = ArgbToColor(argb, bitmap.Transparent);
            bitmap.Dirty = true;
            renderStateDirty = true;
        }

        private static uint GetBitmapPixel(DynamicBitmapData bitmap, int x, int y, bool includeAlpha)
        {
            int index = BitmapIndex(bitmap, x, y);
            if (index < 0) return 0u;
            Color32 color = bitmap.Pixels[index];
            uint rgb = ((uint)color.r << 16) | ((uint)color.g << 8) | color.b;
            return includeAlpha ? ((uint)color.a << 24) | rgb : rgb;
        }

        private void FillBitmapRect(DynamicBitmapData bitmap, Avm1Object rectangle, uint argb)
        {
            if (bitmap == null || bitmap.Disposed) return;
            int x = rectangle != null ? Mathf.FloorToInt(Avm1Float(rectangle.Get("x"), 0f)) : 0;
            int y = rectangle != null ? Mathf.FloorToInt(Avm1Float(rectangle.Get("y"), 0f)) : 0;
            int width = rectangle != null ? Mathf.CeilToInt(Avm1Float(rectangle.Get("width"), bitmap.Width)) : bitmap.Width;
            int height = rectangle != null ? Mathf.CeilToInt(Avm1Float(rectangle.Get("height"), bitmap.Height)) : bitmap.Height;
            int x0 = Mathf.Clamp(x, 0, bitmap.Width);
            int y0 = Mathf.Clamp(y, 0, bitmap.Height);
            int x1 = Mathf.Clamp(x + width, 0, bitmap.Width);
            int y1 = Mathf.Clamp(y + height, 0, bitmap.Height);
            Color32 color = ArgbToColor(argb, bitmap.Transparent);

            for (int py = y0; py < y1; py++)
            for (int px = x0; px < x1; px++)
                bitmap.Pixels[BitmapIndex(bitmap, px, py)] = color;

            bitmap.Dirty = true;
            renderStateDirty = true;
        }

        private void CopyBitmapPixels(
            DynamicBitmapData destination,
            DynamicBitmapData source,
            Avm1Object sourceRectangle,
            Avm1Object destinationPoint)
        {
            if (destination == null || source == null || destination.Disposed || source.Disposed)
                return;
            int sourceX = sourceRectangle != null ? Mathf.FloorToInt(Avm1Float(sourceRectangle.Get("x"), 0f)) : 0;
            int sourceY = sourceRectangle != null ? Mathf.FloorToInt(Avm1Float(sourceRectangle.Get("y"), 0f)) : 0;
            int width = sourceRectangle != null ? Mathf.CeilToInt(Avm1Float(sourceRectangle.Get("width"), source.Width)) : source.Width;
            int height = sourceRectangle != null ? Mathf.CeilToInt(Avm1Float(sourceRectangle.Get("height"), source.Height)) : source.Height;
            int destinationX = destinationPoint != null ? Mathf.FloorToInt(Avm1Float(destinationPoint.Get("x"), 0f)) : 0;
            int destinationY = destinationPoint != null ? Mathf.FloorToInt(Avm1Float(destinationPoint.Get("y"), 0f)) : 0;

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int sourceIndex = BitmapIndex(source, sourceX + x, sourceY + y);
                int destinationIndex = BitmapIndex(destination, destinationX + x, destinationY + y);
                if (sourceIndex >= 0 && destinationIndex >= 0)
                    destination.Pixels[destinationIndex] = source.Pixels[sourceIndex];
            }

            destination.Dirty = true;
            renderStateDirty = true;
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
                case "new:bitmapdata":
                    return CreateBitmapDataObject(arguments);

                case "new:rectangle":
                    return CreateGeometryObject(arguments, "x", "y", "width", "height");

                case "new:point":
                    return CreateGeometryObject(arguments, "x", "y");

                case "new:matrix":
                    return CreateMatrixObject(arguments);

                case "new:colortransform":
                    return CreateGeometryObject(
                        arguments,
                        "redMultiplier", "greenMultiplier", "blueMultiplier", "alphaMultiplier",
                        "redOffset", "greenOffset", "blueOffset", "alphaOffset");

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
                        ReportTimelineControl("audio Sound.stop()");
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

                case "attachbitmap":
                {
                    if (!(receiver is Avm1Object owner) || arguments.Count == 0 ||
                        !(arguments[0] is Avm1Object bitmapObject) ||
                        !dynamicBitmapData.TryGetValue(bitmapObject, out DynamicBitmapData bitmap))
                    {
                        return false;
                    }

                    int depth = ArgumentInt(arguments, 1, 0);
                    bool smoothing = arguments.Count > 3 && Avm1Boolean(arguments[3], false);
                    bitmap.Texture.filterMode = smoothing
                        ? FilterMode.Bilinear
                        : SwfRenderQuality.Settings.BitmapFilter;

                    if (!attachedBitmaps.TryGetValue(owner, out List<AttachedBitmap> attachments))
                    {
                        attachments = new List<AttachedBitmap>();
                        attachedBitmaps[owner] = attachments;
                    }

                    for (int i = attachments.Count - 1; i >= 0; i--)
                        if (attachments[i].Depth == depth) attachments.RemoveAt(i);

                    attachments.Add(new AttachedBitmap { Bitmap = bitmap, Depth = depth });
                    attachments.Sort((left, right) => left.Depth.CompareTo(right.Depth));
                    renderStateDirty = true;
                    return true;
                }

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

                case "getbytesloaded":
                case "getbytestotal":
                    // OpenSWFUnity currently loads a local byte array atomically, so
                    // there is no streaming interval during which these can differ.
                    // Returning the real asset size lets generic Flash preloaders
                    // leave their splash screen without game-specific exceptions.
                    return swfFile?.bytes != null ? (double)swfFile.bytes.LongLength : 0d;

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
                    ReportTimelineControl("audio stopSounds opcode");
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

                        renderStateDirty = true;
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
                    // The legacy ActionGotoFrame opcode is a goto-and-stop action.
                    // ActionGotoFrame2 carries the separate Play flag and is decoded
                    // above as either gotoAndPlay or gotoAndStop. Preserving the old
                    // play state here makes multi-frame scene containers run through
                    // every cave/menu image after selecting a single destination.
                    SetDynamicClipFrame(receiver, arguments, false);
                    return true;

                case "swapdepths":
                    SwapDynamicClipDepth(receiver, arguments);
                    return true;

                case "gotolabel":
                    SetDynamicClipFrame(receiver, arguments, false);
                    return true;
                case "call":
                case "startdrag":
                case "stopdrag":
                    return true;

                default:
                    return null;
            }
        }

        private object HandleAvm2ExternalFunction(string functionName, IReadOnlyList<object> arguments)
        {
            if (string.IsNullOrEmpty(functionName))
                return null;

            if (verboseLogging)
                Debug.Log("[AVM2] External function requested: " + functionName);

            return null;
        }

        private object HandleAvm2ExternalMethod(
            object receiver,
            string functionName,
            IReadOnlyList<object> arguments
        )
        {
            if (string.IsNullOrEmpty(functionName))
                return null;

            if (verboseLogging)
                Debug.Log("[AVM2] External method requested: " + functionName);

            return null;
        }

        private List<PlaceObject2Tag> BuildDisplayListForFrame(
            SwfParser parser,
            List<SwfFrame> frames,
            int frameIndex
        )
        {
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

            Dictionary<ushort, PlaceObject2Tag> activeByDepth =
                new Dictionary<ushort, PlaceObject2Tag>();
            int firstFrameToReplay = 0;

            // Timeline playback normally asks for N immediately after N-1. Start
            // from that immutable snapshot instead of replaying frames 0..N for
            // every new frame. This turns a long sprite timeline from O(n^2) tag
            // parsing into O(n), which removes the periodic first-visit stalls in
            // games containing thousands of nested sprites.
            for (int previous = maxFrame - 1; previous >= 0; previous--)
            {
                if (!framesCache.TryGetValue(previous, out List<PlaceObject2Tag> snapshot))
                    continue;

                for (int i = 0; i < snapshot.Count; i++)
                    activeByDepth[snapshot[i].Depth] = snapshot[i];

                firstFrameToReplay = previous + 1;
                break;
            }

            for (int f = firstFrameToReplay; f <= maxFrame; f++)
            {
                SwfFrame frame = frames[f];

                if (frame != null && frame.ControlTags != null)
                {
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

                List<PlaceObject2Tag> snapshot =
                    new List<PlaceObject2Tag>(activeByDepth.Values);
                snapshot.Sort((a, b) => a.Depth.CompareTo(b.Depth));
                framesCache[f] = snapshot;
            }

            return framesCache[maxFrame];
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
            public bool FrameEnteredPending;
            public bool HasExecutedFrameActions;
            public SwfMatrix TimelineMatrix;
            public bool TimelineVisible;
            public int TimelineStartFrame;
            public long LastSeenTimelineTick;
            public long Serial;
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
            public long Serial;
        }

        private sealed class DynamicBitmapData
        {
            public Avm1Object ScriptObject;
            public Texture2D Texture;
            public Color32[] Pixels;
            public int Width;
            public int Height;
            public bool Transparent;
            public bool Dirty;
            public bool Disposed;
        }

        private sealed class AttachedBitmap
        {
            public DynamicBitmapData Bitmap;
            public int Depth;
        }
    }
}
