using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Serialization;

namespace DeformableImage
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(DeformableImageMesh))]
    public sealed class DeformableImageInteractor : MonoBehaviour
    {
        public enum DeformationTool
        {
            Drag,
            Inflate,
            Pinch,
            Cut
        }

        [Header("Input")]
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool ignorePointerOverUi = true;
        [SerializeField] private bool enableKeyboardShortcuts = true;

        [Header("Brush")]
        [SerializeField] private DeformationTool activeTool = DeformationTool.Drag;
        [SerializeField, Min(0.01f)] private float brushRadius = 0.75f;
        [SerializeField, Range(0.25f, 8f)] private float falloffPower = 2f;
        [SerializeField, Min(0.01f)] private float inflateStrength = 3f;
        [SerializeField, Min(0.01f)] private float wheelRadiusSpeed = 0.15f;
        [SerializeField, Min(0.01f)] private float minimumBrushRadius = 0.1f;
        [SerializeField, Min(0.02f)] private float maximumBrushRadius = 3f;

        [Header("Cloth-like Dragging")]
        [Tooltip("Only grabs this close to the visible Sprite outline may fold over and reveal the back image.")]
        [SerializeField, Min(0.001f)] private float visibleEdgeGrabDistance = 0.2f;
        [Tooltip("Interior pulls are clamped to this fraction of the brush radius so the rest of the image remains anchored.")]
        [SerializeField, Range(0.05f, 0.45f)] private float maximumInteriorStretch = 0.32f;
        [Tooltip("Prevents intact interior triangles from collapsing to almost zero area.")]
        [SerializeField, Range(0.001f, 0.25f)] private float minimumTriangleAreaRatio = 0.02f;

        [Header("Release / Pin")]
        [SerializeField, Min(0.05f)] private float reboundDuration = 0.45f;
        [SerializeField, Range(1f, 12f)] private float reboundDamping = 6f;
        [SerializeField, Range(0.5f, 5f)] private float reboundOscillations = 2f;

        [Header("Cutting")]
        [SerializeField, Min(0.005f)] private float cutPointSpacing = 0.03f;
        [Tooltip("Maximum world-space distance used to join two non-adjacent parts of the knife stroke into a closed loop.")]
        [SerializeField, Min(0.01f)] private float closedLoopDistance = 0.18f;
        [Tooltip("Rejects tiny accidental loops. This is measured in normalized image UV area, where the full image area is 1.")]
        [SerializeField, Min(0.00001f)] private float minimumClosedLoopArea = 0.001f;
        [Tooltip("Physical and transparent width of open cuts and the seam around closed cut pieces. Use 0.08-0.20 for a clearly visible gap on a height-5 image.")]
        [FormerlySerializedAs("openCutWidth")]
        [SerializeField, Min(0.005f)] private float transparentCutWidth = 0.08f;
        [Tooltip("Mouse-wheel adjustment step while the Cut tool is active.")]
        [SerializeField, Min(0.001f)] private float cutWidthScrollStep = 0.01f;
        [Tooltip("Keeps the cyan knife preview visible even when the physical cut width is extremely small.")]
        [FormerlySerializedAs("cutPreviewWidth")]
        [SerializeField, Min(0.002f)] private float minimumCutPreviewWidth = 0.025f;
        [SerializeField] private Color cutPreviewColor = new Color(0.1f, 1f, 1f, 0.95f);
        [Tooltip("Moves the cyan knife preview slightly toward the camera after conforming it to the current 3D skin surface.")]
        [SerializeField, Min(0.001f)] private float cutPreviewSurfaceOffset = 0.025f;
        [SerializeField, TextArea(2, 4)] private string lastCutStatus = "No cut attempted yet.";
        [SerializeField] private bool showRuntimeToolStatus = true;

        [Header("Layer-Aware Cutting")]
        [Tooltip("Routes the Cut tool to the first intact tissue under the pointer: Skin, then Fat, then Muscle.")]
        [SerializeField] private bool cutTopVisibleTissue = true;
        [SerializeField] private DeformableImageMesh skinCutLayer;
        [SerializeField] private DeformableImageMesh fatCutLayer;
        [SerializeField] private DeformableImageMesh muscleCutLayer;

        [Header("Detached Piece Cloth")]
        [SerializeField, Min(0f)] private float cutPieceGravity = 4.5f;
        [SerializeField, Range(0f, 0.5f)] private float cutPieceDamping = 0.08f;
        [SerializeField, Range(0.05f, 1f)] private float cutPieceEdgeStiffness = 0.95f;
        [Tooltip("1.06 allows at most 6% stretch per cloth edge.")]
        [SerializeField, Range(1f, 1.5f)] private float cutPieceMaximumStretch = 1.06f;
        [Tooltip("Whole-piece transport for an interior grab. Lower values feel heavier and bend more locally.")]
        [SerializeField, Range(0f, 1f)] private float cutPieceGrabTransport = 0.65f;
        [Tooltip("Whole-piece transport for an edge grab. Keep this low so pulling an edge folds the cloth instead of moving it like a board.")]
        [SerializeField, Range(0f, 1f)] private float cutPieceEdgeGrabTransport = 0.06f;
        [SerializeField, Range(0f, 1f)] private float cutPieceOutOfPlaneTransport = 0.12f;
        [SerializeField, Range(0f, 1f)] private float cutPieceEdgeOutOfPlaneTransport = 0.02f;
        [Tooltip("Higher values make the held cloth point catch up to the cursor more quickly.")]
        [SerializeField, Min(0.1f)] private float cutPieceGrabResponse = 18f;
        [Tooltip("Maximum local-space speed of the held cloth point. This gives large pieces visible weight.")]
        [SerializeField, Min(0.1f)] private float cutPieceMaximumGrabSpeed = 12f;
        [SerializeField, Min(0f)] private float cutPieceLiftDepthScale = 2.5f;
        [SerializeField, Min(0f)] private float cutPieceMaximumLiftDepth = 3f;
        [SerializeField, Range(1, 16)] private int cutPieceConstraintIterations = 10;
        [SerializeField, Min(1000)] private int cutPieceSolverEdgePassBudget = 60000;
        [SerializeField, Min(0.01f)] private float cutPieceReturnDistance = 0.6f;
        [SerializeField, Range(0f, 0.5f)] private float cutPieceReturnRelativeSize = 0.2f;
        [SerializeField, Range(0f, 1f)] private float cutPieceReturnCoverage = 0.15f;
        [SerializeField, Min(0.1f)] private float cutPieceReturnSpeed = 9f;

        [Header("Detached Piece Box Collision")]
        [SerializeField] private bool cutPieceCollidesWithBoxes = true;
        [SerializeField] private LayerMask cutPieceCollisionLayers = ~0;
        [SerializeField] private bool cutPieceCollidesWithTriggers;
        [SerializeField, Min(0f)] private float cutPieceCollisionThickness = 0.025f;
        [SerializeField, Range(0f, 1f)] private float cutPieceCollisionFriction = 0.4f;
        [SerializeField] private bool cutPieceFallsAfterRelease = true;

        private DeformableImageMesh deformableMesh;
        private BoneSkinCoupler2D boneSkinCoupler;
        private Vector3[] operationStartVertices;
        private Vector3[] workingVertices;
        private int[] operationTriangles;
        private float[] operationGeodesicDistances;
        private Vector3 operationStartLocalPoint;
        private Vector3 operationStartMousePosition;
        private bool operationActive;
        private bool operationMayRevealBack;
        private bool operationPinned;
        private bool reboundActive;
        private float reboundElapsed;
        private Vector3[] reboundStartVertices;
        private Vector3[] reboundTargetVertices;
        private Vector3[] reboundWorkingVertices;
        private readonly List<Vector2> cutPathUvs = new List<Vector2>();
        private readonly List<Vector3> cutPathWorld = new List<Vector3>();
        private bool cutActive;
        private LineRenderer cutPreview;
        private Material cutPreviewMaterial;
        private DeformableImageMesh activeCutMesh;

        public DeformationTool ActiveTool
        {
            get { return activeTool; }
            set { activeTool = value; }
        }

        public float BrushRadius
        {
            get { return brushRadius; }
            set { brushRadius = Mathf.Clamp(value, minimumBrushRadius, maximumBrushRadius); }
        }

        public void ConfigureLayeredCutTargets(
            DeformableImageMesh skin,
            DeformableImageMesh fat,
            DeformableImageMesh muscle)
        {
            skinCutLayer = skin;
            fatCutLayer = fat;
            muscleCutLayer = muscle;
            cutTopVisibleTissue = skin != null && fat != null && muscle != null;
        }

        private void Awake()
        {
            deformableMesh = GetComponent<DeformableImageMesh>();
            boneSkinCoupler = GetComponent<BoneSkinCoupler2D>();
            ResolveLayeredCutTargets();
            activeCutMesh = deformableMesh;
            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }
        }

        private void OnValidate()
        {
            minimumBrushRadius = Mathf.Max(0.01f, minimumBrushRadius);
            maximumBrushRadius = Mathf.Max(minimumBrushRadius, maximumBrushRadius);
            brushRadius = Mathf.Clamp(brushRadius, minimumBrushRadius, maximumBrushRadius);
            visibleEdgeGrabDistance = Mathf.Max(0.001f, visibleEdgeGrabDistance);
            cutPointSpacing = Mathf.Max(0.005f, cutPointSpacing);
            closedLoopDistance = Mathf.Max(0.01f, closedLoopDistance);
            minimumClosedLoopArea = Mathf.Max(0.00001f, minimumClosedLoopArea);
            transparentCutWidth = Mathf.Max(0.005f, transparentCutWidth);
            cutWidthScrollStep = Mathf.Max(0.001f, cutWidthScrollStep);
            minimumCutPreviewWidth = Mathf.Max(0.002f, minimumCutPreviewWidth);
            cutPieceGravity = Mathf.Max(0f, cutPieceGravity);
            cutPieceDamping = Mathf.Clamp(cutPieceDamping, 0f, 0.5f);
            cutPieceEdgeStiffness = Mathf.Clamp(cutPieceEdgeStiffness, 0.05f, 1f);
            cutPieceMaximumStretch = Mathf.Clamp(cutPieceMaximumStretch, 1f, 1.5f);
            cutPieceGrabTransport = Mathf.Clamp01(cutPieceGrabTransport);
            cutPieceEdgeGrabTransport = Mathf.Clamp01(cutPieceEdgeGrabTransport);
            cutPieceOutOfPlaneTransport = Mathf.Clamp01(cutPieceOutOfPlaneTransport);
            cutPieceEdgeOutOfPlaneTransport = Mathf.Clamp01(cutPieceEdgeOutOfPlaneTransport);
            cutPieceGrabResponse = Mathf.Max(0.1f, cutPieceGrabResponse);
            cutPieceMaximumGrabSpeed = Mathf.Max(0.1f, cutPieceMaximumGrabSpeed);
            cutPieceLiftDepthScale = Mathf.Max(0f, cutPieceLiftDepthScale);
            cutPieceMaximumLiftDepth = Mathf.Max(0f, cutPieceMaximumLiftDepth);
            cutPieceConstraintIterations = Mathf.Clamp(cutPieceConstraintIterations, 1, 16);
            cutPieceSolverEdgePassBudget = Mathf.Max(1000, cutPieceSolverEdgePassBudget);
            cutPieceReturnDistance = Mathf.Max(0.01f, cutPieceReturnDistance);
            cutPieceReturnRelativeSize = Mathf.Clamp(cutPieceReturnRelativeSize, 0f, 0.5f);
            cutPieceReturnCoverage = Mathf.Clamp01(cutPieceReturnCoverage);
            cutPieceReturnSpeed = Mathf.Max(0.1f, cutPieceReturnSpeed);
            cutPieceCollisionThickness = Mathf.Max(0f, cutPieceCollisionThickness);
            cutPieceCollisionFriction = Mathf.Clamp01(cutPieceCollisionFriction);
            cutPreviewSurfaceOffset = Mathf.Max(0.001f, cutPreviewSurfaceOffset);
        }

        private void OnDestroy()
        {
            if (cutPreview != null)
            {
                Destroy(cutPreview.gameObject);
            }

            if (cutPreviewMaterial != null)
            {
                Destroy(cutPreviewMaterial);
            }
        }

        private void OnGUI()
        {
            if (!showRuntimeToolStatus || !Application.isPlaying || !FaceRuntimeUILayout.Left(1))
            {
                return;
            }

            string pinHint = activeTool == DeformationTool.Drag
                ? "Release: rebound | Middle mouse: pin"
                : activeTool == DeformationTool.Cut
                    ? "Left drag: draw cut | Mouse wheel: cut width "
                        + transparentCutWidth.ToString("0.000")
                    : "1 Drag | 2 Inflate | 3 Pinch | 4 Cut";
            GUI.Box(
                FaceRuntimeUILayout.Enabled ? FaceRuntimeUILayout.Panel(false, 110f) : new Rect(12f, 12f, 620f, 82f),
                "Active Tool: " + activeTool.ToString().ToUpperInvariant()
                + "\n" + pinHint
                + (activeTool == DeformationTool.Cut ? "\n" + lastCutStatus : string.Empty));
        }

        private void Update()
        {
            if (deformableMesh == null || deformableMesh.LogicalVertexCount == 0)
            {
                return;
            }

            if (BoneCutInteractor.IsAnyBoneToolActive)
            {
                StopRebound();
                if (operationActive)
                {
                    CancelOperation();
                }

                if (cutActive)
                {
                    CancelCut();
                }

                return;
            }

            if (AnatomyBoneFragmentMover.IsPointerCaptured)
            {
                StopRebound();
                if (operationActive)
                {
                    CancelOperation();
                }

                if (cutActive)
                {
                    CancelCut();
                }

                return;
            }

            HandleShortcuts();
            UpdateRebound();

            if (Input.GetMouseButtonDown(2))
            {
                PinCurrentShape();
            }

            if (activeTool == DeformationTool.Cut)
            {
                HandleCutWidth();
                HandleCutInput();
                return;
            }

            HandleBrushRadius();

            if (Input.GetMouseButtonDown(0))
            {
                BeginOperation();
            }

            if (operationActive && Input.GetMouseButton(0))
            {
                UpdateOperation();
            }

            if (operationActive && Input.GetMouseButtonUp(0))
            {
                EndOperation();
            }

            if (operationActive && Input.GetKeyDown(KeyCode.Escape))
            {
                CancelOperation();
            }
        }

        private void HandleShortcuts()
        {
            if (!enableKeyboardShortcuts)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Alpha1))
            {
                activeTool = DeformationTool.Drag;
            }
            else if (Input.GetKeyDown(KeyCode.Alpha2))
            {
                activeTool = DeformationTool.Inflate;
            }
            else if (Input.GetKeyDown(KeyCode.Alpha3))
            {
                activeTool = DeformationTool.Pinch;
            }
            else if (Input.GetKeyDown(KeyCode.Alpha4))
            {
                activeTool = DeformationTool.Cut;
            }

            if (Input.GetKeyDown(KeyCode.R) && !operationActive)
            {
                StopRebound();
                if (boneSkinCoupler != null)
                {
                    boneSkinCoupler.ResetFaceToBoneShape();
                }
                else
                {
                    deformableMesh.ResetDeformation();
                }
            }
        }

        private void HandleBrushRadius()
        {
            if (operationActive)
            {
                return;
            }

            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.001f)
            {
                BrushRadius = brushRadius + wheel * wheelRadiusSpeed;
            }
        }

        private void BeginOperation()
        {
            if (DeformableImagePieceMover.IsPointerCaptured)
            {
                return;
            }

            StopRebound();

            if (ignorePointerOverUi && EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                return;
            }

            if (!TryGetLocalPointerPosition(out Vector3 localPoint))
            {
                return;
            }

            if (!deformableMesh.TryClassifyGrabPoint(
                    localPoint,
                    visibleEdgeGrabDistance,
                    out operationMayRevealBack))
            {
                return;
            }

            if (!deformableMesh.HasVertexWithinRadius(localPoint, brushRadius))
            {
                return;
            }

            operationStartVertices = deformableMesh.CopyCurrentVertices();
            workingVertices = new Vector3[operationStartVertices.Length];
            operationTriangles = deformableMesh.CopyMechanicallyIntactTriangles();
            deformableMesh.TryBuildGeodesicDistances(
                localPoint,
                brushRadius,
                out operationGeodesicDistances);
            operationStartLocalPoint = localPoint;
            operationStartMousePosition = Input.mousePosition;
            operationPinned = false;
            operationActive = true;
        }

        private void UpdateOperation()
        {
            if (!TryGetLocalPointerPosition(out Vector3 localPoint))
            {
                return;
            }

            Vector3 dragDelta = localPoint - operationStartLocalPoint;
            float verticalMouseDelta = (Input.mousePosition.y - operationStartMousePosition.y)
                / Mathf.Max(1f, Screen.height);
            float scaleDelta = verticalMouseDelta * inflateStrength;
            if (activeTool == DeformationTool.Pinch)
            {
                scaleDelta = -scaleDelta;
            }

            BuildWorkingVertices(dragDelta, scaleDelta, 1f);

            if (!operationMayRevealBack
                && !PreservesTriangleOrientation(operationStartVertices, workingVertices))
            {
                float safeScale = 0f;
                float unsafeScale = 1f;

                // Binary-search the strongest deformation that keeps every intact triangle
                // on its current side.
                for (int iteration = 0; iteration < 12; iteration++)
                {
                    float candidateScale = (safeScale + unsafeScale) * 0.5f;
                    BuildWorkingVertices(dragDelta, scaleDelta, candidateScale);
                    if (PreservesTriangleOrientation(operationStartVertices, workingVertices))
                    {
                        safeScale = candidateScale;
                    }
                    else
                    {
                        unsafeScale = candidateScale;
                    }
                }

                BuildWorkingVertices(dragDelta, scaleDelta, safeScale);
            }

            deformableMesh.SetLogicalVertices(workingVertices);
        }

        private void BuildWorkingVertices(Vector3 dragDelta, float scaleDelta, float deformationScale)
        {
            Vector3 localDrag = Vector3.ClampMagnitude(
                dragDelta,
                brushRadius * maximumInteriorStretch);

            for (int i = 0; i < operationStartVertices.Length; i++)
            {
                Vector3 original = operationStartVertices[i];
                Vector2 fromBrush = (Vector2)(original - operationStartLocalPoint);
                float distance = operationGeodesicDistances != null
                    && i < operationGeodesicDistances.Length
                    ? operationGeodesicDistances[i]
                    : fromBrush.magnitude;
                float weight = 0f;

                if (distance <= brushRadius)
                {
                    float normalizedDistance = distance / brushRadius;
                    weight = Mathf.Pow(1f - Mathf.Clamp01(normalizedDistance), falloffPower);
                }

                if (activeTool == DeformationTool.Drag)
                {
                    if (operationMayRevealBack)
                    {
                        workingVertices[i] = original + dragDelta * weight;
                    }
                    else
                    {
                        workingVertices[i] = original
                            + localDrag * (weight * deformationScale);
                    }
                }
                else
                {
                    Vector3 radialDirection = new Vector3(fromBrush.x, fromBrush.y, 0f);
                    workingVertices[i] = original
                        + radialDirection * (scaleDelta * weight * deformationScale);
                }
            }
        }

        private bool PreservesTriangleOrientation(Vector3[] baseline, Vector3[] candidate)
        {
            if (operationTriangles == null)
            {
                return true;
            }

            const float degenerateThreshold = 0.0000001f;
            for (int triangle = 0; triangle < operationTriangles.Length; triangle += 3)
            {
                int indexA = operationTriangles[triangle];
                int indexB = operationTriangles[triangle + 1];
                int indexC = operationTriangles[triangle + 2];
                float baselineArea = SignedDoubleArea(
                    baseline[indexA],
                    baseline[indexB],
                    baseline[indexC]);

                if (Mathf.Abs(baselineArea) < degenerateThreshold)
                {
                    continue;
                }

                float candidateArea = SignedDoubleArea(
                    candidate[indexA],
                    candidate[indexB],
                    candidate[indexC]);
                float orientedCandidateArea = candidateArea * Mathf.Sign(baselineArea);

                if (orientedCandidateArea < Mathf.Abs(baselineArea) * minimumTriangleAreaRatio)
                {
                    return false;
                }
            }

            return true;
        }

        private static float SignedDoubleArea(Vector3 a, Vector3 b, Vector3 c)
        {
            return (b.x - a.x) * (c.y - a.y)
                - (b.y - a.y) * (c.x - a.x);
        }

        private void EndOperation()
        {
            if (activeTool == DeformationTool.Drag
                && !operationPinned
                && operationStartVertices != null)
            {
                StartRebound(operationStartVertices);
            }

            ClearOperationState();
        }

        private void ClearOperationState()
        {
            operationActive = false;
            operationStartVertices = null;
            workingVertices = null;
            operationTriangles = null;
            operationGeodesicDistances = null;
            operationMayRevealBack = false;
            operationPinned = false;
        }

        private void CancelOperation()
        {
            if (operationStartVertices != null)
            {
                deformableMesh.SetLogicalVertices(operationStartVertices);
            }

            ClearOperationState();
        }

        private void PinCurrentShape()
        {
            if (operationActive)
            {
                operationPinned = true;
                return;
            }

            // Middle-clicking while the mesh is springing freezes it at the current frame.
            StopRebound();
        }

        private void StartRebound(Vector3[] targetVertices)
        {
            reboundStartVertices = deformableMesh.CopyCurrentVertices();
            reboundTargetVertices = (Vector3[])targetVertices.Clone();
            reboundWorkingVertices = new Vector3[reboundStartVertices.Length];
            reboundElapsed = 0f;
            reboundActive = true;
        }

        private void UpdateRebound()
        {
            if (!reboundActive || reboundStartVertices == null || reboundTargetVertices == null)
            {
                return;
            }

            reboundElapsed += Time.deltaTime;
            float normalizedTime = Mathf.Clamp01(reboundElapsed / Mathf.Max(0.05f, reboundDuration));
            float springFactor = 1f - Mathf.Exp(-reboundDamping * normalizedTime)
                * Mathf.Cos(Mathf.PI * 2f * reboundOscillations * normalizedTime);

            for (int index = 0; index < reboundWorkingVertices.Length; index++)
            {
                reboundWorkingVertices[index] = Vector3.LerpUnclamped(
                    reboundStartVertices[index],
                    reboundTargetVertices[index],
                    springFactor);
            }

            if (normalizedTime >= 1f)
            {
                deformableMesh.SetLogicalVertices(reboundTargetVertices);
                StopRebound();
            }
            else
            {
                deformableMesh.SetLogicalVertices(reboundWorkingVertices);
            }
        }

        private void StopRebound()
        {
            reboundActive = false;
            reboundStartVertices = null;
            reboundTargetVertices = null;
            reboundWorkingVertices = null;
        }

        private void HandleCutInput()
        {
            if (Input.GetMouseButtonDown(0))
            {
                BeginCut();
            }

            if (cutActive && Input.GetMouseButton(0))
            {
                AddCutPoint();
            }

            if (cutActive && Input.GetMouseButtonUp(0))
            {
                CompleteCut();
            }

            if (cutActive && Input.GetKeyDown(KeyCode.Escape))
            {
                CancelCut();
            }
        }

        private void HandleCutWidth()
        {
            if (cutActive)
            {
                return;
            }

            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) <= 0.001f)
            {
                return;
            }

            transparentCutWidth = Mathf.Max(
                0.005f,
                transparentCutWidth + wheel * cutWidthScrollStep);
            UpdateCutPreviewWidth();
            lastCutStatus = "Transparent cut width: " + transparentCutWidth.ToString("0.000");
        }

        private void BeginCut()
        {
            if (DeformableImagePieceMover.IsPointerCaptured)
            {
                return;
            }

            if (ignorePointerOverUi && EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                return;
            }

            if (!TrySelectCutTarget(out activeCutMesh))
            {
                lastCutStatus = "Cut blocked: no exposed tissue under the pointer.";
                return;
            }

            StopRebound();
            cutPathUvs.Clear();
            cutPathWorld.Clear();
            cutActive = true;
            lastCutStatus = "Drawing cut on " + GetCutLayerName(activeCutMesh) + "...";
            EnsureCutPreview();
            cutPreview.positionCount = 0;
            AddCutPoint();

            if (cutPathUvs.Count == 0)
            {
                CancelCut();
            }
        }

        private void AddCutPoint()
        {
            DeformableImageMesh cutMesh = activeCutMesh != null
                ? activeCutMesh
                : deformableMesh;
            if (!TryGetLocalPointerPosition(cutMesh, out Vector3 localPoint)
                || !cutMesh.TryGetNormalizedUvAtCurrentPoint(
                    localPoint,
                    out Vector2 normalizedUv,
                    out Vector3 surfacePoint))
            {
                return;
            }

            Vector3 worldPoint = cutMesh.transform.TransformPoint(surfacePoint);
            Camera cameraToUse = targetCamera != null ? targetCamera : Camera.main;
            if (cameraToUse != null)
            {
                Vector3 towardCamera = cameraToUse.orthographic
                    ? -cameraToUse.transform.forward
                    : (cameraToUse.transform.position - worldPoint).normalized;
                worldPoint += towardCamera * cutPreviewSurfaceOffset;
            }
            else
            {
                worldPoint -= transform.forward * cutPreviewSurfaceOffset;
            }

            if (cutPathWorld.Count > 0
                && Vector3.Distance(cutPathWorld[cutPathWorld.Count - 1], worldPoint) < cutPointSpacing)
            {
                return;
            }

            cutPathUvs.Add(normalizedUv);
            cutPathWorld.Add(worldPoint);
            cutPreview.positionCount = cutPathWorld.Count;
            cutPreview.SetPosition(cutPathWorld.Count - 1, worldPoint);
        }

        private void CompleteCut()
        {
            double processingStarted = Time.realtimeSinceStartupAsDouble;
            DeformableImageMesh cutMesh = activeCutMesh != null
                ? activeCutMesh
                : deformableMesh;
            string cutLayerName = GetCutLayerName(cutMesh);
            cutActive = false;
            if (cutPreview != null)
            {
                cutPreview.positionCount = 0;
            }

            if (cutPathUvs.Count < 2)
            {
                lastCutStatus = "Cut ignored: fewer than two sampled points.";
                Debug.LogWarning(lastCutStatus, this);
                return;
            }

            bool isClosed = TryExtractClosedCutLoop(out List<Vector2> closedLoop);

            bool applied;
            if (isClosed)
            {
                applied = cutMesh.ApplyClosedCut(closedLoop, out DeformableImageMesh piece);
                if (applied)
                {
                    // Remove half of the seam from the outside mesh and half from the
                    // detached piece. Together they create a real adjustable transparent gap.
                    // The parent hole and detached piece are already separate physical
                    // meshes. The seam only needs alpha/red visualization; cutting both
                    // meshes a second time is redundant and very expensive on large pieces.
                    cutMesh.ApplyOpenCut(
                        closedLoop,
                        transparentCutWidth,
                        false);
                    if (piece != null)
                    {
                        piece.ApplyOpenCut(
                            closedLoop,
                            transparentCutWidth,
                            false,
                            true);
                        ConfigureDetachedPiece(piece);
                    }
                }

                lastCutStatus = applied
                    ? cutLayerName + " closed cut applied. Extracted loop and transparent seam width "
                        + transparentCutWidth.ToString("0.000") + "."
                    : "Closed cut failed: the loop did not contain any visible solid pixels.";
            }
            else
            {
                bool couldCloseExistingCuts = cutMesh.CouldCloseRegionWithExistingCuts(
                    cutPathUvs,
                    transparentCutWidth);
                applied = cutMesh.ApplyOpenCut(cutPathUvs, transparentCutWidth);
                if (applied)
                {
                    DeformableImageMesh enclosedPiece = null;
                    bool extractedEnclosedPiece = couldCloseExistingCuts
                        && cutMesh.TryExtractEnclosedCutComponent(
                            minimumClosedLoopArea,
                            out enclosedPiece);
                    if (extractedEnclosedPiece)
                    {
                        ConfigureDetachedPiece(enclosedPiece);
                        lastCutStatus = "Multiple knife strokes formed an enclosed "
                            + cutLayerName + " region; the piece was physically detached.";
                    }
                    else
                    {
                        lastCutStatus = cutLayerName + " open cut applied. Width "
                            + transparentCutWidth.ToString("0.000")
                            + "; opposite lips share no triangles or vertices.";
                    }
                }
                else
                {
                    lastCutStatus = "Open cut failed: the path did not cross any remaining visible pixels.";
                }
            }

            int processingMilliseconds = Mathf.Max(
                0,
                Mathf.RoundToInt((float)(
                    (Time.realtimeSinceStartupAsDouble - processingStarted) * 1000.0)));
            lastCutStatus += " Processing: " + processingMilliseconds + " ms.";

            if (applied)
            {
                Debug.Log(lastCutStatus, this);
            }
            else
            {
                Debug.LogWarning(lastCutStatus, this);
            }

            cutPathUvs.Clear();
            cutPathWorld.Clear();
        }

        private void ConfigureDetachedPiece(DeformableImageMesh piece)
        {
            if (piece == null)
            {
                return;
            }

            BoneSkinCoupler2D cutLayerBoneCoupler = activeCutMesh != null
                ? activeCutMesh.GetComponent<BoneSkinCoupler2D>()
                : boneSkinCoupler;
            DetachedSkinBoneBinding2D boneBinding = cutLayerBoneCoupler != null
                ? cutLayerBoneCoupler.CreateDetachedPieceBinding(piece)
                : null;
            DeformableImagePieceMover mover = piece.GetComponent<DeformableImagePieceMover>();
            if (mover == null)
            {
                return;
            }

            mover.ConfigureBoneAttachment(boneBinding);

            mover.ConfigureCloth(
                cutPieceGravity,
                cutPieceDamping,
                cutPieceEdgeStiffness,
                cutPieceMaximumStretch,
                cutPieceGrabTransport,
                cutPieceEdgeGrabTransport,
                cutPieceOutOfPlaneTransport,
                cutPieceEdgeOutOfPlaneTransport,
                cutPieceGrabResponse,
                cutPieceMaximumGrabSpeed,
                cutPieceLiftDepthScale,
                cutPieceMaximumLiftDepth,
                cutPieceConstraintIterations,
                cutPieceSolverEdgePassBudget,
                cutPieceReturnDistance,
                cutPieceReturnRelativeSize,
                cutPieceReturnCoverage,
                cutPieceReturnSpeed);
            mover.ConfigureBoxCollision(
                cutPieceCollidesWithBoxes,
                cutPieceCollisionLayers,
                cutPieceCollidesWithTriggers,
                cutPieceCollisionThickness,
                cutPieceCollisionFriction,
                cutPieceFallsAfterRelease);
        }

        private bool TryExtractClosedCutLoop(out List<Vector2> bestLoop)
        {
            bestLoop = null;
            if (cutPathUvs.Count < 3 || cutPathWorld.Count != cutPathUvs.Count)
            {
                return false;
            }

            int pointCount = cutPathUvs.Count;
            Vector2[] planarPoints = new Vector2[pointCount];
            Transform cutTransform = activeCutMesh != null
                ? activeCutMesh.transform
                : transform;
            Vector3 planeRight = cutTransform.right.normalized;
            Vector3 planeUp = cutTransform.up.normalized;
            for (int index = 0; index < pointCount; index++)
            {
                Vector3 offset = cutPathWorld[index] - cutTransform.position;
                planarPoints[index] = new Vector2(
                    Vector3.Dot(offset, planeRight),
                    Vector3.Dot(offset, planeUp));
            }

            float bestArea = minimumClosedLoopArea;
            float closureDistanceSquared = closedLoopDistance * closedLoopDistance;
            int bestFirstSegment = -1;
            int bestSecondSegment = -1;
            float bestFirstParameter = 0f;
            float bestSecondParameter = 0f;
            float[] prefixCross = new float[pointCount];
            for (int point = 0; point + 1 < pointCount; point++)
            {
                prefixCross[point + 1] = prefixCross[point]
                    + Cross(cutPathUvs[point], cutPathUvs[point + 1]);
            }

            // Preserve the familiar start-to-end closure rule, but snap both ends to
            // one UV so the polygon passed to Clipper is mathematically closed.
            if ((planarPoints[0] - planarPoints[pointCount - 1]).sqrMagnitude
                <= closureDistanceSquared)
            {
                Vector2 join = (cutPathUvs[0] + cutPathUvs[pointCount - 1]) * 0.5f;
                float area = ClosedSubpathArea(0, pointCount - 2, join, prefixCross);
                if (area > bestArea)
                {
                    bestArea = area;
                    bestFirstSegment = 0;
                    bestSecondSegment = pointCount - 2;
                    bestFirstParameter = 0f;
                    bestSecondParameter = 1f;
                }
            }

            // Spatially index earlier segments and only compare nearby cells. This keeps
            // long strokes close to linear time instead of testing every segment pair.
            int segmentCount = pointCount - 1;
            float cellSize = Mathf.Max(0.001f, closedLoopDistance);
            Dictionary<long, List<int>> segmentGrid = new Dictionary<long, List<int>>();
            int[] candidateMarks = new int[segmentCount];
            for (int secondSegment = 0; secondSegment < segmentCount; secondSegment++)
            {
                Vector2 secondStart = planarPoints[secondSegment];
                Vector2 secondEnd = planarPoints[secondSegment + 1];
                GetSegmentCellRange(
                    secondStart,
                    secondEnd,
                    closedLoopDistance,
                    cellSize,
                    out int queryMinimumX,
                    out int queryMaximumX,
                    out int queryMinimumY,
                    out int queryMaximumY);

                int queryMark = secondSegment + 1;
                for (int cellX = queryMinimumX; cellX <= queryMaximumX; cellX++)
                {
                    for (int cellY = queryMinimumY; cellY <= queryMaximumY; cellY++)
                    {
                        if (!segmentGrid.TryGetValue(
                                SegmentGridKey(cellX, cellY),
                                out List<int> candidates))
                        {
                            continue;
                        }

                        for (int candidateIndex = 0;
                            candidateIndex < candidates.Count;
                            candidateIndex++)
                        {
                            int firstSegment = candidates[candidateIndex];
                            if (secondSegment - firstSegment < 2
                                || candidateMarks[firstSegment] == queryMark)
                            {
                                continue;
                            }

                            candidateMarks[firstSegment] = queryMark;
                            float distanceSquared = ClosestSegmentDistanceSquared(
                                planarPoints[firstSegment],
                                planarPoints[firstSegment + 1],
                                secondStart,
                                secondEnd,
                                out float firstParameter,
                                out float secondParameter);
                            if (distanceSquared > closureDistanceSquared)
                            {
                                continue;
                            }

                            Vector2 firstUv = Vector2.Lerp(
                                cutPathUvs[firstSegment],
                                cutPathUvs[firstSegment + 1],
                                firstParameter);
                            Vector2 secondUv = Vector2.Lerp(
                                cutPathUvs[secondSegment],
                                cutPathUvs[secondSegment + 1],
                                secondParameter);
                            Vector2 join = (firstUv + secondUv) * 0.5f;
                            float area = ClosedSubpathArea(
                                firstSegment,
                                secondSegment,
                                join,
                                prefixCross);
                            if (area <= bestArea)
                            {
                                continue;
                            }

                            bestArea = area;
                            bestFirstSegment = firstSegment;
                            bestSecondSegment = secondSegment;
                            bestFirstParameter = firstParameter;
                            bestSecondParameter = secondParameter;
                        }
                    }
                }

                GetSegmentCellRange(
                    secondStart,
                    secondEnd,
                    0f,
                    cellSize,
                    out int insertMinimumX,
                    out int insertMaximumX,
                    out int insertMinimumY,
                    out int insertMaximumY);
                for (int cellX = insertMinimumX; cellX <= insertMaximumX; cellX++)
                {
                    for (int cellY = insertMinimumY; cellY <= insertMaximumY; cellY++)
                    {
                        long key = SegmentGridKey(cellX, cellY);
                        if (!segmentGrid.TryGetValue(key, out List<int> segments))
                        {
                            segments = new List<int>(4);
                            segmentGrid.Add(key, segments);
                        }

                        segments.Add(secondSegment);
                    }
                }
            }

            if (bestFirstSegment < 0 || bestSecondSegment < 0)
            {
                return false;
            }

            Vector2 bestFirstUv = Vector2.Lerp(
                cutPathUvs[bestFirstSegment],
                cutPathUvs[bestFirstSegment + 1],
                bestFirstParameter);
            Vector2 bestSecondUv = Vector2.Lerp(
                cutPathUvs[bestSecondSegment],
                cutPathUvs[bestSecondSegment + 1],
                bestSecondParameter);
            Vector2 bestJoin = (bestFirstUv + bestSecondUv) * 0.5f;
            List<Vector2> rawLoop = new List<Vector2>(
                bestSecondSegment - bestFirstSegment + 3)
            {
                bestJoin
            };
            for (int point = bestFirstSegment + 1; point <= bestSecondSegment; point++)
            {
                rawLoop.Add(cutPathUvs[point]);
            }

            rawLoop.Add(bestJoin);
            return TrySanitizeClosedLoop(rawLoop, out bestLoop);
        }

        private bool TrySanitizeClosedLoop(IList<Vector2> rawLoop, out List<Vector2> cleanLoop)
        {
            cleanLoop = null;
            if (rawLoop == null || rawLoop.Count < 4)
            {
                return false;
            }

            Vector2[] inputPath = new Vector2[rawLoop.Count];
            for (int index = 0; index < rawLoop.Count; index++)
            {
                inputPath[index] = rawLoop[index];
            }

            if (!DeformableImagePolygonUtility.TryNormalizePaths(
                    new List<Vector2[]> { inputPath },
                    out List<Vector2[]> normalizedPaths))
            {
                return false;
            }

            Vector2[] selectedPath = null;
            float selectedArea = minimumClosedLoopArea;
            for (int pathIndex = 0; pathIndex < normalizedPaths.Count; pathIndex++)
            {
                Vector2[] path = normalizedPaths[pathIndex];
                float area = Mathf.Abs(SignedPolygonArea(path));
                if (path.Length >= 3 && area > selectedArea)
                {
                    selectedPath = path;
                    selectedArea = area;
                }
            }

            if (selectedPath == null)
            {
                return false;
            }

            cleanLoop = new List<Vector2>(selectedPath.Length + 1);
            cleanLoop.AddRange(selectedPath);
            cleanLoop.Add(selectedPath[0]);
            return true;
        }

        private float ClosedSubpathArea(
            int firstSegment,
            int secondSegment,
            Vector2 join,
            float[] prefixCross)
        {
            if (secondSegment - firstSegment < 2)
            {
                return 0f;
            }

            float doubleArea = Cross(join, cutPathUvs[firstSegment + 1]);
            doubleArea += prefixCross[secondSegment] - prefixCross[firstSegment + 1];
            doubleArea += Cross(cutPathUvs[secondSegment], join);
            return Mathf.Abs(doubleArea * 0.5f);
        }

        private static void GetSegmentCellRange(
            Vector2 start,
            Vector2 end,
            float padding,
            float cellSize,
            out int minimumX,
            out int maximumX,
            out int minimumY,
            out int maximumY)
        {
            minimumX = Mathf.FloorToInt((Mathf.Min(start.x, end.x) - padding) / cellSize);
            maximumX = Mathf.FloorToInt((Mathf.Max(start.x, end.x) + padding) / cellSize);
            minimumY = Mathf.FloorToInt((Mathf.Min(start.y, end.y) - padding) / cellSize);
            maximumY = Mathf.FloorToInt((Mathf.Max(start.y, end.y) + padding) / cellSize);
        }

        private static long SegmentGridKey(int x, int y)
        {
            return ((long)x << 32) ^ (uint)y;
        }

        private static float SignedPolygonArea(IList<Vector2> polygon)
        {
            float doubleArea = 0f;
            for (int index = 0; index < polygon.Count; index++)
            {
                Vector2 current = polygon[index];
                Vector2 next = polygon[(index + 1) % polygon.Count];
                doubleArea += current.x * next.y - next.x * current.y;
            }

            return doubleArea * 0.5f;
        }

        private static float ClosestSegmentDistanceSquared(
            Vector2 firstStart,
            Vector2 firstEnd,
            Vector2 secondStart,
            Vector2 secondEnd,
            out float firstParameter,
            out float secondParameter)
        {
            Vector2 firstDirection = firstEnd - firstStart;
            Vector2 secondDirection = secondEnd - secondStart;
            float cross = Cross(firstDirection, secondDirection);
            if (Mathf.Abs(cross) > 0.000001f)
            {
                Vector2 offset = secondStart - firstStart;
                float intersectionFirst = Cross(offset, secondDirection) / cross;
                float intersectionSecond = Cross(offset, firstDirection) / cross;
                if (intersectionFirst >= 0f && intersectionFirst <= 1f
                    && intersectionSecond >= 0f && intersectionSecond <= 1f)
                {
                    firstParameter = intersectionFirst;
                    secondParameter = intersectionSecond;
                    return 0f;
                }
            }

            firstParameter = 0f;
            secondParameter = ClosestPointParameter(firstStart, secondStart, secondEnd);
            float bestDistanceSquared = (
                firstStart - Vector2.Lerp(secondStart, secondEnd, secondParameter)).sqrMagnitude;

            ConsiderSegmentEndpoint(
                firstEnd, 1f, secondStart, secondEnd,
                ref firstParameter, ref secondParameter, ref bestDistanceSquared);
            ConsiderSegmentEndpoint(
                secondStart, 0f, firstStart, firstEnd,
                ref secondParameter, ref firstParameter, ref bestDistanceSquared);
            ConsiderSegmentEndpoint(
                secondEnd, 1f, firstStart, firstEnd,
                ref secondParameter, ref firstParameter, ref bestDistanceSquared);
            return bestDistanceSquared;
        }

        private static void ConsiderSegmentEndpoint(
            Vector2 point,
            float pointParameter,
            Vector2 otherStart,
            Vector2 otherEnd,
            ref float pointResult,
            ref float otherResult,
            ref float bestDistanceSquared)
        {
            float otherParameter = ClosestPointParameter(point, otherStart, otherEnd);
            float distanceSquared = (point - Vector2.Lerp(otherStart, otherEnd, otherParameter)).sqrMagnitude;
            if (distanceSquared >= bestDistanceSquared)
            {
                return;
            }

            bestDistanceSquared = distanceSquared;
            pointResult = pointParameter;
            otherResult = otherParameter;
        }

        private static float ClosestPointParameter(Vector2 point, Vector2 start, Vector2 end)
        {
            Vector2 direction = end - start;
            float denominator = direction.sqrMagnitude;
            return denominator <= 0.0000001f
                ? 0f
                : Mathf.Clamp01(Vector2.Dot(point - start, direction) / denominator);
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private void CancelCut()
        {
            cutActive = false;
            cutPathUvs.Clear();
            cutPathWorld.Clear();
            if (cutPreview != null)
            {
                cutPreview.positionCount = 0;
            }
        }

        private void EnsureCutPreview()
        {
            if (cutPreview == null)
            {
                GameObject previewObject = new GameObject("Cut Preview");
                cutPreview = previewObject.AddComponent<LineRenderer>();
                cutPreview.useWorldSpace = true;
                cutPreview.loop = false;
                cutPreview.numCapVertices = 4;
                cutPreview.numCornerVertices = 4;
                cutPreview.startColor = cutPreviewColor;
                cutPreview.endColor = cutPreviewColor;

                Shader shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    cutPreviewMaterial = new Material(shader)
                    {
                        name = "Cut Preview Material",
                        hideFlags = HideFlags.DontSave
                    };
                    cutPreview.material = cutPreviewMaterial;
                }

                cutPreview.sortingOrder = short.MaxValue;
            }

            UpdateCutPreviewWidth();
        }

        private void UpdateCutPreviewWidth()
        {
            if (cutPreview == null)
            {
                return;
            }

            float previewWidth = Mathf.Max(minimumCutPreviewWidth, transparentCutWidth);
            cutPreview.startWidth = previewWidth;
            cutPreview.endWidth = previewWidth;
        }

        private void ResolveLayeredCutTargets()
        {
            if (skinCutLayer != null && fatCutLayer != null && muscleCutLayer != null)
            {
                return;
            }

            FacialFeaturePrecut2D anatomy = FindObjectOfType<FacialFeaturePrecut2D>(true);
            if (anatomy == null)
            {
                return;
            }

            skinCutLayer = anatomy.SkinLayer;
            fatCutLayer = anatomy.FatLayer;
            muscleCutLayer = anatomy.MuscleLayer;
        }

        private bool TrySelectCutTarget(out DeformableImageMesh target)
        {
            target = null;
            if (!cutTopVisibleTissue)
            {
                target = deformableMesh;
                return target != null;
            }

            ResolveLayeredCutTargets();
            DeformableImageMesh[] outsideToInside =
            {
                skinCutLayer,
                fatCutLayer,
                muscleCutLayer
            };
            for (int index = 0; index < outsideToInside.Length; index++)
            {
                DeformableImageMesh candidate = outsideToInside[index];
                if (candidate == null || !candidate.isActiveAndEnabled)
                {
                    continue;
                }

                MeshRenderer renderer = candidate.GetComponent<MeshRenderer>();
                if (renderer != null && !renderer.enabled)
                {
                    continue;
                }

                if (TryGetLocalPointerPosition(candidate, out Vector3 localPoint)
                    && candidate.IsVisibleMaterialAtCurrentPoint(localPoint))
                {
                    target = candidate;
                    return true;
                }
            }

            return false;
        }

        private string GetCutLayerName(DeformableImageMesh mesh)
        {
            if (mesh == skinCutLayer)
            {
                return "Skin";
            }

            if (mesh == fatCutLayer)
            {
                return "Fat";
            }

            if (mesh == muscleCutLayer)
            {
                return "Muscle";
            }

            return mesh != null ? mesh.name : "Tissue";
        }

        private bool TryGetLocalPointerPosition(out Vector3 localPoint)
        {
            return TryGetLocalPointerPosition(deformableMesh, out localPoint);
        }

        private bool TryGetLocalPointerPosition(
            DeformableImageMesh targetMesh,
            out Vector3 localPoint)
        {
            localPoint = default(Vector3);
            Camera cameraToUse = targetCamera != null ? targetCamera : Camera.main;
            if (cameraToUse == null || targetMesh == null)
            {
                return false;
            }

            Ray ray = cameraToUse.ScreenPointToRay(Input.mousePosition);
            Transform targetTransform = targetMesh.transform;
            Plane interactionPlane = new Plane(
                targetTransform.forward,
                targetTransform.position);
            if (!interactionPlane.Raycast(ray, out float distance))
            {
                return false;
            }

            Vector3 worldPoint = ray.GetPoint(distance);
            localPoint = targetTransform.InverseTransformPoint(worldPoint);
            localPoint.z = 0f;
            return true;
        }
    }
}
