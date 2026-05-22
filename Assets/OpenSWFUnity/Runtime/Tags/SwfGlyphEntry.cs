namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfGlyphEntry
    {
        public int GlyphIndex;
        public int GlyphAdvance;

        public override string ToString()
        {
            return "Glyph Index=" + GlyphIndex + " Advance=" + GlyphAdvance;
        }
    }
}