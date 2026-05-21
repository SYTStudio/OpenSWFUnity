using UnityEngine;

namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfShapeEdge
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