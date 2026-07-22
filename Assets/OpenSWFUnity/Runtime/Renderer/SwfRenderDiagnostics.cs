using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OpenSWFUnity.Runtime.Renderer
{
    public enum SwfRenderProblem
    {
        TriangulationFailed,
        DisconnectedPath,
        UnsupportedFillStyle,
        InvalidMatrix,
        MissingBitmap,
        DegenerateGeometry
    }

    // Reports rendering faults without flooding the console.
    //
    // A fault in geometry or an asset repeats on every frame the shape is drawn, so
    // reporting it each time would bury everything else. Each distinct (problem,
    // character) pair is logged once with full context and counted thereafter; the
    // totals stay queryable so a failure can never be silently swallowed.
    public static class SwfRenderDiagnostics
    {
        private readonly struct Key
        {
            public readonly SwfRenderProblem Problem;
            public readonly int CharacterId;
            public readonly int Detail;

            public Key(SwfRenderProblem problem, int characterId, int detail)
            {
                Problem = problem;
                CharacterId = characterId;
                Detail = detail;
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (((int)Problem * 397) ^ CharacterId) * 397 ^ Detail;
                }
            }

            public override bool Equals(object obj)
            {
                return obj is Key other &&
                       other.Problem == Problem &&
                       other.CharacterId == CharacterId &&
                       other.Detail == Detail;
            }
        }

        private static readonly Dictionary<Key, int> occurrences = new Dictionary<Key, int>();
        private static readonly Dictionary<SwfRenderProblem, int> totals =
            new Dictionary<SwfRenderProblem, int>();

        public static bool Enabled { get; set; } = true;

        // Where a first-occurrence report goes. Defaults to the Unity console;
        // replaceable so the renderer can be exercised outside the editor, where
        // Debug.LogWarning is unavailable.
        public static System.Action<string> Sink { get; set; } = message => Debug.LogWarning(message);

        public static int TotalFor(SwfRenderProblem problem)
        {
            return totals.TryGetValue(problem, out int count) ? count : 0;
        }

        public static int DistinctCount => occurrences.Count;

        // Cleared when a new movie loads so counts describe the current content.
        public static void Reset()
        {
            occurrences.Clear();
            totals.Clear();
        }

        // `detail` separates variants of the same problem on one character - a fill
        // group index, a bitmap id - so each is reported once rather than only the
        // first being seen.
        public static void Report(
            SwfRenderProblem problem,
            int characterId,
            int detail,
            string message
        )
        {
            totals.TryGetValue(problem, out int total);
            totals[problem] = total + 1;

            if (!Enabled)
                return;

            Key key = new Key(problem, characterId, detail);
            occurrences.TryGetValue(key, out int seen);
            occurrences[key] = seen + 1;

            if (seen > 0)
                return;

            // Reporting must never disturb the pipeline it is observing. Several of
            // these fire from inside the shape parser, whose per-shape try/catch would
            // otherwise treat a failing log sink as a failed shape and discard the
            // geometry - a warning destroying the very thing it was warning about.
            try
            {
                Sink?.Invoke(
                    "[SWF render] " + problem + " on character " + characterId +
                    ": " + message +
                    " (reported once; further occurrences are counted only)");
            }
            catch
            {
                // A sink that cannot log is not a rendering fault; the counts above
                // still record that the problem happened.
            }
        }

        public static string Describe()
        {
            if (totals.Count == 0)
                return "SWF render diagnostics: no problems reported.";

            StringBuilder builder = new StringBuilder("SWF render diagnostics:");

            foreach (KeyValuePair<SwfRenderProblem, int> entry in totals)
                builder.Append(' ').Append(entry.Key).Append('=').Append(entry.Value);

            builder.Append(" (").Append(occurrences.Count).Append(" distinct sites)");
            return builder.ToString();
        }
    }
}
