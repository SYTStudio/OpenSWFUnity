using System;
using System.Text;

namespace OpenSWFUnity.Runtime.AVM2.Abc
{
    // Bounds-checked cursor over ABC bytes.
    //
    // Every primitive in the format is variable width, so a truncated file shows up
    // as a read running off the end rather than as an obviously wrong value. Each
    // accessor validates before it consumes and throws AbcFormatException, which the
    // parser turns into a diagnostic instead of letting an IndexOutOfRangeException
    // escape into Unity.
    public sealed class AbcReader
    {
        private readonly byte[] data;
        private int position;

        public AbcReader(byte[] data)
        {
            this.data = data ?? throw new ArgumentNullException(nameof(data));
            position = 0;
        }

        public int Position => position;
        public int Length => data.Length;
        public int Remaining => data.Length - position;
        public bool HasMore => position < data.Length;

        public byte ReadU8()
        {
            Require(1);
            return data[position++];
        }

        public ushort ReadU16()
        {
            Require(2);
            ushort value = (ushort)(data[position] | (data[position + 1] << 8));
            position += 2;
            return value;
        }

        // Branch offsets are signed 24-bit little-endian.
        public int ReadS24()
        {
            Require(3);
            int value = data[position] | (data[position + 1] << 8) | (data[position + 2] << 16);
            position += 3;

            if ((value & 0x800000) != 0)
                value |= unchecked((int)0xFF000000);

            return value;
        }

        // Variable-length integer: seven bits per byte, high bit continues. The format
        // permits at most five bytes; a sixth means the stream is not ABC data.
        public uint ReadU32()
        {
            uint value = 0;
            int shift = 0;

            for (int i = 0; i < 5; i++)
            {
                Require(1);
                byte current = data[position++];
                value |= (uint)(current & 0x7F) << shift;

                if ((current & 0x80) == 0)
                    return value;

                shift += 7;
            }

            throw new AbcFormatException("Variable-length integer exceeds five bytes", position);
        }

        public int ReadS32()
        {
            return unchecked((int)ReadU32());
        }

        // u30 is a u32 constrained to 30 bits. Indices and counts use it, so rejecting
        // oversized values here stops absurd magnitudes before they reach a caller
        // that would size a collection from them.
        public uint ReadU30()
        {
            int start = position;
            uint value = ReadU32();

            if (value > 0x3FFFFFFF)
                throw new AbcFormatException("u30 value " + value + " exceeds 30 bits", start);

            return value;
        }

        public int ReadIndex()
        {
            return (int)ReadU30();
        }

        public double ReadD64()
        {
            Require(8);
            double value = BitConverter.ToDouble(data, position);
            position += 8;
            return value;
        }

        public string ReadString()
        {
            int start = position;
            uint length = ReadU30();

            if (length > AbcLimits.MaxStringLength)
                throw new AbcFormatException("String length " + length + " is implausible", start);

            Require((int)length);
            string value = Encoding.UTF8.GetString(data, position, (int)length);
            position += (int)length;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            Require(count);
            byte[] value = new byte[count];
            Array.Copy(data, position, value, 0, count);
            position += count;
            return value;
        }

        public void Skip(int count)
        {
            Require(count);
            position += count;
        }

        // Validates an entry count before the caller allocates for it. A count is only
        // credible if the remaining bytes could hold that many entries at the smallest
        // possible size, which rejects the "one billion methods" case outright.
        public int ReadEntryCount(string what, int hardLimit, int minimumBytesPerEntry = AbcLimits.MinimumBytesPerEntry)
        {
            int start = position;
            uint count = ReadU30();

            if (count > hardLimit)
            {
                throw new AbcFormatException(
                    what + " count " + count + " exceeds the supported maximum of " + hardLimit,
                    start
                );
            }

            long required = (long)count * Math.Max(1, minimumBytesPerEntry);

            if (required > Remaining)
            {
                throw new AbcFormatException(
                    what + " count " + count + " needs at least " + required +
                    " bytes but only " + Remaining + " remain",
                    start
                );
            }

            return (int)count;
        }

        private void Require(int count)
        {
            if (count < 0)
                throw new AbcFormatException("Negative read length " + count, position);

            if (position + count > data.Length)
            {
                throw new AbcFormatException(
                    "Unexpected end of ABC data: needed " + count +
                    " bytes but only " + Remaining + " remain",
                    position
                );
            }
        }
    }
}
