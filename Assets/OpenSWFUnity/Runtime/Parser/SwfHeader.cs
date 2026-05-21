using UnityEngine;

namespace OpenSWFUnity.Runtime.Parser
{
    [System.Serializable]
    public class SwfHeader
    {
        public string Signature;
        public byte Version;
        public uint FileLength;

        public float StageWidth;
        public float StageHeight;

        public float FrameRate;
        public ushort FrameCount;

        public override string ToString()
        {
            return
                $"SWF Header\n" +
                $"Signature: {Signature}\n" +
                $"Version: {Version}\n" +
                $"FileLength: {FileLength}\n" +
                $"StageWidth: {StageWidth}\n" +
                $"StageHeight: {StageHeight}\n" +
                $"FrameRate: {FrameRate}\n" +
                $"FrameCount: {FrameCount}";
        }
    }
}