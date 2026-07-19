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

        // Bitmap fills store the matrix that maps texture space into shape space,
        // so deriving a UV for a shape-space point needs the inverse.
        public bool TryInvert(out SwfMatrix inverse)
        {
            float determinant = ScaleX * ScaleY - RotateSkew1 * RotateSkew0;

            if (Mathf.Abs(determinant) < 1e-9f)
            {
                inverse = Identity;
                return false;
            }

            float invDet = 1f / determinant;

            inverse = new SwfMatrix
            {
                ScaleX = ScaleY * invDet,
                RotateSkew1 = -RotateSkew1 * invDet,
                RotateSkew0 = -RotateSkew0 * invDet,
                ScaleY = ScaleX * invDet,
                TranslateX = (RotateSkew1 * TranslateY - ScaleY * TranslateX) * invDet,
                TranslateY = (RotateSkew0 * TranslateX - ScaleX * TranslateY) * invDet
            };

            return true;
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
