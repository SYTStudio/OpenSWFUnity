namespace OpenSWFUnity.Runtime.Tags
{
    public class DefineSoundTag
    {
        public ushort SoundId;
        public byte SoundFormat;
        public int SampleRate;
        public bool Is16Bit;
        public bool IsStereo;
        public uint SampleCount;
        public short Mp3SeekSamples;
        public byte[] SoundData;

        public bool IsMp3 => SoundFormat == 2;

        public override string ToString()
        {
            return
                "DefineSound SoundId=" + SoundId +
                " Format=" + SoundFormat +
                " Rate=" + SampleRate +
                " Stereo=" + IsStereo +
                " Samples=" + SampleCount +
                " Bytes=" + (SoundData != null ? SoundData.Length : 0);
        }
    }
}
