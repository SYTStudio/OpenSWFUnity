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

        public static SwfMatrix Identity => new SwfMatrix
        {
            ScaleX = 1f,
            ScaleY = 1f,
            RotateSkew0 = 0f,
            RotateSkew1 = 0f,
            TranslateX = 0f,
            TranslateY = 0f
        };

        public override string ToString()
        {
            return $"Matrix Scale({ScaleX}, {ScaleY}) RotateSkew({RotateSkew0}, {RotateSkew1}) Translate({TranslateX}, {TranslateY})";
        }

        public static SwfMatrix Combine(SwfMatrix parent, SwfMatrix child)
        {
            return new SwfMatrix
            {
                ScaleX = parent.ScaleX * child.ScaleX,
                ScaleY = parent.ScaleY * child.ScaleY,

                RotateSkew0 = parent.RotateSkew0 + child.RotateSkew0,
                RotateSkew1 = parent.RotateSkew1 + child.RotateSkew1,

                TranslateX = parent.TranslateX + child.TranslateX * parent.ScaleX,
                TranslateY = parent.TranslateY + child.TranslateY * parent.ScaleY
            };
        }
    }
}