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
        private const float ManualDragTravelScale = 0.5f;

        public enum DeformationTool
        {
            [InspectorName("手动拖拽")] Drag,
            Inflate,
            Pinch,
            Cut,
            SkinHook,
            Retractor,
            Dissector,
            Suture
        }

        [Header("Input")]
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool ignorePointerOverUi = true;
        [SerializeField] private bool enableKeyboardShortcuts = true;

        [Header("Brush")]
        [SerializeField] private DeformationTool activeTool = DeformationTool.Drag;
        [SerializeField, Min(0.01f)] private float brushRadius = 0.75f;
        [Tooltip("Radial falloff for Inflate / Pinch. Manual Drag uses its own smooth broad grip.")]
        [SerializeField, Range(0.25f, 8f)] private float falloffPower = 2f;
        [SerializeField, Min(0.01f)] private float inflateStrength = 3f;
        [SerializeField, Min(0.01f)] private float wheelRadiusSpeed = 0.15f;
        [SerializeField, Min(0.01f)] private float minimumBrushRadius = 0.1f;
        [SerializeField, Min(0.02f)] private float maximumBrushRadius = 3f;

        [Header("Cloth-like Dragging")]
        [Tooltip("Only grabs this close to an incision may fold over and reveal the back image. Original silhouette stays anchored.")]
        [SerializeField, Min(0.001f)] private float visibleEdgeGrabDistance = 0.2f;
        [Tooltip("Base travel as a fraction of the brush radius. Manual drag uses half of the geometry-safe limit.")]
        [SerializeField, Range(0.05f, 0.45f)] private float maximumInteriorStretch = 0.32f;
        [Tooltip("Prevents intact interior triangles from collapsing to almost zero area.")]
        [SerializeField, Range(0.001f, 0.25f)] private float minimumTriangleAreaRatio = 0.02f;

        [Header("Manual Drag Depth")]
        [Tooltip("Small lift toward the camera per unit of manual dragging. Separates folded surfaces without preventing incision flips.")]
        [SerializeField, Range(0f, 0.3f)] private float manualDragDepthRatio = 0.12f;
        [SerializeField, Min(0f)] private float maximumManualDragDepth = 0.08f;
        [Header("Incision Manual Drag")]
        [Tooltip("Base travel near an incision as a fraction of the brush radius. Manual drag uses half of this limit.")]
        [SerializeField, Range(0.05f, 1.2f)] private float maximumIncisionStretch = 0.6f;
        [SerializeField] private bool enableLowerEyelidFoldPrototype;

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
        private SurgicalInstrumentPrototype2D instruments;
        private Vector2 toolPanelScroll;
        private static readonly List<DeformableImageInteractor> ToolPanels = new List<DeformableImageInteractor>();
        private static DeformableImageInteractor selectedLayer;
        public static DeformableImageInteractor PanelOwner
        {
            get
            {
                DeformableImageInteractor first = null;
                foreach (var tool in ToolPanels)
                {
                    if (tool == null || !tool.isActiveAndEnabled || !tool.showRuntimeToolStatus) continue;
                    if (first == null) first = tool;
                    var curves = tool.GetComponent<FacialFeaturePrecut2D>();
                    if (curves != null && curves.SkinLayer == tool.deformableMesh) return tool;
                }
                return first;
            }
        }
        public static DeformableImageInteractor SelectedLayer
        {
            get
            {
                if (selectedLayer == null || !selectedLayer.isActiveAndEnabled || !ToolPanels.Contains(selectedLayer)) selectedLayer = PanelOwner;
                return selectedLayer;
            }
        }
        public static bool HasToolPanel
        {
            get { return PanelOwner != null; }
        }

        private void OnEnable() { if (!ToolPanels.Contains(this)) ToolPanels.Add(this); }
        private void OnDisable()
        {
            if (cutActive) CancelCut();
            if (deformableMesh != null) deformableMesh.SetDragWeightDisplay(null);
            ToolPanels.Remove(this);
        }
        private Vector3[] operationStartVertices;
        private Vector3[] workingVertices;
        private int[] operationTriangles;
        private bool[] operationOutlineAnchors;
        private float[] operationFeatureStrengths;
        private float[] operationCornerStrengths;
        private float[] operationDragWeights;
        private Vector3[] operationMaterialVertices;
        private Vector2 operationMaterialGrab;
        private float operationDragLimit;
        private float[] operationEyeWeights;
        private static bool showDragWeightDisplay;
        private Vector3 previousDragInput;
        private Vector3[] previousDragPose;
        private Vector3[] dragFramePositions;
        private float[] operationGeodesicDistances;
        private LowerEyelidDrag operationLowerEyelidDrag;
        private Vector3 operationStartLocalPoint;
        private Vector3 operationStartMousePosition;
        private bool operationActive;
        private bool operationMayRevealBack;
        private float operationDepthSign = -1f;
        private bool operationPinned;
        private bool reboundActive;
        private float reboundElapsed;
        private Vector3[] reboundStartVertices;
        private Vector3[] reboundTargetVertices;
        private Vector3[] reboundWorkingVertices;
        private readonly List<Vector2> cutPathUvs = new List<Vector2>();
        private readonly List<Vector3> cutPathWorld = new List<Vector3>();
        private bool cutActive;
        private DeformableImageMesh activeCutMesh;
        private int previewedCutPointCount;

        public DeformationTool ActiveTool
        {
            get { return activeTool; }
            set
            {
                if (activeTool == value && !BoneCutInteractor.IsAnyBoneToolActive) return;
                BoneCutInteractor.DeactivateAllTools();
                if (operationActive) EndOperation();
                if (cutActive) CancelCut();
                StopRebound();
                if (instruments != null) instruments.CancelPendingInput();
                activeTool = value;
            }
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
            instruments = GetComponent<SurgicalInstrumentPrototype2D>();
            if (instruments == null) instruments = gameObject.AddComponent<SurgicalInstrumentPrototype2D>();
            instruments.Configure(this, deformableMesh, targetCamera);
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
        }

        private void OnGUI()
        {
            if (!showRuntimeToolStatus || !Application.isPlaying || !FaceRuntimeUILayout.Left(1) || PanelOwner != this)
            {
                return;
            }

            GUILayout.BeginArea(FaceRuntimeUILayout.Panel(false, Screen.height - 66f), GUI.skin.box);
            toolPanelScroll = GUILayout.BeginScrollView(toolPanelScroll);
            GUILayout.Label("操作工具 · 点击选择", FaceRuntimeUILayout.WrappedLabel);
            GUILayout.BeginHorizontal();
            foreach (var layer in ToolPanels)
            {
                if (layer == null || !layer.isActiveAndEnabled || !layer.showRuntimeToolStatus) continue;
                string name = layer == PanelOwner ? "皮肤" : layer.gameObject.name.ToLowerInvariant().Contains("muscle") ? "肌肉" : layer.gameObject.name.ToLowerInvariant().Contains("fat") ? "脂肪" : layer.gameObject.name;
                if (GUILayout.Toggle(SelectedLayer == layer, name, FaceRuntimeUILayout.ToolButton) && SelectedLayer != layer)
                {
                    if (SelectedLayer != null) SelectedLayer.CancelCurrentToolOperation();
                    BoneCutInteractor.DeactivateAllTools();
                    selectedLayer = layer;
                }
            }
            GUILayout.EndHorizontal();
            if (SelectedLayer != null) SelectedLayer.DrawToolPanel();
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        private void DrawToolPanel()
        {
            int selected = GUILayout.SelectionGrid(BoneCutInteractor.IsAnyBoneToolActive ? -1 : (int)activeTool,
                new[] { "手动拖拽", "膨胀", "挤压", "组织切割", "皮钩", "牵开器", "剥离器", "缝合" }, 2, FaceRuntimeUILayout.ToolButton);
            if (selected >= 0) ActiveTool = (DeformationTool)selected;
            BoneCutInteractor.DrawToolButtons(SelectBoneTool);
            GUILayout.Space(6f);
            GUILayout.Label("作用半径：" + brushRadius.ToString("0.00") + "（滚轮调节）", FaceRuntimeUILayout.WrappedLabel);
            if (BoneCutInteractor.IsAnyBoneToolActive)
                GUILayout.Label("当前为骨骼切割。左键画闭合轮廓；点击上方任意组织工具即可返回。", FaceRuntimeUILayout.WrappedLabel);
            else if (activeTool == DeformationTool.Cut)
            {
                GUILayout.Label("按住左键显示透明切割线，松手后切开网格；Esc 取消预览。"
                    + "\n滚轮调节切口宽度 " + transparentCutWidth.ToString("0.000") + "\n" + lastCutStatus,
                    FaceRuntimeUILayout.WrappedLabel);
            }
            else if (activeTool == DeformationTool.Drag)
            {
                if (GUILayout.Button(showDragWeightDisplay ? "隐藏拖拽权重（F8）" : "显示拖拽权重（F8）", FaceRuntimeUILayout.ToolButton))
                    ToggleDragWeightDisplay();
                if (showDragWeightDisplay)
                    GUILayout.Label(operationActive
                        ? "本次笔刷 · 蓝 0 / 红 1 · 位移上限 " + operationDragLimit.ToString("0.000")
                        : "部位权重 · 蓝 0 / 红 1 · 按住查看本次笔刷", FaceRuntimeUILayout.WrappedLabel);
                else
                    GUILayout.Label("手动拖拽：左键拉动，松手回弹；中键固定。", FaceRuntimeUILayout.WrappedLabel);
            }
            else if (activeTool == DeformationTool.Inflate || activeTool == DeformationTool.Pinch)
                GUILayout.Label("按住左键向上拖动以膨胀／挤压。", FaceRuntimeUILayout.WrappedLabel);
            if (instruments != null && !BoneCutInteractor.IsAnyBoneToolActive) instruments.DrawControls();
            if (GUILayout.Button("取消当前操作", FaceRuntimeUILayout.ToolButton)) CancelCurrentToolOperation();
            if (GUILayout.Button("重置当前组织与器械", FaceRuntimeUILayout.ToolButton)) ResetFaceAndInstruments();
            GUILayout.Label("选中工具后，在面部用鼠标操作。R 重置；Esc 取消。", FaceRuntimeUILayout.WrappedLabel);
        }

        public void SelectBoneTool(BoneCutInteractor tool)
        {
            CancelCurrentToolOperation();
            bool wasActive = tool != null && tool.ToolActive;
            BoneCutInteractor.DeactivateAllTools();
            if (tool != null && !wasActive) tool.SetToolActive(true);
        }

        private void CancelCurrentToolOperation()
        {
            if (operationActive) CancelOperation();
            if (cutActive) CancelCut();
            StopRebound();
            if (instruments != null) instruments.CancelPendingInput();
            BoneCutInteractor.CancelAllDrawing();
        }

        private void ResetFaceAndInstruments()
        {
            CancelCurrentToolOperation();
            if (instruments != null) instruments.ResetInstruments();
            if (boneSkinCoupler != null) boneSkinCoupler.ResetFaceToBoneShape();
            else deformableMesh.ResetDeformation();
        }

        private void Update()
        {
            if (SelectedLayer != null && SelectedLayer != this)
            {
                if (operationActive || cutActive || reboundActive) CancelCurrentToolOperation();
                if (deformableMesh != null && deformableMesh.ShowsDragWeights) deformableMesh.SetDragWeightDisplay(null);
                return;
            }
            if (deformableMesh == null || deformableMesh.LogicalVertexCount == 0)
            {
                return;
            }

            if (enableKeyboardShortcuts && activeTool == DeformationTool.Drag && !BoneCutInteractor.IsAnyBoneToolActive
                && Input.GetKeyDown(KeyCode.F8)) ToggleDragWeightDisplay();
            bool displayWanted = showDragWeightDisplay && activeTool == DeformationTool.Drag && !BoneCutInteractor.IsAnyBoneToolActive;
            if (displayWanted != deformableMesh.ShowsDragWeights) RefreshDragWeightDisplay();

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

            if (activeTool >= DeformationTool.SkinHook)
            {
                HandleBrushRadius();
                if (instruments != null) instruments.HandleInput(activeTool);
                return;
            }

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

        private void ToggleDragWeightDisplay()
        {
            showDragWeightDisplay = !showDragWeightDisplay;
            RefreshDragWeightDisplay();
        }

        private void RefreshDragWeightDisplay()
        {
            if (deformableMesh == null) return;
            if (!showDragWeightDisplay || activeTool != DeformationTool.Drag || BoneCutInteractor.IsAnyBoneToolActive)
            {
                deformableMesh.SetDragWeightDisplay(null);
                return;
            }

            if (operationActive && operationDragWeights != null && operationDragWeights.Length == deformableMesh.LogicalVertexCount)
            {
                deformableMesh.SetDragWeightDisplay(operationDragWeights);
                return;
            }

            float[] regionWeights = deformableMesh.CopyManualCornerStrengths();
            bool[] anchors = deformableMesh.GetOriginalOutlineAnchors();
            for (int i = 0; i < regionWeights.Length; i++)
                if (anchors[i]) regionWeights[i] = 0f;
            deformableMesh.SetDragWeightDisplay(regionWeights);
        }

        private void HandleShortcuts()
        {
            if (!enableKeyboardShortcuts)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.R) && !operationActive)
            {
                ResetFaceAndInstruments();
            }
        }

        private void HandleBrushRadius()
        {
            if (operationActive || IsPointerOverToolUI())
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

            if (IsPointerOverToolUI())
            {
                return;
            }

            if (!TryGetLocalPointerPosition(out Vector3 localPoint))
            {
                return;
            }

            bool manualDrag = activeTool == DeformationTool.Drag;
            DeformableImageMesh.ManualDragHit hit = default;
            if (manualDrag)
            {
                Camera cameraToUse = targetCamera != null ? targetCamera : Camera.main;
                Ray worldRay = cameraToUse.ScreenPointToRay(Input.mousePosition);
                Transform surface = deformableMesh.transform;
                var localRay = new Ray(surface.InverseTransformPoint(worldRay.origin),
                    surface.InverseTransformVector(worldRay.direction));
                if (!deformableMesh.TryPickManualDragSurface(localRay, out hit)) return;
            }
            else if (!deformableMesh.TryClassifyGrabPoint(localPoint, visibleEdgeGrabDistance, out _)
                || !deformableMesh.HasVertexWithinRadius(localPoint, brushRadius))
            {
                return;
            }

            StopRebound();
            operationStartVertices = deformableMesh.CopyCurrentVertices();
            operationOutlineAnchors = deformableMesh.CopyOriginalOutlineAnchors();
            operationFeatureStrengths = manualDrag ? null : deformableMesh.CopyToolStretchStrengths();
            operationCornerStrengths = manualDrag ? deformableMesh.CopyManualCornerStrengths() : null;
            workingVertices = new Vector3[operationStartVertices.Length];
            operationTriangles = deformableMesh.CopyMechanicallyIntactTriangles();
            if (manualDrag) PrepareManualDragMaterial(hit);
            else
            {
                operationMayRevealBack = deformableMesh.IsNearCutBoundary(localPoint, visibleEdgeGrabDistance);
                deformableMesh.TryBuildGeodesicDistances(localPoint, brushRadius, out operationGeodesicDistances);
            }
            operationLowerEyelidDrag = manualDrag && enableLowerEyelidFoldPrototype
                ? deformableMesh.CreateLowerEyelidDrag(localPoint, operationGeodesicDistances)
                : null;
            operationStartLocalPoint = localPoint;
            previousDragInput = Vector3.zero;
            if (manualDrag)
            {
                PrepareManualDrag();
                previousDragPose = (Vector3[])operationStartVertices.Clone();
                dragFramePositions = new Vector3[operationStartVertices.Length];
            }
            operationDepthSign = ManualDragDepthSign(localPoint);
            operationStartMousePosition = Input.mousePosition;
            operationPinned = false;
            operationActive = true;
            if (manualDrag && showDragWeightDisplay) RefreshDragWeightDisplay();
        }

        private void PrepareManualDragMaterial(DeformableImageMesh.ManualDragHit hit)
        {
            operationMaterialVertices = deformableMesh.CopyMaterialSpaceVertices();
            operationMaterialGrab = hit.MaterialPoint;
            operationMayRevealBack = deformableMesh.IsNearMaterialCutBoundary(hit.MaterialPoint, visibleEdgeGrabDistance);
            operationGeodesicDistances = deformableMesh.BuildManualDragDistances(hit, brushRadius, out operationEyeWeights);
        }

        private float ManualDragDepthSign(Vector3 localPoint)
        {
            Camera cameraToUse = targetCamera != null ? targetCamera : Camera.main;
            Vector3 towardCamera = cameraToUse != null
                ? (cameraToUse.orthographic ? -cameraToUse.transform.forward
                    : cameraToUse.transform.position - deformableMesh.transform.TransformPoint(localPoint))
                : -deformableMesh.transform.forward;
            // The sprite is authored in local XY. In Facetest, local -Z maps to world +Y.
            float localDepth = deformableMesh.transform.InverseTransformVector(towardCamera).z;
            return localDepth >= 0f ? 1f : -1f;
        }

        private void UpdateOperation()
        {
            if (deformableMesh.LogicalVertexCount != operationStartVertices.Length)
            {
                ClearOperationState();
                return;
            }
            if (!TryGetLocalPointerPosition(out Vector3 localPoint))
            {
                return;
            }

            Vector3 dragDelta = localPoint - operationStartLocalPoint;
            if (activeTool == DeformationTool.Drag)
            {
                if (dragDelta == previousDragInput) return;
                previousDragInput = dragDelta;
            }
            float verticalMouseDelta = (Input.mousePosition.y - operationStartMousePosition.y)
                / Mathf.Max(1f, Screen.height);
            float scaleDelta = verticalMouseDelta * inflateStrength;
            if (activeTool == DeformationTool.Pinch)
            {
                scaleDelta = -scaleDelta;
            }

            BuildSafeWorkingVertices(dragDelta, scaleDelta);

            if (activeTool == DeformationTool.Drag) ApplyManualDragDelta();
            else deformableMesh.SetManualVertices(workingVertices);
        }

        private void ApplyManualDragDelta()
        {
            // Expressions and retained instruments can change the surface between
            // mouse updates. Apply only the change owned by this gesture.
            for (int i = 0; i < dragFramePositions.Length; i++)
                dragFramePositions[i] = deformableMesh.GetLogicalVertex(i) + (workingVertices[i] - previousDragPose[i]);
            deformableMesh.SetManualVertices(dragFramePositions);
            System.Array.Copy(workingVertices, previousDragPose, workingVertices.Length);
        }

        private Vector3[] CurrentPoseWithoutDrag()
        {
            if (previousDragPose == null) return operationStartVertices;
            for (int i = 0; i < dragFramePositions.Length; i++)
                dragFramePositions[i] = deformableMesh.GetLogicalVertex(i) - (previousDragPose[i] - operationStartVertices[i]);
            return dragFramePositions;
        }

        private void BuildSafeWorkingVertices(Vector3 dragDelta, float scaleDelta)
        {
            BuildWorkingVertices(dragDelta, scaleDelta, 1f);
            if (activeTool == DeformationTool.Drag) return;

            if (!operationMayRevealBack
                && !PreservesTriangleOrientation(operationStartVertices, workingVertices))
            {
                float safeScale = 0f;
                float unsafeScale = 1f;

                // Preserve the original interior orientation guard. Incision grabs
                // intentionally retain their previous freedom to fold.
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

        }

        private void PrepareManualDrag()
        {
            Vector3[] reference = operationMaterialVertices ?? operationStartVertices;
            Vector2 grip = operationMaterialVertices != null ? operationMaterialGrab : (Vector2)operationStartLocalPoint;
            operationDragWeights = new float[operationStartVertices.Length];
            for (int i = 0; i < operationDragWeights.Length; i++)
            {
                if (operationOutlineAnchors != null && operationOutlineAnchors[i]) continue;
                // Reachability protects opposite cut lips. Use the smooth material
                // radius inside the reachable area, rather than zigzag edge lengths.
                if (operationGeodesicDistances != null && float.IsPositiveInfinity(operationGeodesicDistances[i])) continue;
                float distance = Vector2.Distance(reference[i], grip);
                float corner = operationCornerStrengths != null ? operationCornerStrengths[i] : 1f;
                float eye = operationEyeWeights != null ? operationEyeWeights[i] : 1f;
                operationDragWeights[i] = FeatureBoundaryMath.ManualDragFalloff(distance / brushRadius) * corner * eye;
            }

            operationDragLimit = brushRadius * (operationMayRevealBack ? maximumIncisionStretch : maximumInteriorStretch);
            if (!operationMayRevealBack && operationTriangles != null)
                RelaxManualDragWeights(reference);
            operationDragLimit *= ManualDragTravelScale;
        }

        private struct DragTriangleGradient
        {
            public int A, B, C;
            public Vector2 AGradient, BGradient, CGradient;
        }

        private void RelaxManualDragWeights(Vector3[] reference)
        {
            // Each triangle constrains the slope of its own brush field. Reducing
            // that slope locally keeps one thin cut triangle from capping all travel.
            float allowedSlope = (1f - minimumTriangleAreaRatio) / operationDragLimit;
            var included = new bool[operationDragWeights.Length];
            for (int i = 0; i < included.Length; i++) included[i] = operationDragWeights[i] > 0f;
            var triangles = new List<DragTriangleGradient>();
            for (int i = 0; i < operationTriangles.Length; i += 3)
            {
                int a = operationTriangles[i], b = operationTriangles[i + 1], c = operationTriangles[i + 2];
                if (!included[a] && !included[b] && !included[c]) continue;
                Vector2 ab = reference[b] - reference[a];
                Vector2 ac = reference[c] - reference[a];
                float area = ab.x * ac.y - ab.y * ac.x;
                if (Mathf.Abs(area) < 1e-7f) continue;
                Vector2 bGradient = new Vector2(ac.y, -ac.x) / area;
                Vector2 cGradient = new Vector2(-ab.y, ab.x) / area;
                triangles.Add(new DragTriangleGradient
                {
                    A = a, B = b, C = c,
                    AGradient = -bGradient - cGradient,
                    BGradient = bGradient,
                    CGradient = cGradient
                });
            }

            // Leave a small margin for neighbouring triangles that share vertices.
            float targetSlope = allowedSlope * 0.80f;
            if (triangles.Count > 0) FadeSharpBrushEdges(reference, triangles, targetSlope);
            for (int pass = 0; pass < 48; pass++)
            {
                bool changed = false;
                for (int index = 0; index < triangles.Count; index++)
                {
                    DragTriangleGradient tri = triangles[pass % 2 == 0 ? index : triangles.Count - index - 1];
                    Vector2 gradient = GetDragWeightGradient(tri);
                    float slope = gradient.magnitude;
                    if (slope <= allowedSlope) continue;
                    Vector2 axis = gradient / slope;
                    float a = included[tri.A] ? Vector2.Dot(tri.AGradient, axis) : 0f;
                    float b = included[tri.B] ? Vector2.Dot(tri.BGradient, axis) : 0f;
                    float c = included[tri.C] ? Vector2.Dot(tri.CGradient, axis) : 0f;
                    float share = a * a + b * b + c * c;
                    if (share <= 1e-8f) continue;
                    float correction = (slope - targetSlope) / share;
                    if (included[tri.A]) AdjustDragWeight(tri.A, -a * correction);
                    if (included[tri.B]) AdjustDragWeight(tri.B, -b * correction);
                    if (included[tri.C]) AdjustDragWeight(tri.C, -c * correction);
                    changed = true;
                }
                if (!changed) break;
            }

            // Keep the orientation guard for intact surface. A thin strip formed
            // by clipping an incision is part of its free lip; its projected
            // winding is too sensitive to use as a limit for the entire stroke.
            foreach (DragTriangleGradient triangle in triangles)
            {
                float slope = GetDragWeightGradient(triangle).magnitude;
                if (slope <= allowedSlope) continue;
                Vector2 a = reference[triangle.A], b = reference[triangle.B], c = reference[triangle.C];
                float longestSquared = Mathf.Max((b - a).sqrMagnitude, Mathf.Max((c - a).sqrMagnitude, (c - b).sqrMagnitude));
                float area = Mathf.Abs(SignedDoubleArea(a, b, c));
                if (area <= longestSquared * 0.12f && deformableMesh != null
                    && deformableMesh.IsNearMaterialCutBoundary((a + b + c) / 3f, 0.05f))
                    continue;
                operationDragLimit = Mathf.Min(operationDragLimit, (1f - minimumTriangleAreaRatio) / slope);
            }
        }

        private void FadeSharpBrushEdges(Vector3[] reference, List<DragTriangleGradient> triangles, float slope)
        {
            // A hard cut/outline exclusion can sit very close to a movable vertex
            // on clipped topology. Feather that boundary through intact edges.
            for (int pass = 0; pass < 16; pass++)
            {
                bool changed = false;
                for (int index = 0; index < triangles.Count; index++)
                {
                    DragTriangleGradient triangle = triangles[pass % 2 == 0 ? index : triangles.Count - index - 1];
                    changed |= LimitEdge(triangle.A, triangle.B);
                    changed |= LimitEdge(triangle.B, triangle.C);
                    changed |= LimitEdge(triangle.C, triangle.A);
                }
                if (!changed) break;
            }

            bool LimitEdge(int a, int b)
            {
                if (operationDragWeights[a] < operationDragWeights[b]) (a, b) = (b, a);
                float maximum = operationDragWeights[b] + Vector2.Distance(reference[a], reference[b]) * slope;
                if (operationDragWeights[a] <= maximum) return false;
                operationDragWeights[a] = maximum;
                return true;
            }
        }

        private Vector2 GetDragWeightGradient(DragTriangleGradient triangle)
        {
            float a = operationDragWeights[triangle.A];
            return triangle.BGradient * (operationDragWeights[triangle.B] - a)
                + triangle.CGradient * (operationDragWeights[triangle.C] - a);
        }

        private void AdjustDragWeight(int index, float correction)
        {
            float maximum = operationCornerStrengths != null ? operationCornerStrengths[index] : 1f;
            operationDragWeights[index] = Mathf.Clamp(operationDragWeights[index] + correction, 0.00001f, maximum);
        }

        private void BuildManualDragVertices(Vector3 dragDelta)
        {
            float length = dragDelta.magnitude;
            float distance = Mathf.Min(length, operationDragLimit);
            if (!operationMayRevealBack)
            {
                float easeLength = operationDragLimit * 0.2f;
                float easeStart = operationDragLimit - easeLength;
                if (easeLength > 0f && length > easeStart)
                    distance = operationDragLimit - easeLength / (1f + (length - easeStart) / easeLength);
            }
            Vector3 displacement = length > 0f ? dragDelta * (distance / length) : Vector3.zero;
            displacement.z += Mathf.Min(distance * manualDragDepthRatio, maximumManualDragDepth) * operationDepthSign;
            if (operationMayRevealBack) displacement = Vector3.ClampMagnitude(displacement, operationDragLimit);
            for (int i = 0; i < workingVertices.Length; i++)
                workingVertices[i] = operationStartVertices[i] + displacement * operationDragWeights[i];
        }

        private bool BuildWorkingVertices(Vector3 dragDelta, float scaleDelta, float deformationScale)
        {
            float translationScale = 1f;
            float foldAngle = operationLowerEyelidDrag != null
                ? operationLowerEyelidDrag.GetFoldAngle(dragDelta, out translationScale) * deformationScale : 0f;
            if (activeTool == DeformationTool.Drag && foldAngle <= 0f)
            {
                if (operationDragWeights == null || operationDragWeights.Length != operationStartVertices.Length)
                    PrepareManualDrag();
                BuildManualDragVertices(dragDelta * translationScale);
                return false;
            }

            bool incisionDrag = operationMayRevealBack || foldAngle > 0f;
            float maximumTravel = brushRadius * (incisionDrag ? maximumIncisionStretch : maximumInteriorStretch)
                * ManualDragTravelScale;
            Vector3 localDrag = Vector3.ClampMagnitude(dragDelta, maximumTravel) * translationScale;
            float depthLift = Mathf.Min(localDrag.magnitude * manualDragDepthRatio, maximumManualDragDepth)
                * operationDepthSign;

            for (int i = 0; i < operationStartVertices.Length; i++)
            {
                Vector3 original = operationStartVertices[i];
                if (operationOutlineAnchors != null && operationOutlineAnchors[i])
                {
                    workingVertices[i] = original;
                    continue;
                }
                Vector2 fromBrush = (Vector2)(original - operationStartLocalPoint);
                float distance = operationGeodesicDistances != null
                    && i < operationGeodesicDistances.Length
                    ? operationGeodesicDistances[i]
                    : fromBrush.magnitude;
                float weight = 0f;

                if (distance <= brushRadius)
                {
                    float normalizedDistance = distance / brushRadius;
                    weight = activeTool == DeformationTool.Drag
                        ? FeatureBoundaryMath.ManualDragFalloff(normalizedDistance)
                        : Mathf.Pow(1f - Mathf.Clamp01(normalizedDistance), falloffPower);
                }
                float featureStrength = operationFeatureStrengths != null && i < operationFeatureStrengths.Length
                    ? operationFeatureStrengths[i] : 1f;
                if (activeTool == DeformationTool.Drag && operationEyeWeights != null) weight *= operationEyeWeights[i];
                float brushWeight = weight;
                weight *= featureStrength;

                if (activeTool == DeformationTool.Drag)
                {
                    if (operationCornerStrengths != null && i < operationCornerStrengths.Length)
                        weight *= operationCornerStrengths[i];
                    // Limit the shared displacement before weighting. Clamping each
                    // vertex afterwards lets weak distant vertices overtake the centre
                    // during large pulls and introduces a ring of crushed tissue.
                    workingVertices[i] = original + localDrag * (weight * deformationScale);
                    // Use the same weight as the planar pull: overlapping fold branches
                    // acquire distinct depths rather than competing on one plane.
                    workingVertices[i].z += depthLift * weight * deformationScale;
                    if (foldAngle > 0f && brushWeight > 0f)
                        workingVertices[i] += operationLowerEyelidDrag.GetFoldOffset(i, foldAngle, brushWeight, operationDepthSign);
                    if (incisionDrag)
                        workingVertices[i] = original + Vector3.ClampMagnitude(workingVertices[i] - original,
                            maximumTravel * featureStrength);
                }
                else
                {
                    Vector3 radialDirection = new Vector3(fromBrush.x, fromBrush.y, 0f);
                    workingVertices[i] = original
                        + radialDirection * (scaleDelta * weight * deformationScale);
                }
                // Only a per-stroke pull limit; eye-corner resistance above is a soft
                // manual response, not a fixed contour or cumulative displacement budget.
                if (featureStrength < 0.999f)
                    workingVertices[i] = original + Vector3.ClampMagnitude(workingVertices[i] - original,
                        brushRadius * 1.2f * featureStrength);
            }
            return foldAngle > 0f;
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
                StartRebound(CurrentPoseWithoutDrag());
            }

            ClearOperationState();
        }

        private void ClearOperationState()
        {
            operationActive = false;
            operationStartVertices = null;
            workingVertices = null;
            operationTriangles = null;
            operationOutlineAnchors = null;
            operationFeatureStrengths = null;
            operationCornerStrengths = null;
            operationDragWeights = null;
            operationMaterialVertices = null;
            previousDragPose = null;
            dragFramePositions = null;
            operationGeodesicDistances = null;
            operationLowerEyelidDrag = null;
            operationEyeWeights = null;
            operationMayRevealBack = false;
            operationPinned = false;
            if (deformableMesh != null && deformableMesh.ShowsDragWeights) RefreshDragWeightDisplay();
        }

        private void CancelOperation()
        {
            if (operationStartVertices != null && operationStartVertices.Length == deformableMesh.LogicalVertexCount)
            {
                deformableMesh.SetManualVertices(CurrentPoseWithoutDrag());
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
                deformableMesh.SetManualVertices(reboundTargetVertices);
                StopRebound();
            }
            else
            {
                deformableMesh.SetManualVertices(reboundWorkingVertices);
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
                AddCutPoint(true);
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
            lastCutStatus = "Transparent cut width: " + transparentCutWidth.ToString("0.000");
        }

        private void BeginCut()
        {
            if (DeformableImagePieceMover.IsPointerCaptured)
            {
                return;
            }

            if (IsPointerOverToolUI())
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
            previewedCutPointCount = 0;
            cutActive = true;
            lastCutStatus = "Drawing cut on " + GetCutLayerName(activeCutMesh) + "...";
            AddCutPoint();

            if (cutPathUvs.Count == 0)
            {
                CancelCut();
            }
        }

        private void AddCutPoint(bool forceEndpoint = false)
        {
            DeformableImageMesh cutMesh = activeCutMesh != null
                ? activeCutMesh
                : deformableMesh;
            if (!TryGetLocalPointerPosition(cutMesh, out Vector3 localPoint)
                || !cutMesh.TryGetNormalizedUvAtCurrentPoint(localPoint,
                    out Vector2 normalizedUv, out Vector3 surfacePoint))
            {
                return;
            }

            Vector3 worldPoint = cutMesh.transform.TransformPoint(surfacePoint);
            if (cutPathWorld.Count > 0
                && Vector3.Distance(cutPathWorld[cutPathWorld.Count - 1], worldPoint)
                    < (forceEndpoint ? 0.0001f : cutPointSpacing))
            {
                return;
            }

            cutPathUvs.Add(normalizedUv);
            cutPathWorld.Add(worldPoint);
            UpdateCutMaskPreview();
        }

        private void UpdateCutMaskPreview()
        {
            DeformableImageMesh cutMesh = activeCutMesh != null ? activeCutMesh : deformableMesh;
            if (cutMesh == null || cutPathUvs.Count < 2) return;
            cutMesh.PreviewOpenCut(cutPathUvs, Mathf.Max(0, previewedCutPointCount - 1), transparentCutWidth);
            previewedCutPointCount = cutPathUvs.Count;
        }

        private void CompleteCut()
        {
            double processingStarted = Time.realtimeSinceStartupAsDouble;
            DeformableImageMesh cutMesh = activeCutMesh != null
                ? activeCutMesh
                : deformableMesh;
            string cutLayerName = GetCutLayerName(cutMesh);
            cutActive = false;
            cutMesh.EndCutPreview();

            if (cutPathUvs.Count < 2)
            {
                lastCutStatus = "Cut ignored: fewer than two sampled points.";
                Debug.LogWarning(lastCutStatus, this);
                ClearCutStroke();
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

            ClearCutStroke();
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
            ClearCutStroke();
            lastCutStatus = "已取消透明切割预览，组织未被切开。";
        }

        private void ClearCutStroke()
        {
            cutActive = false;
            DeformableImageMesh cutMesh = activeCutMesh != null ? activeCutMesh : deformableMesh;
            if (cutMesh != null) cutMesh.EndCutPreview();
            cutPathUvs.Clear();
            cutPathWorld.Clear();
            previewedCutPointCount = 0;
            activeCutMesh = null;
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

        public bool IsPointerOverToolUI()
        {
            if (!ignorePointerOverUi) return false;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return true;
            Vector2 p = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return FaceRuntimeUILayout.Enabled
                ? p.y >= 12f && (p.x <= FaceRuntimeUILayout.Width + 12f || p.x >= Screen.width - FaceRuntimeUILayout.Width - 12f)
                : FaceRuntimeUILayout.Left(1) && FaceRuntimeUILayout.Panel(false, Screen.height - 66f).Contains(p);
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
