using UnityEngine;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Renderer;
using OpenSWFUnity.Runtime.Tags;

namespace OpenSWFUnity.Runtime
{
    public class SwfPlayer : MonoBehaviour
    {
        [Header("SWF File")]
        public TextAsset swfFile;

        [Header("Render Options")]
        public bool applyBackgroundColor = true;

        [Tooltip("Draws SWF fills using a software rasterizer with even-odd fill rule.")]
        public bool enableRasterFills = false;

        [Tooltip("Draws parsed SWF ShapeRecords as outline lines.")]
        public bool enableShapeOutlines = true;

        [Tooltip("Draws experimental filled meshes. This is not a full SWF fill renderer yet.")]
        public bool enableShapeFills = false;

        [Tooltip("Uses stencil masking to cut detected hole contours from filled SWF shapes.")]
        public bool enableStencilHoles = false;

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

                if (enableDebugLines || enableShapeOutlines || enableShapeFills || enableRasterFills || enableFillContourDebug)
                {
                    RenderTopLevelDebug(parser);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("Failed to parse SWF: " + e.Message);
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

                if(
    child.name.StartsWith("Shape_") ||
    child.name.StartsWith("Debug_") ||
    child.name.StartsWith("Outline_") ||
    child.name.StartsWith("Fill_") ||
    child.name.StartsWith("RasterFill_") ||
    child.name.StartsWith("FillContour_")
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
                {
                    continue;
                }

                RenderCharacterDebug(
                    parser,
                    renderer,
                    placed.CharacterId,
                    placed.Matrix,
                    "Depth_" + placed.Depth
                );
            }
        }

        private void RenderCharacterDebug(
            SwfParser parser,
            SwfDebugRenderer renderer,
            ushort characterId,
            SwfMatrix worldMatrix,
            string path
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
                        "RasterFill_" + characterId + "_" + path
                    );
                }

                if (enableShapeFills)
                {
                    SwfMeshRenderer meshRenderer = new SwfMeshRenderer(transform);

                    meshRenderer.DrawShapeFill(
                        shape,
                        worldMatrix,
                        "Fill_" + characterId + "_" + path,
                        enableStencilHoles
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
                    path + "_Sprite_" + sprite.SpriteId
                );

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
            string path
        )
        {
            foreach (SwfTag innerTag in sprite.ControlTags)
            {
                if (innerTag.Code == 26) // PlaceObject2
                {
                    try
                    {
                        PlaceObject2Tag innerPlace = parser.ParsePlaceObject2FromTag(innerTag);

                        if (!innerPlace.HasCharacter || innerPlace.CharacterId == 0)
                        {
                            continue;
                        }

                        SwfMatrix combinedMatrix = SwfMatrix.Combine(
                            parentMatrix,
                            innerPlace.Matrix
                        );

                        RenderCharacterDebug(
                            parser,
                            renderer,
                            innerPlace.CharacterId,
                            combinedMatrix,
                            path + "_Depth_" + innerPlace.Depth
                        );
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning("Failed inner PlaceObject2 parse in sprite " + sprite.SpriteId + ": " + e.Message);
                    }
                }
            }
        }
    }
}