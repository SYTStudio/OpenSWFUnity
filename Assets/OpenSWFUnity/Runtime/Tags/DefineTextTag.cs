using System.Collections.Generic;
using OpenSWFUnity.Runtime.Parser;

namespace OpenSWFUnity.Runtime.Tags
{
    public class DefineTextTag
    {
        public ushort CharacterId;
        public SwfRect TextBounds;
        public SwfMatrix TextMatrix;

        public byte GlyphBits;
        public byte AdvanceBits;

        public List<SwfTextRecord> Records = new List<SwfTextRecord>();

        public override string ToString()
        {
            return
                "DefineText CharacterId=" + CharacterId +
                " Bounds=" + TextBounds +
                " GlyphBits=" + GlyphBits +
                " AdvanceBits=" + AdvanceBits +
                " Records=" + Records.Count;
        }
    }
}