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
        [SerializeField] private bool enableKeyboardShortcut = true;
        [SerializeField] private KeyCode shortcut = KeyCode.Alpha5;
        [SerializeField] private bool ignorePointerOverUi = true;

        [Header("Closed Bone Removal")]
        [SerializeField] private bool toolActive;
        [SerializeField, Min(0.005f)] private float pointSpacing = 0.03f;
        [SerializeField, Min(0.01f)] private float closedLoopDistance = 0.2f;
        [SerializeField, Min(0.002f)] private float previewWidth = 0.025f;
        [SerializeField] private Color previewColor = new Color(1f, 0.75f, 0.1f, 0.98f);
        [SerializeField, TextArea(2, 4)] private string lastStatus =
            "Press 5, then draw a closed lasso over the bone.";

        private static readonly List<BoneCutInteractor> Instances = new List<BoneCutInteractor>();
        private readonly List<Vector2> pathUvs = new List<Vector2>();
        private readonly List<Vector3> pathWorld = new List<Vector3>();
        private AnatomyBoneMesh boneMesh;
        private bool drawing;
        private LineRenderer preview;
        private Material previewMaterial;

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
            if (!Application.isPlaying || !toolActive)
            {
                return;
            }

            GUI.Box(
                new Rect(12f, 102f, 620f, 64f),
                "Active Tool: BONE CLOSED CUT (5)"
                + "\nLeft drag a closed lasso; yellow is preview."
                + "\n" + lastStatus);
        }

        private void Update()
        {
            if (enableKeyboardShortcut && Input.GetKeyDown(shortcut))
            {
                toolActive = !toolActive;
                lastStatus = toolActive
                    ? "Bone cutting enabled. Draw a closed lasso."
                    : "Bone cutting disabled.";
                if (!toolActive)
                {
                    CancelDrawing();
                }
            }

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
            if (AnatomyBoneFragmentMover.IsPointerCaptured
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
