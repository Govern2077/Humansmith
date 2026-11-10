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
            Subscribe();
        }

        private void Start()
        {
            BindRestPose();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

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
            if (applying)
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
                for (int index = 0; index < sourceCurrentVertices.Length; index++)
                {
                    currentSourceWorld[index] = sourceLayer.transform.TransformPoint(
                        sourceCurrentVertices[index]);
                }

                for (int index = 0; index < drivenBindVertices.Length; index++)
                {
                    Vector3 worldDelta = SampleWorldDelta(index, currentSourceWorld);
                    Vector3 localDelta = drivenLayer.transform.InverseTransformVector(worldDelta);
                    rawOffsets[index] = Vector3.ClampMagnitude(
                        localDelta * response,
                        maximumDisplacement);
                }

                // The neighbour graph changes only when a topology/rest-pose event rebinds
                // this coupler. Rebuilding it on every drag event caused substantial GC and
                // CPU spikes, especially across the Muscle -> Fat -> Skin chain.
                SmoothOffsets();

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
            int offset = drivenIndex * SamplesPerVertex;
            sampleIndices[offset] = sourceIndex;
            sampleWeights[offset] = 1f;
            for (int sample = 1; sample < SamplesPerVertex; sample++)
            {
                sampleIndices[offset + sample] = sourceIndex;
                sampleWeights[offset + sample] = 0f;
            }
        }

        private void BuildNearestUvBinding(int drivenIndex, Vector2 uv, Vector2[] sourceUvs)
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
                        sampleIndices[offset + shift] = sampleIndices[offset + shift - 1];
                    }

                    bestDistances[sample] = distance;
                    sampleIndices[offset + sample] = sourceIndex;
                    break;
                }
            }

            if (bestDistances[0] <= 0.00000001f)
            {
                SetDirectBinding(drivenIndex, sampleIndices[offset]);
                return;
            }

            float total = 0f;
            for (int sample = 0; sample < SamplesPerVertex; sample++)
            {
                float weight = 1f / Mathf.Max(0.00000001f, bestDistances[sample]);
                sampleWeights[offset + sample] = weight;
                total += weight;
            }

            for (int sample = 0; sample < SamplesPerVertex; sample++)
            {
                sampleWeights[offset + sample] /= total;
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
