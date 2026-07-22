namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfInitAction
    {
        public ushort SpriteId { get; set; }
        public byte[] ActionBytes { get; set; }
    }

    public class SwfDoAbcBlock
    {
        public uint Flags { get; set; }
        public string Name { get; set; }
        public byte[] AbcData { get; set; }
    }
}
