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

    // Editable/dynamic text field (tag 37). Even read-only UI labels frequently use
    // this tag so ActionScript can replace their value at runtime.
    public class DefineEditTextTag
    {
        public ushort CharacterId;
        public SwfRect Bounds;
        public ushort Flags;
        public ushort FontId;
        public string FontClass = string.Empty;
        public ushort FontHeight = 240;
        public UnityEngine.Color Color = UnityEngine.Color.black;
        public ushort MaxLength;
        public byte Alignment;
        public ushort LeftMargin;
        public ushort RightMargin;
        public ushort Indent;
        public short Leading;
        public string VariableName = string.Empty;
        public string InitialText = string.Empty;

        public bool HasText => (Flags & 0x8000) != 0;
        public bool HasTextColor => (Flags & 0x0400) != 0;
        public bool HasMaxLength => (Flags & 0x0200) != 0;
        public bool HasFont => (Flags & 0x0100) != 0;
        public bool HasFontClass => (Flags & 0x0080) != 0;
        public bool HasLayout => (Flags & 0x0020) != 0;
        public bool IsMultiline => (Flags & 0x2000) != 0;
    }
}
