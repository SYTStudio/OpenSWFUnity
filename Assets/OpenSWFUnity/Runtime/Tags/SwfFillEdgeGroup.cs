using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfFillEdgeGroup
    {
        public int FillStyleIndex;
        public List<SwfShapeEdge> Edges = new List<SwfShapeEdge>();
        public List<SwfFillContour> Contours = new List<SwfFillContour>();

        public override string ToString()
        {
            return
                $"FillEdgeGroup FillStyle={FillStyleIndex} " +
                $"Edges={Edges.Count} " +
                $"Contours={Contours.Count}";
        }
    }
}