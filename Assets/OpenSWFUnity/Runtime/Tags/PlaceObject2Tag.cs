using OpenSWFUnity.Runtime.Parser;

namespace OpenSWFUnity.Runtime.Tags
{
    public class PlaceObject2Tag
    {
        public bool HasClipActions;
        public bool HasClipDepth;
        public bool HasName;
        public bool HasRatio;
        public bool HasColorTransform;
        public bool HasMatrix;
        public bool HasCharacter;
        public bool Move;

        public ushort Depth;
        public ushort CharacterId;
        public SwfMatrix Matrix;
        public SwfColorTransform ColorTransform = SwfColorTransform.Identity;

        public override string ToString()
        {
            return $"PlaceObject2 Move={Move} Depth={Depth} CharacterId={CharacterId} HasMatrix={HasMatrix} {Matrix}";
        }
    }
}