using System;

namespace OpenSWFUnity.Runtime.Tags
{
    // DefineFontAlignZones (tag 73). Zone records depend on the glyph count in
    // the referenced font, so the encoded table is retained losslessly for the
    // text rasterizer to consume when hinting support is enabled.
    [Serializable]
    public class SwfFontAlignZones
    {
        public ushort FontId;
        public int CsmTableHint;
        public byte[] EncodedZoneTable = Array.Empty<byte>();
    }

    // CSMTextSettings (tag 74) controls FlashType/grid fitting for a text
    // character. Keeping these values distinct from quality presets prevents low
    // render quality from accidentally changing the authored text metrics.
    [Serializable]
    public class SwfCsmTextSettings
    {
        public ushort TextId;
        public int UseFlashType;
        public int GridFit;
        public float Thickness;
        public float Sharpness;
    }
}
