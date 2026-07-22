using System;
using UnityEngine;

namespace OpenSWFUnity.Runtime.Renderer
{
    public enum SwfQualityLevel
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3
    }

    // Concrete rendering parameters for one quality level.
    //
    // Every field below changes measurable work. Profiling the sample content showed
    // triangulation dominating the CPU side at roughly 1.9 ms per fill group, scaling
    // with contour point count, so the geometry levers (curve subdivision at parse
    // time, contour simplification at mesh build) are the ones that actually move
    // frame cost; the rest control memory and visual fidelity.
    public struct SwfQualitySettings
    {
        // Segments per quadratic curve, applied when shape records are parsed.
        public int CurveSubdivisionSteps;

        // Points closer together than this (in Flash pixels) are dropped before
        // triangulation. 0 keeps the geometry exactly as parsed.
        public float ContourSimplifyTolerance;

        // Multiplier on the mesh's on-screen sampling density. Drives how finely a
        // shape is tessellated relative to its size.
        public float RasterizationScale;

        public FilterMode BitmapFilter;
        public int BitmapAnisoLevel;

        // Decoded bitmaps larger than this on either axis are downscaled once, at
        // decode time, and the smaller texture is what the renderer keeps.
        public int MaxTextureSize;

        // Requested MSAA sample count. 0 disables it.
        public int AntiAliasing;

        // Backing font resolution for text. Bigger costs texture memory but keeps
        // glyphs sharp when the stage is scaled up.
        public int TextFontSize;

        // Stencil-based masking is exact but costs an extra draw per mask. Low turns
        // it off and falls back to drawing masked content unclipped.
        public bool StencilMasks;

        // Cached local meshes retained before the least recently used are dropped.
        public int MeshCacheBudget;

        public override string ToString()
        {
            return "curveSteps=" + CurveSubdivisionSteps +
                   " simplify=" + ContourSimplifyTolerance.ToString("0.##") +
                   " rasterScale=" + RasterizationScale.ToString("0.##") +
                   " filter=" + BitmapFilter +
                   " aniso=" + BitmapAnisoLevel +
                   " maxTexture=" + MaxTextureSize +
                   " msaa=" + AntiAliasing +
                   " fontSize=" + TextFontSize +
                   " stencilMasks=" + StencilMasks +
                   " meshCache=" + MeshCacheBudget;
        }
    }

    // The active quality level.
    //
    // Held statically because the parser and the decoded-bitmap cache both need it
    // and neither has a reference to the player. Curve subdivision is consumed while
    // shape records are parsed, so it is fixed for the life of a loaded movie; every
    // other setting is re-read on change and takes effect on the next rendered frame.
    public static class SwfRenderQuality
    {
        public static SwfQualityLevel Level { get; private set; } = SwfQualityLevel.High;
        public static SwfQualitySettings Settings { get; private set; } = Resolve(SwfQualityLevel.High);

        // Incremented whenever the settings change, so caches can compare a stored
        // stamp instead of the whole settings block.
        public static int Revision { get; private set; }

        // The subdivision the currently loaded movie was actually parsed with. Detail
        // beyond it cannot be recovered without re-parsing.
        public static int ParsedCurveSubdivisionSteps { get; private set; }

        public static event Action QualityChanged;

        public static SwfQualitySettings Resolve(SwfQualityLevel level)
        {
            switch (level)
            {
                case SwfQualityLevel.Low:
                    return new SwfQualitySettings
                    {
                        CurveSubdivisionSteps = 3,
                        ContourSimplifyTolerance = 4f,
                        RasterizationScale = 0.5f,
                        BitmapFilter = FilterMode.Point,
                        BitmapAnisoLevel = 0,
                        MaxTextureSize = 256,
                        AntiAliasing = 0,
                        TextFontSize = 24,
                        StencilMasks = false,
                        MeshCacheBudget = 512
                    };

                case SwfQualityLevel.Medium:
                    return new SwfQualitySettings
                    {
                        CurveSubdivisionSteps = 5,
                        ContourSimplifyTolerance = 1.5f,
                        RasterizationScale = 0.75f,
                        BitmapFilter = FilterMode.Bilinear,
                        BitmapAnisoLevel = 1,
                        MaxTextureSize = 512,
                        AntiAliasing = 2,
                        TextFontSize = 48,
                        StencilMasks = true,
                        MeshCacheBudget = 1024
                    };

                case SwfQualityLevel.Ultra:
                    return new SwfQualitySettings
                    {
                        CurveSubdivisionSteps = 14,
                        ContourSimplifyTolerance = 0f,
                        RasterizationScale = 2f,
                        BitmapFilter = FilterMode.Trilinear,
                        BitmapAnisoLevel = 8,
                        MaxTextureSize = 4096,
                        AntiAliasing = 8,
                        TextFontSize = 96,
                        StencilMasks = true,
                        MeshCacheBudget = 8192
                    };

                // High is the reference level and matches what the renderer produced
                // before quality was configurable, so upgrading a project changes
                // nothing until the level is moved.
                default:
                    return new SwfQualitySettings
                    {
                        CurveSubdivisionSteps = 8,
                        ContourSimplifyTolerance = 0.5f,
                        RasterizationScale = 1f,
                        BitmapFilter = FilterMode.Bilinear,
                        BitmapAnisoLevel = 2,
                        MaxTextureSize = 2048,
                        AntiAliasing = 4,
                        TextFontSize = 64,
                        StencilMasks = true,
                        MeshCacheBudget = 4096
                    };
            }
        }

        public static bool Apply(SwfQualityLevel level)
        {
            if (level == Level && Revision > 0)
                return false;

            Level = level;
            Settings = Resolve(level);
            Revision++;
            QualityChanged?.Invoke();
            return true;
        }

        // Called by the parser as it starts reading shapes, recording the fidelity the
        // geometry is actually built at.
        public static void MarkParsed()
        {
            ParsedCurveSubdivisionSteps = Settings.CurveSubdivisionSteps;
        }

        // True when the active level asks for finer curves than the loaded movie was
        // parsed with. The extra detail needs a reload; everything else still applies.
        public static bool CurveDetailIsCapped =>
            ParsedCurveSubdivisionSteps > 0 &&
            Settings.CurveSubdivisionSteps > ParsedCurveSubdivisionSteps;

        public static string Describe()
        {
            return "SWF render quality " + Level + " (" + Settings + ")" +
                   (CurveDetailIsCapped
                       ? " [curves parsed at " + ParsedCurveSubdivisionSteps +
                         " steps; reload to gain more]"
                       : string.Empty);
        }
    }
}
