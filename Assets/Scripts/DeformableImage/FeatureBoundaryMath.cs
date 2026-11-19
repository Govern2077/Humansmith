using System;

namespace DeformableImage
{
    /// <summary>Reduced tool strength near authored eye/lip curves, without pinning their boundaries.</summary>
    public static class FeatureBoundaryMath
    {
        internal const float ManualEyeCornerStrength = 0.05f;

        public static float DistanceToSegment(float px, float py, float ax, float ay, float bx, float by)
        {
            double dx = bx - ax, dy = by - ay, length = dx * dx + dy * dy;
            double t = length < 1e-16 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / length));
            double x = px - ax - dx * t, y = py - ay - dy * t;
            return (float)Math.Sqrt(x * x + y * y);
        }

        public static float StretchStrength(float distanceUv)
        {
            const float core = 0.018f, outer = 0.055f;
            float t = Math.Max(0f, Math.Min(1f, (distanceUv - core) / (outer - core)));
            return 0.7f + 0.3f * t * t * (3f - 2f * t);
        }

        /// <summary>Broad manual grip with zero slope at the centre and support boundary.</summary>
        public static float ManualDragFalloff(float normalizedDistance)
        {
            float t = Math.Max(0f, Math.Min(1f, normalizedDistance));
            return (1f - t) * (1f - t) * (1f + 2f * t);
        }

        /// <summary>Soft endpoint resistance, scaled by the authored feature width.</summary>
        public static float ManualCornerStrength(float distance, float featureWidth, float minimumStrength = 0.3f)
        {
            if (featureWidth <= 0.000001f) return 1f;
            return 1f - (1f - minimumStrength) * ManualDragFalloff(distance / (featureWidth * 0.25f));
        }

        internal static float ManualEyeOppositeTransfer(float distanceFromTip, float distanceFromCut, float cutLength)
        {
            // The opposite lip only follows right beside the shared tip. Use a
            // wider transition farther from the slit, where the skin is continuous.
            float follow = ManualEyeCornerStrength * ManualDragFalloff(distanceFromTip / (cutLength * 0.08f));
            float lipTransfer = follow / ManualCornerStrength(distanceFromTip, cutLength, ManualEyeCornerStrength);
            float skinTransfer = ManualDragFalloff(distanceFromTip / (cutLength * 0.25f));
            float lipInfluence = ManualDragFalloff((distanceFromCut / cutLength - 0.16f) / 0.26f);
            return skinTransfer + (lipTransfer - skinTransfer) * lipInfluence;
        }
    }
}
