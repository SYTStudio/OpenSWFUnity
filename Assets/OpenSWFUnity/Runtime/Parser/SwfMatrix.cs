using UnityEngine;

namespace OpenSWFUnity.Runtime.Parser
{
    public struct SwfMatrix
    {
        public float ScaleX;
        public float ScaleY;
        public float RotateSkew0;
        public float RotateSkew1;
        public float TranslateX;
        public float TranslateY;

        public static SwfMatrix Identity
        {
            get
            {
                return new SwfMatrix
                {
                    ScaleX = 1f,
                    ScaleY = 1f,
                    RotateSkew0 = 0f,
                    RotateSkew1 = 0f,
                    TranslateX = 0f,
                    TranslateY = 0f
                };
            }
        }

        public override string ToString()
        {
            return $"Matrix Scale({ScaleX}, {ScaleY}) RotateSkew({RotateSkew0}, {RotateSkew1}) Translate({TranslateX}, {TranslateY})";
        }

        public Vector2 TransformPoint(Vector2 point)
        {
            return new Vector2(
                point.x * ScaleX + point.y * RotateSkew1 + TranslateX,
                point.x * RotateSkew0 + point.y * ScaleY + TranslateY
            );
        }

        public Vector2 TransformPoint(float x, float y)
        {
            return TransformPoint(new Vector2(x, y));
        }

        public static SwfMatrix Combine(SwfMatrix parent, SwfMatrix child)
        {
            // SWF stores a standard affine 2x3 matrix:
            // x' = ScaleX*x + RotateSkew1*y + TranslateX
            // y' = RotateSkew0*x + ScaleY*y + TranslateY
            return new SwfMatrix
            {
                ScaleX =
                    parent.ScaleX * child.ScaleX +
                    parent.RotateSkew1 * child.RotateSkew0,

                RotateSkew0 =
                    parent.RotateSkew0 * child.ScaleX +
                    parent.ScaleY * child.RotateSkew0,

                RotateSkew1 =
                    parent.ScaleX * child.RotateSkew1 +
                    parent.RotateSkew1 * child.ScaleY,

                ScaleY =
                    parent.RotateSkew0 * child.RotateSkew1 +
                    parent.ScaleY * child.ScaleY,

                TranslateX =
                    parent.ScaleX * child.TranslateX +
                    parent.RotateSkew1 * child.TranslateY +
                    parent.TranslateX,

                TranslateY =
                    parent.RotateSkew0 * child.TranslateX +
                    parent.ScaleY * child.TranslateY +
                    parent.TranslateY
            };
        }
    }
}
