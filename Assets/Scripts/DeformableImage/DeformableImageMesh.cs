using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeformableImage
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class DeformableImageMesh : MonoBehaviour
    {
        private const string DefaultShaderName = "Deformable Image/Unlit Transparent";

        [Header("Images")]
        [SerializeField] private Sprite frontSprite;
        [SerializeField] private Sprite backSprite;
        [SerializeField] private bool flipBackHorizontally = true;

        [Header("Grid")]
        [SerializeField, Min(1)] private int subdivisionsX = 32;
        [SerializeField, Min(1)] private int subdivisionsY = 32;
        [SerializeField, Min(0.01f)] private float height = 5f;
        [SerializeField] private bool respectSpritePivot = true;

        [Header("Rendering")]
        [Tooltip("Optional. Leave empty to use the included depth-correct lit transparent shader.")]
        [SerializeField] private Material materialOverride;
        [SerializeField] private Color tint = Color.white;
        [SerializeField] private Color cutVisualizationColor = new Color(1f, 0.35f, 0.35f, 0.72f);
        [SerializeField] private bool recalculateNormalsWhenDeformed;
        [Tooltip("Base brightness retained when a surface faces away from the main light.")]
        [SerializeField, Range(0f, 2f)] private float ambientLightStrength = 0.65f;
        [Tooltip("How strongly the main directional/forward light models the facial relief.")]
        [SerializeField, Range(0f, 2f)] private float diffuseLightStrength = 0.55f;
        [Tooltip("Small highlights make raised skin readable. Keep this low for photographic skin.")]
        [SerializeField, Range(0f, 1f)] private float specularStrength = 0.08f;
        [SerializeField, Range(0.02f, 1f)] private float surfaceSmoothness = 0.28f;

        [Header("Detached Cut Piece")]
        [Tooltip("Adds interior cloth vertices to closed cut pieces. 2 is a good balance; 3 is smoother but heavier.")]
        [SerializeField, Range(0, 3)] private int cutPieceSubdivisionLevels = 2;
        [Tooltip("Automatically reduces subdivision for large pieces so runtime cloth remains responsive.")]
        [SerializeField, Min(128)] private int cutPieceMaximumTriangles = 3000;

        [Header("Mesh Debug Visualization")]
        [Tooltip("Draw the current physical topology in the Scene view and in Game view when Gizmos is enabled.")]
        [SerializeField] private bool showMeshDebug;
        [SerializeField] private bool showTriangleEdges = true;
        [SerializeField] private bool showBoundaryEdges = true;
        [SerializeField] private bool showMeshVertices;
        [SerializeField] private Color triangleEdgeColor = new Color(0.1f, 0.9f, 1f, 0.42f);
        [SerializeField] private Color outerBoundaryColor = new Color(1f, 0.82f, 0.12f, 0.95f);
        [SerializeField] private Color cutBoundaryColor = new Color(1f, 0.08f, 0.35f, 1f);
        [SerializeField] private Color meshVertexColor = new Color(1f, 1f, 1f, 0.9f);
        [SerializeField, Min(100)] private int meshDebugMaximumTriangleEdges = 2000;
        [SerializeField, Min(100)] private int meshDebugMaximumVertices = 1000;
        [SerializeField, Range(0.001f, 0.05f)] private float meshVertexRadius = 0.008f;
        [SerializeField] private float meshDebugZOffset = -0.015f;

        [Serializable]
        private sealed class BakedOpenCutPath
        {
            public Vector2[] Points = Array.Empty<Vector2>();

            public BakedOpenCutPath(IList<Vector2> source)
            {
                Points = new Vector2[source.Count];
                for (int index = 0; index < source.Count; index++)
                {
                    Points[index] = source[index];
                }
            }
        }

        [Header("Editor-Baked Topology")]
        [SerializeField, HideInInspector] private bool useBakedOpenCutTopology;
        [SerializeField, HideInInspector] private DeformableImageMesh bakedTopologySource;
        [SerializeField, HideInInspector] private Vector2[] bakedNormalizedVertices =
            Array.Empty<Vector2>();
        [SerializeField, HideInInspector] private int[] bakedLogicalTriangles = Array.Empty<int>();
        [SerializeField, HideInInspector] private BakedOpenCutPath[] bakedOpenCutPaths =
            Array.Empty<BakedOpenCutPath>();
        [SerializeField, HideInInspector] private float bakedOpenCutWidth;

        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Mesh generatedMesh;
        private Material generatedMaterial;
        private MaterialPropertyBlock frontProperties;
        private MaterialPropertyBlock backProperties;

        // Material-space vertices stay tied to the original sprite UV/layout. They are
        // deliberately kept separate from poseRestVertices so a personalized face can
        // become the new mechanical home pose without corrupting cutting coordinates.
        private Vector3[] restVertices = Array.Empty<Vector3>();
        private Vector3[] poseRestVertices = Array.Empty<Vector3>();
        private Vector3[] logicalVertices = Array.Empty<Vector3>();
        private Vector3[] renderVertices = Array.Empty<Vector3>();
        private int[] logicalTriangles = Array.Empty<int>();
        private readonly List<Vector2[]> visibleContours = new List<Vector2[]>();
        private readonly List<Vector2> visibleBoundarySegments = new List<Vector2>();
        private Vector2[] visibleGeometryVertices = Array.Empty<Vector2>();
        private ushort[] visibleGeometryTriangles = Array.Empty<ushort>();
        private byte[] alphaMask = Array.Empty<byte>();
        private int alphaMaskWidth;
        private int alphaMaskHeight;
        private byte[] cutMaskAlpha = Array.Empty<byte>();
        private Color32[] cutMaskPixels = Array.Empty<Color32>();
        private Texture2D cutMaskTexture;
        private byte[] cutVisualAlpha = Array.Empty<byte>();
        private Color32[] cutVisualPixels = Array.Empty<Color32>();
        private Texture2D cutVisualTexture;
        private int[] cutComponentLabels = Array.Empty<int>();
        private bool cutComponentsDirty = true;
        private int cutComponentCount;
        private List<int>[] deformationGraph = Array.Empty<List<int>>();
        private int[] mechanicallyIntactTriangles = Array.Empty<int>();
        private bool deformationGraphDirty = true;
        private bool hasAppliedCuts;
        private readonly List<CutBarrier> openCutBarriers = new List<CutBarrier>();
        private Bounds localBounds;
        private Vector2 gridMinimum;
        private Vector2 gridSize;
        [SerializeField, HideInInspector] private DeformableImageMesh detachedFromMesh;
        [NonSerialized] private int[] debugTopologySource;
        [NonSerialized] private int[] debugUniqueEdges = Array.Empty<int>();
        [NonSerialized] private int[] debugOuterBoundaryEdges = Array.Empty<int>();
        [NonSerialized] private int[] debugCutBoundaryEdges = Array.Empty<int>();

        public event Action MeshRebuilt;
        public event Action RestPoseChanged;
        public event Action VerticesChanged;

        public Sprite FrontSprite { get { return frontSprite; } }
        public Sprite BackSprite { get { return backSprite; } }
        public DeformableImageMesh DetachedFromMesh { get { return detachedFromMesh; } }
        public int LogicalVertexCount { get { return logicalVertices.Length; } }
        public int LogicalTriangleCount { get { return logicalTriangles.Length / 3; } }
        public bool HasBakedOpenCutTopology
        {
            get { return GetBakedTopologyOwner() != null; }
        }
        public bool UsesSharedBakedTopology
        {
            get { return bakedTopologySource != null && GetBakedTopologyOwner() != null; }
        }
        public bool MeshDebugVisible
        {
            get { return showMeshDebug; }
            set { showMeshDebug = value; }
        }
        public int ConnectedRegionCount
        {
            get
            {
                EnsureCutComponentLabels();
                return cutComponentCount;
            }
        }
        public Bounds LocalBounds { get { return localBounds; } }
        public float BrushReferenceSize { get { return Mathf.Max(localBounds.size.x, localBounds.size.y); } }
        private void OnEnable()
        {
            CacheComponents();
            RebuildMesh();
        }

        private void OnValidate()
        {
            subdivisionsX = Mathf.Max(1, subdivisionsX);
            subdivisionsY = Mathf.Max(1, subdivisionsY);
            height = Mathf.Max(0.01f, height);
            ambientLightStrength = Mathf.Clamp(ambientLightStrength, 0f, 2f);
            diffuseLightStrength = Mathf.Clamp(diffuseLightStrength, 0f, 2f);
            specularStrength = Mathf.Clamp01(specularStrength);
            surfaceSmoothness = Mathf.Clamp(surfaceSmoothness, 0.02f, 1f);
            cutPieceSubdivisionLevels = Mathf.Clamp(cutPieceSubdivisionLevels, 0, 3);
            cutPieceMaximumTriangles = Mathf.Max(128, cutPieceMaximumTriangles);
            meshDebugMaximumTriangleEdges = Mathf.Max(100, meshDebugMaximumTriangleEdges);
            meshDebugMaximumVertices = Mathf.Max(100, meshDebugMaximumVertices);
            meshVertexRadius = Mathf.Clamp(meshVertexRadius, 0.001f, 0.05f);

            // Keep lighting sliders live in the Inspector without rebuilding topology.
            if (meshRenderer != null)
            {
                ApplyTextures();
            }
        }

        private void OnDrawGizmos()
        {
            if (!showMeshDebug || logicalVertices == null || logicalVertices.Length == 0)
            {
                return;
            }

            EnsureDebugTopology();
            Matrix4x4 previousMatrix = Gizmos.matrix;
            Color previousColor = Gizmos.color;
            Gizmos.matrix = transform.localToWorldMatrix;

            if (showTriangleEdges)
            {
                DrawDebugEdges(
                    debugUniqueEdges,
                    triangleEdgeColor,
                    meshDebugMaximumTriangleEdges);
            }

            if (showBoundaryEdges)
            {
                DrawDebugEdges(debugOuterBoundaryEdges, outerBoundaryColor);
                DrawDebugEdges(debugCutBoundaryEdges, cutBoundaryColor);
            }

            if (showMeshVertices)
            {
                Gizmos.color = meshVertexColor;
                float radius = Mathf.Max(0.0001f, BrushReferenceSize * meshVertexRadius);
                int vertexStep = Mathf.Max(
                    1,
                    Mathf.CeilToInt(
                        logicalVertices.Length / (float)meshDebugMaximumVertices));
                for (int index = 0; index < logicalVertices.Length; index += vertexStep)
                {
                    Vector3 point = logicalVertices[index];
                    point.z += meshDebugZOffset;
                    Gizmos.DrawSphere(point, radius);
                }
            }

            Gizmos.matrix = previousMatrix;
            Gizmos.color = previousColor;
        }

        private void OnDestroy()
        {
            DestroyGeneratedResources();
        }

        public void ConfigureSprites(Sprite front, Sprite back)
        {
            frontSprite = front;
            backSprite = back;
            RebuildMesh();
        }

        /// <summary>
        /// Replaces only the front appearance. The supplied sprite must use the same
        /// texture layout as the existing front sprite; topology, cuts and rest poses are
        /// intentionally preserved.
        /// </summary>
        public void SetFrontSpriteWithoutRebuild(Sprite front)
        {
            if (front == null)
            {
                Debug.LogError("Cannot assign an empty front sprite.", this);
                return;
            }

            frontSprite = front;
            ApplyTextures();
        }

        [ContextMenu("Rebuild Mesh")]
        public void RebuildMesh()
        {
            CacheComponents();
            EnsureMaterial();

            if (frontSprite == null)
            {
                ClearMesh();
                ApplyTextures();
                return;
            }

            if (HasBakedOpenCutTopology)
            {
                RebuildFromBakedOpenCutTopology();
                return;
            }

            int vertexColumns = subdivisionsX + 1;
            int vertexRows = subdivisionsY + 1;
            int logicalCount = vertexColumns * vertexRows;

            restVertices = new Vector3[logicalCount];
            poseRestVertices = new Vector3[logicalCount];
            logicalVertices = new Vector3[logicalCount];
            renderVertices = new Vector3[logicalCount * 2];
            logicalTriangles = new int[subdivisionsX * subdivisionsY * 6];
            Vector2[] renderUvs = new Vector2[logicalCount * 2];
            Vector2[] renderCutUvs = new Vector2[logicalCount * 2];
            Vector3[] normals = new Vector3[logicalCount * 2];

            float spriteWidth = Mathf.Max(1f, frontSprite.rect.width);
            float spriteHeight = Mathf.Max(1f, frontSprite.rect.height);
            float width = height * spriteWidth / spriteHeight;

            Vector2 normalizedPivot = respectSpritePivot
                ? new Vector2(frontSprite.pivot.x / spriteWidth, frontSprite.pivot.y / spriteHeight)
                : new Vector2(0.5f, 0.5f);

            float minX = -width * normalizedPivot.x;
            float minY = -height * normalizedPivot.y;
            gridMinimum = new Vector2(minX, minY);
            gridSize = new Vector2(width, height);

            for (int y = 0; y < vertexRows; y++)
            {
                float fy = y / (float)subdivisionsY;

                for (int x = 0; x < vertexColumns; x++)
                {
                    float fx = x / (float)subdivisionsX;
                    int index = y * vertexColumns + x;
                    Vector3 position = new Vector3(minX + fx * width, minY + fy * height, 0f);

                    restVertices[index] = position;
                    poseRestVertices[index] = position;
                    logicalVertices[index] = position;
                    renderVertices[index] = position;
                    renderVertices[index + logicalCount] = position;

                    renderUvs[index] = SpriteUv(frontSprite, fx, fy);
                    Sprite actualBack = backSprite != null ? backSprite : frontSprite;
                    renderUvs[index + logicalCount] = SpriteUv(
                        actualBack,
                        flipBackHorizontally ? 1f - fx : fx,
                        fy);
                    renderCutUvs[index] = new Vector2(fx, fy);
                    renderCutUvs[index + logicalCount] = new Vector2(fx, fy);

                    normals[index] = Vector3.back;
                    normals[index + logicalCount] = Vector3.forward;
                }
            }

            int triangleIndex = 0;
            for (int y = 0; y < subdivisionsY; y++)
            {
                for (int x = 0; x < subdivisionsX; x++)
                {
                    int bottomLeft = y * vertexColumns + x;
                    int bottomRight = bottomLeft + 1;
                    int topLeft = bottomLeft + vertexColumns;
                    int topRight = topLeft + 1;

                    logicalTriangles[triangleIndex++] = bottomLeft;
                    logicalTriangles[triangleIndex++] = topLeft;
                    logicalTriangles[triangleIndex++] = bottomRight;
                    logicalTriangles[triangleIndex++] = bottomRight;
                    logicalTriangles[triangleIndex++] = topLeft;
                    logicalTriangles[triangleIndex++] = topRight;
                }
            }

            int[] backTriangles = new int[logicalTriangles.Length];
            for (int i = 0; i < logicalTriangles.Length; i += 3)
            {
                backTriangles[i] = logicalTriangles[i] + logicalCount;
                backTriangles[i + 1] = logicalTriangles[i + 2] + logicalCount;
                backTriangles[i + 2] = logicalTriangles[i + 1] + logicalCount;
            }

            ReplaceGeneratedMesh();
            generatedMesh.name = "Deformable Image Mesh";
            generatedMesh.indexFormat = renderVertices.Length > 65535
                ? IndexFormat.UInt32
                : IndexFormat.UInt16;
            generatedMesh.MarkDynamic();
            generatedMesh.vertices = renderVertices;
            generatedMesh.uv = renderUvs;
            generatedMesh.uv2 = renderCutUvs;
            generatedMesh.normals = normals;
            generatedMesh.subMeshCount = 2;
            generatedMesh.SetTriangles(logicalTriangles, 0, false);
            generatedMesh.SetTriangles(backTriangles, 1, false);
            generatedMesh.RecalculateBounds();

            meshFilter.sharedMesh = generatedMesh;
            localBounds = generatedMesh.bounds;
            RebuildVisibleContours();
            RebuildAlphaMask();
            CreateSolidCutMask();
            ApplyTextures();
            MeshRebuilt?.Invoke();
        }

        /// <summary>
        /// Performs the expensive cut once in Edit Mode and serializes its normalized
        /// vertices and triangle indices. Runtime rebuilds then load this topology directly.
        /// </summary>
        public bool BakeOpenCutTopology(
            IList<IList<Vector2>> normalizedPaths,
            float worldWidth)
        {
            if (Application.isPlaying
                || normalizedPaths == null
                || normalizedPaths.Count == 0)
            {
                return false;
            }

            useBakedOpenCutTopology = false;
            bakedTopologySource = null;
            bakedNormalizedVertices = Array.Empty<Vector2>();
            bakedLogicalTriangles = Array.Empty<int>();
            bakedOpenCutPaths = Array.Empty<BakedOpenCutPath>();
            bakedOpenCutWidth = 0f;
            RebuildMesh();
            if (!ApplyOpenCuts(normalizedPaths, worldWidth, true, false, false))
            {
                return false;
            }

            // The slit remains in the alpha mask and physical topology. The red overlay is
            // useful for an interactive knife, but baked facial openings should look natural.
            ClearRedCutVisualization();

            bakedNormalizedVertices = CopyNormalizedRestUvs();
            bakedLogicalTriangles = CopyLogicalTriangles();
            bakedOpenCutPaths = new BakedOpenCutPath[normalizedPaths.Count];
            for (int index = 0; index < normalizedPaths.Count; index++)
            {
                bakedOpenCutPaths[index] = new BakedOpenCutPath(normalizedPaths[index]);
            }

            bakedOpenCutWidth = Mathf.Max(0.0001f, worldWidth);
            useBakedOpenCutTopology = true;
            return true;
        }

        /// <summary>
        /// Shares an already baked normalized topology with an aligned tissue layer.
        /// The recipient still builds and owns its own runtime mesh, sprites, depth and pose;
        /// only the immutable serialized template is shared to avoid three large YAML copies.
        /// </summary>
        public bool CopyBakedOpenCutTopologyFrom(DeformableImageMesh source)
        {
            if (Application.isPlaying || source == null || !source.HasBakedOpenCutTopology)
            {
                return false;
            }

            DeformableImageMesh owner = source.GetBakedTopologyOwner();
            bakedTopologySource = owner;
            bakedNormalizedVertices = Array.Empty<Vector2>();
            bakedLogicalTriangles = Array.Empty<int>();
            bakedOpenCutPaths = Array.Empty<BakedOpenCutPath>();
            bakedOpenCutWidth = 0f;
            useBakedOpenCutTopology = true;
            RebuildMesh();
            return true;
        }

        public void ClearBakedOpenCutTopology()
        {
            useBakedOpenCutTopology = false;
            bakedTopologySource = null;
            bakedNormalizedVertices = Array.Empty<Vector2>();
            bakedLogicalTriangles = Array.Empty<int>();
            bakedOpenCutPaths = Array.Empty<BakedOpenCutPath>();
            bakedOpenCutWidth = 0f;
            RebuildMesh();
        }

        public Vector3[] CopyCurrentVertices()
        {
            return (Vector3[])logicalVertices.Clone();
        }

        public bool CopyCurrentVerticesNonAlloc(Vector3[] destination)
        {
            if (destination == null || destination.Length != logicalVertices.Length)
            {
                return false;
            }

            Array.Copy(logicalVertices, destination, logicalVertices.Length);
            return true;
        }

        public Vector3[] CopyRestVertices()
        {
            return (Vector3[])poseRestVertices.Clone();
        }

        /// <summary>
        /// Returns the immutable material/UV reference geometry. Use CopyRestVertices
        /// when the caller needs the personalized mechanical home pose.
        /// </summary>
        public Vector3[] CopyMaterialSpaceVertices()
        {
            return (Vector3[])restVertices.Clone();
        }

        public Vector2[] CopyNormalizedRestUvs()
        {
            Vector2[] result = new Vector2[restVertices.Length];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = RestPointToNormalized(restVertices[index]);
            }

            return result;
        }

        public bool ApplyRestDepths(float[] localDepths, bool preserveCurrentOffsets = true)
        {
            if (localDepths == null
                || localDepths.Length != poseRestVertices.Length
                || logicalVertices.Length != poseRestVertices.Length)
            {
                Debug.LogError("Depth array size does not match the deformable image mesh.", this);
                return false;
            }

            for (int index = 0; index < poseRestVertices.Length; index++)
            {
                float currentOffset = preserveCurrentOffsets
                    ? logicalVertices[index].z - poseRestVertices[index].z
                    : 0f;
                Vector3 rest = poseRestVertices[index];
                rest.z = localDepths[index];
                poseRestVertices[index] = rest;

                Vector3 current = logicalVertices[index];
                current.z = rest.z + currentOffset;
                logicalVertices[index] = current;
            }

            CommitLogicalVertices(true);
            RestPoseChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Makes a generated or authored shape the new undeformed home pose while the
        /// original material coordinates remain available for UV lookup and cutting.
        /// </summary>
        public bool ApplyRestPose(Vector3[] positions, bool preserveCurrentOffsets = false)
        {
            if (positions == null
                || positions.Length != restVertices.Length
                || logicalVertices.Length != restVertices.Length)
            {
                Debug.LogError("Rest pose array size does not match the deformable image mesh.", this);
                return false;
            }

            if (poseRestVertices.Length != positions.Length)
            {
                poseRestVertices = new Vector3[positions.Length];
            }

            for (int index = 0; index < positions.Length; index++)
            {
                Vector3 offset = preserveCurrentOffsets
                    ? logicalVertices[index] - poseRestVertices[index]
                    : Vector3.zero;
                poseRestVertices[index] = positions[index];
                logicalVertices[index] = positions[index] + offset;
            }

            CommitLogicalVertices(true);
            RestPoseChanged?.Invoke();
            return true;
        }

        public int[] CopyLogicalTriangles()
        {
            return (int[])logicalTriangles.Clone();
        }

        public int[] CopyMechanicallyIntactTriangles()
        {
            if (!hasAppliedCuts)
            {
                return CopyLogicalTriangles();
            }

            EnsureDeformationGraph();
            return (int[])mechanicallyIntactTriangles.Clone();
        }

        public Vector3 GetLogicalVertex(int index)
        {
            return logicalVertices[index];
        }

        public void SetLogicalVertices(Vector3[] positions)
        {
            SetLogicalVertices(positions, true);
        }

        public void SetLogicalVertices(Vector3[] positions, bool recalculateBounds)
        {
            if (positions == null || positions.Length != logicalVertices.Length)
            {
                Debug.LogError("Vertex array size does not match the deformable image mesh.", this);
                return;
            }

            Array.Copy(positions, logicalVertices, positions.Length);
            CommitLogicalVertices(recalculateBounds);
        }

        [ContextMenu("Reset Deformation")]
        public void ResetDeformation()
        {
            if (poseRestVertices.Length == 0)
            {
                return;
            }

            Array.Copy(poseRestVertices, logicalVertices, poseRestVertices.Length);
            CommitLogicalVertices();
        }

        [ContextMenu("Clear Red Cut Visualization")]
        public void ClearRedCutVisualization()
        {
            if (cutVisualAlpha == null || cutVisualAlpha.Length == 0)
            {
                return;
            }

            Array.Clear(cutVisualAlpha, 0, cutVisualAlpha.Length);
            UploadCutMask();
        }

        public bool HasVertexWithinRadius(Vector3 localPoint, float radius)
        {
            float radiusSquared = radius * radius;
            for (int i = 0; i < logicalVertices.Length; i++)
            {
                Vector2 delta = (Vector2)(logicalVertices[i] - localPoint);
                if (delta.sqrMagnitude <= radiusSquared)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds a per-vertex deformation mask for the visible island under the pointer.
        /// A cut that reaches the outer silhouette splits the alpha mask into independent
        /// connected regions, so deformation weights can no longer jump across the incision.
        /// </summary>
        public bool TryBuildDeformationRegion(Vector3 currentLocalPoint, out bool[] vertexMask)
        {
            vertexMask = null;
            if (logicalVertices.Length == 0
                || !TryMapCurrentPointToRest(currentLocalPoint, out Vector2 restPoint))
            {
                return false;
            }

            EnsureCutComponentLabels();
            vertexMask = new bool[logicalVertices.Length];

            // Without a readable alpha mask there is no reliable pixel topology. Preserve
            // the old whole-mesh behaviour instead of making the image impossible to grab.
            if (cutComponentCount <= 1 || cutComponentLabels.Length == 0)
            {
                for (int index = 0; index < vertexMask.Length; index++)
                {
                    vertexMask[index] = true;
                }

                return true;
            }

            int pointerX = Mathf.RoundToInt(
                (restPoint.x - gridMinimum.x) / Mathf.Max(0.0001f, gridSize.x)
                * (alphaMaskWidth - 1));
            int pointerY = Mathf.RoundToInt(
                (restPoint.y - gridMinimum.y) / Mathf.Max(0.0001f, gridSize.y)
                * (alphaMaskHeight - 1));
            int grabbedComponent = FindNearestCutComponent(
                pointerX,
                pointerY,
                Mathf.Max(alphaMaskWidth, alphaMaskHeight));
            if (grabbedComponent <= 0)
            {
                vertexMask = null;
                return false;
            }

            int vertexSearchRadius = Mathf.Max(
                2,
                Mathf.CeilToInt(Mathf.Max(
                    alphaMaskWidth / (float)Mathf.Max(1, subdivisionsX),
                    alphaMaskHeight / (float)Mathf.Max(1, subdivisionsY)) * 1.5f));

            for (int vertexIndex = 0; vertexIndex < restVertices.Length; vertexIndex++)
            {
                Vector2 normalized = new Vector2(
                    (restVertices[vertexIndex].x - gridMinimum.x) / Mathf.Max(0.0001f, gridSize.x),
                    (restVertices[vertexIndex].y - gridMinimum.y) / Mathf.Max(0.0001f, gridSize.y));
                int pixelX = Mathf.RoundToInt(normalized.x * (alphaMaskWidth - 1));
                int pixelY = Mathf.RoundToInt(normalized.y * (alphaMaskHeight - 1));
                vertexMask[vertexIndex] = FindNearestCutComponent(
                    pixelX,
                    pixelY,
                    vertexSearchRadius) == grabbedComponent;
            }

            return true;
        }

        /// <summary>
        /// Returns shortest-path distances through mechanically intact mesh edges. Unlike a
        /// Euclidean brush, these distances must travel around an incision endpoint and can
        /// never jump directly across a cut, even while the whole Sprite is still connected.
        /// </summary>
        public bool TryBuildGeodesicDistances(
            Vector3 currentLocalPoint,
            float maximumDistance,
            out float[] distances)
        {
            distances = null;
            if (!hasAppliedCuts
                || logicalVertices.Length == 0
                || !TryMapCurrentPointToRest(currentLocalPoint, out Vector2 restPoint))
            {
                return false;
            }

            EnsureDeformationGraph();
            if (deformationGraph.Length != logicalVertices.Length)
            {
                return false;
            }

            int seed = FindNearestTraversableVertex(restPoint);
            if (seed < 0)
            {
                return false;
            }

            distances = new float[logicalVertices.Length];
            for (int index = 0; index < distances.Length; index++)
            {
                distances[index] = float.PositiveInfinity;
            }

            float seedDistance = Vector2.Distance((Vector2)currentLocalPoint, (Vector2)logicalVertices[seed]);
            distances[seed] = seedDistance;
            MinDistanceHeap heap = new MinDistanceHeap();
            heap.Push(seed, seedDistance);
            float distanceLimit = Mathf.Max(0.001f, maximumDistance);

            while (heap.TryPop(out int current, out float currentDistance))
            {
                if (currentDistance > distances[current] + 0.000001f)
                {
                    continue;
                }

                if (currentDistance > distanceLimit)
                {
                    break;
                }

                List<int> neighbours = deformationGraph[current];
                for (int neighbourIndex = 0; neighbourIndex < neighbours.Count; neighbourIndex++)
                {
                    int neighbour = neighbours[neighbourIndex];
                    float edgeLength = Vector2.Distance(
                        (Vector2)logicalVertices[current],
                        (Vector2)logicalVertices[neighbour]);
                    float candidate = currentDistance + edgeLength;
                    if (candidate >= distances[neighbour] || candidate > distanceLimit)
                    {
                        continue;
                    }

                    distances[neighbour] = candidate;
                    heap.Push(neighbour, candidate);
                }
            }

            // A geodesic path may legitimately travel around an open cut endpoint. For an
            // editing brush this can still feel like the force leaked across the incision.
            // Apply a stricter visibility rule: if the direct rest-space segment from the
            // grab point to a vertex intersects any knife stroke, that vertex gets no weight.
            for (int index = 0; index < distances.Length; index++)
            {
                if (!float.IsPositiveInfinity(distances[index])
                    && IsBlockedByOpenCutCenterline(restPoint, restVertices[index]))
                {
                    distances[index] = float.PositiveInfinity;
                }
            }

            return true;
        }

        /// <summary>
        /// Maps the clicked point on the currently deformed grid back to the original Sprite space,
        /// rejects transparent areas, and reports whether the point is close to the visible outline.
        /// </summary>
        public bool TryClassifyGrabPoint(Vector3 currentLocalPoint, float edgeDistance, out bool isNearVisibleEdge)
        {
            if (!TryMapCurrentPointToRest(currentLocalPoint, out Vector2 restPoint))
            {
                isNearVisibleEdge = false;
                return false;
            }

            if (alphaMask.Length > 0)
            {
                return TryClassifyFromAlphaMask(restPoint, edgeDistance, out isNearVisibleEdge);
            }

            float distanceSquared = DistanceSquaredToCurrentVisibleBoundary((Vector2)currentLocalPoint);
            isNearVisibleEdge = distanceSquared <= edgeDistance * edgeDistance;

            // Accept a narrow band on either side of the outline. Requiring the click to be
            // strictly inside made anti-aliased edge pixels frustrating or impossible to grab.
            if (isNearVisibleEdge)
            {
                return true;
            }

            if (!IsInsideVisibleShape(restPoint))
            {
                return false;
            }

            return true;
        }

        public bool TryGetNormalizedUvAtCurrentPoint(Vector3 currentLocalPoint, out Vector2 normalizedUv)
        {
            return TryGetNormalizedUvAtCurrentPoint(
                currentLocalPoint,
                out normalizedUv,
                out _);
        }

        public bool TryGetNormalizedUvAtCurrentPoint(
            Vector3 currentLocalPoint,
            out Vector2 normalizedUv,
            out Vector3 currentSurfacePoint)
        {
            normalizedUv = default(Vector2);
            currentSurfacePoint = default(Vector3);
            if (!TryMapCurrentPointToRest(
                    currentLocalPoint,
                    out Vector2 restPoint,
                    out currentSurfacePoint))
            {
                return false;
            }

            normalizedUv = new Vector2(
                (restPoint.x - gridMinimum.x) / Mathf.Max(0.0001f, gridSize.x),
                (restPoint.y - gridMinimum.y) / Mathf.Max(0.0001f, gridSize.y));
            return normalizedUv.x >= 0f && normalizedUv.x <= 1f
                && normalizedUv.y >= 0f && normalizedUv.y <= 1f;
        }

        /// <summary>
        /// Returns true only when the point lies on currently visible, uncut material.
        /// An intact outer tissue can therefore block tools from deeper layers, while a
        /// real cut or a removed piece exposes the next layer at that exact position.
        /// </summary>
        public bool IsVisibleMaterialAtCurrentPoint(Vector3 currentLocalPoint)
        {
            if (!TryMapCurrentPointToRest(currentLocalPoint, out Vector2 restPoint))
            {
                return false;
            }

            if (alphaMask.Length == 0 || alphaMaskWidth <= 0 || alphaMaskHeight <= 0)
            {
                return IsInsideVisibleShape(restPoint);
            }

            float normalizedX = (restPoint.x - gridMinimum.x)
                / Mathf.Max(0.0001f, gridSize.x);
            float normalizedY = (restPoint.y - gridMinimum.y)
                / Mathf.Max(0.0001f, gridSize.y);
            if (normalizedX < 0f || normalizedX > 1f
                || normalizedY < 0f || normalizedY > 1f)
            {
                return false;
            }

            int x = Mathf.Clamp(
                Mathf.RoundToInt(normalizedX * (alphaMaskWidth - 1)),
                0,
                alphaMaskWidth - 1);
            int y = Mathf.Clamp(
                Mathf.RoundToInt(normalizedY * (alphaMaskHeight - 1)),
                0,
                alphaMaskHeight - 1);
            return IsCombinedSolid(x, y, 16);
        }

        public bool TryGetCurrentSurfacePoint(
            Vector2 normalizedUv,
            out Vector3 currentLocalPoint)
        {
            currentLocalPoint = default(Vector3);
            if (normalizedUv.x < 0f || normalizedUv.x > 1f
                || normalizedUv.y < 0f || normalizedUv.y > 1f
                || restVertices.Length == 0
                || logicalVertices.Length != restVertices.Length)
            {
                return false;
            }

            Vector2 restPoint = gridMinimum + Vector2.Scale(normalizedUv, gridSize);
            const float tolerance = -0.0001f;
            for (int triangle = 0; triangle + 2 < logicalTriangles.Length; triangle += 3)
            {
                int a = logicalTriangles[triangle];
                int b = logicalTriangles[triangle + 1];
                int c = logicalTriangles[triangle + 2];
                if (!TryGetBarycentricCoordinates(
                        restPoint,
                        restVertices[a],
                        restVertices[b],
                        restVertices[c],
                        out Vector3 barycentric)
                    || barycentric.x < tolerance
                    || barycentric.y < tolerance
                    || barycentric.z < tolerance)
                {
                    continue;
                }

                float total = barycentric.x + barycentric.y + barycentric.z;
                if (Mathf.Abs(total) > 0.000001f)
                {
                    barycentric /= total;
                }

                currentLocalPoint = logicalVertices[a] * barycentric.x
                    + logicalVertices[b] * barycentric.y
                    + logicalVertices[c] * barycentric.z;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Converts UV to the authored/rest surface without requiring a triangle at that
        /// location. This remains valid directly on a baked cut where physical triangles
        /// have deliberately been removed, so editor curve handles never disappear.
        /// </summary>
        public bool TryGetAuthoringSurfacePoint(
            Vector2 normalizedUv,
            out Vector3 localPoint)
        {
            localPoint = default(Vector3);
            if (normalizedUv.x < 0f || normalizedUv.x > 1f
                || normalizedUv.y < 0f || normalizedUv.y > 1f
                || gridSize.x <= 0f || gridSize.y <= 0f)
            {
                return false;
            }

            Vector2 planarPoint = gridMinimum + Vector2.Scale(normalizedUv, gridSize);
            // Authoring handles use an always-visible overlay, so they only need stable
            // material-space X/Y. Avoid a nearest-vertex scan for every sampled curve point.
            localPoint = new Vector3(planarPoint.x, planarPoint.y, 0f);
            return true;
        }

        /// <summary>
        /// Direct inverse of the authoring UV mapping. Unlike the runtime surface query it
        /// does not need a surviving triangle underneath the cursor.
        /// </summary>
        public bool TryGetAuthoringNormalizedUv(
            Vector3 localPoint,
            out Vector2 normalizedUv)
        {
            normalizedUv = default(Vector2);
            if (gridSize.x <= 0f || gridSize.y <= 0f)
            {
                return false;
            }

            normalizedUv = new Vector2(
                (localPoint.x - gridMinimum.x) / gridSize.x,
                (localPoint.y - gridMinimum.y) / gridSize.y);
            return normalizedUv.x >= 0f && normalizedUv.x <= 1f
                && normalizedUv.y >= 0f && normalizedUv.y <= 1f;
        }

        public bool ApplyOpenCut(
            IList<Vector2> normalizedPath,
            float worldWidth,
            bool rebuildPhysicalTopology = true,
            bool replaceCutVisualization = false)
        {
            IList<Vector2>[] paths = { normalizedPath };
            return ApplyOpenCuts(
                paths,
                worldWidth,
                rebuildPhysicalTopology,
                replaceCutVisualization,
                true);
        }

        /// <summary>
        /// Applies several independent open incisions with one physical topology rebuild.
        /// This is substantially faster than calling ApplyOpenCut once per path while
        /// preserving a separate mechanical barrier for every incision.
        /// </summary>
        public bool ApplyOpenCuts(
            IList<IList<Vector2>> normalizedPaths,
            float worldWidth,
            bool rebuildPhysicalTopology = true,
            bool replaceCutVisualization = false,
            bool showCutVisualization = true)
        {
            if (normalizedPaths == null || normalizedPaths.Count == 0 || cutMaskAlpha.Length == 0)
            {
                return false;
            }

            List<IList<Vector2>> validPaths = new List<IList<Vector2>>(normalizedPaths.Count);
            List<Vector2[]> physicalCutPolygons = rebuildPhysicalTopology
                ? new List<Vector2[]>()
                : null;
            float normalizedWidth = worldWidth
                / Mathf.Max(0.0001f, Mathf.Min(gridSize.x, gridSize.y));
            for (int pathIndex = 0; pathIndex < normalizedPaths.Count; pathIndex++)
            {
                IList<Vector2> path = normalizedPaths[pathIndex];
                if (path == null || path.Count < 2)
                {
                    continue;
                }

                if (rebuildPhysicalTopology)
                {
                    List<Vector2[]> pathPolygons =
                        DeformableImagePolygonUtility.BuildOpenCutPolygons(
                            path,
                            normalizedWidth);
                    if (pathPolygons.Count == 0)
                    {
                        continue;
                    }

                    physicalCutPolygons.AddRange(pathPolygons);
                }

                validPaths.Add(path);
            }

            if (validPaths.Count == 0
                || rebuildPhysicalTopology && physicalCutPolygons.Count == 0)
            {
                return false;
            }

            bool changed = false;
            List<int> changedPixelIndices = new List<int>();
            List<byte> previousPixelValues = new List<byte>();
            if (showCutVisualization && replaceCutVisualization)
            {
                if (cutVisualAlpha.Length != cutMaskAlpha.Length)
                {
                    cutVisualAlpha = new byte[cutMaskAlpha.Length];
                }
                else
                {
                    Array.Clear(cutVisualAlpha, 0, cutVisualAlpha.Length);
                }
            }

            int dirtyMinX = alphaMaskWidth;
            int dirtyMinY = alphaMaskHeight;
            int dirtyMaxX = -1;
            int dirtyMaxY = -1;
            float radius = Mathf.Max(0.0005f, worldWidth * 0.5f);
            float radiusSquared = radius * radius;
            int paddingX = Mathf.CeilToInt(radius / Mathf.Max(0.0001f, gridSize.x) * alphaMaskWidth) + 1;
            int paddingY = Mathf.CeilToInt(radius / Mathf.Max(0.0001f, gridSize.y) * alphaMaskHeight) + 1;

            // Rasterize only the small pixel rectangles around each knife segment. The old
            // implementation tested every source pixel against a Clipper polygon, which was
            // needlessly expensive for high-resolution portraits.
            for (int pathIndex = 0; pathIndex < validPaths.Count; pathIndex++)
            {
                IList<Vector2> normalizedPath = validPaths[pathIndex];
                for (int segment = 0; segment + 1 < normalizedPath.Count; segment++)
                {
                    Vector2 normalizedStart = normalizedPath[segment];
                    Vector2 normalizedEnd = normalizedPath[segment + 1];
                    Vector2 localStart = gridMinimum + Vector2.Scale(normalizedStart, gridSize);
                    Vector2 localEnd = gridMinimum + Vector2.Scale(normalizedEnd, gridSize);
                    int startX = Mathf.RoundToInt(normalizedStart.x * (alphaMaskWidth - 1));
                    int startY = Mathf.RoundToInt(normalizedStart.y * (alphaMaskHeight - 1));
                    int endX = Mathf.RoundToInt(normalizedEnd.x * (alphaMaskWidth - 1));
                    int endY = Mathf.RoundToInt(normalizedEnd.y * (alphaMaskHeight - 1));
                    int minX = Mathf.Clamp(Mathf.Min(startX, endX) - paddingX, 0, alphaMaskWidth - 1);
                    int maxX = Mathf.Clamp(Mathf.Max(startX, endX) + paddingX, 0, alphaMaskWidth - 1);
                    int minY = Mathf.Clamp(Mathf.Min(startY, endY) - paddingY, 0, alphaMaskHeight - 1);
                    int maxY = Mathf.Clamp(Mathf.Max(startY, endY) + paddingY, 0, alphaMaskHeight - 1);

                    for (int y = minY; y <= maxY; y++)
                    {
                        float normalizedY = (y + 0.5f) / alphaMaskHeight;
                        for (int x = minX; x <= maxX; x++)
                        {
                            int index = y * alphaMaskWidth + x;
                            if (cutMaskAlpha[index] == 0 || alphaMask[index] < 16)
                            {
                                continue;
                            }

                            Vector2 normalizedPoint = new Vector2(
                                (x + 0.5f) / alphaMaskWidth,
                                normalizedY);
                            Vector2 localPoint = gridMinimum
                                + Vector2.Scale(normalizedPoint, gridSize);
                            if (DistanceSquaredToSegment(
                                    localPoint,
                                    localStart,
                                    localEnd) > radiusSquared)
                            {
                                continue;
                            }

                            changedPixelIndices.Add(index);
                            previousPixelValues.Add(cutMaskAlpha[index]);
                            cutMaskAlpha[index] = 0;
                            if (showCutVisualization && replaceCutVisualization)
                            {
                                cutVisualAlpha[index] = 255;
                            }
                            changed = true;
                            dirtyMinX = Mathf.Min(dirtyMinX, x);
                            dirtyMinY = Mathf.Min(dirtyMinY, y);
                            dirtyMaxX = Mathf.Max(dirtyMaxX, x);
                            dirtyMaxY = Mathf.Max(dirtyMaxY, y);
                        }
                    }
                }
            }

            if (changed)
            {
                if (rebuildPhysicalTopology && !ApplyPhysicalCut(physicalCutPolygons))
                {
                    for (int changedIndex = 0;
                        changedIndex < changedPixelIndices.Count;
                        changedIndex++)
                    {
                        cutMaskAlpha[changedPixelIndices[changedIndex]] =
                            previousPixelValues[changedIndex];
                    }

                    Debug.LogError(
                        "Physical topology cut failed. The visual cut was rolled back so rendering cannot disagree with the mesh.",
                        this);
                    return false;
                }

                if (rebuildPhysicalTopology)
                {
                    for (int pathIndex = 0; pathIndex < validPaths.Count; pathIndex++)
                    {
                        IList<Vector2> normalizedPath = validPaths[pathIndex];
                        Vector2[] localBarrierPoints = new Vector2[normalizedPath.Count];
                        for (int pointIndex = 0;
                            pointIndex < normalizedPath.Count;
                            pointIndex++)
                        {
                            localBarrierPoints[pointIndex] = gridMinimum
                                + Vector2.Scale(normalizedPath[pointIndex], gridSize);
                        }

                        openCutBarriers.Add(new CutBarrier(localBarrierPoints, radius));
                    }
                }

                hasAppliedCuts = true;
                InvalidateDeformationGraph();
                InvalidateCutComponents();
                if (showCutVisualization && !replaceCutVisualization)
                {
                    UpdateCutBoundaryVisualization(dirtyMinX, dirtyMinY, dirtyMaxX, dirtyMaxY);
                }
                UploadCutMaskRegion(dirtyMinX, dirtyMinY, dirtyMaxX, dirtyMaxY);
            }

            return changed;
        }

        public bool CouldCloseRegionWithExistingCuts(
            IList<Vector2> normalizedPath,
            float worldWidth)
        {
            if (normalizedPath == null
                || normalizedPath.Count < 2
                || openCutBarriers.Count < 2)
            {
                return false;
            }

            Vector2[] localPoints = new Vector2[normalizedPath.Count];
            for (int point = 0; point < normalizedPath.Count; point++)
            {
                localPoints[point] = gridMinimum
                    + Vector2.Scale(normalizedPath[point], gridSize);
            }

            int intersectedStrokes = 0;
            float newRadius = Mathf.Max(0.0005f, worldWidth * 0.5f);
            for (int barrierIndex = 0;
                barrierIndex < openCutBarriers.Count;
                barrierIndex++)
            {
                CutBarrier barrier = openCutBarriers[barrierIndex];
                float joinRadius = newRadius + barrier.Radius;
                float joinRadiusSquared = joinRadius * joinRadius;
                bool intersectsBarrier = false;
                for (int newSegment = 0;
                    newSegment + 1 < localPoints.Length && !intersectsBarrier;
                    newSegment++)
                {
                    for (int oldSegment = 0;
                        oldSegment + 1 < barrier.Points.Length;
                        oldSegment++)
                    {
                        if (DistanceSquaredBetweenSegments(
                                localPoints[newSegment],
                                localPoints[newSegment + 1],
                                barrier.Points[oldSegment],
                                barrier.Points[oldSegment + 1]) <= joinRadiusSquared)
                        {
                            intersectsBarrier = true;
                            break;
                        }
                    }
                }

                if (intersectsBarrier && ++intersectedStrokes >= 2)
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryExtractEnclosedCutComponent(
            float minimumNormalizedArea,
            out DeformableImageMesh piece)
        {
            piece = null;
            EnsureCutComponentLabels();
            int pixelCount = alphaMaskWidth * alphaMaskHeight;
            if (cutComponentCount < 2
                || pixelCount <= 0
                || cutComponentLabels.Length != pixelCount
                || cutMaskAlpha.Length != pixelCount)
            {
                return false;
            }

            int[] componentPixelCounts = new int[cutComponentCount + 1];
            bool[] touchesSourceExterior = new bool[cutComponentCount + 1];
            const byte solidThreshold = 16;
            for (int y = 0; y < alphaMaskHeight; y++)
            {
                int row = y * alphaMaskWidth;
                for (int x = 0; x < alphaMaskWidth; x++)
                {
                    int label = cutComponentLabels[row + x];
                    if (label <= 0)
                    {
                        continue;
                    }

                    componentPixelCounts[label]++;
                    if (!touchesSourceExterior[label]
                        && PixelTouchesSourceExterior(x, y, solidThreshold))
                    {
                        touchesSourceExterior[label] = true;
                    }
                }
            }

            int minimumPixels = Mathf.Max(
                4,
                Mathf.CeilToInt(Mathf.Max(0f, minimumNormalizedArea) * pixelCount));
            int selectedLabel = 0;
            int selectedPixelCount = 0;
            for (int label = 1; label <= cutComponentCount; label++)
            {
                if (touchesSourceExterior[label]
                    || componentPixelCounts[label] < minimumPixels
                    || componentPixelCounts[label] <= selectedPixelCount)
                {
                    continue;
                }

                selectedLabel = label;
                selectedPixelCount = componentPixelCounts[label];
            }

            if (selectedLabel == 0)
            {
                return false;
            }

            List<Vector2[]> componentRuns = BuildComponentRowRunPolygons(selectedLabel);
            if (!DeformableImagePolygonUtility.TryNormalizePaths(
                    componentRuns,
                    out List<Vector2[]> componentContours)
                || !DeformableImagePolygonUtility.TryTriangulatePaths(
                    componentContours,
                    out DeformableImagePolygonUtility.TriangulatedPolygon triangulation))
            {
                return false;
            }

            byte[] pieceAlpha = new byte[pixelCount];
            for (int index = 0; index < pixelCount; index++)
            {
                if (cutComponentLabels[index] == selectedLabel)
                {
                    pieceAlpha[index] = cutMaskAlpha[index];
                }
            }

            piece = CreateRuntimePiece(pieceAlpha, triangulation);
            if (piece == null)
            {
                return false;
            }

            // The surrounding open strokes have already severed this island from the
            // rest of the cloth. Remove the island triangles from the parent mesh so it
            // exists only in the new movable piece.
            if (!ApplyPhysicalCut(componentContours))
            {
                DestroyGeneratedObject(piece.gameObject);
                piece = null;
                return false;
            }

            for (int index = 0; index < pixelCount; index++)
            {
                if (pieceAlpha[index] != 0)
                {
                    cutMaskAlpha[index] = 0;
                }
            }

            RebuildCutBoundaryVisualization();
            hasAppliedCuts = true;
            InvalidateDeformationGraph();
            InvalidateCutComponents();
            UploadCutMask();
            return true;
        }

        private bool PixelTouchesSourceExterior(int x, int y, byte solidThreshold)
        {
            if (x <= 0 || x >= alphaMaskWidth - 1
                || y <= 0 || y >= alphaMaskHeight - 1)
            {
                return true;
            }

            // Use eight neighbours so antialiased or diagonal Sprite outlines are still
            // classified as the original exterior, not as knife-created enclosures.
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                int row = (y + offsetY) * alphaMaskWidth;
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if ((offsetX != 0 || offsetY != 0)
                        && alphaMask[row + x + offsetX] < solidThreshold)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private List<Vector2[]> BuildComponentRowRunPolygons(int label)
        {
            List<Vector2[]> runs = new List<Vector2[]>(alphaMaskHeight);
            float inverseWidth = 1f / Mathf.Max(1, alphaMaskWidth);
            float inverseHeight = 1f / Mathf.Max(1, alphaMaskHeight);
            for (int y = 0; y < alphaMaskHeight; y++)
            {
                int row = y * alphaMaskWidth;
                int x = 0;
                while (x < alphaMaskWidth)
                {
                    while (x < alphaMaskWidth && cutComponentLabels[row + x] != label)
                    {
                        x++;
                    }

                    int runStart = x;
                    while (x < alphaMaskWidth && cutComponentLabels[row + x] == label)
                    {
                        x++;
                    }

                    if (runStart >= x)
                    {
                        continue;
                    }

                    float minimumX = runStart * inverseWidth;
                    float maximumX = x * inverseWidth;
                    float minimumY = y * inverseHeight;
                    float maximumY = (y + 1) * inverseHeight;
                    runs.Add(new[]
                    {
                        new Vector2(minimumX, minimumY),
                        new Vector2(maximumX, minimumY),
                        new Vector2(maximumX, maximumY),
                        new Vector2(minimumX, maximumY)
                    });
                }
            }

            return runs;
        }

        public bool ApplyClosedCut(IList<Vector2> normalizedPolygon, out DeformableImageMesh piece)
        {
            piece = null;
            if (normalizedPolygon == null || normalizedPolygon.Count < 3 || cutMaskAlpha.Length == 0)
            {
                return false;
            }

            if (!DeformableImagePolygonUtility.TryTriangulateClosedPath(
                    normalizedPolygon,
                    out DeformableImagePolygonUtility.TriangulatedPolygon triangulation))
            {
                return false;
            }

            byte[] pieceAlpha = new byte[cutMaskAlpha.Length];
            int retainedSolidPixels = 0;

            // Rasterize only each tessellated triangle's pixel bounds. The previous
            // implementation scanned the complete source texture and then searched
            // every polygon triangle for every pixel, which became very slow on 2K/4K faces.
            const float barycentricTolerance = -0.00001f;
            for (int triangle = 0;
                triangle + 2 < triangulation.Triangles.Length;
                triangle += 3)
            {
                Vector2 a = triangulation.Vertices[triangulation.Triangles[triangle]];
                Vector2 b = triangulation.Vertices[triangulation.Triangles[triangle + 1]];
                Vector2 c = triangulation.Vertices[triangulation.Triangles[triangle + 2]];
                Vector2 minimum = Vector2.Min(a, Vector2.Min(b, c));
                Vector2 maximum = Vector2.Max(a, Vector2.Max(b, c));
                int minimumX = Mathf.Clamp(
                    Mathf.FloorToInt(minimum.x * alphaMaskWidth),
                    0,
                    alphaMaskWidth - 1);
                int maximumX = Mathf.Clamp(
                    Mathf.CeilToInt(maximum.x * alphaMaskWidth) - 1,
                    0,
                    alphaMaskWidth - 1);
                int minimumY = Mathf.Clamp(
                    Mathf.FloorToInt(minimum.y * alphaMaskHeight),
                    0,
                    alphaMaskHeight - 1);
                int maximumY = Mathf.Clamp(
                    Mathf.CeilToInt(maximum.y * alphaMaskHeight) - 1,
                    0,
                    alphaMaskHeight - 1);

                for (int y = minimumY; y <= maximumY; y++)
                {
                    float normalizedY = (y + 0.5f) / alphaMaskHeight;
                    for (int x = minimumX; x <= maximumX; x++)
                    {
                        int index = y * alphaMaskWidth + x;
                        if (cutMaskAlpha[index] == 0 || alphaMask[index] < 16)
                        {
                            continue;
                        }

                        Vector2 point = new Vector2(
                            (x + 0.5f) / alphaMaskWidth,
                            normalizedY);
                        if (!TryGetBarycentricCoordinates(point, a, b, c, out Vector3 barycentric)
                            || barycentric.x < barycentricTolerance
                            || barycentric.y < barycentricTolerance
                            || barycentric.z < barycentricTolerance)
                        {
                            continue;
                        }

                        pieceAlpha[index] = cutMaskAlpha[index];
                        retainedSolidPixels++;
                    }
                }
            }

            if (retainedSolidPixels == 0)
            {
                return false;
            }

            piece = CreateRuntimePiece(pieceAlpha, triangulation);
            if (piece == null)
            {
                return false;
            }

            Vector2[] cutterPath = new Vector2[normalizedPolygon.Count];
            for (int index = 0; index < normalizedPolygon.Count; index++)
            {
                cutterPath[index] = normalizedPolygon[index];
            }

            List<Vector2[]> cutterInput = new List<Vector2[]> { cutterPath };
            if (!DeformableImagePolygonUtility.TryNormalizePaths(
                    cutterInput,
                    out List<Vector2[]> normalizedCutters)
                || !ApplyPhysicalCut(normalizedCutters))
            {
                if (piece != null)
                {
                    DestroyGeneratedObject(piece.gameObject);
                    piece = null;
                }

                Debug.LogError(
                    "Physical closed topology cut failed. The detached piece and visual mask were rolled back.",
                    this);
                return false;
            }

            for (int index = 0; index < pieceAlpha.Length; index++)
            {
                if (pieceAlpha[index] != 0)
                {
                    cutMaskAlpha[index] = 0;
                }
            }

            RebuildCutBoundaryVisualization();
            hasAppliedCuts = true;
            InvalidateDeformationGraph();
            InvalidateCutComponents();
            UploadCutMask();
            return true;
        }

        private bool ApplyPhysicalCut(IList<Vector2[]> cutterPolygons)
        {
            if (cutterPolygons == null
                || cutterPolygons.Count == 0
                || logicalTriangles.Length < 3)
            {
                return false;
            }

            Vector2 cutterMinimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 cutterMaximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int pathIndex = 0; pathIndex < cutterPolygons.Count; pathIndex++)
            {
                Vector2[] path = cutterPolygons[pathIndex];
                for (int pointIndex = 0; pointIndex < path.Length; pointIndex++)
                {
                    cutterMinimum = Vector2.Min(cutterMinimum, path[pointIndex]);
                    cutterMaximum = Vector2.Max(cutterMaximum, path[pointIndex]);
                }
            }

            Vector3[] sourceRest = restVertices;
            Vector3[] sourcePoseRest = poseRestVertices;
            Vector3[] sourceCurrent = logicalVertices;
            int[] sourceTriangles = logicalTriangles;
            List<Vector3> newRest = new List<Vector3>(sourceRest.Length + 128);
            List<Vector3> newPoseRest = new List<Vector3>(sourceRest.Length + 128);
            List<Vector3> newCurrent = new List<Vector3>(sourceCurrent.Length + 128);
            List<Vector2> newNormalizedUvs = new List<Vector2>(sourceRest.Length + 128);
            List<int> newTriangles = new List<int>(sourceTriangles.Length + 256);
            Dictionary<long, int> vertexLookup = new Dictionary<long, int>();
            int conservativeFallbackTriangles = 0;

            for (int triangle = 0; triangle < sourceTriangles.Length; triangle += 3)
            {
                int sourceA = sourceTriangles[triangle];
                int sourceB = sourceTriangles[triangle + 1];
                int sourceC = sourceTriangles[triangle + 2];
                Vector2 normalizedA = RestPointToNormalized(sourceRest[sourceA]);
                Vector2 normalizedB = RestPointToNormalized(sourceRest[sourceB]);
                Vector2 normalizedC = RestPointToNormalized(sourceRest[sourceC]);
                Vector2 triangleMinimum = Vector2.Min(normalizedA, Vector2.Min(normalizedB, normalizedC));
                Vector2 triangleMaximum = Vector2.Max(normalizedA, Vector2.Max(normalizedB, normalizedC));

                if (triangleMaximum.x < cutterMinimum.x
                    || triangleMaximum.y < cutterMinimum.y
                    || triangleMinimum.x > cutterMaximum.x
                    || triangleMinimum.y > cutterMaximum.y)
                {
                    AddClippedTriangle(
                        normalizedA,
                        normalizedB,
                        normalizedC,
                        sourceA,
                        sourceB,
                        sourceC,
                        sourceRest,
                        sourcePoseRest,
                        sourceCurrent,
                        vertexLookup,
                        newRest,
                        newPoseRest,
                        newCurrent,
                        newNormalizedUvs,
                        newTriangles);
                    continue;
                }

                List<Vector2[]> subject = new List<Vector2[]>
                {
                    new[] { normalizedA, normalizedB, normalizedC }
                };
                if (!DeformableImagePolygonUtility.TryBooleanDifference(
                        subject,
                        cutterPolygons,
                        out List<Vector2[]> retainedPaths))
                {
                    if (TriangleIntersectsCutters(
                            normalizedA,
                            normalizedB,
                            normalizedC,
                            cutterPolygons))
                    {
                        conservativeFallbackTriangles++;
                        continue;
                    }

                    AddClippedTriangle(
                        normalizedA,
                        normalizedB,
                        normalizedC,
                        sourceA,
                        sourceB,
                        sourceC,
                        sourceRest,
                        sourcePoseRest,
                        sourceCurrent,
                        vertexLookup,
                        newRest,
                        newPoseRest,
                        newCurrent,
                        newNormalizedUvs,
                        newTriangles);
                    continue;
                }

                if (retainedPaths.Count == 0)
                {
                    continue;
                }

                if (!DeformableImagePolygonUtility.TryTriangulatePaths(
                        retainedPaths,
                        out DeformableImagePolygonUtility.TriangulatedPolygon clipped))
                {
                    // Degenerate slivers can occasionally make LibTess reject one source
                    // triangle. Removing that triangle is mechanically safe: the gap may
                    // become slightly wider, but no edge can survive across the knife.
                    conservativeFallbackTriangles++;
                    continue;
                }

                for (int clippedTriangle = 0;
                    clippedTriangle < clipped.Triangles.Length;
                    clippedTriangle += 3)
                {
                    AddClippedTriangle(
                        clipped.Vertices[clipped.Triangles[clippedTriangle]],
                        clipped.Vertices[clipped.Triangles[clippedTriangle + 1]],
                        clipped.Vertices[clipped.Triangles[clippedTriangle + 2]],
                        sourceA,
                        sourceB,
                        sourceC,
                        sourceRest,
                        sourcePoseRest,
                        sourceCurrent,
                        vertexLookup,
                        newRest,
                        newPoseRest,
                        newCurrent,
                        newNormalizedUvs,
                        newTriangles);
                }
            }

            if (newTriangles.Count < 3 || newRest.Count < 3)
            {
                return false;
            }

            if (conservativeFallbackTriangles > 0)
            {
                Debug.LogWarning(
                    "Physical cut used the conservative fallback for "
                    + conservativeFallbackTriangles
                    + " degenerate triangle(s). Those triangles were removed to guarantee disconnection.",
                    this);
            }

            restVertices = newRest.ToArray();
            poseRestVertices = newPoseRest.ToArray();
            logicalVertices = newCurrent.ToArray();
            logicalTriangles = newTriangles.ToArray();
            RebuildPhysicalRenderMesh(newNormalizedUvs.ToArray());
            InvalidateDeformationGraph();
            InvalidateCutComponents();
            MeshRebuilt?.Invoke();
            VerticesChanged?.Invoke();
            return true;
        }

        private static bool TriangleIntersectsCutters(
            Vector2 a,
            Vector2 b,
            Vector2 c,
            IList<Vector2[]> cutterPolygons)
        {
            if (DeformableImagePolygonUtility.ContainsPoint(cutterPolygons, a)
                || DeformableImagePolygonUtility.ContainsPoint(cutterPolygons, b)
                || DeformableImagePolygonUtility.ContainsPoint(cutterPolygons, c))
            {
                return true;
            }

            for (int polygonIndex = 0; polygonIndex < cutterPolygons.Count; polygonIndex++)
            {
                Vector2[] polygon = cutterPolygons[polygonIndex];
                if (polygon == null || polygon.Length < 3)
                {
                    continue;
                }

                for (int pointIndex = 0; pointIndex < polygon.Length; pointIndex++)
                {
                    Vector2 point = polygon[pointIndex];
                    if (TryGetBarycentricCoordinates(point, a, b, c, out Vector3 barycentric)
                        && barycentric.x >= -0.00001f
                        && barycentric.y >= -0.00001f
                        && barycentric.z >= -0.00001f)
                    {
                        return true;
                    }

                    Vector2 next = polygon[(pointIndex + 1) % polygon.Length];
                    if (SegmentsIntersect(point, next, a, b)
                        || SegmentsIntersect(point, next, b, c)
                        || SegmentsIntersect(point, next, c, a))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void AddClippedTriangle(
            Vector2 normalizedA,
            Vector2 normalizedB,
            Vector2 normalizedC,
            int sourceA,
            int sourceB,
            int sourceC,
            Vector3[] sourceRest,
            Vector3[] sourcePoseRest,
            Vector3[] sourceCurrent,
            Dictionary<long, int> vertexLookup,
            List<Vector3> newRest,
            List<Vector3> newPoseRest,
            List<Vector3> newCurrent,
            List<Vector2> newNormalizedUvs,
            List<int> newTriangles)
        {
            int a = AddClippedVertex(
                normalizedA, sourceA, sourceB, sourceC, sourceRest, sourcePoseRest, sourceCurrent,
                vertexLookup, newRest, newPoseRest, newCurrent, newNormalizedUvs);
            int b = AddClippedVertex(
                normalizedB, sourceA, sourceB, sourceC, sourceRest, sourcePoseRest, sourceCurrent,
                vertexLookup, newRest, newPoseRest, newCurrent, newNormalizedUvs);
            int c = AddClippedVertex(
                normalizedC, sourceA, sourceB, sourceC, sourceRest, sourcePoseRest, sourceCurrent,
                vertexLookup, newRest, newPoseRest, newCurrent, newNormalizedUvs);
            if (a == b || b == c || c == a)
            {
                return;
            }

            float area = SignedDoubleArea(newRest[a], newRest[b], newRest[c]);
            if (Mathf.Abs(area) < 0.0000001f)
            {
                return;
            }

            newTriangles.Add(a);
            if (area < 0f)
            {
                newTriangles.Add(b);
                newTriangles.Add(c);
            }
            else
            {
                newTriangles.Add(c);
                newTriangles.Add(b);
            }
        }

        private int AddClippedVertex(
            Vector2 normalized,
            int sourceA,
            int sourceB,
            int sourceC,
            Vector3[] sourceRest,
            Vector3[] sourcePoseRest,
            Vector3[] sourceCurrent,
            Dictionary<long, int> vertexLookup,
            List<Vector3> newRest,
            List<Vector3> newPoseRest,
            List<Vector3> newCurrent,
            List<Vector2> newNormalizedUvs)
        {
            normalized = new Vector2(Mathf.Clamp01(normalized.x), Mathf.Clamp01(normalized.y));
            long key = NormalizedVertexKey(normalized);
            if (vertexLookup.TryGetValue(key, out int existing))
            {
                return existing;
            }

            Vector3 restPoint = gridMinimum + Vector2.Scale(normalized, gridSize);
            Vector3 poseRestPoint = restPoint;
            Vector3 currentPoint = restPoint;
            if (TryGetBarycentricCoordinates(
                    restPoint,
                    sourceRest[sourceA],
                    sourceRest[sourceB],
                    sourceRest[sourceC],
                    out Vector3 barycentric))
            {
                barycentric.x = Mathf.Clamp01(barycentric.x);
                barycentric.y = Mathf.Clamp01(barycentric.y);
                barycentric.z = Mathf.Clamp01(barycentric.z);
                float sum = Mathf.Max(0.000001f, barycentric.x + barycentric.y + barycentric.z);
                barycentric /= sum;
                poseRestPoint = sourcePoseRest[sourceA] * barycentric.x
                    + sourcePoseRest[sourceB] * barycentric.y
                    + sourcePoseRest[sourceC] * barycentric.z;
                currentPoint = sourceCurrent[sourceA] * barycentric.x
                    + sourceCurrent[sourceB] * barycentric.y
                    + sourceCurrent[sourceC] * barycentric.z;
            }

            int index = newRest.Count;
            vertexLookup.Add(key, index);
            newRest.Add(restPoint);
            newPoseRest.Add(poseRestPoint);
            newCurrent.Add(currentPoint);
            newNormalizedUvs.Add(normalized);
            return index;
        }

        private void RebuildFromBakedOpenCutTopology()
        {
            DeformableImageMesh topology = GetBakedTopologyOwner();
            if (topology == null)
            {
                return;
            }

            float spriteWidth = Mathf.Max(1f, frontSprite.rect.width);
            float spriteHeight = Mathf.Max(1f, frontSprite.rect.height);
            float width = height * spriteWidth / spriteHeight;
            Vector2 normalizedPivot = respectSpritePivot
                ? new Vector2(
                    frontSprite.pivot.x / spriteWidth,
                    frontSprite.pivot.y / spriteHeight)
                : new Vector2(0.5f, 0.5f);
            gridMinimum = new Vector2(
                -width * normalizedPivot.x,
                -height * normalizedPivot.y);
            gridSize = new Vector2(width, height);

            Vector2[] normalizedVertices = topology.bakedNormalizedVertices;
            int[] topologyTriangles = topology.bakedLogicalTriangles;
            BakedOpenCutPath[] topologyPaths = topology.bakedOpenCutPaths;
            float topologyCutWidth = topology.bakedOpenCutWidth;
            int logicalCount = normalizedVertices.Length;
            restVertices = new Vector3[logicalCount];
            poseRestVertices = new Vector3[logicalCount];
            logicalVertices = new Vector3[logicalCount];
            for (int index = 0; index < logicalCount; index++)
            {
                Vector2 normalized = normalizedVertices[index];
                Vector3 point = gridMinimum + Vector2.Scale(normalized, gridSize);
                restVertices[index] = point;
                poseRestVertices[index] = point;
                logicalVertices[index] = point;
            }

            logicalTriangles = (int[])topologyTriangles.Clone();
            RebuildPhysicalRenderMesh(normalizedVertices);
            generatedMesh.name = "Editor-Baked Cut Deformable Image Mesh";
            RebuildVisibleContours();
            RebuildAlphaMask();
            CreateSolidCutMask();

            List<IList<Vector2>> paths = new List<IList<Vector2>>(topologyPaths.Length);
            for (int pathIndex = 0; pathIndex < topologyPaths.Length; pathIndex++)
            {
                Vector2[] points = topologyPaths[pathIndex] != null
                    ? topologyPaths[pathIndex].Points
                    : null;
                if (points != null && points.Length >= 2)
                {
                    paths.Add(points);
                }
            }

            // This pass only restores the tiny alpha slit. No polygon clipping or
            // triangulation occurs because the physical topology is already serialized.
            if (paths.Count > 0)
            {
                ApplyOpenCuts(paths, topologyCutWidth, false, false, false);
                ClearRedCutVisualization();
                float radius = Mathf.Max(0.0005f, topologyCutWidth * 0.5f);
                openCutBarriers.Clear();
                for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
                {
                    IList<Vector2> path = paths[pathIndex];
                    Vector2[] localPoints = new Vector2[path.Count];
                    for (int pointIndex = 0; pointIndex < path.Count; pointIndex++)
                    {
                        localPoints[pointIndex] = gridMinimum
                            + Vector2.Scale(path[pointIndex], gridSize);
                    }

                    openCutBarriers.Add(new CutBarrier(localPoints, radius));
                }

                hasAppliedCuts = true;
                InvalidateDeformationGraph();
                InvalidateCutComponents();
            }

            ApplyTextures();
            MeshRebuilt?.Invoke();
        }

        private DeformableImageMesh GetBakedTopologyOwner()
        {
            if (!useBakedOpenCutTopology)
            {
                return null;
            }

            DeformableImageMesh candidate = bakedTopologySource != null
                && bakedTopologySource != this
                ? bakedTopologySource
                : this;
            return candidate.useBakedOpenCutTopology
                && candidate.bakedNormalizedVertices != null
                && candidate.bakedNormalizedVertices.Length >= 3
                && candidate.bakedLogicalTriangles != null
                && candidate.bakedLogicalTriangles.Length >= 3
                && candidate.bakedOpenCutPaths != null
                && candidate.bakedOpenCutPaths.Length > 0
                ? candidate
                : null;
        }

        private void RebuildPhysicalRenderMesh(Vector2[] normalizedUvs)
        {
            int logicalCount = logicalVertices.Length;
            renderVertices = new Vector3[logicalCount * 2];
            Vector2[] renderUvs = new Vector2[logicalCount * 2];
            Vector2[] renderCutUvs = new Vector2[logicalCount * 2];
            Vector3[] normals = new Vector3[logicalCount * 2];
            Sprite actualBack = backSprite != null ? backSprite : frontSprite;

            for (int index = 0; index < logicalCount; index++)
            {
                Vector2 normalized = normalizedUvs[index];
                renderVertices[index] = logicalVertices[index];
                renderVertices[index + logicalCount] = logicalVertices[index];
                renderUvs[index] = SpriteUv(frontSprite, normalized.x, normalized.y);
                renderUvs[index + logicalCount] = SpriteUv(
                    actualBack,
                    flipBackHorizontally ? 1f - normalized.x : normalized.x,
                    normalized.y);
                renderCutUvs[index] = normalized;
                renderCutUvs[index + logicalCount] = normalized;
                normals[index] = Vector3.back;
                normals[index + logicalCount] = Vector3.forward;
            }

            int[] backTriangles = new int[logicalTriangles.Length];
            for (int triangle = 0; triangle < logicalTriangles.Length; triangle += 3)
            {
                backTriangles[triangle] = logicalTriangles[triangle] + logicalCount;
                backTriangles[triangle + 1] = logicalTriangles[triangle + 2] + logicalCount;
                backTriangles[triangle + 2] = logicalTriangles[triangle + 1] + logicalCount;
            }

            ReplaceGeneratedMesh();
            generatedMesh.name = "Physically Cut Deformable Image Mesh";
            generatedMesh.indexFormat = renderVertices.Length > 65535
                ? IndexFormat.UInt32
                : IndexFormat.UInt16;
            generatedMesh.MarkDynamic();
            generatedMesh.vertices = renderVertices;
            generatedMesh.uv = renderUvs;
            generatedMesh.uv2 = renderCutUvs;
            generatedMesh.normals = normals;
            generatedMesh.subMeshCount = 2;
            generatedMesh.SetTriangles(logicalTriangles, 0, false);
            generatedMesh.SetTriangles(backTriangles, 1, false);
            generatedMesh.RecalculateBounds();
            meshFilter.sharedMesh = generatedMesh;
            localBounds = generatedMesh.bounds;
            ApplyTextures();
        }

        private Vector2 RestPointToNormalized(Vector3 restPoint)
        {
            return new Vector2(
                (restPoint.x - gridMinimum.x) / Mathf.Max(0.0001f, gridSize.x),
                (restPoint.y - gridMinimum.y) / Mathf.Max(0.0001f, gridSize.y));
        }

        private static long NormalizedVertexKey(Vector2 normalized)
        {
            int x = Mathf.RoundToInt(normalized.x * 1000000f);
            int y = Mathf.RoundToInt(normalized.y * 1000000f);
            return ((long)x << 32) ^ (uint)y;
        }

        private static float SignedDoubleArea(Vector3 a, Vector3 b, Vector3 c)
        {
            return (b.x - a.x) * (c.y - a.y)
                - (b.y - a.y) * (c.x - a.x);
        }

        private void CommitLogicalVertices(bool recalculateBounds = true)
        {
            if (generatedMesh == null || renderVertices.Length != logicalVertices.Length * 2)
            {
                return;
            }

            int logicalCount = logicalVertices.Length;
            for (int i = 0; i < logicalCount; i++)
            {
                renderVertices[i] = logicalVertices[i];
                renderVertices[i + logicalCount] = logicalVertices[i];
            }

            generatedMesh.vertices = renderVertices;
            if (recalculateNormalsWhenDeformed)
            {
                generatedMesh.RecalculateNormals();
            }

            if (recalculateBounds)
            {
                generatedMesh.RecalculateBounds();
                localBounds = generatedMesh.bounds;
            }
            VerticesChanged?.Invoke();
        }

        private static Vector2 SpriteUv(Sprite sprite, float x, float y)
        {
            if (sprite == null || sprite.texture == null)
            {
                return new Vector2(x, y);
            }

            Rect textureRect = sprite.textureRect;
            float textureWidth = sprite.texture.width;
            float textureHeight = sprite.texture.height;

            return new Vector2(
                Mathf.Lerp(textureRect.xMin / textureWidth, textureRect.xMax / textureWidth, x),
                Mathf.Lerp(textureRect.yMin / textureHeight, textureRect.yMax / textureHeight, y));
        }

        private void RebuildVisibleContours()
        {
            visibleContours.Clear();
            visibleBoundarySegments.Clear();
            visibleGeometryVertices = Array.Empty<Vector2>();
            visibleGeometryTriangles = Array.Empty<ushort>();

            if (frontSprite != null)
            {
                int shapeCount = frontSprite.GetPhysicsShapeCount();
                float naturalHeight = frontSprite.rect.height / Mathf.Max(0.0001f, frontSprite.pixelsPerUnit);
                float shapeScale = gridSize.y / Mathf.Max(0.0001f, naturalHeight);
                Vector2 pivotOffset = respectSpritePivot
                    ? Vector2.zero
                    : (frontSprite.pivot - frontSprite.rect.size * 0.5f)
                        / Mathf.Max(0.0001f, frontSprite.pixelsPerUnit);
                List<Vector2> points = new List<Vector2>();

                Vector2[] spriteVertices = frontSprite.vertices;
                visibleGeometryVertices = new Vector2[spriteVertices.Length];
                for (int vertexIndex = 0; vertexIndex < spriteVertices.Length; vertexIndex++)
                {
                    visibleGeometryVertices[vertexIndex] = (spriteVertices[vertexIndex] + pivotOffset) * shapeScale;
                }

                visibleGeometryTriangles = frontSprite.triangles;

                for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
                {
                    points.Clear();
                    frontSprite.GetPhysicsShape(shapeIndex, points);
                    if (points.Count < 3)
                    {
                        continue;
                    }

                    Vector2[] contour = new Vector2[points.Count];
                    for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
                    {
                        contour[pointIndex] = (points[pointIndex] + pivotOffset) * shapeScale;
                    }

                    visibleContours.Add(contour);
                    AddClosedContourSegments(contour);
                }

                if (visibleBoundarySegments.Count == 0)
                {
                    AddSpriteGeometryBoundarySegments();
                }
            }

            // A Sprite may use Full Rect geometry and have fallback physics-shape generation
            // disabled. The rectangular grid remains a predictable last-resort shape.
            if (visibleBoundarySegments.Count == 0)
            {
                Vector2[] rectangle =
                {
                    gridMinimum,
                    new Vector2(gridMinimum.x + gridSize.x, gridMinimum.y),
                    gridMinimum + gridSize,
                    new Vector2(gridMinimum.x, gridMinimum.y + gridSize.y)
                };
                visibleContours.Add(rectangle);
                AddClosedContourSegments(rectangle);
            }
        }

        private void RebuildAlphaMask()
        {
            alphaMask = Array.Empty<byte>();
            alphaMaskWidth = 0;
            alphaMaskHeight = 0;

            if (frontSprite == null || frontSprite.texture == null || !frontSprite.texture.isReadable)
            {
                return;
            }

            try
            {
                Texture2D texture = frontSprite.texture;
                Rect rect = frontSprite.textureRect;
                int originX = Mathf.Clamp(Mathf.RoundToInt(rect.x), 0, texture.width - 1);
                int originY = Mathf.Clamp(Mathf.RoundToInt(rect.y), 0, texture.height - 1);
                alphaMaskWidth = Mathf.Clamp(Mathf.RoundToInt(rect.width), 1, texture.width - originX);
                alphaMaskHeight = Mathf.Clamp(Mathf.RoundToInt(rect.height), 1, texture.height - originY);
                Color32[] texturePixels = texture.GetPixels32();
                alphaMask = new byte[alphaMaskWidth * alphaMaskHeight];

                for (int y = 0; y < alphaMaskHeight; y++)
                {
                    int sourceRow = (originY + y) * texture.width + originX;
                    int targetRow = y * alphaMaskWidth;
                    for (int x = 0; x < alphaMaskWidth; x++)
                    {
                        alphaMask[targetRow + x] = texturePixels[sourceRow + x].a;
                    }
                }
            }
            catch (UnityException exception)
            {
                alphaMask = Array.Empty<byte>();
                alphaMaskWidth = 0;
                alphaMaskHeight = 0;
                Debug.LogWarning("Could not read Sprite alpha; using geometry outline fallback. " + exception.Message, this);
            }
        }

        private bool TryClassifyFromAlphaMask(Vector2 restPoint, float edgeDistance, out bool isNearEdge)
        {
            isNearEdge = false;
            if (alphaMask.Length == 0 || alphaMaskWidth <= 0 || alphaMaskHeight <= 0)
            {
                return false;
            }

            float normalizedX = (restPoint.x - gridMinimum.x) / Mathf.Max(0.0001f, gridSize.x);
            float normalizedY = (restPoint.y - gridMinimum.y) / Mathf.Max(0.0001f, gridSize.y);
            int centerX = Mathf.RoundToInt(normalizedX * (alphaMaskWidth - 1));
            int centerY = Mathf.RoundToInt(normalizedY * (alphaMaskHeight - 1));
            int radiusX = Mathf.Max(1, Mathf.CeilToInt(edgeDistance / Mathf.Max(0.0001f, gridSize.x) * alphaMaskWidth));
            int radiusY = Mathf.Max(1, Mathf.CeilToInt(edgeDistance / Mathf.Max(0.0001f, gridSize.y) * alphaMaskHeight));
            bool foundSolid = false;
            bool foundTransparent = false;
            const byte solidThreshold = 16;

            for (int offsetY = -radiusY; offsetY <= radiusY; offsetY++)
            {
                float normalizedOffsetY = offsetY / (float)radiusY;
                for (int offsetX = -radiusX; offsetX <= radiusX; offsetX++)
                {
                    float normalizedOffsetX = offsetX / (float)radiusX;
                    if (normalizedOffsetX * normalizedOffsetX + normalizedOffsetY * normalizedOffsetY > 1f)
                    {
                        continue;
                    }

                    int sampleX = centerX + offsetX;
                    int sampleY = centerY + offsetY;
                    if (sampleX < 0 || sampleX >= alphaMaskWidth || sampleY < 0 || sampleY >= alphaMaskHeight)
                    {
                        foundTransparent = true;
                    }
                    else if (IsCombinedSolid(sampleX, sampleY, solidThreshold))
                    {
                        foundSolid = true;
                    }
                    else
                    {
                        foundTransparent = true;
                    }

                    if (foundSolid && foundTransparent)
                    {
                        isNearEdge = true;
                        return true;
                    }
                }
            }

            if (centerX < 0 || centerX >= alphaMaskWidth || centerY < 0 || centerY >= alphaMaskHeight)
            {
                return false;
            }

            return IsCombinedSolid(centerX, centerY, solidThreshold);
        }

        private bool IsCombinedSolid(int x, int y, byte threshold)
        {
            int index = y * alphaMaskWidth + x;
            bool sourceSolid = index >= 0 && index < alphaMask.Length && alphaMask[index] >= threshold;
            bool cutSolid = cutMaskAlpha.Length != alphaMask.Length || cutMaskAlpha[index] >= threshold;
            return sourceSolid && cutSolid;
        }

        private void InvalidateCutComponents()
        {
            cutComponentsDirty = true;
            cutComponentCount = 0;
        }

        private void InvalidateDeformationGraph()
        {
            deformationGraphDirty = true;
        }

        private void EnsureDeformationGraph()
        {
            if (!deformationGraphDirty && deformationGraph.Length == logicalVertices.Length)
            {
                return;
            }

            deformationGraphDirty = false;
            deformationGraph = new List<int>[logicalVertices.Length];
            for (int index = 0; index < deformationGraph.Length; index++)
            {
                deformationGraph[index] = new List<int>(6);
            }

            HashSet<ulong> visitedEdges = new HashSet<ulong>();
            HashSet<ulong> traversableEdges = new HashSet<ulong>();
            for (int triangle = 0; triangle < logicalTriangles.Length; triangle += 3)
            {
                AddDeformationGraphEdge(
                    logicalTriangles[triangle],
                    logicalTriangles[triangle + 1],
                    visitedEdges,
                    traversableEdges);
                AddDeformationGraphEdge(
                    logicalTriangles[triangle + 1],
                    logicalTriangles[triangle + 2],
                    visitedEdges,
                    traversableEdges);
                AddDeformationGraphEdge(
                    logicalTriangles[triangle + 2],
                    logicalTriangles[triangle],
                    visitedEdges,
                    traversableEdges);
            }

            List<int> intact = new List<int>(logicalTriangles.Length);
            for (int triangle = 0; triangle < logicalTriangles.Length; triangle += 3)
            {
                int a = logicalTriangles[triangle];
                int b = logicalTriangles[triangle + 1];
                int c = logicalTriangles[triangle + 2];
                if (!traversableEdges.Contains(EdgeKey(a, b))
                    || !traversableEdges.Contains(EdgeKey(b, c))
                    || !traversableEdges.Contains(EdgeKey(c, a)))
                {
                    continue;
                }

                intact.Add(a);
                intact.Add(b);
                intact.Add(c);
            }

            mechanicallyIntactTriangles = intact.ToArray();
        }

        private void AddDeformationGraphEdge(
            int first,
            int second,
            HashSet<ulong> visitedEdges,
            HashSet<ulong> traversableEdges)
        {
            ulong key = EdgeKey(first, second);
            if (!visitedEdges.Add(key)
                || !IsRestSegmentTraversable(restVertices[first], restVertices[second]))
            {
                return;
            }

            traversableEdges.Add(key);
            deformationGraph[first].Add(second);
            deformationGraph[second].Add(first);
        }

        private static ulong EdgeKey(int first, int second)
        {
            uint minimum = (uint)Mathf.Min(first, second);
            uint maximum = (uint)Mathf.Max(first, second);
            return ((ulong)minimum << 32) | maximum;
        }

        private int FindNearestTraversableVertex(Vector2 restPoint)
        {
            int bestIndex = -1;
            float bestDistanceSquared = float.PositiveInfinity;
            for (int index = 0; index < restVertices.Length; index++)
            {
                float distanceSquared = ((Vector2)restVertices[index] - restPoint).sqrMagnitude;
                if (distanceSquared >= bestDistanceSquared
                    || !IsRestSegmentTraversable(restPoint, restVertices[index]))
                {
                    continue;
                }

                bestDistanceSquared = distanceSquared;
                bestIndex = index;
            }

            return bestIndex;
        }

        private bool IsRestSegmentTraversable(Vector2 localStart, Vector2 localEnd)
        {
            if (alphaMask.Length == 0 || cutMaskAlpha.Length != alphaMask.Length)
            {
                return true;
            }

            Vector2 normalizedStart = new Vector2(
                (localStart.x - gridMinimum.x) / Mathf.Max(0.0001f, gridSize.x),
                (localStart.y - gridMinimum.y) / Mathf.Max(0.0001f, gridSize.y));
            Vector2 normalizedEnd = new Vector2(
                (localEnd.x - gridMinimum.x) / Mathf.Max(0.0001f, gridSize.x),
                (localEnd.y - gridMinimum.y) / Mathf.Max(0.0001f, gridSize.y));
            float pixelDeltaX = Mathf.Abs(normalizedEnd.x - normalizedStart.x) * alphaMaskWidth;
            float pixelDeltaY = Mathf.Abs(normalizedEnd.y - normalizedStart.y) * alphaMaskHeight;
            int samples = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(pixelDeltaX, pixelDeltaY)), 2, 128);
            bool midpointSourceSolid = false;

            for (int sample = 1; sample < samples; sample++)
            {
                float factor = sample / (float)samples;
                Vector2 normalized = Vector2.Lerp(normalizedStart, normalizedEnd, factor);
                int x = Mathf.Clamp(Mathf.RoundToInt(normalized.x * (alphaMaskWidth - 1)), 0, alphaMaskWidth - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt(normalized.y * (alphaMaskHeight - 1)), 0, alphaMaskHeight - 1);
                int pixelIndex = y * alphaMaskWidth + x;
                bool sourceSolid = alphaMask[pixelIndex] >= 16;
                if (sample == samples / 2)
                {
                    midpointSourceSolid = sourceSolid;
                }

            }

            return midpointSourceSolid;
        }

        private bool IsBlockedByOpenCutBarrier(Vector2 segmentStart, Vector2 segmentEnd)
        {
            for (int barrierIndex = 0; barrierIndex < openCutBarriers.Count; barrierIndex++)
            {
                CutBarrier barrier = openCutBarriers[barrierIndex];
                float radiusSquared = barrier.Radius * barrier.Radius;
                for (int pointIndex = 0; pointIndex + 1 < barrier.Points.Length; pointIndex++)
                {
                    if (DistanceSquaredBetweenSegments(
                            segmentStart,
                            segmentEnd,
                            barrier.Points[pointIndex],
                            barrier.Points[pointIndex + 1]) <= radiusSquared)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsBlockedByOpenCutCenterline(Vector2 segmentStart, Vector2 segmentEnd)
        {
            for (int barrierIndex = 0; barrierIndex < openCutBarriers.Count; barrierIndex++)
            {
                Vector2[] points = openCutBarriers[barrierIndex].Points;
                for (int pointIndex = 0; pointIndex + 1 < points.Length; pointIndex++)
                {
                    if (SegmentsIntersect(
                            segmentStart,
                            segmentEnd,
                            points[pointIndex],
                            points[pointIndex + 1]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static float DistanceSquaredBetweenSegments(
            Vector2 firstStart,
            Vector2 firstEnd,
            Vector2 secondStart,
            Vector2 secondEnd)
        {
            if (SegmentsIntersect(firstStart, firstEnd, secondStart, secondEnd))
            {
                return 0f;
            }

            return Mathf.Min(
                Mathf.Min(
                    DistanceSquaredToSegment(firstStart, secondStart, secondEnd),
                    DistanceSquaredToSegment(firstEnd, secondStart, secondEnd)),
                Mathf.Min(
                    DistanceSquaredToSegment(secondStart, firstStart, firstEnd),
                    DistanceSquaredToSegment(secondEnd, firstStart, firstEnd)));
        }

        private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            const float epsilon = 0.000001f;
            float abC = Cross(b - a, c - a);
            float abD = Cross(b - a, d - a);
            float cdA = Cross(d - c, a - c);
            float cdB = Cross(d - c, b - c);
            if (((abC > epsilon && abD < -epsilon) || (abC < -epsilon && abD > epsilon))
                && ((cdA > epsilon && cdB < -epsilon) || (cdA < -epsilon && cdB > epsilon)))
            {
                return true;
            }

            return Mathf.Abs(abC) <= epsilon && DistanceSquaredToSegment(c, a, b) <= epsilon * epsilon
                || Mathf.Abs(abD) <= epsilon && DistanceSquaredToSegment(d, a, b) <= epsilon * epsilon
                || Mathf.Abs(cdA) <= epsilon && DistanceSquaredToSegment(a, c, d) <= epsilon * epsilon
                || Mathf.Abs(cdB) <= epsilon && DistanceSquaredToSegment(b, c, d) <= epsilon * epsilon;
        }

        private static float Cross(Vector2 first, Vector2 second)
        {
            return first.x * second.y - first.y * second.x;
        }

        private sealed class CutBarrier
        {
            public readonly Vector2[] Points;
            public readonly float Radius;

            public CutBarrier(Vector2[] points, float radius)
            {
                Points = points;
                Radius = radius;
            }
        }

        private sealed class MinDistanceHeap
        {
            private struct Entry
            {
                public int Vertex;
                public float Distance;
            }

            private readonly List<Entry> entries = new List<Entry>();

            public void Push(int vertex, float distance)
            {
                Entry entry = new Entry { Vertex = vertex, Distance = distance };
                entries.Add(entry);
                int child = entries.Count - 1;
                while (child > 0)
                {
                    int parent = (child - 1) / 2;
                    if (entries[parent].Distance <= distance)
                    {
                        break;
                    }

                    entries[child] = entries[parent];
                    child = parent;
                }

                entries[child] = entry;
            }

            public bool TryPop(out int vertex, out float distance)
            {
                if (entries.Count == 0)
                {
                    vertex = -1;
                    distance = 0f;
                    return false;
                }

                Entry root = entries[0];
                int lastIndex = entries.Count - 1;
                Entry tail = entries[lastIndex];
                entries.RemoveAt(lastIndex);
                if (entries.Count > 0)
                {
                    int parent = 0;
                    while (true)
                    {
                        int left = parent * 2 + 1;
                        if (left >= entries.Count)
                        {
                            break;
                        }

                        int right = left + 1;
                        int smallest = right < entries.Count
                            && entries[right].Distance < entries[left].Distance
                            ? right
                            : left;
                        if (entries[smallest].Distance >= tail.Distance)
                        {
                            break;
                        }

                        entries[parent] = entries[smallest];
                        parent = smallest;
                    }

                    entries[parent] = tail;
                }

                vertex = root.Vertex;
                distance = root.Distance;
                return true;
            }
        }

        private void EnsureCutComponentLabels()
        {
            int pixelCount = alphaMaskWidth * alphaMaskHeight;
            if (!cutComponentsDirty
                && cutComponentLabels.Length == pixelCount)
            {
                return;
            }

            cutComponentsDirty = false;
            cutComponentCount = 0;
            if (pixelCount <= 0 || alphaMask.Length != pixelCount)
            {
                cutComponentLabels = Array.Empty<int>();
                return;
            }

            if (cutComponentLabels.Length != pixelCount)
            {
                cutComponentLabels = new int[pixelCount];
            }

            const byte solidThreshold = 16;
            for (int index = 0; index < pixelCount; index++)
            {
                cutComponentLabels[index] = alphaMask[index] >= solidThreshold
                    && (cutMaskAlpha.Length != pixelCount || cutMaskAlpha[index] >= solidThreshold)
                    ? 0
                    : -1;
            }

            int[] queue = new int[pixelCount];
            for (int seed = 0; seed < pixelCount; seed++)
            {
                if (cutComponentLabels[seed] != 0)
                {
                    continue;
                }

                int label = ++cutComponentCount;
                int read = 0;
                int write = 0;
                queue[write++] = seed;
                cutComponentLabels[seed] = label;

                while (read < write)
                {
                    int current = queue[read++];
                    int x = current % alphaMaskWidth;
                    int y = current / alphaMaskWidth;

                    TryQueueCutComponentPixel(x - 1, y, label, queue, ref write);
                    TryQueueCutComponentPixel(x + 1, y, label, queue, ref write);
                    TryQueueCutComponentPixel(x, y - 1, label, queue, ref write);
                    TryQueueCutComponentPixel(x, y + 1, label, queue, ref write);
                }
            }
        }

        private void TryQueueCutComponentPixel(
            int x,
            int y,
            int label,
            int[] queue,
            ref int write)
        {
            if (x < 0 || x >= alphaMaskWidth || y < 0 || y >= alphaMaskHeight)
            {
                return;
            }

            int index = y * alphaMaskWidth + x;
            if (cutComponentLabels[index] != 0)
            {
                return;
            }

            cutComponentLabels[index] = label;
            queue[write++] = index;
        }

        private int FindNearestCutComponent(int centerX, int centerY, int maximumRadius)
        {
            if (cutComponentLabels.Length == 0)
            {
                return 0;
            }

            centerX = Mathf.Clamp(centerX, 0, alphaMaskWidth - 1);
            centerY = Mathf.Clamp(centerY, 0, alphaMaskHeight - 1);
            int centerLabel = cutComponentLabels[centerY * alphaMaskWidth + centerX];
            if (centerLabel > 0)
            {
                return centerLabel;
            }

            int maxRadius = Mathf.Min(
                Mathf.Max(1, maximumRadius),
                Mathf.Max(alphaMaskWidth, alphaMaskHeight));
            for (int radius = 1; radius <= maxRadius; radius++)
            {
                int minX = Mathf.Max(0, centerX - radius);
                int maxX = Mathf.Min(alphaMaskWidth - 1, centerX + radius);
                int minY = Mathf.Max(0, centerY - radius);
                int maxY = Mathf.Min(alphaMaskHeight - 1, centerY + radius);
                int bestLabel = 0;
                float bestDistanceSquared = float.PositiveInfinity;

                for (int x = minX; x <= maxX; x++)
                {
                    ConsiderCutComponentPixel(x, minY, centerX, centerY, ref bestLabel, ref bestDistanceSquared);
                    if (maxY != minY)
                    {
                        ConsiderCutComponentPixel(x, maxY, centerX, centerY, ref bestLabel, ref bestDistanceSquared);
                    }
                }

                for (int y = minY + 1; y < maxY; y++)
                {
                    ConsiderCutComponentPixel(minX, y, centerX, centerY, ref bestLabel, ref bestDistanceSquared);
                    if (maxX != minX)
                    {
                        ConsiderCutComponentPixel(maxX, y, centerX, centerY, ref bestLabel, ref bestDistanceSquared);
                    }
                }

                if (bestLabel > 0)
                {
                    return bestLabel;
                }
            }

            return 0;
        }

        private void ConsiderCutComponentPixel(
            int x,
            int y,
            int centerX,
            int centerY,
            ref int bestLabel,
            ref float bestDistanceSquared)
        {
            int label = cutComponentLabels[y * alphaMaskWidth + x];
            if (label <= 0)
            {
                return;
            }

            float deltaX = x - centerX;
            float deltaY = y - centerY;
            float distanceSquared = deltaX * deltaX + deltaY * deltaY;
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                bestLabel = label;
            }
        }

        private bool TryMapCurrentPointToRest(Vector3 currentPoint, out Vector2 restPoint)
        {
            return TryMapCurrentPointToRest(currentPoint, out restPoint, out _);
        }

        private bool TryMapCurrentPointToRest(
            Vector3 currentPoint,
            out Vector2 restPoint,
            out Vector3 currentSurfacePoint)
        {
            restPoint = default(Vector2);
            currentSurfacePoint = default(Vector3);
            const float tolerance = -0.0001f;

            for (int triangle = 0; triangle < logicalTriangles.Length; triangle += 3)
            {
                int indexA = logicalTriangles[triangle];
                int indexB = logicalTriangles[triangle + 1];
                int indexC = logicalTriangles[triangle + 2];

                if (!TryGetBarycentricCoordinates(
                        (Vector2)currentPoint,
                        (Vector2)logicalVertices[indexA],
                        (Vector2)logicalVertices[indexB],
                        (Vector2)logicalVertices[indexC],
                        out Vector3 barycentric))
                {
                    continue;
                }

                if (barycentric.x < tolerance || barycentric.y < tolerance || barycentric.z < tolerance)
                {
                    continue;
                }

                restPoint = (Vector2)restVertices[indexA] * barycentric.x
                    + (Vector2)restVertices[indexB] * barycentric.y
                    + (Vector2)restVertices[indexC] * barycentric.z;
                currentSurfacePoint = logicalVertices[indexA] * barycentric.x
                    + logicalVertices[indexB] * barycentric.y
                    + logicalVertices[indexC] * barycentric.z;
                return true;
            }

            return false;
        }

        private bool IsInsideVisibleShape(Vector2 point)
        {
            if (visibleContours.Count == 0 && visibleGeometryTriangles.Length >= 3)
            {
                for (int triangle = 0; triangle < visibleGeometryTriangles.Length; triangle += 3)
                {
                    Vector2 a = visibleGeometryVertices[visibleGeometryTriangles[triangle]];
                    Vector2 b = visibleGeometryVertices[visibleGeometryTriangles[triangle + 1]];
                    Vector2 c = visibleGeometryVertices[visibleGeometryTriangles[triangle + 2]];
                    if (TryGetBarycentricCoordinates(point, a, b, c, out Vector3 barycentric)
                        && barycentric.x >= -0.0001f
                        && barycentric.y >= -0.0001f
                        && barycentric.z >= -0.0001f)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool inside = false;

            for (int contourIndex = 0; contourIndex < visibleContours.Count; contourIndex++)
            {
                Vector2[] contour = visibleContours[contourIndex];
                int previous = contour.Length - 1;

                for (int current = 0; current < contour.Length; current++)
                {
                    Vector2 a = contour[current];
                    Vector2 b = contour[previous];
                    bool crossesScanLine = (a.y > point.y) != (b.y > point.y);
                    if (crossesScanLine)
                    {
                        float intersectionX = (b.x - a.x) * (point.y - a.y)
                            / (b.y - a.y) + a.x;
                        if (point.x < intersectionX)
                        {
                            inside = !inside;
                        }
                    }

                    previous = current;
                }
            }

            return inside;
        }

        private float DistanceSquaredToCurrentVisibleBoundary(Vector2 point)
        {
            float nearestDistanceSquared = float.PositiveInfinity;

            for (int segmentIndex = 0; segmentIndex + 1 < visibleBoundarySegments.Count; segmentIndex += 2)
            {
                Vector2 a = MapRestPointToCurrent(visibleBoundarySegments[segmentIndex]);
                Vector2 b = MapRestPointToCurrent(visibleBoundarySegments[segmentIndex + 1]);
                nearestDistanceSquared = Mathf.Min(
                    nearestDistanceSquared,
                    DistanceSquaredToSegment(point, a, b));
            }

            return nearestDistanceSquared;
        }

        private void AddClosedContourSegments(Vector2[] contour)
        {
            if (contour == null || contour.Length < 2)
            {
                return;
            }

            Vector2 previous = contour[contour.Length - 1];
            for (int index = 0; index < contour.Length; index++)
            {
                Vector2 current = contour[index];
                visibleBoundarySegments.Add(previous);
                visibleBoundarySegments.Add(current);
                previous = current;
            }
        }

        private void AddSpriteGeometryBoundarySegments()
        {
            if (visibleGeometryTriangles.Length < 3)
            {
                return;
            }

            Dictionary<ulong, int> edgeUseCounts = new Dictionary<ulong, int>();
            for (int triangle = 0; triangle < visibleGeometryTriangles.Length; triangle += 3)
            {
                CountEdge(edgeUseCounts, visibleGeometryTriangles[triangle], visibleGeometryTriangles[triangle + 1]);
                CountEdge(edgeUseCounts, visibleGeometryTriangles[triangle + 1], visibleGeometryTriangles[triangle + 2]);
                CountEdge(edgeUseCounts, visibleGeometryTriangles[triangle + 2], visibleGeometryTriangles[triangle]);
            }

            foreach (KeyValuePair<ulong, int> edge in edgeUseCounts)
            {
                if (edge.Value != 1)
                {
                    continue;
                }

                int first = (int)(edge.Key >> 32);
                int second = (int)(edge.Key & uint.MaxValue);
                visibleBoundarySegments.Add(visibleGeometryVertices[first]);
                visibleBoundarySegments.Add(visibleGeometryVertices[second]);
            }
        }

        private static void CountEdge(Dictionary<ulong, int> counts, int first, int second)
        {
            uint minimum = (uint)Mathf.Min(first, second);
            uint maximum = (uint)Mathf.Max(first, second);
            ulong key = ((ulong)minimum << 32) | maximum;
            counts.TryGetValue(key, out int count);
            counts[key] = count + 1;
        }

        private Vector2 MapRestPointToCurrent(Vector2 restPoint)
        {
            return MapRestPointToCurrent3D(restPoint);
        }

        private Vector3 MapRestPointToCurrent3D(Vector2 restPoint)
        {
            const float tolerance = -0.0001f;
            for (int triangle = 0; triangle < logicalTriangles.Length; triangle += 3)
            {
                int a = logicalTriangles[triangle];
                int b = logicalTriangles[triangle + 1];
                int c = logicalTriangles[triangle + 2];
                if (!TryGetBarycentricCoordinates(
                        restPoint,
                        restVertices[a],
                        restVertices[b],
                        restVertices[c],
                        out Vector3 barycentric)
                    || barycentric.x < tolerance
                    || barycentric.y < tolerance
                    || barycentric.z < tolerance)
                {
                    continue;
                }

                return logicalVertices[a] * barycentric.x
                    + logicalVertices[b] * barycentric.y
                    + logicalVertices[c] * barycentric.z;
            }

            return restPoint;
        }

        private Vector3 MapRestPointToRest3D(Vector2 restPoint)
        {
            const float tolerance = -0.0001f;
            for (int triangle = 0; triangle < logicalTriangles.Length; triangle += 3)
            {
                int a = logicalTriangles[triangle];
                int b = logicalTriangles[triangle + 1];
                int c = logicalTriangles[triangle + 2];
                if (!TryGetBarycentricCoordinates(
                        restPoint,
                        restVertices[a],
                        restVertices[b],
                        restVertices[c],
                        out Vector3 barycentric)
                    || barycentric.x < tolerance
                    || barycentric.y < tolerance
                    || barycentric.z < tolerance)
                {
                    continue;
                }

                return poseRestVertices[a] * barycentric.x
                    + poseRestVertices[b] * barycentric.y
                    + poseRestVertices[c] * barycentric.z;
            }

            return restPoint;
        }

        private static bool TryGetBarycentricCoordinates(
            Vector2 point,
            Vector2 a,
            Vector2 b,
            Vector2 c,
            out Vector3 barycentric)
        {
            float denominator = (b.y - c.y) * (a.x - c.x)
                + (c.x - b.x) * (a.y - c.y);

            if (Mathf.Abs(denominator) < 0.0000001f)
            {
                barycentric = default(Vector3);
                return false;
            }

            float weightA = ((b.y - c.y) * (point.x - c.x)
                + (c.x - b.x) * (point.y - c.y)) / denominator;
            float weightB = ((c.y - a.y) * (point.x - c.x)
                + (a.x - c.x) * (point.y - c.y)) / denominator;
            barycentric = new Vector3(weightA, weightB, 1f - weightA - weightB);
            return true;
        }

        private static float DistanceSquaredToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 segment = b - a;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared < 0.0000001f)
            {
                return (point - a).sqrMagnitude;
            }

            float factor = Mathf.Clamp01(Vector2.Dot(point - a, segment) / lengthSquared);
            Vector2 nearest = a + segment * factor;
            return (point - nearest).sqrMagnitude;
        }

        private void CreateSolidCutMask()
        {
            hasAppliedCuts = false;
            openCutBarriers.Clear();
            InvalidateDeformationGraph();
            InvalidateCutComponents();
            if (alphaMaskWidth <= 0 || alphaMaskHeight <= 0)
            {
                cutMaskAlpha = Array.Empty<byte>();
                cutMaskPixels = Array.Empty<Color32>();
                cutVisualAlpha = Array.Empty<byte>();
                cutVisualPixels = Array.Empty<Color32>();
                DestroyGeneratedObject(cutMaskTexture);
                DestroyGeneratedObject(cutVisualTexture);
                cutMaskTexture = null;
                cutVisualTexture = null;
                return;
            }

            cutMaskAlpha = new byte[alphaMaskWidth * alphaMaskHeight];
            cutVisualAlpha = new byte[cutMaskAlpha.Length];
            for (int index = 0; index < cutMaskAlpha.Length; index++)
            {
                cutMaskAlpha[index] = 255;
            }

            UploadCutMask();
        }

        private void UploadCutMask()
        {
            if (cutMaskAlpha.Length == 0 || alphaMaskWidth <= 0 || alphaMaskHeight <= 0)
            {
                return;
            }

            if (cutMaskTexture == null
                || cutMaskTexture.width != alphaMaskWidth
                || cutMaskTexture.height != alphaMaskHeight)
            {
                DestroyGeneratedObject(cutMaskTexture);
                cutMaskTexture = new Texture2D(alphaMaskWidth, alphaMaskHeight, TextureFormat.RGBA32, false, true)
                {
                    name = "Deformable Image Cut Mask",
                    hideFlags = HideFlags.DontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                cutMaskPixels = new Color32[cutMaskAlpha.Length];
            }
            else if (cutMaskPixels.Length != cutMaskAlpha.Length)
            {
                cutMaskPixels = new Color32[cutMaskAlpha.Length];
            }

            for (int index = 0; index < cutMaskAlpha.Length; index++)
            {
                byte value = cutMaskAlpha[index];
                cutMaskPixels[index] = new Color32(value, value, value, value);
            }

            cutMaskTexture.SetPixels32(cutMaskPixels);
            cutMaskTexture.Apply(false, false);

            if (cutVisualAlpha.Length != cutMaskAlpha.Length)
            {
                cutVisualAlpha = new byte[cutMaskAlpha.Length];
            }

            if (!HasCutVisualization())
            {
                DestroyGeneratedObject(cutVisualTexture);
                cutVisualTexture = null;
                cutVisualPixels = Array.Empty<Color32>();
                ApplyTextures();
                return;
            }

            if (cutVisualTexture == null
                || cutVisualTexture.width != alphaMaskWidth
                || cutVisualTexture.height != alphaMaskHeight)
            {
                DestroyGeneratedObject(cutVisualTexture);
                cutVisualTexture = new Texture2D(alphaMaskWidth, alphaMaskHeight, TextureFormat.RGBA32, false, true)
                {
                    name = "Deformable Image Cut Visual Mask",
                    hideFlags = HideFlags.DontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                cutVisualPixels = new Color32[cutMaskAlpha.Length];
            }
            else if (cutVisualPixels.Length != cutMaskAlpha.Length)
            {
                cutVisualPixels = new Color32[cutMaskAlpha.Length];
            }

            for (int index = 0; index < cutVisualAlpha.Length; index++)
            {
                byte value = cutVisualAlpha[index];
                cutVisualPixels[index] = new Color32(value, value, value, value);
            }

            cutVisualTexture.SetPixels32(cutVisualPixels);
            cutVisualTexture.Apply(false, false);
            ApplyTextures();
        }

        private void UploadCutMaskRegion(int minX, int minY, int maxX, int maxY)
        {
            if (cutMaskTexture == null
                || cutMaskPixels.Length != cutMaskAlpha.Length)
            {
                UploadCutMask();
                return;
            }

            bool hasCutVisualization = HasCutVisualization();
            if (hasCutVisualization
                && (cutVisualTexture == null
                    || cutVisualPixels.Length != cutVisualAlpha.Length))
            {
                UploadCutMask();
                return;
            }

            const int visualPadding = 2;
            minX = Mathf.Clamp(minX - visualPadding, 0, alphaMaskWidth - 1);
            minY = Mathf.Clamp(minY - visualPadding, 0, alphaMaskHeight - 1);
            maxX = Mathf.Clamp(maxX + visualPadding, 0, alphaMaskWidth - 1);
            maxY = Mathf.Clamp(maxY + visualPadding, 0, alphaMaskHeight - 1);
            int width = maxX - minX + 1;
            int height = maxY - minY + 1;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            Color32[] cutBlock = new Color32[width * height];
            Color32[] visualBlock = hasCutVisualization
                ? new Color32[width * height]
                : null;
            for (int y = 0; y < height; y++)
            {
                int sourceRow = (minY + y) * alphaMaskWidth + minX;
                int blockRow = y * width;
                for (int x = 0; x < width; x++)
                {
                    int sourceIndex = sourceRow + x;
                    byte cutValue = cutMaskAlpha[sourceIndex];
                    Color32 cutColor = new Color32(cutValue, cutValue, cutValue, cutValue);
                    cutMaskPixels[sourceIndex] = cutColor;
                    cutBlock[blockRow + x] = cutColor;
                    if (hasCutVisualization)
                    {
                        byte visualValue = cutVisualAlpha[sourceIndex];
                        Color32 visualColor = new Color32(
                            visualValue,
                            visualValue,
                            visualValue,
                            visualValue);
                        cutVisualPixels[sourceIndex] = visualColor;
                        visualBlock[blockRow + x] = visualColor;
                    }
                }
            }

            cutMaskTexture.SetPixels32(minX, minY, width, height, cutBlock);
            cutMaskTexture.Apply(false, false);
            if (hasCutVisualization)
            {
                cutVisualTexture.SetPixels32(minX, minY, width, height, visualBlock);
                cutVisualTexture.Apply(false, false);
            }
            ApplyTextures();
        }

        private bool HasCutVisualization()
        {
            for (int index = 0; index < cutVisualAlpha.Length; index++)
            {
                if (cutVisualAlpha[index] != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdateCutBoundaryVisualization(int minX, int minY, int maxX, int maxY)
        {
            if (cutMaskAlpha.Length == 0 || alphaMask.Length != cutMaskAlpha.Length)
            {
                return;
            }

            if (cutVisualAlpha.Length != cutMaskAlpha.Length)
            {
                cutVisualAlpha = new byte[cutMaskAlpha.Length];
            }

            const int boundaryRadius = 2;
            const byte solidThreshold = 16;
            minX = Mathf.Clamp(minX - boundaryRadius, 0, alphaMaskWidth - 1);
            minY = Mathf.Clamp(minY - boundaryRadius, 0, alphaMaskHeight - 1);
            maxX = Mathf.Clamp(maxX + boundaryRadius, 0, alphaMaskWidth - 1);
            maxY = Mathf.Clamp(maxY + boundaryRadius, 0, alphaMaskHeight - 1);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int index = y * alphaMaskWidth + x;
                    if (alphaMask[index] < solidThreshold)
                    {
                        continue;
                    }

                    bool currentCutState = cutMaskAlpha[index] >= solidThreshold;
                    bool touchesOtherSide = false;
                    for (int offsetY = -boundaryRadius; offsetY <= boundaryRadius && !touchesOtherSide; offsetY++)
                    {
                        int sampleY = y + offsetY;
                        if (sampleY < 0 || sampleY >= alphaMaskHeight)
                        {
                            continue;
                        }

                        for (int offsetX = -boundaryRadius; offsetX <= boundaryRadius; offsetX++)
                        {
                            if (offsetX * offsetX + offsetY * offsetY > boundaryRadius * boundaryRadius)
                            {
                                continue;
                            }

                            int sampleX = x + offsetX;
                            if (sampleX < 0 || sampleX >= alphaMaskWidth)
                            {
                                continue;
                            }

                            int sampleIndex = sampleY * alphaMaskWidth + sampleX;
                            if (alphaMask[sampleIndex] >= solidThreshold
                                && (cutMaskAlpha[sampleIndex] >= solidThreshold) != currentCutState)
                            {
                                touchesOtherSide = true;
                                break;
                            }
                        }
                    }

                    if (touchesOtherSide)
                    {
                        cutVisualAlpha[index] = 255;
                    }
                }
            }
        }

        private void RebuildCutBoundaryVisualization()
        {
            if (cutMaskAlpha.Length == 0 || alphaMask.Length != cutMaskAlpha.Length)
            {
                return;
            }

            if (cutVisualAlpha.Length != cutMaskAlpha.Length)
            {
                cutVisualAlpha = new byte[cutMaskAlpha.Length];
            }
            else
            {
                Array.Clear(cutVisualAlpha, 0, cutVisualAlpha.Length);
            }

            const int boundaryRadius = 2;
            const byte solidThreshold = 16;
            for (int y = 0; y < alphaMaskHeight; y++)
            {
                for (int x = 0; x < alphaMaskWidth; x++)
                {
                    int index = y * alphaMaskWidth + x;
                    if (alphaMask[index] < solidThreshold)
                    {
                        continue;
                    }

                    bool currentCutState = cutMaskAlpha[index] >= solidThreshold;
                    bool touchesOtherSide = false;
                    for (int offsetY = -boundaryRadius; offsetY <= boundaryRadius && !touchesOtherSide; offsetY++)
                    {
                        int sampleY = y + offsetY;
                        if (sampleY < 0 || sampleY >= alphaMaskHeight)
                        {
                            continue;
                        }

                        for (int offsetX = -boundaryRadius; offsetX <= boundaryRadius; offsetX++)
                        {
                            if (offsetX * offsetX + offsetY * offsetY > boundaryRadius * boundaryRadius)
                            {
                                continue;
                            }

                            int sampleX = x + offsetX;
                            if (sampleX < 0 || sampleX >= alphaMaskWidth)
                            {
                                continue;
                            }

                            int sampleIndex = sampleY * alphaMaskWidth + sampleX;
                            if (alphaMask[sampleIndex] < solidThreshold)
                            {
                                continue;
                            }

                            bool sampleCutState = cutMaskAlpha[sampleIndex] >= solidThreshold;
                            if (sampleCutState != currentCutState)
                            {
                                touchesOtherSide = true;
                                break;
                            }
                        }
                    }

                    if (touchesOtherSide)
                    {
                        cutVisualAlpha[index] = 255;
                    }
                }
            }
        }

        private void SetCutMaskAlpha(byte[] source)
        {
            if (source == null || source.Length != alphaMaskWidth * alphaMaskHeight)
            {
                throw new ArgumentException("Cut mask dimensions do not match the Sprite alpha mask.", nameof(source));
            }

            cutMaskAlpha = (byte[])source.Clone();
            hasAppliedCuts = false;
            for (int index = 0; index < cutMaskAlpha.Length; index++)
            {
                if (alphaMask[index] >= 16 && cutMaskAlpha[index] < 16)
                {
                    hasAppliedCuts = true;
                    break;
                }
            }

            InvalidateDeformationGraph();
            InvalidateCutComponents();
            RebuildCutBoundaryVisualization();
            UploadCutMask();
        }

        private DeformableImageMesh CreateRuntimePiece(
            byte[] pieceAlpha,
            DeformableImagePolygonUtility.TriangulatedPolygon triangulation)
        {
            GameObject pieceObject = new GameObject(gameObject.name + " - Cut Piece");
            pieceObject.transform.SetParent(transform.parent, false);
            pieceObject.transform.localPosition = transform.localPosition;
            pieceObject.transform.localRotation = transform.localRotation;
            pieceObject.transform.localScale = transform.localScale;

            DeformableImageMesh piece = pieceObject.AddComponent<DeformableImageMesh>();
            piece.detachedFromMesh = detachedFromMesh != null ? detachedFromMesh : this;
            piece.frontSprite = frontSprite;
            piece.backSprite = backSprite;
            piece.flipBackHorizontally = flipBackHorizontally;
            piece.subdivisionsX = subdivisionsX;
            piece.subdivisionsY = subdivisionsY;
            piece.height = height;
            piece.respectSpritePivot = respectSpritePivot;
            piece.materialOverride = materialOverride;
            piece.tint = tint;
            piece.cutVisualizationColor = cutVisualizationColor;
            piece.recalculateNormalsWhenDeformed = recalculateNormalsWhenDeformed;
            piece.ambientLightStrength = ambientLightStrength;
            piece.diffuseLightStrength = diffuseLightStrength;
            piece.specularStrength = specularStrength;
            piece.surfaceSmoothness = surfaceSmoothness;
            piece.cutPieceSubdivisionLevels = cutPieceSubdivisionLevels;
            piece.cutPieceMaximumTriangles = cutPieceMaximumTriangles;
            piece.RebuildMesh();
            int effectiveSubdivisionLevels = CalculateCutPieceSubdivisionLevels(
                triangulation.Triangles.Length / 3);
            DeformableImagePolygonUtility.TriangulatedPolygon refinedPiece =
                DeformableImagePolygonUtility.SubdivideTriangulation(
                    triangulation,
                    effectiveSubdivisionLevels);
            piece.ReplaceWithPolygonMesh(refinedPiece, this);
            piece.SetCutMaskAlpha(pieceAlpha);

            MeshRenderer sourceRenderer = GetComponent<MeshRenderer>();
            MeshRenderer pieceRenderer = pieceObject.GetComponent<MeshRenderer>();
            pieceRenderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
            pieceRenderer.receiveShadows = sourceRenderer.receiveShadows;
            pieceRenderer.sortingLayerID = sourceRenderer.sortingLayerID;
            pieceRenderer.sortingOrder = sourceRenderer.sortingOrder + 1;
            pieceObject.AddComponent<DeformableImagePieceMover>();
            return piece;
        }

        private int CalculateCutPieceSubdivisionLevels(int sourceTriangleCount)
        {
            int levels = cutPieceSubdivisionLevels;
            long estimatedTriangles = Mathf.Max(1, sourceTriangleCount);
            while (levels > 0)
            {
                long multiplier = 1L << (levels * 2);
                if (estimatedTriangles * multiplier <= cutPieceMaximumTriangles)
                {
                    break;
                }

                levels--;
            }

            return levels;
        }

        private void ReplaceWithPolygonMesh(
            DeformableImagePolygonUtility.TriangulatedPolygon polygon,
            DeformableImageMesh deformationSource = null)
        {
            int vertexCount = polygon.Vertices.Length;
            Vector3[] polygonMaterialVertices = new Vector3[vertexCount];
            Vector3[] polygonRestVertices = new Vector3[vertexCount];
            Vector3[] polygonCurrentVertices = new Vector3[vertexCount];
            Vector2[] renderUvs = new Vector2[vertexCount * 2];
            Vector2[] renderCutUvs = new Vector2[vertexCount * 2];
            Vector3[] normals = new Vector3[vertexCount * 2];

            // Sample the current deformed grid before replacing it with the polygon topology.
            for (int index = 0; index < vertexCount; index++)
            {
                Vector2 normalizedUv = polygon.Vertices[index];
                Vector2 restPoint = gridMinimum + Vector2.Scale(normalizedUv, gridSize);
                polygonMaterialVertices[index] = restPoint;
                polygonRestVertices[index] = deformationSource != null
                    ? deformationSource.MapRestPointToRest3D(restPoint)
                    : MapRestPointToRest3D(restPoint);
                polygonCurrentVertices[index] = deformationSource != null
                    ? deformationSource.MapRestPointToCurrent3D(restPoint)
                    : MapRestPointToCurrent3D(restPoint);
                renderUvs[index] = SpriteUv(frontSprite, normalizedUv.x, normalizedUv.y);
                Sprite actualBack = backSprite != null ? backSprite : frontSprite;
                renderUvs[index + vertexCount] = SpriteUv(
                    actualBack,
                    flipBackHorizontally ? 1f - normalizedUv.x : normalizedUv.x,
                    normalizedUv.y);
                renderCutUvs[index] = normalizedUv;
                renderCutUvs[index + vertexCount] = normalizedUv;
                normals[index] = Vector3.back;
                normals[index + vertexCount] = Vector3.forward;
            }

            int[] frontTriangles = (int[])polygon.Triangles.Clone();
            for (int triangle = 0; triangle < frontTriangles.Length; triangle += 3)
            {
                Vector3 a = polygonRestVertices[frontTriangles[triangle]];
                Vector3 b = polygonRestVertices[frontTriangles[triangle + 1]];
                Vector3 c = polygonRestVertices[frontTriangles[triangle + 2]];
                float signedArea = (b.x - a.x) * (c.y - a.y)
                    - (b.y - a.y) * (c.x - a.x);
                if (signedArea > 0f)
                {
                    int swap = frontTriangles[triangle + 1];
                    frontTriangles[triangle + 1] = frontTriangles[triangle + 2];
                    frontTriangles[triangle + 2] = swap;
                }
            }

            int[] backTriangles = new int[frontTriangles.Length];
            for (int triangle = 0; triangle < frontTriangles.Length; triangle += 3)
            {
                backTriangles[triangle] = frontTriangles[triangle] + vertexCount;
                backTriangles[triangle + 1] = frontTriangles[triangle + 2] + vertexCount;
                backTriangles[triangle + 2] = frontTriangles[triangle + 1] + vertexCount;
            }

            restVertices = polygonMaterialVertices;
            poseRestVertices = polygonRestVertices;
            logicalVertices = polygonCurrentVertices;
            logicalTriangles = frontTriangles;
            InvalidateDeformationGraph();
            renderVertices = new Vector3[vertexCount * 2];
            for (int index = 0; index < vertexCount; index++)
            {
                renderVertices[index] = polygonCurrentVertices[index];
                renderVertices[index + vertexCount] = polygonCurrentVertices[index];
            }

            ReplaceGeneratedMesh();
            generatedMesh.name = "Deformable Image Cut Piece Mesh";
            generatedMesh.indexFormat = renderVertices.Length > 65535
                ? IndexFormat.UInt32
                : IndexFormat.UInt16;
            generatedMesh.MarkDynamic();
            generatedMesh.vertices = renderVertices;
            generatedMesh.uv = renderUvs;
            generatedMesh.uv2 = renderCutUvs;
            generatedMesh.normals = normals;
            generatedMesh.subMeshCount = 2;
            generatedMesh.SetTriangles(frontTriangles, 0, false);
            generatedMesh.SetTriangles(backTriangles, 1, false);
            generatedMesh.RecalculateBounds();
            meshFilter.sharedMesh = generatedMesh;
            localBounds = generatedMesh.bounds;
            ApplyTextures();
        }

        private static float EllipticalDistanceToSegment(
            Vector2 point,
            Vector2 start,
            Vector2 end,
            float radiusX,
            float radiusY)
        {
            Vector2 scale = new Vector2(
                1f / Mathf.Max(1f, radiusX),
                1f / Mathf.Max(1f, radiusY));
            Vector2 scaledPoint = Vector2.Scale(point, scale);
            Vector2 scaledStart = Vector2.Scale(start, scale);
            Vector2 scaledEnd = Vector2.Scale(end, scale);
            return Mathf.Sqrt(DistanceSquaredToSegment(scaledPoint, scaledStart, scaledEnd));
        }

        private static bool IsPointInsidePolygon(Vector2 point, IList<Vector2> polygon)
        {
            bool inside = false;
            int previous = polygon.Count - 1;

            for (int current = 0; current < polygon.Count; current++)
            {
                Vector2 a = polygon[current];
                Vector2 b = polygon[previous];
                bool crosses = (a.y > point.y) != (b.y > point.y);
                if (crosses)
                {
                    float intersectionX = (b.x - a.x) * (point.y - a.y)
                        / (b.y - a.y) + a.x;
                    if (point.x < intersectionX)
                    {
                        inside = !inside;
                    }
                }

                previous = current;
            }

            return inside;
        }

        private void EnsureDebugTopology()
        {
            if (ReferenceEquals(debugTopologySource, logicalTriangles))
            {
                return;
            }

            debugTopologySource = logicalTriangles;
            if (logicalTriangles == null || logicalTriangles.Length < 3)
            {
                debugUniqueEdges = Array.Empty<int>();
                debugOuterBoundaryEdges = Array.Empty<int>();
                debugCutBoundaryEdges = Array.Empty<int>();
                return;
            }

            Dictionary<ulong, int> edgeUseCounts = new Dictionary<ulong, int>(logicalTriangles.Length);
            List<int> uniqueEdges = new List<int>(logicalTriangles.Length);
            for (int triangle = 0; triangle < logicalTriangles.Length; triangle += 3)
            {
                AddDebugEdge(logicalTriangles[triangle], logicalTriangles[triangle + 1], edgeUseCounts, uniqueEdges);
                AddDebugEdge(logicalTriangles[triangle + 1], logicalTriangles[triangle + 2], edgeUseCounts, uniqueEdges);
                AddDebugEdge(logicalTriangles[triangle + 2], logicalTriangles[triangle], edgeUseCounts, uniqueEdges);
            }

            List<int> outerEdges = new List<int>();
            List<int> cutEdges = new List<int>();
            foreach (KeyValuePair<ulong, int> pair in edgeUseCounts)
            {
                if (pair.Value != 1)
                {
                    continue;
                }

                int a = (int)(pair.Key >> 32);
                int b = (int)(pair.Key & uint.MaxValue);
                List<int> destination = IsOriginalOuterBoundaryEdge(a, b) ? outerEdges : cutEdges;
                destination.Add(a);
                destination.Add(b);
            }

            debugUniqueEdges = uniqueEdges.ToArray();
            debugOuterBoundaryEdges = outerEdges.ToArray();
            debugCutBoundaryEdges = cutEdges.ToArray();
        }

        private static void AddDebugEdge(
            int a,
            int b,
            IDictionary<ulong, int> edgeUseCounts,
            ICollection<int> uniqueEdges)
        {
            int minimum = Mathf.Min(a, b);
            int maximum = Mathf.Max(a, b);
            ulong key = ((ulong)(uint)minimum << 32) | (uint)maximum;
            if (edgeUseCounts.TryGetValue(key, out int count))
            {
                edgeUseCounts[key] = count + 1;
                return;
            }

            edgeUseCounts.Add(key, 1);
            uniqueEdges.Add(minimum);
            uniqueEdges.Add(maximum);
        }

        private bool IsOriginalOuterBoundaryEdge(int a, int b)
        {
            if (a < 0 || b < 0 || a >= restVertices.Length || b >= restVertices.Length)
            {
                return false;
            }

            Vector2 normalizedA = RestPointToNormalized(restVertices[a]);
            Vector2 normalizedB = RestPointToNormalized(restVertices[b]);
            const float tolerance = 0.00001f;
            return (Mathf.Abs(normalizedA.x) <= tolerance && Mathf.Abs(normalizedB.x) <= tolerance)
                || (Mathf.Abs(normalizedA.x - 1f) <= tolerance && Mathf.Abs(normalizedB.x - 1f) <= tolerance)
                || (Mathf.Abs(normalizedA.y) <= tolerance && Mathf.Abs(normalizedB.y) <= tolerance)
                || (Mathf.Abs(normalizedA.y - 1f) <= tolerance && Mathf.Abs(normalizedB.y - 1f) <= tolerance);
        }

        private void DrawDebugEdges(
            int[] edgeIndices,
            Color color,
            int maximumEdges = int.MaxValue)
        {
            if (edgeIndices == null)
            {
                return;
            }

            Gizmos.color = color;
            int edgeCount = edgeIndices.Length / 2;
            int edgeStep = Mathf.Max(
                1,
                Mathf.CeilToInt(edgeCount / (float)Mathf.Max(1, maximumEdges)));
            for (int edge = 0; edge + 1 < edgeIndices.Length; edge += 2 * edgeStep)
            {
                int a = edgeIndices[edge];
                int b = edgeIndices[edge + 1];
                if (a < 0 || b < 0 || a >= logicalVertices.Length || b >= logicalVertices.Length)
                {
                    continue;
                }

                Vector3 pointA = logicalVertices[a];
                Vector3 pointB = logicalVertices[b];
                pointA.z += meshDebugZOffset;
                pointB.z += meshDebugZOffset;
                Gizmos.DrawLine(pointA, pointB);
            }
        }

        private void CacheComponents()
        {
            if (meshFilter == null)
            {
                meshFilter = GetComponent<MeshFilter>();
            }

            if (meshRenderer == null)
            {
                meshRenderer = GetComponent<MeshRenderer>();
            }
        }

        private void ReplaceGeneratedMesh()
        {
            if (generatedMesh != null)
            {
                if (meshFilter != null && meshFilter.sharedMesh == generatedMesh)
                {
                    meshFilter.sharedMesh = null;
                }

                DestroyGeneratedObject(generatedMesh);
            }

            generatedMesh = new Mesh
            {
                hideFlags = HideFlags.DontSave
            };
        }

        private void EnsureMaterial()
        {
            if (meshRenderer == null)
            {
                return;
            }

            Material selectedMaterial = materialOverride;
            if (selectedMaterial == null)
            {
                if (generatedMaterial == null)
                {
                    Shader shader = Shader.Find(DefaultShaderName);
                    if (shader == null)
                    {
                        Debug.LogError("Missing shader: " + DefaultShaderName, this);
                        return;
                    }

                    generatedMaterial = new Material(shader)
                    {
                        name = "Deformable Image Runtime Material",
                        hideFlags = HideFlags.DontSave
                    };
                }

                selectedMaterial = generatedMaterial;
            }

            meshRenderer.sharedMaterials = new[] { selectedMaterial, selectedMaterial };
        }

        private void ApplyTextures()
        {
            if (meshRenderer == null)
            {
                return;
            }

            EnsureMaterial();
            frontProperties = frontProperties ?? new MaterialPropertyBlock();
            backProperties = backProperties ?? new MaterialPropertyBlock();

            frontProperties.Clear();
            frontProperties.SetTexture("_MainTex", frontSprite != null ? frontSprite.texture : Texture2D.whiteTexture);
            frontProperties.SetTexture("_CutMask", cutMaskTexture != null ? cutMaskTexture : Texture2D.whiteTexture);
            frontProperties.SetTexture("_CutVisualMask", cutVisualTexture != null ? cutVisualTexture : Texture2D.blackTexture);
            frontProperties.SetColor("_CutVisualColor", cutVisualizationColor);
            frontProperties.SetColor("_Color", tint);
            frontProperties.SetFloat("_AmbientStrength", ambientLightStrength);
            frontProperties.SetFloat("_DiffuseStrength", diffuseLightStrength);
            frontProperties.SetFloat("_SpecularStrength", specularStrength);
            frontProperties.SetFloat("_Smoothness", surfaceSmoothness);

            Sprite actualBack = backSprite != null ? backSprite : frontSprite;
            backProperties.Clear();
            backProperties.SetTexture("_MainTex", actualBack != null ? actualBack.texture : Texture2D.whiteTexture);
            backProperties.SetTexture("_CutMask", cutMaskTexture != null ? cutMaskTexture : Texture2D.whiteTexture);
            backProperties.SetTexture("_CutVisualMask", cutVisualTexture != null ? cutVisualTexture : Texture2D.blackTexture);
            backProperties.SetColor("_CutVisualColor", cutVisualizationColor);
            backProperties.SetColor("_Color", tint);
            backProperties.SetFloat("_AmbientStrength", ambientLightStrength);
            backProperties.SetFloat("_DiffuseStrength", diffuseLightStrength);
            backProperties.SetFloat("_SpecularStrength", specularStrength);
            backProperties.SetFloat("_Smoothness", surfaceSmoothness);

            meshRenderer.SetPropertyBlock(frontProperties, 0);
            meshRenderer.SetPropertyBlock(backProperties, 1);
        }

        private void ClearMesh()
        {
            restVertices = Array.Empty<Vector3>();
            poseRestVertices = Array.Empty<Vector3>();
            logicalVertices = Array.Empty<Vector3>();
            renderVertices = Array.Empty<Vector3>();
            logicalTriangles = Array.Empty<int>();
            visibleContours.Clear();
            visibleBoundarySegments.Clear();
            visibleGeometryVertices = Array.Empty<Vector2>();
            visibleGeometryTriangles = Array.Empty<ushort>();
            alphaMask = Array.Empty<byte>();
            alphaMaskWidth = 0;
            alphaMaskHeight = 0;
            cutMaskAlpha = Array.Empty<byte>();
            cutMaskPixels = Array.Empty<Color32>();
            cutVisualAlpha = Array.Empty<byte>();
            cutVisualPixels = Array.Empty<Color32>();
            cutComponentLabels = Array.Empty<int>();
            deformationGraph = Array.Empty<List<int>>();
            mechanicallyIntactTriangles = Array.Empty<int>();
            hasAppliedCuts = false;
            openCutBarriers.Clear();
            InvalidateDeformationGraph();
            InvalidateCutComponents();
            DestroyGeneratedObject(cutMaskTexture);
            DestroyGeneratedObject(cutVisualTexture);
            cutMaskTexture = null;
            cutVisualTexture = null;
            localBounds = new Bounds(Vector3.zero, Vector3.zero);
            gridMinimum = Vector2.zero;
            gridSize = Vector2.zero;

            if (generatedMesh != null)
            {
                generatedMesh.Clear();
            }
        }

        private void DestroyGeneratedResources()
        {
            if (meshFilter != null && meshFilter.sharedMesh == generatedMesh)
            {
                meshFilter.sharedMesh = null;
            }

            DestroyGeneratedObject(generatedMesh);
            DestroyGeneratedObject(generatedMaterial);
            DestroyGeneratedObject(cutMaskTexture);
            DestroyGeneratedObject(cutVisualTexture);
            generatedMesh = null;
            generatedMaterial = null;
            cutMaskTexture = null;
            cutVisualTexture = null;
        }

        private static void DestroyGeneratedObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }
    }
}
