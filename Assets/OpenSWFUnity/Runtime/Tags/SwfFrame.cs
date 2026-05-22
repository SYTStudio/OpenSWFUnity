using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfFrame
    {
        public int FrameIndex;
        public List<SwfTag> ControlTags = new List<SwfTag>();

        public override string ToString()
        {
            return "Frame " + FrameIndex + " Tags=" + ControlTags.Count;
        }
    }
}