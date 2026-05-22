using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.Tags
{
    public class DefineFont3Tag
    {
        public ushort FontId;
        public bool HasLayout;
        public bool ShiftJis;
        public bool SmallText;
        public bool Ansi;
        public bool WideOffsets;
        public bool WideCodes;
        public bool Italic;
        public bool Bold;
        public byte LanguageCode;
        public string FontName;

        public int GlyphCount;

        public List<int> CodeTable = new List<int>();

        public override string ToString()
        {
            return
                "DefineFont3 FontId=" + FontId +
                " Name=" + FontName +
                " Glyphs=" + GlyphCount +
                " WideOffsets=" + WideOffsets +
                " WideCodes=" + WideCodes +
                " HasLayout=" + HasLayout;
        }
    }
}