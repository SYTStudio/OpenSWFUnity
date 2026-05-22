using System.Collections.Generic;
using OpenSWFUnity.Runtime.Parser;

namespace OpenSWFUnity.Runtime.Tags
{
    public class DefineShapeTag
    {
        public ushort CharacterId;
        public SwfRect ShapeBounds;
        public SwfShapeData ShapeData;
        public List<SwfFrame> Frames = new List<SwfFrame>();

        public override string ToString()
        {
            return $"DefineShape CharacterId={CharacterId} Bounds={ShapeBounds} Frames={Frames.Count}";
        }
    }
}