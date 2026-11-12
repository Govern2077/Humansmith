using System;

namespace DeformableImage
{
    /// <summary>Reduced tool strength near authored eye/lip curves, without pinning their boundaries.</summary>
    public static class FeatureBoundaryMath
    {
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
    }
}
