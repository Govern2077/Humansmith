using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeformableImage
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(DeformableImageMesh))]
    public sealed class DetachedSkinBoneBinding2D : MonoBehaviour
    {
        private DeformableImageMesh skinMesh;
        private AnatomyBoneMesh jawBone;
        private float jawRegionBottom;
        private float jawRegionPeak;
        private float jawRegionTop;
        private float influenceRadius;
        private float minimumUsefulWeight;
        private float response;
        private float maximumDisplacement;
        private int smoothingIterations;
        private float smoothingStrength;
        private bool preserveOriginalVertexY;

        private Vector3[] bindVertices = Array.Empty<Vector3>();
        private float[] detachedVertexY = Array.Empty<float>();
        private Vector2[] bindBoneAnchors = Array.Empty<Vector2>();
        private float[] jawWeights = Array.Empty<float>();
        private Vector3[] rawOffsets = Array.Empty<Vector3>();
        private Vector3[] smoothOffsets = Array.Empty<Vector3>();
        private Vector3[] smoothingBuffer = Array.Empty<Vector3>();
        private List<int>[] neighbours = Array.Empty<List<int>>();
        private bool configured;

        public event Action TargetsChanged;

        public bool IsConfigured { get { return configured; } }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        public bool Configure(
            DeformableImageMesh mesh,
            AnatomyBoneMesh bone,
            float configuredJawRegionBottom,
            float configuredJawRegionPeak,
            float configuredJawRegionTop,
            float configuredInfluenceRadius,
            float configuredMinimumUsefulWeight,
            float configuredResponse,
            float configuredMaximumDisplacement,
            int configuredSmoothingIterations,
            float configuredSmoothingStrength,
            bool configuredPreserveOriginalVertexY)
        {
            Unsubscribe();
            configured = false;
            skinMesh = mesh != null ? mesh : GetComponent<DeformableImageMesh>();
            jawBone = bone;
            jawRegionBottom = Mathf.Clamp01(configuredJawRegionBottom);
            jawRegionTop = Mathf.Clamp01(configuredJawRegionTop);
            jawRegionPeak = Mathf.Clamp(
                configuredJawRegionPeak,
                jawRegionBottom,
                jawRegionTop);
            influenceRadius = Mathf.Max(0.01f, configuredInfluenceRadius);
            minimumUsefulWeight = Mathf.Clamp01(configuredMinimumUsefulWeight);
            response = Mathf.Max(0f, configuredResponse);
            maximumDisplacement = Mathf.Max(0.01f, configuredMaximumDisplacement);
            smoothingIterations = Mathf.Clamp(configuredSmoothingIterations, 0, 50);
            smoothingStrength = Mathf.Clamp01(configuredSmoothingStrength);
            preserveOriginalVertexY = configuredPreserveOriginalVertexY;

            if (skinMesh == null || jawBone == null)
            {
                return false;
            }

            Vector3[] contourWorld = jawBone.CopyLargestContourWorld();
            bindVertices = skinMesh.CopyCurrentVertices();
            if (contourWorld.Length < 3 || bindVertices.Length == 0)
            {
                return false;
            }

            Vector2[] bindContour = ConvertContourToSkinLocal(contourWorld);
            int vertexCount = bindVertices.Length;
            detachedVertexY = new float[vertexCount];
            bindBoneAnchors = new Vector2[vertexCount];
            jawWeights = new float[vertexCount];
            rawOffsets = new Vector3[vertexCount];
            smoothOffsets = new Vector3[vertexCount];
            smoothingBuffer = new Vector3[vertexCount];

            for (int index = 0; index < vertexCount; index++)
            {
                // This is captured immediately after extraction, before cloth gravity can
                // move the piece. It is the material point's original facial Y coordinate.
                detachedVertexY[index] = bindVertices[index].y;
                Vector2 skinPoint = bindVertices[index];
                Vector2 anchor = ClosestPointOnClosedContour(skinPoint, bindContour);
                bindBoneAnchors[index] = anchor;

                Vector3 worldPoint = skinMesh.transform.TransformPoint(bindVertices[index]);
                Vector3 boneLocalPoint = jawBone.transform.InverseTransformPoint(worldPoint);
                float normalizedY = jawBone.LocalToNormalizedUnchecked(boneLocalPoint).y;
                float verticalWeight = EvaluateJawBand(normalizedY);
                float distance = Vector2.Distance(skinPoint, anchor);
                float distanceWeight = Mathf.SmoothStep(
                    0f,
                    1f,
                    1f - Mathf.Clamp01(distance / influenceRadius));
                float weight = verticalWeight * distanceWeight;
                jawWeights[index] = weight >= minimumUsefulWeight ? weight : 0f;
            }

            BuildNeighbourGraph(skinMesh.CopyLogicalTriangles());
            configured = true;
            Subscribe();
            return true;
        }

        public bool WriteCurrentTargets(Vector3[] destination)
        {
            if (!configured
                || destination == null
                || destination.Length != bindVertices.Length
                || jawBone == null)
            {
                return false;
            }

            Vector3[] contourWorld = jawBone.CopyLargestContourWorld();
            if (contourWorld.Length < 3)
            {
                return false;
            }

            Vector2[] currentContour = ConvertContourToSkinLocal(contourWorld);
            for (int index = 0; index < bindVertices.Length; index++)
            {
                if (jawWeights[index] <= 0f)
                {
                    rawOffsets[index] = Vector3.zero;
                    continue;
                }

                Vector2 newAnchor = ClosestPointOnClosedContour(
                    bindBoneAnchors[index],
                    currentContour);
                Vector2 anchorDelta = Vector2.ClampMagnitude(
                    newAnchor - bindBoneAnchors[index],
                    maximumDisplacement);
                if (preserveOriginalVertexY)
                {
                    anchorDelta.y = 0f;
                }

                rawOffsets[index] = anchorDelta * (jawWeights[index] * response);
            }

            SmoothOffsets();
            for (int index = 0; index < destination.Length; index++)
            {
                Vector3 target = bindVertices[index] + smoothOffsets[index];
                if (preserveOriginalVertexY && index < detachedVertexY.Length)
                {
                    target.y = detachedVertexY[index];
                }

                destination[index] = target;
            }

            return true;
        }

        private void HandleBoneTopologyChanged()
        {
            TargetsChanged?.Invoke();
        }

        private void Subscribe()
        {
            if (jawBone != null)
            {
                jawBone.TopologyChanged -= HandleBoneTopologyChanged;
                jawBone.TopologyChanged += HandleBoneTopologyChanged;
            }
        }

        private void Unsubscribe()
        {
            if (jawBone != null)
            {
                jawBone.TopologyChanged -= HandleBoneTopologyChanged;
            }
        }

        private Vector2[] ConvertContourToSkinLocal(Vector3[] worldContour)
        {
            Vector2[] result = new Vector2[worldContour.Length];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = skinMesh.transform.InverseTransformPoint(worldContour[index]);
            }

            return result;
        }

        private float EvaluateJawBand(float normalizedY)
        {
            if (normalizedY <= jawRegionBottom || normalizedY >= jawRegionTop)
            {
                return 0f;
            }

            if (normalizedY <= jawRegionPeak)
            {
                return Mathf.SmoothStep(
                    0f,
                    1f,
                    Mathf.InverseLerp(jawRegionBottom, jawRegionPeak, normalizedY));
            }

            return Mathf.SmoothStep(
                0f,
                1f,
                1f - Mathf.InverseLerp(jawRegionPeak, jawRegionTop, normalizedY));
        }

        private void BuildNeighbourGraph(int[] triangles)
        {
            neighbours = new List<int>[bindVertices.Length];
            for (int index = 0; index < neighbours.Length; index++)
            {
                neighbours[index] = new List<int>(6);
            }

            for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
            {
                AddNeighbourPair(triangles[triangle], triangles[triangle + 1]);
                AddNeighbourPair(triangles[triangle + 1], triangles[triangle + 2]);
                AddNeighbourPair(triangles[triangle + 2], triangles[triangle]);
            }
        }

        private void AddNeighbourPair(int first, int second)
        {
            if (first < 0
                || second < 0
                || first >= neighbours.Length
                || second >= neighbours.Length
                || first == second)
            {
                return;
            }

            if (!neighbours[first].Contains(second))
            {
                neighbours[first].Add(second);
            }

            if (!neighbours[second].Contains(first))
            {
                neighbours[second].Add(first);
            }
        }

        private void SmoothOffsets()
        {
            Array.Copy(rawOffsets, smoothOffsets, rawOffsets.Length);
            for (int iteration = 0; iteration < smoothingIterations; iteration++)
            {
                for (int index = 0; index < smoothOffsets.Length; index++)
                {
                    if (jawWeights[index] <= 0f || neighbours[index].Count == 0)
                    {
                        smoothingBuffer[index] = Vector3.zero;
                        continue;
                    }

                    Vector3 average = Vector3.zero;
                    int contributing = 0;
                    for (int neighbourIndex = 0;
                        neighbourIndex < neighbours[index].Count;
                        neighbourIndex++)
                    {
                        int neighbour = neighbours[index][neighbourIndex];
                        if (jawWeights[neighbour] <= 0f)
                        {
                            continue;
                        }

                        average += smoothOffsets[neighbour];
                        contributing++;
                    }

                    if (contributing == 0)
                    {
                        smoothingBuffer[index] = rawOffsets[index];
                        continue;
                    }

                    average /= contributing;
                    float retainTarget = Mathf.Lerp(0.25f, 0.9f, jawWeights[index]);
                    Vector3 targetPreservingAverage = Vector3.Lerp(
                        average,
                        rawOffsets[index],
                        retainTarget);
                    smoothingBuffer[index] = Vector3.Lerp(
                        smoothOffsets[index],
                        targetPreservingAverage,
                        smoothingStrength);
                }

                Vector3[] swap = smoothOffsets;
                smoothOffsets = smoothingBuffer;
                smoothingBuffer = swap;
            }
        }

        private static Vector2 ClosestPointOnClosedContour(Vector2 point, Vector2[] contour)
        {
            Vector2 closest = contour[0];
            float closestDistanceSquared = float.PositiveInfinity;
            Vector2 previous = contour[contour.Length - 1];
            for (int index = 0; index < contour.Length; index++)
            {
                Vector2 current = contour[index];
                Vector2 candidate = ClosestPointOnSegment(point, previous, current);
                float distanceSquared = (candidate - point).sqrMagnitude;
                if (distanceSquared < closestDistanceSquared)
                {
                    closest = candidate;
                    closestDistanceSquared = distanceSquared;
                }

                previous = current;
            }

            return closest;
        }

        private static Vector2 ClosestPointOnSegment(Vector2 point, Vector2 start, Vector2 end)
        {
            Vector2 segment = end - start;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared < 0.0000001f)
            {
                return start;
            }

            float factor = Mathf.Clamp01(Vector2.Dot(point - start, segment) / lengthSquared);
            return start + segment * factor;
        }
    }
}
