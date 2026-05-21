using UnityEngine;

namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfFillStyle
    {
        public byte FillType;

        public byte R;
        public byte G;
        public byte B;
        public byte A = 255;

        public Color ToUnityColor()
        {
            return new Color(R / 255f, G / 255f, B / 255f, A / 255f);
        }

        public override string ToString()
        {
            return $"FillStyle Type=0x{FillType:X2} RGBA({R}, {G}, {B}, {A})";
        }
    }
}