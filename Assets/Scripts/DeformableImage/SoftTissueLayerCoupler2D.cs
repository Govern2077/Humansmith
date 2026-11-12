using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeformableImage
{
    /// <summary>
    /// Transfers deformation from one soft-tissue mesh to the layer directly above it.
    /// Bindings are made in material UV space, so the two layers may use different
    /// subdivisions while still sharing the same anatomical image alignment.
    /// </summary>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public sealed class SoftTissueLayerCoupler2D : MonoBehaviour
    {
        private const int SamplesPerVertex = 4;
        private static readonly List<SoftTissueLayerCoupler2D> Instances = new List<SoftTissueLayerCoupler2D>();
        private static bool applyingManualTransfer;
        private int[] reverseSampleIndices = Array.Empty<int>();
        private float[] reverseSampleWeights = Array.Empty<float>();

        [Header("Layer Link")]
        [Tooltip("The deeper layer that drives this layer (for example Muscle -> Fat).")]
        [SerializeField] private DeformableImageMesh sourceLayer;
        [Tooltip("The immediately adjacent outer layer receiving the deformation.")]
        [SerializeField] private DeformableImageMesh drivenLayer;

        [Header("Mechanical Transfer")]
        [SerializeField, Range(0f, 2f)] private float response = 1f;
        [SerializeField, Min(0.01f)] private float maximumDisplacement = 1.25f;
        [SerializeField, Range(0, 30)] private int smoothingIterations = 4;
        [SerializeField, Range(0f, 1f)] private float smoothingStrength = 0.3f;
        [SerializeField] private bool preserveDrivenLayerOffsets = true;

        [Header("Debug")]
        [SerializeField, TextArea(2, 4)] private string lastStatus = "Not bound yet.";

        private Vector3[] sourceBindWorld = Array.Empty<Vector3>();
        private Vector3[] drivenBindVertices = Array.Empty<Vector3>();
        private Vector3[] appliedSourceOffsets = Array.Empty<Vector3>();
        private Vector3[] rawOffsets = Array.Empty<Vector3>();
        private Vector3[] smoothOffsets = Array.Empty<Vector3>();
        private Vector3[] smoothingBuffer = Array.Empty<Vector3>();
        private Vector3[] sourceCurrentVertices = Array.Empty<Vector3>();
        private Vector3[] drivenCurrentVertices = Array.Empty<Vector3>();
        private Vector3[] currentSourceWorld = Array.Empty<Vector3>();
        private Vector3[] resultVertices = Array.Empty<Vector3>();
        private int[] sampleIndices = Array.Empty<int>();
        private float[] sampleWeights = Array.Empty<float>();
        private List<int>[] neighbours = Array.Empty<List<int>>();
        private bool bound;
        private bool applying;
        private Vector2[] releaseUvs = Array.Empty<Vector2>();
        private float[] releasedInfluence = Array.Empty<float>();

        public DeformableImageMesh SourceLayer { get { return sourceLayer; } }
        public DeformableImageMesh DrivenLayer { get { return drivenLayer; } }
        public string LastStatus { get { return lastStatus; } }
        public bool IsBound { get { return bound; } }

        private void Awake()
        {
            if (drivenLayer == null)
            {
                drivenLayer = GetComponent<DeformableImageMesh>();
            }
        }

        private void OnEnable()
        {
            if (!Instances.Contains(this)) Instances.Add(this);
            Subscribe();
        }

        private void Start()
        {
            BindRestPose();
        }

        private void OnDisable()
        {
            Instances.Remove(this);
            Unsubscribe();
        }

        private void OnDestroy() { Instances.Remove(this); }

        private void OnValidate()
        {
            maximumDisplacement = Mathf.Max(0.01f, maximumDisplacement);
            smoothingIterations = Mathf.Clamp(smoothingIterations, 0, 30);
            smoothingStrength = Mathf.Clamp01(smoothingStrength);
        }

        public void Configure(
            DeformableImageMesh source,
            DeformableImageMesh driven,
            float transferResponse = 1f)
        {
            Unsubscribe();
            sourceLayer = source;
            drivenLayer = driven;
            response = Mathf.Clamp(transferResponse, 0f, 2f);
            if (!Instances.Contains(this)) Instances.Add(this);
            Subscribe();
            if (Application.isPlaying)
            {
                BindRestPose();
            }
        }

        [ContextMenu("Bind Adjacent Tissue Layers")]
        public void BindRestPose()
        {
            bound = false;
            reverseSampleIndices = Array.Empty<int>(); reverseSampleWeights = Array.Empty<float>();
            if (sourceLayer == null || drivenLayer == null)
            {
                lastStatus = "Assign both Source Layer and Driven Layer.";
                return;
            }

            if (sourceLayer == drivenLayer)
            {
                lastStatus = "Source Layer and Driven Layer must be different meshes.";
                return;
            }

            Vector3[] sourceVertices = sourceLayer.CopyCurrentVertices();
            Vector2[] sourceUvs = sourceLayer.CopyNormalizedRestUvs();
            drivenBindVertices = drivenLayer.CopyRestVertices();
            Vector2[] drivenUvs = drivenLayer.CopyNormalizedRestUvs();
            releaseUvs = drivenUvs;
            releasedInfluence = new float[drivenUvs.Length];
            if (sourceVertices.Length == 0
                || sourceVertices.Length != sourceUvs.Length
                || drivenBindVertices.Length == 0
                || drivenBindVertices.Length != drivenUvs.Length)
            {
                lastStatus = "One of the tissue meshes has not been built yet.";
                return;
            }

            sourceBindWorld = new Vector3[sourceVertices.Length];
            sourceCurrentVertices = sourceVertices;
            currentSourceWorld = new Vector3[sourceVertices.Length];
            for (int index = 0; index < sourceVertices.Length; index++)
            {
                sourceBindWorld[index] = sourceLayer.transform.TransformPoint(sourceVertices[index]);
            }

            int drivenCount = drivenBindVertices.Length;
            appliedSourceOffsets = new Vector3[drivenCount];
            rawOffsets = new Vector3[drivenCount];
            smoothOffsets = new Vector3[drivenCount];
            smoothingBuffer = new Vector3[drivenCount];
            drivenCurrentVertices = new Vector3[drivenCount];
            resultVertices = new Vector3[drivenCount];
            sampleIndices = new int[drivenCount * SamplesPerVertex];
            sampleWeights = new float[sampleIndices.Length];

            bool identicalUvLayout = HaveIdenticalUvLayout(sourceUvs, drivenUvs);
            for (int index = 0; index < drivenCount; index++)
            {
                if (identicalUvLayout)
                {
                    SetDirectBinding(index, index);
                }
                else
                {
                    BuildNearestUvBinding(index, drivenUvs[index], sourceUvs);
                }
            }

            BuildNeighbourGraph();
            bound = true;
            lastStatus = "Bound " + drivenCount + " vertices: "
                + sourceLayer.name + " -> " + drivenLayer.name + ".";
        }

        public void ApplyCurrentSourceShape()
        {
            if (applying || applyingManualTransfer)
            {
                return;
            }

            if (!bound || sourceLayer == null || drivenLayer == null)
            {
                BindRestPose();
                if (!bound)
                {
                    return;
                }
            }

            if (!sourceLayer.CopyCurrentVerticesNonAlloc(sourceCurrentVertices)
                || !drivenLayer.CopyCurrentVerticesNonAlloc(drivenCurrentVertices))
            {
                BindRestPose();
                return;
            }

            applying = true;
            try
            {
                CalculateSourceOffsets();

                for (int index = 0; index < resultVertices.Length; index++)
                {
                    Vector3 manualOffset = preserveDrivenLayerOffsets
                        ? drivenCurrentVertices[index]
                            - drivenBindVertices[index]
                            - appliedSourceOffsets[index]
                        : Vector3.zero;
                    resultVertices[index] = drivenBindVertices[index]
                        + manualOffset
                        + smoothOffsets[index];
                    appliedSourceOffsets[index] = smoothOffsets[index];
                }

                drivenLayer.SetLogicalVertices(resultVertices);
            }
            finally
            {
                applying = false;
            }
        }

        private void CalculateSourceOffsets()
        {
            sourceLayer.CopyCurrentVerticesNonAlloc(sourceCurrentVertices);
            for (int i = 0; i < sourceCurrentVertices.Length; i++)
                currentSourceWorld[i] = sourceLayer.transform.TransformPoint(sourceCurrentVertices[i]);
            for (int i = 0; i < drivenBindVertices.Length; i++)
                rawOffsets[i] = Vector3.ClampMagnitude(drivenLayer.transform.InverseTransformVector(
                    SampleWorldDelta(i, currentSourceWorld)) * response, maximumDisplacement);
            SmoothOffsets();
            for (int i = 0; i < smoothOffsets.Length; i++) smoothOffsets[i] *= 1f - releasedInfluence[i];
        }

        private bool EnsureBinding()
        {
            if (sourceLayer == null || drivenLayer == null || sourceLayer == drivenLayer) return false;
            if (!bound || sourceCurrentVertices.Length != sourceLayer.LogicalVertexCount
                || drivenCurrentVertices.Length != drivenLayer.LogicalVertexCount) BindRestPose();
            return bound;
        }

        private sealed class ManualTransfer
        {
            public DeformableImageMesh Mesh;
            public Vector3[] Positions, WorldDelta;
        }

        /// <summary>
        /// Propagates one explicit tool displacement through adjacent tissues once in
        /// either direction. Expression/bone updates retain their original outward path.
        /// </summary>
        public static void ApplyManualVertices(DeformableImageMesh origin, Vector3[] positions)
        {
            if (origin == null || positions == null || positions.Length != origin.LogicalVertexCount) return;
            if (applyingManualTransfer) { origin.SetLogicalVertices(positions); return; }
            Vector3[] before = origin.CopyCurrentVertices();
            var first = new ManualTransfer { Mesh = origin, Positions = positions, WorldDelta = new Vector3[before.Length] };
            for (int i = 0; i < before.Length; i++) first.WorldDelta[i] = origin.transform.TransformVector(positions[i] - before[i]);
            var transfers = new List<ManualTransfer> { first };
            var visited = new HashSet<DeformableImageMesh> { origin };
            var links = new List<SoftTissueLayerCoupler2D>();
            foreach (var link in Instances)
                if (link != null && link.isActiveAndEnabled && link.sourceLayer != null && link.drivenLayer != null) links.Add(link);
            for (int step = 0; step < transfers.Count; step++)
            {
                var from = transfers[step];
                foreach (var link in links)
                {
                    bool reverse = link.drivenLayer == from.Mesh;
                    if (!reverse && link.sourceLayer != from.Mesh) continue;
                    var target = reverse ? link.sourceLayer : link.drivenLayer;
                    if (visited.Contains(target) || target.DetachedFromMesh != null || !link.EnsureBinding()) continue;
                    var worldDelta = link.MapManualDelta(from.WorldDelta, reverse);
                    var next = new ManualTransfer { Mesh = target, Positions = target.CopyCurrentVertices(), WorldDelta = worldDelta };
                    bool[] anchors = target.CopyOriginalOutlineAnchors();
                    for (int i = 0; i < next.Positions.Length; i++)
                    {
                        if (anchors[i]) { next.WorldDelta[i] = Vector3.zero; continue; }
                        next.Positions[i] += target.transform.InverseTransformVector(next.WorldDelta[i]);
                    }
                    visited.Add(target); transfers.Add(next);
                }
            }
            applyingManualTransfer = true;
            try
            {
                foreach (var transfer in transfers) transfer.Mesh.SetLogicalVertices(transfer.Positions);
                // Reconcile the ordinary outward driver's contribution without applying
                // it again. Otherwise the next expression update would double the drag.
                foreach (var link in links)
                    if (visited.Contains(link.sourceLayer) && visited.Contains(link.drivenLayer) && link.EnsureBinding())
                    {
                        link.CalculateSourceOffsets();
                        Array.Copy(link.smoothOffsets, link.appliedSourceOffsets, link.smoothOffsets.Length);
                    }
            }
            finally { applyingManualTransfer = false; }
        }

        private Vector3[] MapManualDelta(Vector3[] delta, bool reverse)
        {
            if (reverse && reverseSampleIndices.Length == 0)
            {
                Vector2[] sourceUvs = sourceLayer.CopyNormalizedRestUvs();
                reverseSampleIndices = new int[sourceUvs.Length * SamplesPerVertex];
                reverseSampleWeights = new float[reverseSampleIndices.Length];
                bool identical = HaveIdenticalUvLayout(sourceUvs, releaseUvs);
                for (int i = 0; i < sourceUvs.Length; i++)
                    if (identical) SetDirectBinding(i, i, reverseSampleIndices, reverseSampleWeights);
                    else BuildNearestUvBinding(i, sourceUvs[i], releaseUvs, reverseSampleIndices, reverseSampleWeights);
            }
            int[] indices = reverse ? reverseSampleIndices : sampleIndices;
            float[] weights = reverse ? reverseSampleWeights : sampleWeights;
            int count = reverse ? sourceLayer.LogicalVertexCount : drivenLayer.LogicalVertexCount;
            var mapped = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                for (int s = 0; s < SamplesPerVertex; s++)
                {
                    int offset = i * SamplesPerVertex + s, index = indices[offset];
                    float attachment = 1f - releasedInfluence[reverse ? index : i];
                    mapped[i] += delta[index] * (weights[offset] * attachment * response);
                }
            }
            return mapped;
        }

        public void ResetDrivenLayerToSourceShape()
        {
            if (!bound || drivenLayer == null)
            {
                return;
            }

            Vector3[] result = new Vector3[drivenBindVertices.Length];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = drivenBindVertices[index] + appliedSourceOffsets[index];
            }

            drivenLayer.SetLogicalVertices(result);
        }

        public void ReleaseInfluence(Vector2 uv, float radius, float amount)
        {
            if (!bound) BindRestPose();
            if (!bound) return;
            for (int i = 0; i < releaseUvs.Length; i++)
            {
                float weight = Mathf.Clamp01(1f - Vector2.Distance(releaseUvs[i], uv) / Mathf.Max(0.001f, radius));
                releasedInfluence[i] = Mathf.Clamp01(releasedInfluence[i] + Mathf.Max(0f, amount) * weight * weight);
            }
            ApplyCurrentSourceShape();
        }

        public void ResetReleasedInfluence()
        {
            Array.Clear(releasedInfluence, 0, releasedInfluence.Length);
            if (bound) ApplyCurrentSourceShape();
        }

        private void Subscribe()
        {
            if (sourceLayer != null)
            {
                sourceLayer.VerticesChanged -= HandleSourceVerticesChanged;
                sourceLayer.VerticesChanged += HandleSourceVerticesChanged;
                sourceLayer.MeshRebuilt -= HandleEitherMeshRebuilt;
                sourceLayer.MeshRebuilt += HandleEitherMeshRebuilt;
                sourceLayer.RestPoseChanged -= HandleEitherRestPoseChanged;
                sourceLayer.RestPoseChanged += HandleEitherRestPoseChanged;
            }

            if (drivenLayer != null)
            {
                drivenLayer.MeshRebuilt -= HandleEitherMeshRebuilt;
                drivenLayer.MeshRebuilt += HandleEitherMeshRebuilt;
                drivenLayer.RestPoseChanged -= HandleEitherRestPoseChanged;
                drivenLayer.RestPoseChanged += HandleEitherRestPoseChanged;
            }
        }

        private void Unsubscribe()
        {
            if (sourceLayer != null)
            {
                sourceLayer.VerticesChanged -= HandleSourceVerticesChanged;
                sourceLayer.MeshRebuilt -= HandleEitherMeshRebuilt;
                sourceLayer.RestPoseChanged -= HandleEitherRestPoseChanged;
            }

            if (drivenLayer != null)
            {
                drivenLayer.MeshRebuilt -= HandleEitherMeshRebuilt;
                drivenLayer.RestPoseChanged -= HandleEitherRestPoseChanged;
            }
        }

        private void HandleSourceVerticesChanged()
        {
            if (Application.isPlaying)
            {
                ApplyCurrentSourceShape();
            }
        }

        private void HandleEitherMeshRebuilt()
        {
            if (Application.isPlaying)
            {
                BindRestPose();
            }
        }

        private void HandleEitherRestPoseChanged()
        {
            if (Application.isPlaying)
            {
                BindRestPose();
            }
        }

        private static bool HaveIdenticalUvLayout(Vector2[] first, Vector2[] second)
        {
            if (first.Length != second.Length)
            {
                return false;
            }

            for (int index = 0; index < first.Length; index++)
            {
                if ((first[index] - second[index]).sqrMagnitude > 0.00000001f)
                {
                    return false;
                }
            }

            return true;
        }

        private void SetDirectBinding(int drivenIndex, int sourceIndex)
        {
            SetDirectBinding(drivenIndex, sourceIndex, sampleIndices, sampleWeights);
        }

        private static void SetDirectBinding(int drivenIndex, int sourceIndex, int[] indices, float[] weights)
        {
            int offset = drivenIndex * SamplesPerVertex;
            indices[offset] = sourceIndex;
            weights[offset] = 1f;
            for (int sample = 1; sample < SamplesPerVertex; sample++)
            {
                indices[offset + sample] = sourceIndex;
                weights[offset + sample] = 0f;
            }
        }

        private void BuildNearestUvBinding(int drivenIndex, Vector2 uv, Vector2[] sourceUvs)
        {
            BuildNearestUvBinding(drivenIndex, uv, sourceUvs, sampleIndices, sampleWeights);
        }

        private static void BuildNearestUvBinding(int drivenIndex, Vector2 uv, Vector2[] sourceUvs, int[] indices, float[] weights)
        {
            int offset = drivenIndex * SamplesPerVertex;
            float[] bestDistances =
            {
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity
            };

            for (int sourceIndex = 0; sourceIndex < sourceUvs.Length; sourceIndex++)
            {
                float distance = (sourceUvs[sourceIndex] - uv).sqrMagnitude;
                for (int sample = 0; sample < SamplesPerVertex; sample++)
                {
                    if (distance >= bestDistances[sample])
                    {
                        continue;
                    }

                    for (int shift = SamplesPerVertex - 1; shift > sample; shift--)
                    {
                        bestDistances[shift] = bestDistances[shift - 1];
                        indices[offset + shift] = indices[offset + shift - 1];
                    }

                    bestDistances[sample] = distance;
                    indices[offset + sample] = sourceIndex;
                    break;
                }
            }

            if (bestDistances[0] <= 0.00000001f)
            {
                SetDirectBinding(drivenIndex, indices[offset], indices, weights);
                return;
            }

            float total = 0f;
            for (int sample = 0; sample < SamplesPerVertex; sample++)
            {
                float weight = 1f / Mathf.Max(0.00000001f, bestDistances[sample]);
                weights[offset + sample] = weight;
                total += weight;
            }

            for (int sample = 0; sample < SamplesPerVertex; sample++)
            {
                weights[offset + sample] /= total;
            }
        }

        private Vector3 SampleWorldDelta(int drivenIndex, Vector3[] currentSourceWorld)
        {
            Vector3 delta = Vector3.zero;
            int offset = drivenIndex * SamplesPerVertex;
            for (int sample = 0; sample < SamplesPerVertex; sample++)
            {
                int sourceIndex = sampleIndices[offset + sample];
                delta += (currentSourceWorld[sourceIndex] - sourceBindWorld[sourceIndex])
                    * sampleWeights[offset + sample];
            }

            return delta;
        }

        private void BuildNeighbourGraph()
        {
            neighbours = new List<int>[drivenBindVertices.Length];
            for (int index = 0; index < neighbours.Length; index++)
            {
                neighbours[index] = new List<int>(6);
            }

            int[] triangles = drivenLayer.CopyMechanicallyIntactTriangles();
            for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
            {
                AddNeighbourPair(triangles[triangle], triangles[triangle + 1]);
                AddNeighbourPair(triangles[triangle + 1], triangles[triangle + 2]);
                AddNeighbourPair(triangles[triangle + 2], triangles[triangle]);
            }
        }

        private void AddNeighbourPair(int first, int second)
        {
            if (first < 0 || second < 0 || first >= neighbours.Length || second >= neighbours.Length)
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
                    if (neighbours[index].Count == 0)
                    {
                        smoothingBuffer[index] = rawOffsets[index];
                        continue;
                    }

                    Vector3 average = Vector3.zero;
                    for (int neighbour = 0; neighbour < neighbours[index].Count; neighbour++)
                    {
                        average += smoothOffsets[neighbours[index][neighbour]];
                    }

                    average /= neighbours[index].Count;
                    Vector3 targetPreservingAverage = Vector3.Lerp(average, rawOffsets[index], 0.65f);
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
    }
}
