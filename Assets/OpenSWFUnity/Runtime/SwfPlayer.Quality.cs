using UnityEngine;
using OpenSWFUnity.Runtime.Renderer;

namespace OpenSWFUnity.Runtime
{
    // Applies the inspector's quality level to the shared render settings and keeps
    // the two in step while the movie plays.
    //
    // Quality only ever changes how the frame is drawn. Positions, the timeline,
    // hit testing and every script-visible value are computed from the parsed
    // geometry, which no quality setting touches.
    public partial class SwfPlayer
    {
        private RenderQuality appliedQuality = (RenderQuality)(-1);
        private bool curveDetailCapWarned;

        public SwfQualityLevel ActiveQualityLevel => (SwfQualityLevel)(int)renderQuality;

        // Called before the SWF is parsed, because curve subdivision is consumed
        // while shape records are read and cannot be raised afterwards.
        private void ApplyQualityBeforeLoad()
        {
            SwfRenderQuality.Apply(ActiveQualityLevel);
            appliedQuality = renderQuality;
            curveDetailCapWarned = false;
            ApplyAntiAliasing();

            if (verboseLogging)
                Debug.Log(SwfRenderQuality.Describe());
        }

        // Polled each frame so the level can be changed from the inspector or from
        // script while the movie is running.
        private void SyncQualityIfChanged()
        {
            if (renderQuality == appliedQuality)
                return;

            appliedQuality = renderQuality;

            if (!SwfRenderQuality.Apply(ActiveQualityLevel))
                return;

            ApplyAntiAliasing();

            // Meshes and textures rebuild themselves off the new revision on the next
            // draw; forcing a render here makes the change visible immediately even
            // while the timeline is stopped.
            RenderCurrentFrame();

            Debug.Log("SWF render quality switched to " + SwfRenderQuality.Describe());

            if (SwfRenderQuality.CurveDetailIsCapped && !curveDetailCapWarned)
            {
                curveDetailCapWarned = true;
                Debug.LogWarning(
                    "Curve subdivision is fixed at load: this movie's shapes were flattened at " +
                    SwfRenderQuality.ParsedCurveSubdivisionSteps + " segments per curve, so " +
                    renderQuality + " cannot add detail beyond that without reloading the SWF. " +
                    "Every other quality setting has been applied."
                );
            }
        }

        // Sample count is a project-wide setting in Unity rather than something the
        // batched vector material can carry, so it is driven here.
        private void ApplyAntiAliasing()
        {
            int requested = SwfRenderQuality.Settings.AntiAliasing;

            // Unity accepts 0, 2, 4 or 8 only; anything else is silently ignored,
            // which would make the setting look applied when it was not.
            int samples = requested >= 8 ? 8 : requested >= 4 ? 4 : requested >= 2 ? 2 : 0;

            if (QualitySettings.antiAliasing != samples)
                QualitySettings.antiAliasing = samples;
        }

        // Diagnostics for the current level and what the caches are doing under it.
        public string DescribeRenderQuality()
        {
            string meshes = runtimeRasterRenderer != null
                ? " cachedMeshes=" + runtimeRasterRenderer.MeshCacheCount +
                  " meshBuilds=" + runtimeRasterRenderer.MeshBuildCount
                : string.Empty;

            return SwfRenderQuality.Describe() + meshes;
        }
    }
}
