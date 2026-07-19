using OpenSWFUnity.Runtime.Parser;

namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfButtonRecord
    {
        public bool StateHitTest;
        public bool StateDown;
        public bool StateOver;
        public bool StateUp;
        public bool HasFilterList;
        public bool HasBlendMode;

        public ushort CharacterId;
        public ushort PlaceDepth;
        public SwfMatrix Matrix = SwfMatrix.Identity;
        public SwfColorTransform ColorTransform = SwfColorTransform.Identity;
        public byte BlendMode;
    }
}
