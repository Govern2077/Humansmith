using UnityEngine;

namespace DeformableImage
{
    [DefaultExecutionOrder(-120)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AnatomyBoneMesh))]
    public sealed class AnatomyBoneFragmentMover : MonoBehaviour
    {
        [SerializeField] private Camera targetCamera;
        [SerializeField, Range(0, 2)] private int dragMouseButton;
        [Tooltip("For the main mandible layer, keep dragging on its anatomical vertical axis.")]
        [SerializeField] private bool restrictToVerticalAxis;
        [Header("Localized Mandible Opening")]
        [Tooltip("Deform the main mandible around its center instead of translating the complete bone layer.")]
        [SerializeField] private bool useLocalizedMandibleOpening;
        [Tooltip("0 pins the left/right mandibular body; a small value allows only minimal side movement.")]
        [SerializeField, Range(0f, 0.35f)] private float mandibleSideFollow;
        [Tooltip("Higher values concentrate vertical travel toward the chin center.")]
        [SerializeField, Range(0.5f, 12f)] private float mandibleChinFocusPower = 2.6f;
        [SerializeField, HideInInspector] private int mandibleOpenProfileVersion;

        private static AnatomyBoneFragmentMover capturedMover;
        private AnatomyBoneMesh boneMesh;
        private Plane dragPlane;
        private Vector3 dragStartWorldPoint;
        private Vector3 dragStartPosition;
        private float dragStartOpeningUv;

        public static bool IsPointerCaptured { get { return capturedMover != null; } }

        public void ConfigureAsMandibleMover(bool verticalOnly = true)
        {
            dragMouseButton = 1;
            restrictToVerticalAxis = verticalOnly;
            if (restrictToVerticalAxis && dragMouseButton == 1
                && gameObject.name.IndexOf("Jaw Layer") >= 0)
            {
                useLocalizedMandibleOpening = true;
                mandibleSideFollow = 0f;
                mandibleChinFocusPower = 2.6f;
            }

            mandibleOpenProfileVersion = 3;
        }

        private void Awake()
        {
            EnsureMandibleOpenProfile();
            boneMesh = GetComponent<AnatomyBoneMesh>();
            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }

            // Migrates the already-authored main jaw component without touching
            // runtime fragments, which should remain freely movable pieces.
            if (restrictToVerticalAxis && dragMouseButton == 1
                && gameObject.name.IndexOf("Jaw Layer") >= 0)
            {
                useLocalizedMandibleOpening = true;
            }
        }

        private void OnValidate()
        {
            EnsureMandibleOpenProfile();
            mandibleSideFollow = Mathf.Clamp(mandibleSideFollow, 0f, 0.35f);
            mandibleChinFocusPower = Mathf.Clamp(
                mandibleChinFocusPower,
                0.5f,
                12f);
            if (restrictToVerticalAxis && dragMouseButton == 1
                && gameObject.name.IndexOf("Jaw Layer") >= 0)
            {
                useLocalizedMandibleOpening = true;
            }
        }

        private void EnsureMandibleOpenProfile()
        {
            if (mandibleOpenProfileVersion >= 3)
            {
                return;
            }

            useLocalizedMandibleOpening = true;
            mandibleSideFollow = 0f;
            mandibleChinFocusPower = 2.6f;
            mandibleOpenProfileVersion = 3;
        }

        private void OnDisable()
        {
            if (capturedMover == this)
            {
                capturedMover = null;
            }
        }

        private void Update()
        {
            if (Input.GetMouseButtonDown(dragMouseButton)
                && capturedMover == null
                && !BoneCutInteractor.IsAnyBoneToolActive
                && TryGetPointerWorldPoint(out Vector3 worldPoint))
            {
                Vector3 localPoint = transform.InverseTransformPoint(worldPoint);
                localPoint.z = 0f;
                if (boneMesh.ContainsLocalPoint(localPoint))
                {
                    dragStartWorldPoint = worldPoint;
                    dragStartPosition = transform.position;
                    dragStartOpeningUv = boneMesh.MandibleOpeningUv;
                    capturedMover = this;
                }
            }

            if (capturedMover == this && Input.GetMouseButton(dragMouseButton))
            {
                UpdateDrag();
            }

            if (capturedMover == this && !Input.GetMouseButton(dragMouseButton))
            {
                capturedMover = null;
            }
        }

        private void UpdateDrag()
        {
            Camera cameraToUse = targetCamera != null ? targetCamera : Camera.main;
            if (cameraToUse == null)
            {
                return;
            }

            Ray ray = cameraToUse.ScreenPointToRay(Input.mousePosition);
            if (dragPlane.Raycast(ray, out float distance))
            {
                Vector3 delta = ray.GetPoint(distance) - dragStartWorldPoint;
                if (restrictToVerticalAxis)
                {
                    Vector3 verticalAxis = transform.up.normalized;
                    float verticalTravel = Vector3.Dot(delta, verticalAxis);
                    if (useLocalizedMandibleOpening)
                    {
                        float openingUv = dragStartOpeningUv
                            - verticalTravel / Mathf.Max(0.01f, boneMesh.Height);
                        boneMesh.SetMandibleOpenPose(
                            openingUv,
                            mandibleSideFollow,
                            mandibleChinFocusPower);
                        return;
                    }

                    delta = verticalAxis * verticalTravel;
                }

                transform.position = dragStartPosition + delta;
                boneMesh.NotifyPoseChanged();
            }
        }

        private bool TryGetPointerWorldPoint(out Vector3 worldPoint)
        {
            worldPoint = default(Vector3);
            Camera cameraToUse = targetCamera != null ? targetCamera : Camera.main;
            if (cameraToUse == null)
            {
                return false;
            }

            dragPlane = new Plane(transform.forward, transform.position);
            Ray ray = cameraToUse.ScreenPointToRay(Input.mousePosition);
            if (!dragPlane.Raycast(ray, out float distance))
            {
                return false;
            }

            worldPoint = ray.GetPoint(distance);
            return true;
        }
    }
}
