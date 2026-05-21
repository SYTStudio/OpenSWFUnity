using System.Collections.Generic;
using UnityEngine;

namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfFillContour
    {
        public int FillStyleIndex;
        public List<Vector2> Points = new List<Vector2>();

        public bool IsClosed
        {
            get
            {
                if (Points == null || Points.Count < 3)
                    return false;

                return Vector2.Distance(Points[0], Points[Points.Count - 1]) < 0.5f;
            }
        }

        public override string ToString()
        {
            return $"FillContour FillStyle={FillStyleIndex} Points={Points.Count} Closed={IsClosed}";
        }
    }
}