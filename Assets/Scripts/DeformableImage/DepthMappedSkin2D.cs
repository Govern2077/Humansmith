using UnityEngine;

namespace DeformableImage
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(DeformableImageMesh))]
    public sealed class DepthMappedSkin2D : MonoBehaviour
    {
        [Header("Depth Texture")]
        [SerializeField] private Texture2D depthTexture;
        [Tooltip("The supplied map uses brighter pixels for points closer to the camera.")]
        [SerializeField] private bool brighterIsCloser = true;
        [SerializeField] private bool flipHorizontal;
        [SerializeField] private bool flipVertical;

        [Header("UV Alignment")]
        [SerializeField] private Vector2 uvScale = Vector2.one;
        [SerializeField] private Vector2 uvOffset = Vector2.zero;

        [Header("Z Remapping")]
        [Tooltip("Maximum local-space relief. The current face height is 5, so 0.55 gives visible but controlled depth.")]
        [SerializeField, Min(0f)] private float depthScale = 0.55f;
        [Tooltip("Multiplies the complete facial relief without changing the depth map's black/white calibration.")]
        [SerializeField, Range(0f, 5f)] private float facialReliefMultiplier = 1f;
        [SerializeField, Range(0f, 1f)] private float blackPoint = 0.12f;
        [SerializeField, Range(0f, 1f)] private float whitePoint = 0.98f;
        [SerializeField, Min(0.05f)] private float depthExponent = 1f;
        [Tooltip("For this face orientation, negative local Z points toward the camera.")]
        [SerializeField] private bool towardCameraUsesNegativeZ = true;
        [SerializeField] private float zOffset;

        [Header("Sampling")]
        [Tooltip("A small cross blur suppresses compression noise while preserving the nose and lips.")]
        [SerializeField, Range(0, 4)] private int blurRadiusPixels = 1;
        [SerializeField] private bool applyAutomatically = true;
        [SerializeField, TextArea(2, 4)] private string lastStatus = "Depth has not been applied yet.";

        private DeformableImageMesh faceMesh;
        private bool applying;

        public Texture2D DepthTexture
        {
            get { return depthTexture; }
            set
            {
                depthTexture = value;
                ApplyDepthMap();
            }
        }

        public string LastStatus { get { return lastStatus; } }

        private void Awake()
        {
            CacheMesh();
        }

        private void OnEnable()
        {
            CacheMesh();
            Subscribe();
            if (applyAutomatically)
            {
                ApplyDepthMap();
            }
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void OnValidate()
        {
            uvScale.x = Mathf.Abs(uvScale.x) < 0.0001f ? 0.0001f : uvScale.x;
            uvScale.y = Mathf.Abs(uvScale.y) < 0.0001f ? 0.0001f : uvScale.y;
            depthScale = Mathf.Max(0f, depthScale);
            facialReliefMultiplier = Mathf.Clamp(facialReliefMultiplier, 0f, 5f);
            whitePoint = Mathf.Max(blackPoint + 0.001f, whitePoint);
            depthExponent = Mathf.Max(0.05f, depthExponent);
            blurRadiusPixels = Mathf.Clamp(blurRadiusPixels, 0, 4);

            if (!Application.isPlaying && isActiveAndEnabled && applyAutomatically)
            {
                ApplyDepthMap(false);
            }
        }

        [ContextMenu("Apply Depth Map Now")]
        public void ApplyDepthMap()
        {
            ApplyDepthMap(true);
        }

        [ContextMenu("Reset Skin Z To Flat")]
        public void ResetDepthToFlat()
        {
            CacheMesh();
            if (faceMesh == null || faceMesh.LogicalVertexCount == 0)
            {
                return;
            }

            float[] flatDepths = new float[faceMesh.LogicalVertexCount];
            for (int index = 0; index < flatDepths.Length; index++)
            {
                flatDepths[index] = zOffset;
            }

            faceMesh.ApplyRestDepths(flatDepths);
            lastStatus = "Skin rest Z was reset to a flat surface.";
        }

        private void ApplyDepthMap(bool reportErrors)
        {
            if (applying)
            {
                return;
            }

            CacheMesh();
            if (faceMesh == null || faceMesh.LogicalVertexCount == 0)
            {
                lastStatus = "Face mesh is not ready.";
                return;
            }

            if (depthTexture == null)
            {
                lastStatus = "Assign a readable depth Texture2D.";
                return;
            }

            if (!depthTexture.isReadable)
            {
                lastStatus = "Depth texture Read/Write is disabled.";
                if (reportErrors)
                {
                    Debug.LogError(lastStatus + " Enable Read/Write in its import settings.", this);
                }
                return;
            }

            applying = true;
            try
            {
                Vector2[] normalizedUvs = faceMesh.CopyNormalizedRestUvs();
                float[] localDepths = new float[normalizedUvs.Length];
                float direction = towardCameraUsesNegativeZ ? -1f : 1f;
                float effectiveDepthScale = depthScale * facialReliefMultiplier;
                for (int index = 0; index < normalizedUvs.Length; index++)
                {
                    Vector2 uv = Vector2.Scale(normalizedUvs[index], uvScale) + uvOffset;
                    if (flipHorizontal)
                    {
                        uv.x = 1f - uv.x;
                    }

                    if (flipVertical)
                    {
                        uv.y = 1f - uv.y;
                    }

                    float depth = SampleDepth(uv);
                    if (!brighterIsCloser)
                    {
                        depth = 1f - depth;
                    }

                    float normalizedDepth = Mathf.InverseLerp(blackPoint, whitePoint, depth);
                    normalizedDepth = Mathf.Pow(Mathf.Clamp01(normalizedDepth), depthExponent);
                    localDepths[index] = zOffset + direction * normalizedDepth * effectiveDepthScale;
                }

                if (faceMesh.ApplyRestDepths(localDepths))
                {
                    lastStatus = "Applied depth map to " + localDepths.Length
                        + " skin vertices. Z range: "
                        + zOffset.ToString("0.000") + " to "
                        + (zOffset + direction * effectiveDepthScale).ToString("0.000") + ".";
                }
            }
            finally
            {
                applying = false;
            }
        }

        private float SampleDepth(Vector2 uv)
        {
            uv.x = Mathf.Clamp01(uv.x);
            uv.y = Mathf.Clamp01(uv.y);
            Color center = depthTexture.GetPixelBilinear(uv.x, uv.y);
            if (blurRadiusPixels <= 0)
            {
                return center.grayscale;
            }

            float offsetX = blurRadiusPixels / (float)Mathf.Max(1, depthTexture.width);
            float offsetY = blurRadiusPixels / (float)Mathf.Max(1, depthTexture.height);
            float total = center.grayscale * 4f;
            total += depthTexture.GetPixelBilinear(Mathf.Clamp01(uv.x - offsetX), uv.y).grayscale;
            total += depthTexture.GetPixelBilinear(Mathf.Clamp01(uv.x + offsetX), uv.y).grayscale;
            total += depthTexture.GetPixelBilinear(uv.x, Mathf.Clamp01(uv.y - offsetY)).grayscale;
            total += depthTexture.GetPixelBilinear(uv.x, Mathf.Clamp01(uv.y + offsetY)).grayscale;
            return total / 8f;
        }

        private void HandleMeshRebuilt()
        {
            if (applyAutomatically)
            {
                ApplyDepthMap(false);
            }
        }

        private void CacheMesh()
        {
            if (faceMesh == null)
            {
                faceMesh = GetComponent<DeformableImageMesh>();
            }
        }

        private void Subscribe()
        {
            if (faceMesh != null)
            {
                faceMesh.MeshRebuilt -= HandleMeshRebuilt;
                faceMesh.MeshRebuilt += HandleMeshRebuilt;
            }
        }

        private void Unsubscribe()
        {
            if (faceMesh != null)
            {
                faceMesh.MeshRebuilt -= HandleMeshRebuilt;
            }
        }
    }
}
