using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.Tags
{
    public class DefineSpriteTag
    {
        public ushort SpriteId;
        public ushort FrameCount;

        public List<SwfTag> ControlTags = new List<SwfTag>();

        // New timeline frames.
        public List<SwfFrame> Frames = new List<SwfFrame>();

        // Streaming audio belongs to the sprite timeline that declares it. Large
        // games put narration/music inside nested sprites rather than on _root.
        public SwfSoundStreamHead SoundStreamHead;
        public List<SwfSoundStreamBlock> SoundStreamBlocks =
            new List<SwfSoundStreamBlock>();

        public override string ToString()
        {
            return
                "DefineSprite SpriteId=" + SpriteId +
                " FrameCount=" + FrameCount +
                " ControlTags=" + ControlTags.Count +
                " Frames=" + Frames.Count;
        }
    }
}
