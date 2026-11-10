using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace DeformableImage
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class AnatomyBoneMesh : MonoBehaviour
    {
        private const string DefaultShaderName = "Deformable Image/Unlit Transparent";
        [Header("Bone Image")]
        [SerializeField] private Sprite boneSprite;
        [SerializeField, Min(0.01f)] private float height = 5f;
        [SerializeField] private bool respectSpritePivot = true;

        [Header("Pose Mesh Detail")]
        [Tooltip("Interior triangle subdivision used by local pose weights. The jaw needs interior vertices so front and rear teeth can move differently.")]
        [SerializeField, Range(0, 3)] private int mandiblePoseSubdivisionLevels;

        [Header("Mandible Opening Shape")]
        [Tooltip("Fraction of the actual mandible half-width that translates as one rounded central piece.")]
        [SerializeField, Range(0.15f, 0.6f)] private float mandibleChinPlateau = 0.24f;
        [Tooltip("Horizontal center of the visible mandible inside the full transparent sprite canvas.")]
        [SerializeField, Range(0.35f, 0.65f)] private float mandiblePoseCenterX = 0.498f;
        [Tooltip("Visible mandible half-width inside the full sprite canvas. The opening profile is normalized by this value.")]
        [SerializeField, Range(0.1f, 0.5f)] private float mandiblePoseHalfWidthUv = 0.22f;
        [Tooltip("Normalized distance where the mandibular angles become nearly stationary.")]
        [SerializeField, Range(0.5f, 1f)] private float mandibleJawAngleLock = 0.9f;
        [Tooltip("Center of the left fixed mandibular-angle zone. The right zone is mirrored automatically.")]
        [SerializeField] private Vector2 mandibleAnglePinCenterUv = new Vector2(0.29f, 0.34f);
        [Tooltip("XY radius of each fixed mandibular-angle zone.")]
        [SerializeField] private Vector2 mandibleAnglePinRadiusUv = new Vector2(0.09f, 0.2f);
        [Tooltip("Minimum fraction of chin travel retained at both mandibular angles. The body increases smoothly from this value toward the chin.")]
        [SerializeField, Range(0f, 0.35f)] private float mandibleAngleFollow = 0.25f;
        [Tooltip("Fraction of mandible opening transferred to both mouth corners and the nearby upper-lip corner tissue.")]
        [SerializeField, Range(0f, 0.6f)] private float mandibleMouthCornerFollow = 0.16f;
        [Tooltip("Higher values keep a broader rounded chin arc before bending toward the mandibular angles.")]
        [SerializeField, Range(0.35f, 3f)] private float mandibleArcRoundness = 1.15f;
        [Tooltip("Mandible UV height of the lower dental row used as the perspective-rotation pivot.")]
        [SerializeField, Range(0.2f, 0.65f)] private float mandibleDentalPivotY = 0.405f;
        [Tooltip("Horizontal center of the lower dental arch inside the full sprite canvas.")]
        [SerializeField, Range(0.35f, 0.65f)] private float mandibleDentalCenterX = 0.498f;
        [Tooltip("Half-width from the incisors to the rearmost visible molars. This is intentionally narrower than the complete mandible.")]
        [SerializeField, Range(0.05f, 0.3f)] private float mandibleDentalHalfWidthUv = 0.135f;
        [Tooltip("Half-height of the dental row band that receives the front-to-back tooth follow profile.")]
        [SerializeField, Range(0.02f, 0.16f)] private float mandibleDentalBandHalfHeightUv = 0.1f;
        [Tooltip("Power of the dental arch falloff. A value of 2 produces a quadratic/parabolic tooth arc.")]
        [FormerlySerializedAs("mandibleFrontDentalPlateau")]
        [SerializeField, Range(1f, 4f)] private float mandibleDentalCurvePower = 2f;
        [Tooltip("Feather outside the rearmost molars before the dental rule releases back to the mandibular body. Expressed as a fraction of dental half-width.")]
        [SerializeField, Range(0.02f, 0.5f)] private float mandibleDentalOuterFeather = 0.12f;
        [Tooltip("Fraction of opening retained by the rearmost molars. The dental row increases smoothly toward the incisors.")]
        [SerializeField, Range(0f, 0.5f)] private float mandibleRearDentalFollow = 0.05f;
        [Tooltip("Vertical foreshortening below the teeth per unit of jaw opening. Higher values shorten the tooth-to-bone distance more strongly.")]
        [SerializeField, Range(0f, 3f)] private float mandiblePerspectiveCompression = 1.35f;
        [Tooltip("Horizontal inward travel of each mandibular angle as a fraction of vertical opening.")]
        [SerializeField, Range(0f, 0.4f)] private float mandibleAngleInward = 0.04f;
        [Tooltip("Maximum proportional narrowing of the central chin and foremost teeth at full opening.")]
        [SerializeField, Range(0f, 0.2f)] private float mandibleCentralNarrowing = 0.06f;
        [Tooltip("Opening exponent used by horizontal narrowing. 2 gives accelerating quadratic motion.")]
        [SerializeField, Range(1f, 4f)] private float mandibleNarrowingAcceleration = 2f;
        [SerializeField, HideInInspector] private int mandibleShapeProfileVersion;

        [Header("Rendering")]
        [SerializeField] private Material materialOverride;
        [SerializeField] private Color tint = Color.white;

        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Mesh generatedMesh;
        private Material generatedMaterial;
        private MaterialPropertyBlock properties;
        private readonly List<Vector2[]> currentContours = new List<Vector2[]>();
        private DeformableImagePolygonUtility.TriangulatedPolygon currentTriangulation;
        private Vector2 localMinimum;
        private Vector2 localSize;
        private bool runtimeFragment;
        private float mandibleOpeningUv;
        private float mandibleSideFollow;
        private float mandibleChinFocusPower = 2.6f;

        public event Action TopologyChanged;
        public event Action PoseChanged;

        public Sprite BoneSprite { get { return boneSprite; } }
        public float Height { get { return height; } }
        public float MandibleOpeningUv { get { return mandibleOpeningUv; } }
        public float MandibleSideFollow { get { return mandibleSideFollow; } }
        public float MandibleChinFocusPower { get { return mandibleChinFocusPower; } }
        public float MandibleMouthCornerFollow { get { return mandibleMouthCornerFollow; } }
        public int CurrentVertexCount { get { return generatedMesh != null ? generatedMesh.vertexCount : 0; } }
        public int CurrentTriangleCount
        {
            get { return generatedMesh != null ? generatedMesh.triangles.Length / 3 : 0; }
        }

        private void OnEnable()
        {
            EnsureMandibleShapeProfile();
            CacheComponents();
            if (!runtimeFragment)
            {
                RebuildFromSprite();
            }
        }

        private void OnValidate()
        {
            EnsureMandibleShapeProfile();
            height = Mathf.Max(0.01f, height);
            mandiblePoseSubdivisionLevels = Mathf.Clamp(
                mandiblePoseSubdivisionLevels,
                0,
                3);
            mandibleChinPlateau = Mathf.Clamp(mandibleChinPlateau, 0.15f, 0.6f);
            mandiblePoseCenterX = Mathf.Clamp(mandiblePoseCenterX, 0.35f, 0.65f);
            mandiblePoseHalfWidthUv = Mathf.Clamp(
                mandiblePoseHalfWidthUv,
                0.1f,
                0.5f);
            mandibleJawAngleLock = Mathf.Clamp(
                mandibleJawAngleLock,
                mandibleChinPlateau + 0.05f,
                1f);
            mandibleAnglePinCenterUv = new Vector2(
                Mathf.Clamp(mandibleAnglePinCenterUv.x, 0.05f, 0.45f),
                Mathf.Clamp01(mandibleAnglePinCenterUv.y));
            mandibleAnglePinRadiusUv = new Vector2(
                Mathf.Max(0.01f, mandibleAnglePinRadiusUv.x),
                Mathf.Max(0.01f, mandibleAnglePinRadiusUv.y));
            mandibleAngleFollow = Mathf.Clamp(mandibleAngleFollow, 0f, 0.35f);
            mandibleMouthCornerFollow = Mathf.Clamp(
                mandibleMouthCornerFollow,
                0f,
                0.6f);
            mandibleArcRoundness = Mathf.Clamp(mandibleArcRoundness, 0.35f, 3f);
            mandibleDentalPivotY = Mathf.Clamp(mandibleDentalPivotY, 0.2f, 0.65f);
            mandibleDentalCenterX = Mathf.Clamp(mandibleDentalCenterX, 0.35f, 0.65f);
            mandibleDentalHalfWidthUv = Mathf.Clamp(
                mandibleDentalHalfWidthUv,
                0.05f,
                0.3f);
            mandibleDentalBandHalfHeightUv = Mathf.Clamp(
                mandibleDentalBandHalfHeightUv,
                0.02f,
                0.16f);
            mandibleDentalCurvePower = Mathf.Clamp(
                mandibleDentalCurvePower,
                1f,
                4f);
            mandibleDentalOuterFeather = Mathf.Clamp(
                mandibleDentalOuterFeather,
                0.02f,
                0.5f);
            mandibleRearDentalFollow = Mathf.Clamp(
                mandibleRearDentalFollow,
                0f,
                0.5f);
            mandiblePerspectiveCompression = Mathf.Clamp(
                mandiblePerspectiveCompression,
                0f,
                3f);
            mandibleAngleInward = Mathf.Clamp(mandibleAngleInward, 0f, 0.4f);
            mandibleCentralNarrowing = Mathf.Clamp(
                mandibleCentralNarrowing,
                0f,
                0.2f);
            mandibleNarrowingAcceleration = Mathf.Clamp(
                mandibleNarrowingAcceleration,
                1f,
                4f);
            if (Application.isPlaying && mandibleOpeningUv > 0f)
            {
                RefreshPosedMeshVertices();
                PoseChanged?.Invoke();
            }
        }

        private void EnsureMandibleShapeProfile()
        {
            if (mandibleShapeProfileVersion < 5)
            {
                // Preserve the broad symphyseal/chin arc and place almost all visible
                // stretch in the short mandibular-body spans before the fixed angles.
                mandibleChinPlateau = 0.37f;
                mandibleJawAngleLock = 0.62f;
                mandibleAnglePinCenterUv = new Vector2(0.29f, 0.34f);
                mandibleAnglePinRadiusUv = new Vector2(0.09f, 0.2f);
                mandibleAngleFollow = 0.06f;
                mandibleArcRoundness = 1.15f;
                mandibleDentalPivotY = 0.405f;
                mandiblePerspectiveCompression = 1.35f;
                mandibleSideFollow = 0f;
                mandibleShapeProfileVersion = 5;
            }

            if (mandibleShapeProfileVersion < 6)
            {
                // A compact rigid menton makes the bend visible in the body of the
                // mandible. The angles also converge slightly during opening.
                mandibleChinPlateau = 0.24f;
                mandibleAngleInward = 0.1f;
                mandibleShapeProfileVersion = 6;
            }

            if (mandibleShapeProfileVersion < 7)
            {
                // The source mandible occupies X 0.278..0.718 of its transparent
                // canvas. Normalize against that anatomical span rather than the
                // full sprite, otherwise Jaw Angle Lock is mathematically unreachable.
                mandiblePoseCenterX = 0.498f;
                mandiblePoseHalfWidthUv = 0.22f;
                mandibleShapeProfileVersion = 7;
            }

            if (mandibleShapeProfileVersion < 8)
            {
                // With the profile normalized to the real alpha width, carry the
                // smooth body transition almost all the way to the anatomical angle.
                // The angle still descends slightly, while horizontal convergence
                // remains subtle.
                mandibleJawAngleLock = 0.9f;
                mandibleAngleFollow = 0.16f;
                mandibleAngleInward = 0.04f;
                mandibleShapeProfileVersion = 8;
            }

            if (mandibleShapeProfileVersion < 9)
            {
                mandibleDentalBandHalfHeightUv = 0.08f;
                mandibleRearDentalFollow = 0.12f;
                mandibleShapeProfileVersion = 9;
            }

            if (mandibleShapeProfileVersion < 10)
            {
                mandiblePoseSubdivisionLevels = gameObject.name.IndexOf(
                    "Jaw Layer",
                    StringComparison.OrdinalIgnoreCase) >= 0
                    ? 3
                    : 0;
                mandibleShapeProfileVersion = 10;
            }

            if (mandibleShapeProfileVersion < 11)
            {
                // The visible dental arch spans roughly X 0.363..0.633, much
                // narrower than the complete mandible (X 0.278..0.718).
                mandibleDentalCenterX = 0.498f;
                mandibleDentalHalfWidthUv = 0.135f;
                mandibleDentalBandHalfHeightUv = 0.1f;
                mandibleRearDentalFollow = 0.05f;
                mandibleShapeProfileVersion = 11;
            }

            if (mandibleShapeProfileVersion < 12)
            {
                mandibleDentalCurvePower = 2f;
                mandibleDentalOuterFeather = 0.12f;
                mandibleRearDentalFollow = 0.05f;
                mandibleShapeProfileVersion = 12;
            }

            if (mandibleShapeProfileVersion < 13)
            {
                mandibleCentralNarrowing = 0.06f;
                mandibleNarrowingAcceleration = 2f;
                mandibleAngleInward = 0.04f;
                mandibleShapeProfileVersion = 13;
            }

            if (mandibleShapeProfileVersion < 14)
            {
                mandibleAngleFollow = 0.25f;
                mandibleMouthCornerFollow = 0.16f;
                mandibleShapeProfileVersion = 14;
            }

        }

        private void OnDestroy()
        {
            DestroyGeneratedObject(generatedMesh);
            DestroyGeneratedObject(generatedMaterial);
        }

        public void Configure(Sprite sprite, float worldHeight = 5f)
        {
            boneSprite = sprite;
            height = Mathf.Max(0.01f, worldHeight);
            runtimeFragment = false;
            RebuildFromSprite();
        }

        public void SetMandibleMouthCornerFollow(float strength)
        {
            mandibleMouthCornerFollow = Mathf.Clamp(strength, 0f, 0.6f);
            if (Application.isPlaying && mandibleOpeningUv > 0f)
            {
                RefreshPosedMeshVertices();
                PoseChanged?.Invoke();
            }
        }

        [ContextMenu("Rebuild Bone From Sprite")]
        public void RebuildFromSprite()
        {
            CacheComponents();
            currentContours.Clear();
            if (boneSprite == null)
            {
                ClearMesh();
                return;
            }

            CalculateLocalRect();
            List<Vector2[]> sourceContours = ExtractSpriteContours();
            if (!DeformableImagePolygonUtility.TryNormalizePaths(sourceContours, out List<Vector2[]> normalized)
                || !SetContours(normalized))
            {
                Debug.LogError("Could not build a polygon mesh from the bone Sprite outline.", this);
                ClearMesh();
                return;
            }

            runtimeFragment = false;
            TopologyChanged?.Invoke();
        }

        public bool TryGetNormalizedPoint(Vector3 localPoint, out Vector2 normalized)
        {
            normalized = LocalToNormalizedUnchecked(localPoint);
            return normalized.x >= 0f && normalized.x <= 1f
                && normalized.y >= 0f && normalized.y <= 1f;
        }

        public bool ContainsLocalPoint(Vector3 localPoint)
        {
            return TryGetNormalizedPoint(localPoint, out Vector2 normalized)
                && DeformableImagePolygonUtility.ContainsPoint(currentTriangulation, normalized);
        }

        public Vector3[] CopyLargestContourWorld()
        {
            if (currentContours.Count == 0)
            {
                return Array.Empty<Vector3>();
            }

            Vector2[] largest = currentContours[0];
            float largestArea = Mathf.Abs(SignedArea(largest));
            for (int index = 1; index < currentContours.Count; index++)
            {
                float area = Mathf.Abs(SignedArea(currentContours[index]));
                if (area > largestArea)
                {
                    largest = currentContours[index];
                    largestArea = area;
                }
            }

            Vector3[] world = new Vector3[largest.Length];
            for (int index = 0; index < largest.Length; index++)
            {
                world[index] = transform.TransformPoint(
                    NormalizedToLocalPosed(largest[index]));
            }

            return world;
        }

        public Vector2 LocalToNormalizedUnchecked(Vector3 localPoint)
        {
            float posedX = (localPoint.x - localMinimum.x)
                / Mathf.Max(0.0001f, localSize.x);
            float posedY = (localPoint.y - localMinimum.y)
                / Mathf.Max(0.0001f, localSize.y);
            float normalizedX = posedX;
            float normalizedY = posedY;
            float perspectiveScale = GetMandiblePerspectiveScale();
            for (int iteration = 0; iteration < 5; iteration++)
            {
                Vector2 estimate = new Vector2(normalizedX, normalizedY);
                normalizedX += posedX - EvaluateMandiblePosedX(estimate);
                float beforePerspective = posedY
                    + mandibleOpeningUv * EvaluateMandiblePoseWeight(estimate);
                normalizedY = beforePerspective < mandibleDentalPivotY
                    ? mandibleDentalPivotY
                        + (beforePerspective - mandibleDentalPivotY)
                        / Mathf.Max(0.65f, perspectiveScale)
                    : beforePerspective;
            }
            return new Vector2(normalizedX, normalizedY);
        }

        public Vector3 NormalizedToWorldUnchecked(Vector2 normalized)
        {
            return transform.TransformPoint(NormalizedToLocalPosed(normalized));
        }

        public Vector2 WorldToNormalizedUnchecked(Vector3 worldPoint)
        {
            return LocalToNormalizedUnchecked(transform.InverseTransformPoint(worldPoint));
        }

        public void NotifyPoseChanged()
        {
            PoseChanged?.Invoke();
        }

        public void SetMandibleOpenPose(
            float openingUv,
            float sideFollow,
            float chinFocusPower)
        {
            float clampedOpening = Mathf.Clamp(openingUv, 0f, 0.2f);
            float clampedSideFollow = Mathf.Clamp01(sideFollow);
            float clampedFocusPower = Mathf.Clamp(chinFocusPower, 0.5f, 12f);
            bool changed = !Mathf.Approximately(mandibleOpeningUv, clampedOpening)
                || !Mathf.Approximately(mandibleSideFollow, clampedSideFollow)
                || !Mathf.Approximately(mandibleChinFocusPower, clampedFocusPower);
            mandibleOpeningUv = clampedOpening;
            mandibleSideFollow = clampedSideFollow;
            mandibleChinFocusPower = clampedFocusPower;
            if (!changed)
            {
                return;
            }

            RefreshPosedMeshVertices();
            PoseChanged?.Invoke();
        }

        public bool ApplyClosedRemoval(
            IList<Vector2> normalizedCutter,
            out AnatomyBoneMesh removedFragment)
        {
            removedFragment = null;
            if (!DeformableImagePolygonUtility.TryBooleanDifference(
                    currentContours,
                    normalizedCutter,
                    out List<Vector2[]> retained,
                    out List<Vector2[]> removed)
                || !DeformableImagePolygonUtility.TryTriangulatePaths(
                    retained,
                    out DeformableImagePolygonUtility.TriangulatedPolygon retainedMesh)
                || !DeformableImagePolygonUtility.TryTriangulatePaths(
                    removed,
                    out DeformableImagePolygonUtility.TriangulatedPolygon removedMesh))
            {
                return false;
            }

            BuildMesh(retainedMesh);
            ReplaceContours(retained);
            removedFragment = CreateFragment(removed, removedMesh);
            TopologyChanged?.Invoke();
            return removedFragment != null;
        }

        private bool SetContours(IList<Vector2[]> contours)
        {
            if (!DeformableImagePolygonUtility.TryTriangulatePaths(
                    contours,
                    out DeformableImagePolygonUtility.TriangulatedPolygon triangulation))
            {
                return false;
            }

            ReplaceContours(contours);
            BuildMesh(triangulation);
            return true;
        }

        private void ReplaceContours(IList<Vector2[]> contours)
        {
            currentContours.Clear();
            for (int index = 0; index < contours.Count; index++)
            {
                currentContours.Add((Vector2[])contours[index].Clone());
            }
        }

        private void BuildMesh(DeformableImagePolygonUtility.TriangulatedPolygon triangulation)
        {
            CacheComponents();
            EnsureMaterial();
            currentTriangulation = DeformableImagePolygonUtility.SubdivideTriangulation(
                triangulation,
                mandiblePoseSubdivisionLevels);
            Vector3[] vertices = new Vector3[currentTriangulation.Vertices.Length];
            Vector2[] uvs = new Vector2[vertices.Length];
            int[] triangles = (int[])currentTriangulation.Triangles.Clone();

            for (int index = 0; index < vertices.Length; index++)
            {
                Vector2 normalized = currentTriangulation.Vertices[index];
                vertices[index] = NormalizedToLocalPosed(normalized);
                uvs[index] = SpriteUv(boneSprite, normalized);
            }

            // The included transparent shader expects clockwise front-facing triangles.
            for (int triangle = 0; triangle < triangles.Length; triangle += 3)
            {
                Vector3 a = vertices[triangles[triangle]];
                Vector3 b = vertices[triangles[triangle + 1]];
                Vector3 c = vertices[triangles[triangle + 2]];
                float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                if (area > 0f)
                {
                    int swap = triangles[triangle + 1];
                    triangles[triangle + 1] = triangles[triangle + 2];
                    triangles[triangle + 2] = swap;
                }
            }

            ReplaceMesh();
            generatedMesh.name = runtimeFragment
                ? "Runtime Bone Fragment Mesh"
                : "Runtime Anatomy Bone Mesh";
            generatedMesh.indexFormat = vertices.Length > 65535
                ? IndexFormat.UInt32
                : IndexFormat.UInt16;
            generatedMesh.vertices = vertices;
            generatedMesh.uv = uvs;
            generatedMesh.uv2 = currentTriangulation.Vertices;
            generatedMesh.triangles = triangles;
            generatedMesh.RecalculateBounds();
            meshFilter.sharedMesh = generatedMesh;
            ApplyMaterialProperties();
        }

        private Vector3 NormalizedToLocalPosed(Vector2 normalized)
        {
            float posedX = EvaluateMandiblePosedX(normalized);
            float posedY = normalized.y;
            if (posedY < mandibleDentalPivotY)
            {
                posedY = mandibleDentalPivotY
                    + (posedY - mandibleDentalPivotY)
                    * GetMandiblePerspectiveScale();
            }

            Vector2 local = localMinimum + Vector2.Scale(
                new Vector2(posedX, posedY),
                localSize);
            local.y -= mandibleOpeningUv * height
                * EvaluateMandiblePoseWeight(normalized);
            return new Vector3(local.x, local.y, 0f);
        }

        private float EvaluateMandiblePosedX(Vector2 normalized)
        {
            float opening01 = Mathf.Clamp01(mandibleOpeningUv / 0.2f);
            float acceleratedOpening = Mathf.Pow(
                opening01,
                mandibleNarrowingAcceleration);
            float distanceFromCenter = EvaluateMandibleHorizontalDistance(
                normalized.x);
            float centralWeight = 1f - SmootherStep01(
                Mathf.InverseLerp(
                    mandibleChinPlateau,
                    mandibleJawAngleLock,
                    distanceFromCenter));
            float centralScale = 1f
                - mandibleCentralNarrowing
                * acceleratedOpening
                * centralWeight;
            float posedX = mandiblePoseCenterX
                + (normalized.x - mandiblePoseCenterX) * centralScale;

            float inwardDirection = normalized.x < mandiblePoseCenterX ? 1f : -1f;
            posedX += inwardDirection
                * 0.2f
                * acceleratedOpening
                * mandibleAngleInward
                * EvaluateMandibleInwardWeight(normalized);
            return posedX;
        }

        private float GetMandiblePerspectiveScale()
        {
            return Mathf.Clamp(
                1f - mandibleOpeningUv * mandiblePerspectiveCompression,
                0.65f,
                1f);
        }

        public float EvaluateMandiblePoseWeight(Vector2 normalized)
        {
            float distanceFromCenter = EvaluateMandibleHorizontalDistance(
                normalized.x);

            // The menton is a broad rounded arc, not a single weighted point. Keep
            // that central arc on a rigid translation plateau and distribute the
            // deformation through the mandibular body toward the locked angles.
            // This prevents a pointed/V-shaped chin during opening.
            float transition = Mathf.InverseLerp(
                mandibleChinPlateau,
                Mathf.Max(mandibleChinPlateau + 0.05f, mandibleJawAngleLock),
                distanceFromCenter);
            float curvePower = Mathf.Lerp(
                    0.9f,
                    1.1f,
                    Mathf.InverseLerp(0.5f, 12f, mandibleChinFocusPower))
                * Mathf.Lerp(
                    0.85f,
                    1.15f,
                    Mathf.InverseLerp(0.35f, 3f, mandibleArcRoundness));
            transition = Mathf.Pow(Mathf.Clamp01(transition), curvePower);
            // Quintic smootherstep has zero slope and zero curvature at both ends,
            // eliminating the visible kink between mandibular body and angle.
            transition = SmootherStep01(transition);
            float angleTravel = Mathf.Max(
                mandibleSideFollow,
                mandibleAngleFollow);
            // Monotonic from chin to angle: no zero-weight shelf and no rebound
            // inside the angle pin. This preserves the chin arc and produces one
            // continuous mandibular-body curve.
            float bodyWeight = Mathf.Lerp(1f, angleTravel, transition);

            // Teeth use their own arch profile: incisors share the chin travel,
            // while premolars and molars progressively approach a small hinge-like
            // movement. Blend only inside the dental band so the mandibular body
            // retains its independent smooth curve.
            float distanceFromDentalPivot = Mathf.Abs(
                normalized.y - mandibleDentalPivotY);
            float dentalBand = 1f - SmootherStep01(
                Mathf.InverseLerp(
                    mandibleDentalBandHalfHeightUv * 0.65f,
                    mandibleDentalBandHalfHeightUv,
                    distanceFromDentalPivot));
            float dentalDistanceFromCenter =
                Mathf.Abs(normalized.x - mandibleDentalCenterX)
                    / Mathf.Max(0.01f, mandibleDentalHalfWidthUv);
            float dentalArchMask = 1f - SmootherStep01(
                Mathf.InverseLerp(
                    1f,
                    1f + mandibleDentalOuterFeather,
                    dentalDistanceFromCenter));
            dentalBand *= dentalArchMask;
            float dentalTransition = Mathf.Pow(
                Mathf.Clamp01(dentalDistanceFromCenter),
                mandibleDentalCurvePower);
            float dentalWeight = Mathf.Lerp(
                1f,
                mandibleRearDentalFollow,
                dentalTransition);
            return Mathf.Lerp(bodyWeight, dentalWeight, dentalBand);
        }

        public float EvaluateMandibleAnglePin(Vector2 normalized)
        {
            Vector2 radius = new Vector2(
                Mathf.Max(0.01f, mandibleAnglePinRadiusUv.x),
                Mathf.Max(0.01f, mandibleAnglePinRadiusUv.y));
            float left = EvaluatePinEllipse(
                normalized,
                mandibleAnglePinCenterUv,
                radius);
            Vector2 mirroredCenter = new Vector2(
                Mathf.Clamp01(
                    2f * mandiblePoseCenterX - mandibleAnglePinCenterUv.x),
                mandibleAnglePinCenterUv.y);
            float right = EvaluatePinEllipse(normalized, mirroredCenter, radius);
            return Mathf.Max(left, right);
        }

        private float EvaluateMandibleInwardWeight(Vector2 normalized)
        {
            float distanceFromCenter = EvaluateMandibleHorizontalDistance(
                normalized.x);
            float inwardStart = Mathf.Max(
                mandibleChinPlateau,
                mandibleJawAngleLock - 0.18f);
            float inwardEnd = Mathf.Min(1f, mandibleJawAngleLock + 0.18f);
            float sideWeight = SmootherStep01(
                Mathf.InverseLerp(inwardStart, inwardEnd, distanceFromCenter));
            float belowDentalRow = 1f - Mathf.SmoothStep(
                0f,
                1f,
                Mathf.InverseLerp(
                    mandibleDentalPivotY - 0.05f,
                    mandibleDentalPivotY + 0.01f,
                    normalized.y));
            return sideWeight * belowDentalRow;
        }

        private float EvaluateMandibleHorizontalDistance(float normalizedX)
        {
            return Mathf.Clamp01(
                Mathf.Abs(normalizedX - mandiblePoseCenterX)
                    / Mathf.Max(0.01f, mandiblePoseHalfWidthUv));
        }

        private static float SmootherStep01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * value
                * (value * (value * 6f - 15f) + 10f);
        }

        private static float EvaluatePinEllipse(
            Vector2 point,
            Vector2 center,
            Vector2 radius)
        {
            Vector2 delta = new Vector2(
                (point.x - center.x) / radius.x,
                (point.y - center.y) / radius.y);
            float distance = delta.magnitude;
            if (distance <= 0.72f)
            {
                return 1f;
            }

            if (distance >= 1.15f)
            {
                return 0f;
            }

            return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.72f, 1.15f, distance));
        }

        private void RefreshPosedMeshVertices()
        {
            if (generatedMesh == null || currentTriangulation == null
                || currentTriangulation.Vertices == null)
            {
                return;
            }

            Vector3[] vertices = new Vector3[currentTriangulation.Vertices.Length];
            for (int index = 0; index < vertices.Length; index++)
            {
                vertices[index] = NormalizedToLocalPosed(
                    currentTriangulation.Vertices[index]);
            }

            generatedMesh.vertices = vertices;
            generatedMesh.RecalculateBounds();
        }

        private AnatomyBoneMesh CreateFragment(
            IList<Vector2[]> contours,
            DeformableImagePolygonUtility.TriangulatedPolygon triangulation)
        {
            GameObject fragmentObject = new GameObject(gameObject.name + " - Removed Bone Fragment");
            fragmentObject.transform.SetParent(transform.parent, false);
            fragmentObject.transform.localPosition = transform.localPosition;
            fragmentObject.transform.localRotation = transform.localRotation;
            fragmentObject.transform.localScale = transform.localScale;

            AnatomyBoneMesh fragment = fragmentObject.AddComponent<AnatomyBoneMesh>();
            fragment.InitializeFragment(this, contours, triangulation);

            MeshRenderer fragmentRenderer = fragmentObject.GetComponent<MeshRenderer>();
            fragmentRenderer.shadowCastingMode = meshRenderer.shadowCastingMode;
            fragmentRenderer.receiveShadows = meshRenderer.receiveShadows;
            fragmentRenderer.sortingLayerID = meshRenderer.sortingLayerID;
            fragmentRenderer.sortingOrder = meshRenderer.sortingOrder + 1;
            fragmentObject.AddComponent<AnatomyBoneFragmentMover>();
            return fragment;
        }

        private void InitializeFragment(
            AnatomyBoneMesh source,
            IList<Vector2[]> contours,
            DeformableImagePolygonUtility.TriangulatedPolygon triangulation)
        {
            runtimeFragment = true;
            boneSprite = source.boneSprite;
            height = source.height;
            respectSpritePivot = source.respectSpritePivot;
            mandiblePoseSubdivisionLevels = source.mandiblePoseSubdivisionLevels;
            mandibleChinPlateau = source.mandibleChinPlateau;
            mandiblePoseCenterX = source.mandiblePoseCenterX;
            mandiblePoseHalfWidthUv = source.mandiblePoseHalfWidthUv;
            mandibleJawAngleLock = source.mandibleJawAngleLock;
            mandibleAnglePinCenterUv = source.mandibleAnglePinCenterUv;
            mandibleAnglePinRadiusUv = source.mandibleAnglePinRadiusUv;
            mandibleAngleFollow = source.mandibleAngleFollow;
            mandibleMouthCornerFollow = source.mandibleMouthCornerFollow;
            mandibleArcRoundness = source.mandibleArcRoundness;
            mandibleDentalPivotY = source.mandibleDentalPivotY;
            mandibleDentalCenterX = source.mandibleDentalCenterX;
            mandibleDentalHalfWidthUv = source.mandibleDentalHalfWidthUv;
            mandibleDentalBandHalfHeightUv = source.mandibleDentalBandHalfHeightUv;
            mandibleDentalCurvePower = source.mandibleDentalCurvePower;
            mandibleDentalOuterFeather = source.mandibleDentalOuterFeather;
            mandibleRearDentalFollow = source.mandibleRearDentalFollow;
            mandiblePerspectiveCompression = source.mandiblePerspectiveCompression;
            mandibleAngleInward = source.mandibleAngleInward;
            mandibleCentralNarrowing = source.mandibleCentralNarrowing;
            mandibleNarrowingAcceleration = source.mandibleNarrowingAcceleration;
            mandibleShapeProfileVersion = source.mandibleShapeProfileVersion;
            materialOverride = source.materialOverride;
            tint = source.tint;
            CalculateLocalRect();
            ReplaceContours(contours);
            BuildMesh(triangulation);
        }

        private List<Vector2[]> ExtractSpriteContours()
        {
            List<Vector2[]> contours = new List<Vector2[]>();
            int shapeCount = boneSprite.GetPhysicsShapeCount();
            List<Vector2> shape = new List<Vector2>();
            float pixelsPerUnit = Mathf.Max(0.0001f, boneSprite.pixelsPerUnit);
            Vector2 spriteSize = boneSprite.rect.size;

            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                shape.Clear();
                boneSprite.GetPhysicsShape(shapeIndex, shape);
                if (shape.Count < 3)
                {
                    continue;
                }

                Vector2[] normalized = new Vector2[shape.Count];
                for (int pointIndex = 0; pointIndex < shape.Count; pointIndex++)
                {
                    Vector2 pixelPoint = shape[pointIndex] * pixelsPerUnit + boneSprite.pivot;
                    normalized[pointIndex] = new Vector2(
                        pixelPoint.x / Mathf.Max(1f, spriteSize.x),
                        pixelPoint.y / Mathf.Max(1f, spriteSize.y));
                }

                contours.Add(normalized);
            }

            if (contours.Count == 0)
            {
                contours.Add(new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, 1f)
                });
            }

            return contours;
        }

        private void CalculateLocalRect()
        {
            float spriteWidth = Mathf.Max(1f, boneSprite.rect.width);
            float spriteHeight = Mathf.Max(1f, boneSprite.rect.height);
            float width = height * spriteWidth / spriteHeight;
            Vector2 pivot = respectSpritePivot
                ? new Vector2(boneSprite.pivot.x / spriteWidth, boneSprite.pivot.y / spriteHeight)
                : new Vector2(0.5f, 0.5f);
            localMinimum = new Vector2(-width * pivot.x, -height * pivot.y);
            localSize = new Vector2(width, height);
        }

        private static Vector2 SpriteUv(Sprite sprite, Vector2 normalized)
        {
            // The polygon points are normalized against Sprite.rect (the complete source
            // canvas), not textureRect (Unity's tight alpha bounding box). Mapping a sparse
            // mandible through textureRect enlarges its teeth and clips the lower bone.
            // Both skull assets deliberately share one full canvas, so preserve that space.
            Rect rect = sprite.rect;
            return new Vector2(
                Mathf.Lerp(rect.xMin / sprite.texture.width, rect.xMax / sprite.texture.width, normalized.x),
                Mathf.Lerp(rect.yMin / sprite.texture.height, rect.yMax / sprite.texture.height, normalized.y));
        }

        private static float SignedArea(IList<Vector2> polygon)
        {
            float twiceArea = 0f;
            int previous = polygon.Count - 1;
            for (int current = 0; current < polygon.Count; current++)
            {
                twiceArea += polygon[previous].x * polygon[current].y
                    - polygon[current].x * polygon[previous].y;
                previous = current;
            }

            return twiceArea * 0.5f;
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

        private void EnsureMaterial()
        {
            if (meshRenderer == null)
            {
                return;
            }

            Material selected = materialOverride;
            if (selected == null)
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
                        name = "Runtime Anatomy Bone Material",
                        hideFlags = HideFlags.DontSave
                    };
                }

                selected = generatedMaterial;
            }

            meshRenderer.sharedMaterial = selected;
        }

        private void ApplyMaterialProperties()
        {
            if (meshRenderer == null || boneSprite == null)
            {
                return;
            }

            properties = properties ?? new MaterialPropertyBlock();
            properties.Clear();
            properties.SetTexture("_MainTex", boneSprite.texture);
            properties.SetTexture("_CutMask", Texture2D.whiteTexture);
            properties.SetTexture("_CutVisualMask", Texture2D.blackTexture);
            properties.SetColor("_Color", tint);
            meshRenderer.SetPropertyBlock(properties);
        }

        private void ReplaceMesh()
        {
            if (generatedMesh != null)
            {
                if (meshFilter.sharedMesh == generatedMesh)
                {
                    meshFilter.sharedMesh = null;
                }

                DestroyGeneratedObject(generatedMesh);
            }

            generatedMesh = new Mesh { hideFlags = HideFlags.DontSave };
        }

        private void ClearMesh()
        {
            currentContours.Clear();
            currentTriangulation = null;
            if (generatedMesh != null)
            {
                generatedMesh.Clear();
            }
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
