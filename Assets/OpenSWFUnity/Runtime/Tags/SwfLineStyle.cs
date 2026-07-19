using UnityEngine;

namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfLineStyle
    {
        public float Width;
        public Color Color = Color.black;

        // LineStyle2 (DefineShape4) extras.
        public bool HasFillStyle;
        public int FillStyleIndex = -1;
        public bool NoHScale;
        public bool NoVScale;
        public bool PixelHinting;
        public bool NoClose;
        public int StartCapStyle;
        public int EndCapStyle;
        public int JoinStyle;
        public float MiterLimitFactor = 3f;

        public override string ToString()
        {
            return $"LineStyle Width={Width} Color={Color} HasFillStyle={HasFillStyle}";
        }
    }
}
