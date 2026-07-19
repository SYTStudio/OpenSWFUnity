using System.Collections.Generic;
using UnityEngine;
using OpenSWFUnity.Runtime.Parser;

namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfFillStyle
    {
        public byte FillType;

        public byte R;
        public byte G;
        public byte B;
        public byte A = 255;

        // Gradient fills (FillType 0x10 linear, 0x12 radial, 0x13 focal radial).
        public List<SwfGradientStop> GradientStops;
        public SwfMatrix GradientMatrix = SwfMatrix.Identity;
        public float FocalPoint;

        // Bitmap fills (FillType 0x40-0x43).
        public ushort BitmapId;
        public SwfMatrix BitmapMatrix = SwfMatrix.Identity;
        public bool BitmapSmoothed;
        public bool BitmapClipped;

        public bool IsGradient => FillType == 0x10 || FillType == 0x12 || FillType == 0x13;
        public bool IsBitmap => FillType >= 0x40 && FillType <= 0x43;

        public Color ToUnityColor()
        {
            if (IsGradient && GradientStops != null && GradientStops.Count > 0)
                return AverageGradientColor();

            // Bitmap fills get their colour from the sampled texture; the renderer
            // substitutes white here so the texture passes through untinted. This
            // white is only ever seen if the bitmap failed to decode.
            if (IsBitmap)
                return Color.white;

            return new Color(R / 255f, G / 255f, B / 255f, A / 255f);
        }

        private Color AverageGradientColor()
        {
            // Placeholder until the renderer samples real gradient ramps:
            // weight each stop by the ratio gap it covers so the average
            // is representative of the whole ramp, not just stop count.
            float totalWeight = 0f;
            float r = 0f, g = 0f, b = 0f, a = 0f;
            int previousRatio = 0;

            for (int i = 0; i < GradientStops.Count; i++)
            {
                SwfGradientStop stop = GradientStops[i];
                float weight = Mathf.Max(1, stop.Ratio - previousRatio);
                previousRatio = stop.Ratio;

                r += stop.Color.r * weight;
                g += stop.Color.g * weight;
                b += stop.Color.b * weight;
                a += stop.Color.a * weight;
                totalWeight += weight;
            }

            if (totalWeight <= 0f)
                return GradientStops[0].Color;

            return new Color(r / totalWeight, g / totalWeight, b / totalWeight, a / totalWeight);
        }

        public override string ToString()
        {
            return $"FillStyle Type=0x{FillType:X2} RGBA({R}, {G}, {B}, {A})";
        }
    }
}
