using System.Collections.Generic;
using UnityEngine;

namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfShapePath
    {
        public List<Vector2> Points = new List<Vector2>();

        public int FillStyle0;
        public int FillStyle1;
        public int LineStyle;

        public int SegmentCount
        {
            get
            {
                if (Points == null || Points.Count < 2)
                    return 0;

                return Points.Count - 1;
            }
        }

        public bool HasFill
        {
            get
            {
                return FillStyle0 > 0 || FillStyle1 > 0;
            }
        }

        public override string ToString()
        {
            return
                $"ShapePath Points={Points.Count} " +
                $"Segments={SegmentCount} " +
                $"Fill0={FillStyle0} " +
                $"Fill1={FillStyle1} " +
                $"Line={LineStyle}";
        }
    }
}