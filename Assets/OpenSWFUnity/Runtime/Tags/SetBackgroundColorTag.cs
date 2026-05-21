using UnityEngine;

namespace OpenSWFUnity.Runtime.Tags
{
    public class SetBackgroundColorTag
    {
        public byte R;
        public byte G;
        public byte B;

        public Color ToUnityColor()
        {
            return new Color(R / 255f, G / 255f, B / 255f, 1f);
        }

        public override string ToString()
        {
            return $"SetBackgroundColor RGB({R}, {G}, {B})";
        }
    }
}