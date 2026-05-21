using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.Tags
{
    public class DefineSpriteTag
    {
        public ushort SpriteId;
        public ushort FrameCount;
        public List<SwfTag> ControlTags = new List<SwfTag>();

        public override string ToString()
        {
            return $"DefineSprite SpriteId={SpriteId} FrameCount={FrameCount} InnerTags={ControlTags.Count}";
        }
    }
}