using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace DeformableImage
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AnatomyBoneMesh))]
    public sealed class BoneCutInteractor : MonoBehaviour
    {
        [Header("Input")]
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool ignorePointerOverUi = true;

        [Header("Closed Bone Removal")]
        [SerializeField] private bool toolActive;
        [SerializeField, Min(0.005f)] private float pointSpacing = 0.03f;
        [SerializeField, Min(0.01f)] private float closedLoopDistance = 0.2f;
        [SerializeField, Min(0.002f)] private float previewWidth = 0.025f;
        [SerializeField] private Color previewColor = new Color(1f, 0.75f, 0.1f, 0.98f);
        [SerializeField, TextArea(2, 4)] private string lastStatus =
            "点击面板中的骨骼工具，再在骨骼上画闭合轮廓。";

        private static readonly List<BoneCutInteractor> Instances = new List<BoneCutInteractor>();
        private readonly List<Vector2> pathUvs = new List<Vector2>();
        private readonly List<Vector3> pathWorld = new List<Vector3>();
        private AnatomyBoneMesh boneMesh;
        private bool drawing;
        private LineRenderer preview;
        private Material previewMaterial;
        public bool ToolActive { get { return toolActive; } }
        private string DisplayName
        {
            get
            {
                string label = gameObject.name.ToLowerInvariant();
                return label.Contains("jaw") ? "下颌骨" : label.Contains("skull") ? "颅骨" : gameObject.name;
            }
        }

        public void SetToolActive(bool active)
        {
            toolActive = active;
            if (!active) CancelDrawing();
            lastStatus = active ? "骨骼切割已开启：左键画闭合轮廓。" : "骨骼切割已关闭。";
        }

        public static void DeactivateAllTools()
        {
            foreach (BoneCutInteractor tool in Instances) if (tool != null) tool.SetToolActive(false);
        }

        public static void CancelAllDrawing()
        {
            foreach (BoneCutInteractor tool in Instances) if (tool != null) tool.CancelDrawing();
        }

        public static void DrawToolButtons(System.Action<BoneCutInteractor> select)
        {
            bool heading = false;
            foreach (BoneCutInteractor tool in Instances)
            {
                if (tool == null || !tool.isActiveAndEnabled) continue;
                if (!heading) { GUILayout.Label("骨骼工具 · 选择操作对象", FaceRuntimeUILayout.WrappedLabel); heading = true; }
                Color before = GUI.backgroundColor;
                if (tool.ToolActive) GUI.backgroundColor = new Color(0.55f, 0.85f, 1f);
                if (GUILayout.Button((tool.ToolActive ? "● " : "") + "骨骼切割 · " + tool.DisplayName, FaceRuntimeUILayout.ToolButton)) select(tool);
                GUI.backgroundColor = before;
                if (tool.ToolActive) GUILayout.Label(tool.lastStatus, FaceRuntimeUILayout.WrappedLabel);
            }
        }

        public static bool IsAnyBoneToolActive
        {
            get
            {
                for (int index = 0; index < Instances.Count; index++)
                {
                    if (Instances[index] != null
                        && Instances[index].isActiveAndEnabled
                        && Instances[index].toolActive)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private void Awake()
        {
            boneMesh = GetComponent<AnatomyBoneMesh>();
            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }
        }

        private void OnEnable()
        {
            if (!Instances.Contains(this))
            {
                Instances.Add(this);
            }
        }

        private void OnDisable()
        {
            Instances.Remove(this);
            CancelDrawing();
        }

        private void OnDestroy()
        {
            if (preview != null)
            {
                Destroy(preview.gameObject);
            }

            if (previewMaterial != null)
            {
                Destroy(previewMaterial);
            }
        }

        private void OnGUI()
        {
            if (!Application.isPlaying || !FaceRuntimeUILayout.Left(1) || DeformableImageInteractor.HasToolPanel
                || Instances.Find(tool => tool != null && tool.isActiveAndEnabled) != this)
            {
                return;
            }

            GUILayout.BeginArea(FaceRuntimeUILayout.Panel(false, 310f), GUI.skin.box);
            GUILayout.Label("操作工具 · 点击选择", FaceRuntimeUILayout.WrappedLabel);
            DrawToolButtons(tool =>
            {
                bool wasActive = tool.ToolActive;
                DeactivateAllTools();
                if (!wasActive) tool.SetToolActive(true);
            });
            if (GUILayout.Button("取消当前切割")) CancelAllDrawing();
            GUILayout.EndArea();
        }

        private void Update()
        {
            if (!toolActive)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CancelDrawing();
                lastStatus = "Current bone lasso cancelled.";
                return;
            }

            if (Input.GetMouseButtonDown(0))
            {
                BeginDrawing();
            }

            if (drawing && Input.GetMouseButton(0))
            {
                AddPoint();
            }

            if (drawing && Input.GetMouseButtonUp(0))
            {
                CompleteDrawing();
            }
        }

        private void BeginDrawing()
        {
            Vector2 pointer = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool overPanel = FaceRuntimeUILayout.Enabled
                ? pointer.y >= 12f && (pointer.x <= FaceRuntimeUILayout.Width + 12f || pointer.x >= Screen.width - FaceRuntimeUILayout.Width - 12f)
                : FaceRuntimeUILayout.Left(1) && FaceRuntimeUILayout.Panel(false, Screen.height - 66f).Contains(pointer);
            if (AnatomyBoneFragmentMover.IsPointerCaptured
                || ignorePointerOverUi && overPanel
                || ignorePointerOverUi && EventSystem.current != null
                    && EventSystem.current.IsPointerOverGameObject())
            {
                return;
            }

            pathUvs.Clear();
            pathWorld.Clear();
            drawing = true;
            EnsurePreview();
            preview.positionCount = 0;
            AddPoint();
        }

        private void AddPoint()
        {
            if (!TryGetLocalPointer(out Vector3 localPoint)
                || !boneMesh.TryGetNormalizedPoint(localPoint, out Vector2 normalized))
            {
                return;
            }

            Vector3 worldPoint = transform.TransformPoint(localPoint) - transform.forward * 0.02f;
            if (pathWorld.Count > 0
                && Vector3.Distance(pathWorld[pathWorld.Count - 1], worldPoint) < pointSpacing)
            {
                return;
            }

            pathUvs.Add(normalized);
            pathWorld.Add(worldPoint);
            preview.positionCount = pathWorld.Count;
            preview.SetPosition(pathWorld.Count - 1, worldPoint);
        }

        private void CompleteDrawing()
        {
            drawing = false;
            if (preview != null)
            {
                preview.positionCount = 0;
            }

            if (pathUvs.Count < 3)
            {
                lastStatus = "Bone cut ignored: draw at least three sampled points.";
                ClearPath();
                return;
            }

            if (Vector3.Distance(pathWorld[0], pathWorld[pathWorld.Count - 1]) > closedLoopDistance)
            {
                lastStatus = "Bone cut ignored: the lasso endpoint did not return to its start.";
                ClearPath();
                return;
            }

            pathUvs.Add(pathUvs[0]);
            bool applied = boneMesh.ApplyClosedRemoval(pathUvs, out AnatomyBoneMesh fragment);
            lastStatus = applied
                ? "Bone topology changed. The removed fragment is a separate draggable object."
                : "Bone cut failed: the lasso did not remove a valid part of the current bone.";
            if (applied)
            {
                Debug.Log(lastStatus, fragment);
            }
            else
            {
                Debug.LogWarning(lastStatus, this);
            }

            ClearPath();
        }

        private void CancelDrawing()
        {
            drawing = false;
            ClearPath();
            if (preview != null)
            {
                preview.positionCount = 0;
            }
        }

        private void ClearPath()
        {
            pathUvs.Clear();
            pathWorld.Clear();
        }

        private void EnsurePreview()
        {
            if (preview != null)
            {
                return;
            }

            GameObject previewObject = new GameObject("Bone Cut Preview");
            preview = previewObject.AddComponent<LineRenderer>();
            preview.useWorldSpace = true;
            preview.loop = false;
            preview.startWidth = previewWidth;
            preview.endWidth = previewWidth;
            preview.numCapVertices = 4;
            preview.numCornerVertices = 4;
            preview.startColor = previewColor;
            preview.endColor = previewColor;
            preview.sortingOrder = short.MaxValue;
            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                previewMaterial = new Material(shader)
                {
                    name = "Bone Cut Preview Material",
                    hideFlags = HideFlags.DontSave
                };
                preview.material = previewMaterial;
            }
        }

        private bool TryGetLocalPointer(out Vector3 localPoint)
        {
            localPoint = default(Vector3);
            Camera cameraToUse = targetCamera != null ? targetCamera : Camera.main;
            if (cameraToUse == null)
            {
                return false;
            }

            Plane plane = new Plane(transform.forward, transform.position);
            Ray ray = cameraToUse.ScreenPointToRay(Input.mousePosition);
            if (!plane.Raycast(ray, out float distance))
            {
                return false;
            }

            localPoint = transform.InverseTransformPoint(ray.GetPoint(distance));
            localPoint.z = 0f;
            return true;
        }
    }
}
