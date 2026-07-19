using OpenSWFUnity.Runtime.Parser;
using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.Tags
{
    public class PlaceObject2Tag
    {
        public bool HasClipActions;
        public bool HasClipDepth;
        public bool HasName;
        public string Name;
        public bool HasRatio;
        public bool HasColorTransform;
        public bool HasMatrix;
        public bool HasCharacter;
        public bool Move;
        public bool HasMove;
        public bool HasClassName;
        public bool HasFilterList;
        public bool HasBlendMode;
        public bool HasCacheAsBitmap;
        public bool HasVisible;
        public bool HasOpaqueBackground;
        public ushort Depth;
        public ushort CharacterId;
        public string ClassName;
        public SwfMatrix Matrix;
        public ushort ClipDepth;
        public ushort Ratio;
        public byte BlendMode;
        public byte BitmapCache;
        public byte Visible = 1;
        public SwfColorTransform ColorTransform = SwfColorTransform.Identity;
        public List<SwfClipActionRecord> ClipActions = new List<SwfClipActionRecord>();

        public int TimelineStartFrame;

        public override string ToString()
        {
            return $"PlaceObject2 Move={Move} Depth={Depth} CharacterId={CharacterId} HasMatrix={HasMatrix} {Matrix}";
        }
    }

    public class SwfClipActionRecord
    {
        public uint EventFlags;
        public byte KeyCode;
        public byte[] ActionBytes;

        public bool Matches(uint eventFlag)
        {
            return (EventFlags & eventFlag) != 0;
        }
    }
}
