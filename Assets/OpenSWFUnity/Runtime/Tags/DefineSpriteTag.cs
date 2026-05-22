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