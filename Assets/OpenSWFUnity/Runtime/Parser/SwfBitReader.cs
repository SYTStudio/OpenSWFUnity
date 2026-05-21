using System;

namespace OpenSWFUnity.Runtime.Parser
{
    public class SwfBitReader
    {
        private readonly byte[] data;
        private int bytePosition;
        private int bitPosition;

        public int BytePosition => bytePosition;

        public SwfBitReader(byte[] data, int startOffset = 0)
        {
            this.data = data;
            bytePosition = startOffset;
            bitPosition = 0;
        }

        public void AlignToByte()
        {
            if (bitPosition != 0)
            {
                bitPosition = 0;
                bytePosition++;
            }
        }

        public uint ReadUBits(int count)
        {
            uint value = 0;

            for (int i = 0; i < count; i++)
            {
                if (bytePosition >= data.Length)
                    throw new IndexOutOfRangeException("Read past end of SWF data.");

                int bit = (data[bytePosition] >> (7 - bitPosition)) & 1;

                value = (value << 1) | (uint)bit;

                bitPosition++;

                if (bitPosition == 8)
                {
                    bitPosition = 0;
                    bytePosition++;
                }
            }

            return value;
        }

        public int ReadSBits(int count)
        {
            uint value = ReadUBits(count);

            bool isNegative = (value & (1u << (count - 1))) != 0;

            if (isNegative)
            {
                uint mask = uint.MaxValue << count;
                value |= mask;
            }

            return unchecked((int)value);
        }

        public byte ReadByte()
        {
            AlignToByte();

            if (bytePosition >= data.Length)
                throw new IndexOutOfRangeException("Read past end of SWF data.");
            return data[bytePosition++];
        }

        public ushort ReadUInt16LE()
        {
            AlignToByte();

            if (bytePosition + 1 >= data.Length)
                throw new IndexOutOfRangeException("Read past end of SWF data.");

            ushort value = (ushort)(data[bytePosition] | (data[bytePosition + 1] << 8));
            bytePosition += 2;
            return value;
        }
    }
}