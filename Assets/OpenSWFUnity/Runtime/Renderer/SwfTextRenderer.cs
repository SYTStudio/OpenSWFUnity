using System.Collections.Generic;
using UnityEngine;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Tags;

namespace OpenSWFUnity.Runtime.Renderer
{
    public class SwfTextRenderer
    {
        private readonly Transform poolRoot;
        private readonly float stageWidth;
        private readonly float stageHeight;
        private readonly List<TextInstance> instances = new List<TextInstance>();
        private int usedInstanceCount;

        private const float PixelsPerUnit = 50f;

        public SwfTextRenderer(Transform root, float stageWidth = 600f, float stageHeight = 400f)
        {
            this.stageWidth = stageWidth;
            this.stageHeight = stageHeight;

            Transform existingPool = root.Find("__SWF_TextPool");

            if (existingPool != null)
            {
                poolRoot = existingPool;
            }
            else
            {
                GameObject poolObject = new GameObject("__SWF_TextPool");
                poolObject.transform.SetParent(root, false);
                poolRoot = poolObject.transform;
            }
        }

        public void BeginFrame()
        {
            usedInstanceCount = 0;
        }

        public void EndFrame()
        {
            for (int i = usedInstanceCount; i < instances.Count; i++)
            {
                if (instances[i].GameObject.activeSelf)
                    instances[i].GameObject.SetActive(false);
            }
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
            TextInstance instance = AcquireInstance(name);
            GameObject go = instance.GameObject;
            TextMesh textMesh = instance.TextMesh;
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

            if (!IsFinite(unityPos))
            {
                go.SetActive(false);
                return;
            }

            go.transform.localPosition = unityPos;
        }

        private TextInstance AcquireInstance(string instanceName)
        {
            TextInstance instance;

            if (usedInstanceCount < instances.Count)
            {
                instance = instances[usedInstanceCount];
            }
            else
            {
                GameObject go = new GameObject(instanceName);
                go.transform.SetParent(poolRoot, false);
                instance = new TextInstance
                {
                    GameObject = go,
                    TextMesh = go.AddComponent<TextMesh>()
                };
                instances.Add(instance);
            }

            usedInstanceCount++;
            instance.GameObject.name = instanceName;

            if (!instance.GameObject.activeSelf)
                instance.GameObject.SetActive(true);

            return instance;
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
            Vector2 flashPoint = matrix.TransformPoint(x, y);

            float unityX = flashPoint.x - stageWidth / 2f;
            float unityY = stageHeight / 2f - flashPoint.y;

            return new Vector3(
                unityX / PixelsPerUnit,
                unityY / PixelsPerUnit,
                -0.05f
            );
        }

        private SwfMatrix CombineMatrix(SwfMatrix a, SwfMatrix b)
        {
            return SwfMatrix.Combine(a, b);
        }

        private static bool IsFinite(Vector3 value)
        {
            return
                !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private sealed class TextInstance
        {
            public GameObject GameObject;
            public TextMesh TextMesh;
        }
    }
}
