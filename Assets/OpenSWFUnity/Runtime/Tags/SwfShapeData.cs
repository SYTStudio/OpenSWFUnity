using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfShapeData
    {
        public ushort CharacterId;

        public int FillStyleCount;
        public int LineStyleCount;

        public int NumFillBits;
        public int NumLineBits;

        public List<SwfFillStyle> FillStyles = new List<SwfFillStyle>();
        public List<SwfShapePath> Paths = new List<SwfShapePath>();
        public List<SwfShapeEdge> Edges = new List<SwfShapeEdge>();
        public List<SwfFillEdgeGroup> FillEdgeGroups = new List<SwfFillEdgeGroup>();

        public override string ToString()
        {
            return
                $"ShapeData CharacterId={CharacterId} " +
                $"FillStyles={FillStyleCount} " +
                $"LineStyles={LineStyleCount} " +
                $"NumFillBits={NumFillBits} " +
                $"NumLineBits={NumLineBits} " +
                $"Paths={Paths.Count} " +
                $"Edges={Edges.Count} " +
                $"FillGroups={FillEdgeGroups.Count}";
        }
    }
}