using System.Collections.Generic;
using UnityEngine;

namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfTextRecord
    {
        public bool HasFont;
        public bool HasColor;
        public bool HasYOffset;
        public bool HasXOffset;

        public ushort FontId;
        public Color Color = Color.white;

        public short XOffset;
        public short YOffset;
        public ushort TextHeight;

        public List<SwfGlyphEntry> GlyphEntries = new List<SwfGlyphEntry>();

        public override string ToString()
        {
            return
                "TextRecord FontId=" + FontId +
                " Height=" + TextHeight +
                " X=" + XOffset +
                " Y=" + YOffset +
                " Glyphs=" + GlyphEntries.Count;
        }
    }
}