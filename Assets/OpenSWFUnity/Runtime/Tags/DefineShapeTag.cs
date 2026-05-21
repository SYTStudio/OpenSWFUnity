using OpenSWFUnity.Runtime.Parser;

namespace OpenSWFUnity.Runtime.Tags
{
    public class DefineShapeTag
    {
        public ushort CharacterId;
        public SwfRect ShapeBounds;
        public SwfShapeData ShapeData;

        public override string ToString()
        {
            return $"DefineShape CharacterId={CharacterId} Bounds={ShapeBounds}";
        }
    }
}