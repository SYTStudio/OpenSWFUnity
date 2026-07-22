namespace OpenSWFUnity.Runtime.Audio
{
    // SWF sound compression formats, as encoded in the four-bit format field of
    // DefineSound and SoundStreamHead.
    public enum SwfSoundFormat
    {
        UncompressedNativeEndian = 0,
        Adpcm = 1,
        Mp3 = 2,
        UncompressedLittleEndian = 3,
        Nellymoser16 = 4,
        Nellymoser8 = 5,
        Nellymoser = 6,
        Speex = 11
    }

    public static class SwfSoundFormats
    {
        public static string Describe(int format)
        {
            switch ((SwfSoundFormat)format)
            {
                case SwfSoundFormat.UncompressedNativeEndian: return "Uncompressed (native endian)";
                case SwfSoundFormat.Adpcm: return "ADPCM";
                case SwfSoundFormat.Mp3: return "MP3";
                case SwfSoundFormat.UncompressedLittleEndian: return "Uncompressed (little endian)";
                case SwfSoundFormat.Nellymoser16: return "Nellymoser 16 kHz";
                case SwfSoundFormat.Nellymoser8: return "Nellymoser 8 kHz";
                case SwfSoundFormat.Nellymoser: return "Nellymoser";
                case SwfSoundFormat.Speex: return "Speex";
                default: return "Unknown format " + format;
            }
        }

        // Decoded here, in managed code, without any external dependency.
        public static bool IsDecodedInternally(int format)
        {
            switch ((SwfSoundFormat)format)
            {
                case SwfSoundFormat.UncompressedNativeEndian:
                case SwfSoundFormat.UncompressedLittleEndian:
                case SwfSoundFormat.Adpcm:
                    return true;
                default:
                    return false;
            }
        }

        // Handed to Unity's own decoder rather than decoded here.
        public static bool IsDecodedByUnity(int format)
        {
            return (SwfSoundFormat)format == SwfSoundFormat.Mp3;
        }

        public static bool IsSupported(int format)
        {
            return IsDecodedInternally(format) || IsDecodedByUnity(format);
        }
    }
}
