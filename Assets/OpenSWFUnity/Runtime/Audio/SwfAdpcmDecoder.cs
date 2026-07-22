using System;

namespace OpenSWFUnity.Runtime.Audio
{
    // Flash's ADPCM variant.
    //
    // It is IMA ADPCM with two differences: the sample width is variable (2 to 5
    // bits, chosen by a two-bit header) and the stream is cut into packets of 4096
    // samples, each restarting from an explicit 16-bit sample and 6-bit step index.
    // That packet structure is what allows seeking, and it is why decoding cannot
    // simply run the IMA state machine from the first byte to the last.
    public static class SwfAdpcmDecoder
    {
        private static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
            50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
            253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
            1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
            3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442,
            11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
            32767
        };

        // Index adjustment per magnitude, one table per sample width. The tables grow
        // with the width because a wider code can express a larger jump in step size.
        private static readonly int[] IndexTable2 = { -1, 2 };
        private static readonly int[] IndexTable3 = { -1, -1, 2, 4 };
        private static readonly int[] IndexTable4 = { -1, -1, -1, -1, 2, 4, 6, 8 };
        private static readonly int[] IndexTable5 =
            { -1, -1, -1, -1, -1, -1, -1, -1, 1, 2, 4, 6, 8, 10, 13, 16 };

        private const int SamplesPerPacket = 4096;

        // Returns interleaved samples in [-1, 1], or null when the data is malformed.
        public static float[] Decode(byte[] data, bool stereo, out string failure)
        {
            failure = null;

            if (data == null || data.Length < 2)
            {
                failure = "ADPCM data is empty or too short to contain a header";
                return null;
            }

            BitReader reader = new BitReader(data);

            try
            {
                int codeSize = (int)reader.Read(2);
                int bits = codeSize + 2;

                int[] indexTable = bits switch
                {
                    2 => IndexTable2,
                    3 => IndexTable3,
                    4 => IndexTable4,
                    _ => IndexTable5
                };

                int channels = stereo ? 2 : 1;

                // The sample count is not stored, so capacity is estimated from the
                // available bits and the buffer is trimmed once decoding finishes.
                int estimated = Math.Max(SamplesPerPacket, (data.Length * 8) / Math.Max(1, bits));
                float[] output = new float[(estimated + SamplesPerPacket) * channels];
                int written = 0;

                int[] sample = new int[channels];
                int[] index = new int[channels];

                while (reader.HasBits(16 + 6))
                {
                    // Every packet restarts the predictor from stored state.
                    for (int c = 0; c < channels; c++)
                    {
                        sample[c] = SignExtend16((int)reader.Read(16));
                        index[c] = (int)reader.Read(6);

                        if (index[c] > 88)
                            index[c] = 88;
                    }

                    written = Append(written, sample, channels, ref output);

                    for (int i = 1; i < SamplesPerPacket; i++)
                    {
                        if (!reader.HasBits(bits * channels))
                            break;

                        for (int c = 0; c < channels; c++)
                        {
                            int code = (int)reader.Read(bits);
                            DecodeSample(code, bits, indexTable, ref sample[c], ref index[c]);
                        }

                        written = Append(written, sample, channels, ref output);
                    }
                }

                if (written == 0)
                {
                    failure = "ADPCM stream contained no decodable packets";
                    return null;
                }

                if (written == output.Length)
                    return output;

                float[] trimmed = new float[written];
                Array.Copy(output, trimmed, written);
                return trimmed;
            }
            catch (Exception exception)
            {
                failure = "ADPCM decode failed: " + exception.Message;
                return null;
            }
        }

        // Grows the buffer geometrically rather than per sample, so a long stream
        // costs a handful of reallocations instead of one per frame of audio.
        private static int Append(int written, int[] sample, int channels, ref float[] target)
        {
            if (written + channels > target.Length)
            {
                float[] grown = new float[Math.Max(target.Length * 2, written + channels)];
                Array.Copy(target, grown, written);
                target = grown;
            }

            for (int c = 0; c < channels; c++)
                target[written + c] = sample[c] / 32768f;

            return written + channels;
        }

        // Standard IMA reconstruction generalised to a variable code width: the
        // magnitude bits contribute step/2, step/4, ... in descending order, on top of
        // a base of step >> (bits-1).
        private static void DecodeSample(int code, int bits, int[] indexTable, ref int sample, ref int index)
        {
            int signMask = 1 << (bits - 1);
            int magnitude = code & (signMask - 1);
            int step = StepTable[index];
            int difference = step >> (bits - 1);

            for (int bit = bits - 2; bit >= 0; bit--)
            {
                if ((magnitude & (1 << bit)) != 0)
                    difference += step >> ((bits - 2) - bit);
            }

            sample += (code & signMask) != 0 ? -difference : difference;

            if (sample > 32767)
                sample = 32767;
            else if (sample < -32768)
                sample = -32768;

            index += indexTable[magnitude];

            if (index < 0)
                index = 0;
            else if (index > 88)
                index = 88;
        }

        private static int SignExtend16(int value)
        {
            return (value & 0x8000) != 0 ? value - 0x10000 : value;
        }

        // Most-significant-bit-first reader, which is how SWF packs bit fields.
        private struct BitReader
        {
            private readonly byte[] data;
            private int bytePosition;
            private int bitPosition;

            public BitReader(byte[] data)
            {
                this.data = data;
                bytePosition = 0;
                bitPosition = 0;
            }

            public bool HasBits(int count)
            {
                long remaining = ((long)data.Length - bytePosition) * 8 - bitPosition;
                return remaining >= count;
            }

            public uint Read(int count)
            {
                uint value = 0;

                for (int i = 0; i < count; i++)
                {
                    if (bytePosition >= data.Length)
                        throw new IndexOutOfRangeException("ADPCM stream ended mid-sample");

                    int bit = (data[bytePosition] >> (7 - bitPosition)) & 1;
                    value = (value << 1) | (uint)bit;

                    if (++bitPosition == 8)
                    {
                        bitPosition = 0;
                        bytePosition++;
                    }
                }

                return value;
            }
        }
    }
}
