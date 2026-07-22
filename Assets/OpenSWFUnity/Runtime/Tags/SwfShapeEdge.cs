using UnityEngine;

namespace OpenSWFUnity.Runtime.Tags
{
    // One flattened edge of a shape outline.
    //
    // A struct rather than a class because a large movie produces millions of these:
    // profiling a 36 MB SWF measured 4 GB of allocation during parsing, dominated by
    // per-edge heap objects. Stored in a List<SwfShapeEdge> they occupy one
    // contiguous array with no per-edge header and nothing for the collector to
    // trace. Code that needs to identify a particular edge uses its index rather
    // than its reference.
    public struct SwfShapeEdge
    {
        public Vector2 Start;
        public Vector2 End;

        public int FillStyle0;
        public int FillStyle1;
        public int LineStyle;

        public override string ToString()
        {
            return
                $"ShapeEdge Start={Start} End={End} " +
                $"Fill0={FillStyle0} Fill1={FillStyle1} Line={LineStyle}";
        }
    }
}