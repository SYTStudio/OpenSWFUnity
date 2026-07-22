using System;
using OpenSWFUnity.Runtime.Tags;

namespace OpenSWFUnity.Runtime.Audio
{
    // Result of decoding one sound asset into interleaved float samples.
    public sealed class SwfDecodedSound
    {
        public float[] Samples;
        public int Channels;
        public int SampleRate;

        // Set when decoding could not produce samples; the caller reports it rather
        // than substituting silence.
        public string Failure;

        public bool HasSamples => Samples != null && Samples.Length > 0;
        public int FrameCount => HasSamples && Channels > 0 ? Samples.Length / Channels : 0;

        public float DurationSeconds =>
            SampleRate > 0 ? (float)FrameCount / SampleRate : 0f;
    }

    // Turns SWF sound payloads into PCM.
    //
    // Only the formats that can be decoded correctly in managed code are handled
    // here; MP3 is routed to Unity's decoder by the runtime, and the codecs that
    // need a third-party library are reported by name rather than silently produing
    // silence.
    public static class SwfSoundDecoder
    {
        public static SwfDecodedSound Decode(DefineSoundTag sound)
        {
            if (sound == null)
                return new SwfDecodedSound { Failure = "sound tag is null" };

            return Decode(
                sound.SoundData,
                sound.SoundFormat,
                sound.SampleRate,
                sound.Is16Bit,
                sound.IsStereo);
        }

        public static SwfDecodedSound Decode(
            byte[] data,
            int format,
            int sampleRate,
            bool is16Bit,
            bool stereo
        )
        {
            SwfDecodedSound result = new SwfDecodedSound
            {
                Channels = stereo ? 2 : 1,
                SampleRate = sampleRate > 0 ? sampleRate : 44100
            };

            if (data == null || data.Length == 0)
            {
                result.Failure = "sound payload is empty";
                return result;
            }

            switch ((SwfSoundFormat)format)
            {
                case SwfSoundFormat.UncompressedNativeEndian:
                case SwfSoundFormat.UncompressedLittleEndian:
                    result.Samples = DecodePcm(data, is16Bit, out string pcmFailure);
                    result.Failure = pcmFailure;
                    return result;

                case SwfSoundFormat.Adpcm:
                    result.Samples = SwfAdpcmDecoder.Decode(data, stereo, out string adpcmFailure);
                    result.Failure = adpcmFailure;
                    return result;

                case SwfSoundFormat.Mp3:
                    // Handled by the runtime through Unity's decoder, which needs a
                    // coroutine and cannot run synchronously here.
                    result.Failure = "MP3 is decoded by Unity, not by this decoder";
                    return result;

                default:
                    result.Failure =
                        SwfSoundFormats.Describe(format) +
                        " is not supported; no decoder for it is available in this build";
                    return result;
            }
        }

        // SWF stores uncompressed audio as either unsigned 8-bit or signed 16-bit
        // little-endian. Format 0 is nominally "native endian", but every SWF in
        // practice is little-endian, and treating it as such is what other players do.
        private static float[] DecodePcm(byte[] data, bool is16Bit, out string failure)
        {
            failure = null;

            if (!is16Bit)
            {
                float[] samples8 = new float[data.Length];

                for (int i = 0; i < data.Length; i++)
                    samples8[i] = (data[i] - 128) / 128f;

                return samples8;
            }

            int count = data.Length / 2;

            if (count == 0)
            {
                failure = "16-bit PCM payload is shorter than one sample";
                return null;
            }

            float[] samples16 = new float[count];

            for (int i = 0; i < count; i++)
            {
                short value = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
                samples16[i] = value / 32768f;
            }

            return samples16;
        }
    }
}
