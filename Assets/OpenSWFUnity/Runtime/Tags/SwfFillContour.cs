using System.Collections.Generic;
using UnityEngine;

public class SwfFillContour
{
    public int FillStyleIndex;
    public List<Vector2> Points = new List<Vector2>();

    public bool IsHoleCandidate;

    public bool IsClosed
    {
        get
        {
            if (Points == null || Points.Count < 3)
                return false;

            return Vector2.Distance(Points[0], Points[Points.Count - 1]) < 0.5f;
        }
    }

    public float Area
    {
        get
        {
            if (Points == null || Points.Count < 3)
                return 0f;

            float area = 0f;

            for (int i = 0; i < Points.Count; i++)
            {
                Vector2 a = Points[i];
                Vector2 b = Points[(i + 1) % Points.Count];

                area += a.x * b.y - b.x * a.y;
            }

            return area * 0.5f;
        }
    }

    public override string ToString()
    {
        return
            $"FillContour FillStyle={FillStyleIndex} " +
            $"Points={Points.Count} " +
            $"Closed={IsClosed} " +
            $"Area={Area} " +
            $"HoleCandidate={IsHoleCandidate}";
    }
}