using UnityEngine;

namespace DeformableImage
{
    /// <summary>One manual gesture on the lower lip of a configured eye incision.</summary>
    internal sealed class LowerEyelidDrag
    {
        private readonly Vector2[] path;
        private readonly float[] arcLengths;
        private readonly float cutRadius;
        private readonly float stripWidth;
        private Vector2 grabDown;
        private float threshold;
        private LipVertex[] lipVertices;

        private struct LipVertex
        {
            public Vector2 Down;
            public float HingeDistance;
            public float FoldWeight;
        }

        public LowerEyelidDrag(Vector2[] path, float cutRadius, float stripWidth)
        {
            this.path = path;
            this.cutRadius = cutRadius;
            this.stripWidth = stripWidth;
            arcLengths = new float[path.Length];
            for (int i = 1; i < path.Length; i++)
                arcLengths[i] = arcLengths[i - 1] + Vector2.Distance(path[i - 1], path[i]);
        }

        public bool TryBegin(Vector2 restPoint, float edgeThreshold, out float distance)
        {
            distance = float.PositiveInfinity;
            if (!TryGetLowerLip(restPoint, out Vector2 down, out float fromLip, out _)) return false;
            grabDown = down;
            distance = fromLip;
            threshold = edgeThreshold + Mathf.SmoothStep(0f, stripWidth * 0.8f, fromLip / stripWidth);
            return true;
        }

        public void BindVertices(Vector3[] restVertices, float[] brushDistances)
        {
            lipVertices = new LipVertex[restVertices.Length];
            for (int i = 0; i < restVertices.Length; i++)
            {
                if (float.IsPositiveInfinity(brushDistances[i])
                    || !TryGetLowerLip(restVertices[i], out Vector2 down, out float distance, out float along))
                    continue;
                float cornerFade = Mathf.SmoothStep(0f, 1f, Mathf.Min(along, 1f - along) / 0.2f);
                float hingeFade = 1f - Mathf.SmoothStep(0f, 1f, (distance / stripWidth - 0.45f) / 0.55f);
                lipVertices[i] = new LipVertex
                {
                    Down = down,
                    HingeDistance = stripWidth - distance,
                    FoldWeight = cornerFade * hingeFade
                };
            }
        }

        public float GetFoldAngle(Vector2 drag, out float translationScale)
        {
            float downward = Vector2.Dot(drag, grabDown);
            float alignment = downward / Mathf.Max(0.00001f, drag.magnitude);
            float downwardGesture = Mathf.SmoothStep(0f, 1f, (alignment - 0.4f) / 0.4f);
            translationScale = Mathf.Lerp(1f, 0.16f, downwardGesture);
            float progress = Mathf.SmoothStep(0f, 1f, (downward - threshold) / stripWidth);
            return 155f * Mathf.Deg2Rad * progress * downwardGesture;
        }

        public Vector3 GetFoldOffset(int index, float angle, float brushWeight, float depthSign)
        {
            LipVertex vertex = lipVertices[index];
            angle *= vertex.FoldWeight * brushWeight;
            if (angle <= 0f) return Vector3.zero;

            // Rotate the edge around a hinge on the cheek side. The cosine reverses
            // the projected winding once the lip passes 90 degrees.
            Vector2 planar = vertex.Down * (vertex.HingeDistance * (1f - Mathf.Cos(angle)));
            return new Vector3(planar.x, planar.y, depthSign * vertex.HingeDistance * Mathf.Sin(angle));
        }

        private bool TryGetLowerLip(Vector2 point, out Vector2 down, out float distance, out float along)
        {
            down = Vector2.zero;
            distance = 0f;
            along = 0f;
            float bestSquared = float.PositiveInfinity;
            float signedDistance = 0f;
            float arc = 0f;
            for (int i = 1; i < path.Length; i++)
            {
                Vector2 segment = path[i] - path[i - 1];
                float length = arcLengths[i] - arcLengths[i - 1];
                if (length <= 0.00001f) continue;
                Vector2 tangent = segment / length;
                float offset = Mathf.Clamp(Vector2.Dot(point - path[i - 1], tangent), 0f, length);
                Vector2 relative = point - (path[i - 1] + tangent * offset);
                if (relative.sqrMagnitude >= bestSquared) continue;
                bestSquared = relative.sqrMagnitude;
                down = new Vector2(tangent.y, -tangent.x);
                if (down.y > 0f) down = -down;
                signedDistance = Vector2.Dot(relative, down);
                arc = arcLengths[i - 1] + offset;
            }

            float lengthAlongCut = arcLengths[arcLengths.Length - 1];
            if (arc <= 0f || arc >= lengthAlongCut || signedDistance <= 0f) return false;
            along = arc / lengthAlongCut;
            distance = Mathf.Max(0f, signedDistance - cutRadius);
            return distance < stripWidth;
        }
    }
}
