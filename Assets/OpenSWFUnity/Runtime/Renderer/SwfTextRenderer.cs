using UnityEngine;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Tags;

namespace OpenSWFUnity.Runtime.Renderer
{
    public class SwfTextRenderer
    {
        private readonly Transform root;

        private const float StageWidth = 600f;
        private const float StageHeight = 400f;
        private const float PixelsPerUnit = 50f;

        public SwfTextRenderer(Transform root)
        {
            this.root = root;
        }

        public void DrawText(
            DefineTextTag text,
            SwfTextRecord record,
            string decodedText,
            SwfMatrix worldMatrix,
            string name,
            float characterSize,
            float baselineDivisor,
            float alpha
        )
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(root, false);

            TextMesh textMesh = go.AddComponent<TextMesh>();
            textMesh.text = decodedText;
            textMesh.fontSize = 64;
            textMesh.characterSize = characterSize;
            textMesh.anchor = TextAnchor.UpperLeft;
            textMesh.alignment = TextAlignment.Left;
            Color c = record.Color;
            c.a *= alpha;
            textMesh.color = c;

            Vector2 textPos = GetTextPosition(text, record, baselineDivisor);

            SwfMatrix combinedMatrix = CombineMatrix(worldMatrix, text.TextMatrix);
            Vector3 unityPos = FlashToUnityPoint(
                textPos.x,
                textPos.y,
                combinedMatrix
            );

            go.transform.localPosition = unityPos;
        }

        private Vector2 GetTextPosition(DefineTextTag text, SwfTextRecord record, float baselineDivisor)
        {
            float x = record.XOffset / 20f;
            float y = record.YOffset / 20f;

            y -= record.TextHeight / baselineDivisor;

            return new Vector2(x, y);
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
                -0.05f
            );
        }

        private SwfMatrix CombineMatrix(SwfMatrix a, SwfMatrix b)
        {
            return new SwfMatrix
            {
                ScaleX = a.ScaleX * b.ScaleX,
                ScaleY = a.ScaleY * b.ScaleY,

                TranslateX = a.TranslateX + b.TranslateX * a.ScaleX,
                TranslateY = a.TranslateY + b.TranslateY * a.ScaleY
            };
        }
    }
}