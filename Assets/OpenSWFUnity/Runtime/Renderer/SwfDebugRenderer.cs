using UnityEngine;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Tags;

namespace OpenSWFUnity.Runtime.Renderer
{
    public class SwfDebugRenderer
    {
        private readonly Transform root;
        private readonly Material lineMaterial;

        private const float StageWidth = 600f;
        private const float StageHeight = 400f;
        private const float PixelsPerUnit = 50f;

        public SwfDebugRenderer(Transform root)
        {
            this.root = root;

            Shader shader = Shader.Find("Sprites/Default");
            lineMaterial = new Material(shader);
        }

        public void DrawShapeBounds(DefineShapeTag shape, SwfMatrix matrix, string name)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(root, false);

            LineRenderer line = go.AddComponent<LineRenderer>();
            line.material = lineMaterial;
            line.positionCount = 5;
            line.loop = false;
            line.useWorldSpace = false;
            line.widthMultiplier = 0.04f;

            float x1 = shape.ShapeBounds.XMinPixels;
            float x2 = shape.ShapeBounds.XMaxPixels;
            float y1 = -shape.ShapeBounds.YMinPixels;
            float y2 = -shape.ShapeBounds.YMaxPixels;

            Vector3 p1 = FlashToUnityPoint(x1, y1, matrix);
            Vector3 p2 = FlashToUnityPoint(x2, y1, matrix);
            Vector3 p3 = FlashToUnityPoint(x2, y2, matrix);
            Vector3 p4 = FlashToUnityPoint(x1, y2, matrix);

            line.SetPosition(0, p1);
            line.SetPosition(1, p2);
            line.SetPosition(2, p3);
            line.SetPosition(3, p4);
            line.SetPosition(4, p1);
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
                0f
            );
        }

        public void DrawFillContours(DefineShapeTag shape, SwfMatrix matrix, string name)
        {
            if (shape == null || shape.ShapeData == null || shape.ShapeData.FillEdgeGroups == null)
                return;

            for (int g = 0; g < shape.ShapeData.FillEdgeGroups.Count; g++)
            {
                SwfFillEdgeGroup group = shape.ShapeData.FillEdgeGroups[g];

                if (group == null || group.Contours == null)
                    continue;

                for (int c = 0; c < group.Contours.Count; c++)
                {
                    SwfFillContour contour = group.Contours[c];

                    if (contour == null || contour.Points == null || contour.Points.Count < 2)
                        continue;

                    GameObject go = new GameObject(name + "_Group_" + g + "_Contour_" + c);
                    go.transform.SetParent(root, false);

                    LineRenderer line = go.AddComponent<LineRenderer>();
                    line.material = lineMaterial;
                    line.positionCount = contour.Points.Count;
                    line.loop = true;
                    line.useWorldSpace = false;
                    line.widthMultiplier = 0.06f;

                    line.sortingLayerName = "Default";
                    line.sortingOrder = 20000;

                    Color contourColor = contour.IsHoleCandidate ? Color.red : Color.yellow;
                    
                    line.startColor = contourColor;
                    line.endColor = contourColor;

                    for (int i = 0; i < contour.Points.Count; i++)
                    {
                        Vector2 point = contour.Points[i];
                        line.SetPosition(i, FlashToUnityPoint(point.x, point.y, matrix));
                    }

                    Debug.Log(
                        "Contour Debug Shape=" + shape.CharacterId +
                        " Group=" + g +
                        " Contour=" + c +
                        " Points=" + contour.Points.Count +
                        " Area=" + contour.Area +
                        " Closed=" + contour.IsClosed
                    );
                }
            }
        }

        public void DrawShapeOutline(DefineShapeTag shape, SwfMatrix matrix, string name)
        {
            if (shape == null || shape.ShapeData == null || shape.ShapeData.Paths == null)
                return;

            for (int i = 0; i < shape.ShapeData.Paths.Count; i++)
            {
                SwfShapePath path = shape.ShapeData.Paths[i];

                if (path == null || path.Points == null || path.Points.Count < 2)
                    continue;

                GameObject go = new GameObject(name + "_Path_" + i);
                go.transform.SetParent(root, false);

                LineRenderer line = go.AddComponent<LineRenderer>();
                line.sortingLayerName = "Default";
                line.sortingOrder = 10000;
                line.material = lineMaterial;
                line.positionCount = path.Points.Count;
                line.loop = false;
                line.useWorldSpace = false;
                line.widthMultiplier = 0.01f;

                if (shape.ShapeData.FillStyles.Count > 0)
                {
                    Color fillColor = Color.red;
                    line.startColor = fillColor;
                    line.endColor = fillColor;
                }
                else
                {
                    line.startColor = Color.white;
                    line.endColor = Color.white;
                }

                for (int p = 0; p < path.Points.Count; p++)
                {
                    Vector2 point = path.Points[p];

                    Vector3 unityPoint = FlashToUnityPoint(
                        point.x,
                        point.y,
                        matrix
                    );

                    line.SetPosition(p, unityPoint);
                }
            }
        }
    }
}