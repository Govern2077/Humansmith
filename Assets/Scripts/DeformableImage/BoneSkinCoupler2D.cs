using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeformableImage
{
    [DisallowMultipleComponent]
    public sealed class BoneSkinCoupler2D : MonoBehaviour
    {
        [Header("Layers")]
        [SerializeField] private DeformableImageMesh faceMesh;
        [SerializeField] private AnatomyBoneMesh jawBone;
        [SerializeField] private FacialFeaturePrecut2D facialCurves;

        [Header("Automatic Jaw Weights (Bone UV)")]
        [SerializeField, Range(0f, 1f)] private float jawRegionBottom = 0.18f;
        [SerializeField, Range(0f, 1f)] private float jawRegionPeak = 0.34f;
        [SerializeField, Range(0f, 1f)] private float jawRegionTop = 0.56f;
        [SerializeField, Min(0.01f)] private float influenceRadius = 0.9f;
        [SerializeField, Range(0f, 1f)] private float minimumUsefulWeight = 0.01f;

        [Header("Mandible Edge Attachment")]
        [Tooltip("The teeth and gingiva inside this mandible-UV ellipse never bind to muscle.")]
        [SerializeField] private Vector2 oralExclusionCenterUv = new Vector2(0.5f, 0.375f);
        [SerializeField] private Vector2 oralExclusionRadiusUv = new Vector2(0.205f, 0.07f);
        [Tooltip("Soft transition outside the fully excluded teeth/gingiva region.")]
        [SerializeField, Range(0f, 1f)] private float oralExclusionFeather = 0.28f;

        [Header("Lower-Lip Follow (Mandible UV)")]
        [Tooltip("Only muscle vertices below the authored mouth incision can receive this jaw-follow weight.")]
        [SerializeField] private Vector2 lipFollowCenterUv = new Vector2(0.5f, 0.31f);
        [SerializeField] private Vector2 lipFollowRadiusUv = new Vector2(0.19f, 0.07f);
        [SerializeField, Range(0f, 1.5f)] private float lipFollowStrength = 0.72f;
        [Tooltip("Immediate follow applied to the first lower-lip row below the incision, preventing the two lip edges from remaining stuck.")]
        [SerializeField, Range(0f, 1f)] private float lowerLipInitialFollow = 0.58f;
        [Tooltip("Distance below the mouth incision at which lower-lip follow reaches full strength.")]
        [SerializeField, Range(0.005f, 0.12f)] private float lowerLipFullFollowDepthUv = 0.05f;
        [Tooltip("Depth below the mouth incision treated as one shape-preserving lower-lip band.")]
        [SerializeField, Range(0.01f, 0.12f)] private float lowerLipRigidBandDepthUv = 0.065f;
        [Tooltip("Soft transition from the rigid lip band into the chin and lower face.")]
        [SerializeField, Range(0.01f, 0.15f)] private float lowerLipRigidTransitionDepthUv = 0.055f;
        [Tooltip("How strongly the complete lower-lip band follows the mandible opening amount.")]
        [SerializeField, Range(0f, 1.5f)] private float lowerLipRigidFollow = 0.78f;
        [Tooltip("Editable horizontal jaw-follow profile. X is center-to-corner distance; Y is normalized follow before the corner minimum is applied.")]
        [SerializeField] private AnimationCurve lowerLipHorizontalFalloff =
            CreateDefaultLowerLipHorizontalFalloff();
        [Tooltip("Minimum mandible-follow retained at the left and right mouth corners. The lower-lip center always remains at full follow.")]
        [SerializeField, Range(0f, 0.5f)] private float lowerLipCornerMinimumFollow = 0.08f;

        [Header("Upper-Lip Corner Follow (Skin UV)")]
        [Tooltip("Central half-width around the Cupid's bow/lip bead that remains completely independent of mandible opening.")]
        [SerializeField, Range(0f, 0.5f)] private float upperLipPearlProtection = 0.18f;
        [Tooltip("Height above the mouth incision that can receive upper-lip corner follow.")]
        [SerializeField, Range(0.01f, 0.12f)] private float upperLipFollowHeightUv = 0.065f;
        [Tooltip("Maximum fraction of mandible opening transferred to the upper lip at both mouth corners.")]
        [SerializeField, Range(0f, 0.6f)] private float upperLipCornerFollow = 0.2f;

        [Header("Mouth-Corner Jaw Follow (Skin UV)")]
        [Tooltip("Smooth XY radius around each authored mouth-cut endpoint that follows mandible opening.")]
        [SerializeField] private Vector2 mouthCornerFollowRadiusUv = new Vector2(0.065f, 0.07f);
        [Tooltip("Downward mandible travel transferred to each mouth corner and its surrounding tissue.")]
        [SerializeField, HideInInspector, Range(0f, 0.6f)] private float mouthCornerFollow = 0.16f;
        [Tooltip("Smooth vertical reach above the mouth-cut endpoints, covering the upper lip near each corner.")]
        [SerializeField, Range(0.01f, 0.15f)] private float mouthCornerUpperLipReachUv = 0.075f;

        [Header("Cheekbone Expansion (Skin UV)")]
        [Tooltip("Center of the left cheekbone muscle zone. The right zone is mirrored automatically.")]
        [SerializeField] private Vector2 cheekExpansionCenterUv = new Vector2(0.34f, 0.48f);
        [Tooltip("XY radius of each cheekbone muscle expansion zone.")]
        [SerializeField] private Vector2 cheekExpansionRadiusUv = new Vector2(0.12f, 0.1f);
        [Tooltip("Outward cheek travel as a fraction of mandible opening.")]
        [SerializeField, Range(0f, 0.3f)] private float cheekExpansionFollow = 0.07f;

        [Header("Chin Cut Influence (Mandible UV)")]
        [Tooltip("Explicit menton/chin attachment zone. Cutting its retained bony edge reshapes the chin muscle similarly to the jaw angle.")]
        [SerializeField] private Vector2 chinInfluenceCenterUv = new Vector2(0.5f, 0.235f);
        [SerializeField] private Vector2 chinInfluenceRadiusUv = new Vector2(0.2f, 0.09f);
        [SerializeField, Range(0f, 2f)] private float chinInfluenceStrength = 1.15f;
        [SerializeField, HideInInspector] private int attachmentProfileVersion;

        [Header("Soft Tissue Response")]
        [SerializeField, Range(0f, 2f)] private float response = 1f;
        [SerializeField, Min(0.01f)] private float maximumDisplacement = 0.8f;
        [SerializeField, Range(0, 50)] private int smoothingIterations = 16;
        [SerializeField, Range(0f, 1f)] private float smoothingStrength = 0.45f;
        [SerializeField] private bool preserveManualFaceOffsets = true;
        [Tooltip("Keep every detached skin vertex at the Y height it had when it was peeled. This prevents a cut jaw contour from pulling reattached skin toward an unrelated contour edge.")]
        [SerializeField] private bool preserveDetachedPieceOriginalY = true;

        [Header("Debug")]
        [SerializeField] private bool showWeightGizmos;
        [SerializeField, Min(0.001f)] private float gizmoRadius = 0.018f;
        [SerializeField, TextArea(2, 4)] private string lastStatus = "Not bound yet.";

        private Vector3[] bindFaceVertices = Array.Empty<Vector3>();
        private Vector2[] bindBoneAnchors = Array.Empty<Vector2>();
        private Vector2[] bindBoneAnchorUvs = Array.Empty<Vector2>();
        private Vector2[] bindFaceBoneUvs = Array.Empty<Vector2>();
        private Vector2[] bindFaceUvs = Array.Empty<Vector2>();
        private float[] jawWeights = Array.Empty<float>();
        private float[] rigidLowerLipMasks = Array.Empty<float>();
        private float[] lowerLipPoseRegionMasks = Array.Empty<float>();
        private float[] lowerLipCenterFollowMasks = Array.Empty<float>();
        private float[] upperLipPoseRegionMasks = Array.Empty<float>();
        private float[] upperLipSideFollowMasks = Array.Empty<float>();
        private float[] mouthCornerFollowMasks = Array.Empty<float>();
        private float[] cheekExpansionMasks = Array.Empty<float>();
        private float[] mandibleAnglePinMasks = Array.Empty<float>();
        private Vector3[] appliedBoneOffsets = Array.Empty<Vector3>();
        private Vector3[] rawOffsets = Array.Empty<Vector3>();
        private Vector3[] smoothOffsets = Array.Empty<Vector3>();
        private Vector3[] smoothingBuffer = Array.Empty<Vector3>();
        private List<int>[] neighbours = Array.Empty<List<int>>();
        private bool bound;

        public string LastStatus { get { return lastStatus; } }
        public AnatomyBoneMesh JawBone { get { return jawBone; } }
        public float MouthCornerFollowStrength
        {
            get
            {
                return jawBone != null
                    ? jawBone.MandibleMouthCornerFollow
                    : mouthCornerFollow;
            }
        }
        public Vector2 MouthCornerFollowRadiusUv { get { return mouthCornerFollowRadiusUv; } }
        public float MouthCornerUpperLipReachUv { get { return mouthCornerUpperLipReachUv; } }

        public bool TryCopyAppliedBoneOffsets(Vector3[] destination)
        {
            if (!bound || destination == null
                || destination.Length != appliedBoneOffsets.Length)
            {
                return false;
            }

            Array.Copy(appliedBoneOffsets, destination, appliedBoneOffsets.Length);
            return true;
        }

        private void Awake()
        {
            if (faceMesh == null)
            {
                faceMesh = GetComponent<DeformableImageMesh>();
            }

            if (facialCurves == null)
            {
                facialCurves = FindObjectOfType<FacialFeaturePrecut2D>(true);
            }

            ConfigureAsMandibleAttachment();
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
            ConfigureAsMandibleAttachment();
            jawRegionPeak = Mathf.Clamp(jawRegionPeak, jawRegionBottom, jawRegionTop);
            influenceRadius = Mathf.Max(0.01f, influenceRadius);
            maximumDisplacement = Mathf.Max(0.01f, maximumDisplacement);
            oralExclusionRadiusUv = new Vector2(
                Mathf.Max(0.001f, oralExclusionRadiusUv.x),
                Mathf.Max(0.001f, oralExclusionRadiusUv.y));
            lipFollowRadiusUv = ClampRadius(lipFollowRadiusUv);
            chinInfluenceRadiusUv = ClampRadius(chinInfluenceRadiusUv);
            lowerLipFullFollowDepthUv = Mathf.Max(0.005f, lowerLipFullFollowDepthUv);
            lowerLipRigidBandDepthUv = Mathf.Max(0.01f, lowerLipRigidBandDepthUv);
            lowerLipRigidTransitionDepthUv = Mathf.Max(
                0.01f,
                lowerLipRigidTransitionDepthUv);
            lowerLipCornerMinimumFollow = Mathf.Clamp(
                lowerLipCornerMinimumFollow,
                0f,
                0.5f);
            upperLipPearlProtection = Mathf.Clamp(
                upperLipPearlProtection,
                0f,
                0.5f);
            upperLipFollowHeightUv = Mathf.Clamp(
                upperLipFollowHeightUv,
                0.01f,
                0.12f);
            upperLipCornerFollow = Mathf.Clamp(upperLipCornerFollow, 0f, 0.6f);
            mouthCornerFollowRadiusUv = ClampRadius(mouthCornerFollowRadiusUv);
            mouthCornerFollow = Mathf.Clamp(mouthCornerFollow, 0f, 0.6f);
            mouthCornerUpperLipReachUv = Mathf.Clamp(
                mouthCornerUpperLipReachUv,
                0.01f,
                0.15f);
            cheekExpansionCenterUv = new Vector2(
                Mathf.Clamp(cheekExpansionCenterUv.x, 0.05f, 0.45f),
                Mathf.Clamp01(cheekExpansionCenterUv.y));
            cheekExpansionRadiusUv = ClampRadius(cheekExpansionRadiusUv);
            cheekExpansionFollow = Mathf.Clamp(cheekExpansionFollow, 0f, 0.3f);
            if (lowerLipHorizontalFalloff == null
                || lowerLipHorizontalFalloff.length < 2)
            {
                lowerLipHorizontalFalloff = CreateDefaultLowerLipHorizontalFalloff();
            }

            if (Application.isPlaying && bound)
            {
                RefreshMouthCornerFollowMasks();
                ApplyCurrentBoneShape(true);
            }
        }

        public void Configure(DeformableImageMesh face, AnatomyBoneMesh bone)
        {
            Unsubscribe();
            faceMesh = face;
            jawBone = bone;
            Subscribe();
            if (Application.isPlaying)
            {
                BindRestPose();
            }
        }

        public void ConfigureAsMandibleAttachment()
        {
            if (attachmentProfileVersion < 1)
            {
                jawRegionBottom = 0.16f;
                jawRegionPeak = 0.27f;
                jawRegionTop = 0.58f;
                influenceRadius = 0.78f;
                oralExclusionCenterUv = new Vector2(0.5f, 0.375f);
                oralExclusionRadiusUv = new Vector2(0.205f, 0.07f);
                oralExclusionFeather = 0.28f;
            }

            if (attachmentProfileVersion < 2)
            {
                lipFollowCenterUv = new Vector2(0.5f, 0.365f);
                lipFollowRadiusUv = new Vector2(0.19f, 0.065f);
                lipFollowStrength = 0.72f;
                chinInfluenceCenterUv = new Vector2(0.5f, 0.235f);
                chinInfluenceRadiusUv = new Vector2(0.2f, 0.09f);
                chinInfluenceStrength = 1.15f;
            }

            if (attachmentProfileVersion < 3)
            {
                lipFollowCenterUv = new Vector2(0.5f, 0.31f);
                lipFollowRadiusUv = new Vector2(0.19f, 0.07f);
                lowerLipFullFollowDepthUv = 0.05f;
            }

            if (attachmentProfileVersion < 4)
            {
                lowerLipInitialFollow = 0.58f;
                lowerLipFullFollowDepthUv = 0.045f;
            }

            if (attachmentProfileVersion < 6)
            {
                // The first row directly below the authored lip incision must move as
                // a visible lower-lip edge, not remain behind while deeper vertices
                // stretch. The wider chin zone spreads the central motion into the
                // lower face without re-enabling tooth/gingiva anchors.
                lipFollowCenterUv = new Vector2(0.5f, 0.325f);
                lipFollowRadiusUv = new Vector2(0.215f, 0.09f);
                lipFollowStrength = 1.05f;
                lowerLipInitialFollow = 0.88f;
                lowerLipFullFollowDepthUv = 0.035f;
                chinInfluenceRadiusUv = new Vector2(0.24f, 0.12f);
                chinInfluenceStrength = 1.3f;
            }

            if (attachmentProfileVersion < 7)
            {
                lowerLipRigidBandDepthUv = 0.065f;
                lowerLipRigidTransitionDepthUv = 0.055f;
                lowerLipRigidFollow = 0.78f;
            }

            if (attachmentProfileVersion < 8)
            {
                chinInfluenceCenterUv = new Vector2(0.5f, 0.25f);
                chinInfluenceRadiusUv = new Vector2(0.29f, 0.16f);
                chinInfluenceStrength = 1.18f;
            }

            if (attachmentProfileVersion < 9)
            {
                // Keep the lip itself compact. The larger transition below it moves
                // the chin/mandibular body instead of inflating the vermilion band.
                lowerLipRigidBandDepthUv = 0.052f;
                lowerLipRigidTransitionDepthUv = 0.1f;
                lowerLipRigidFollow = 0.86f;
                chinInfluenceCenterUv = new Vector2(0.5f, 0.245f);
                chinInfluenceRadiusUv = new Vector2(0.34f, 0.18f);
                chinInfluenceStrength = 1.12f;
            }

            if (attachmentProfileVersion < 11)
            {
                lowerLipCornerMinimumFollow = 0.08f;
            }

            if (attachmentProfileVersion < 12)
            {
                lowerLipHorizontalFalloff = CreateDefaultLowerLipHorizontalFalloff();
            }

            if (attachmentProfileVersion < 13)
            {
                upperLipPearlProtection = 0.18f;
                upperLipFollowHeightUv = 0.065f;
                upperLipCornerFollow = 0.2f;
            }

            if (attachmentProfileVersion < 14)
            {
                cheekExpansionCenterUv = new Vector2(0.34f, 0.48f);
                cheekExpansionRadiusUv = new Vector2(0.12f, 0.1f);
                cheekExpansionFollow = 0.07f;
            }

            if (attachmentProfileVersion < 15)
            {
                mouthCornerFollowRadiusUv = new Vector2(0.065f, 0.07f);
                mouthCornerFollow = 0.16f;
            }

            if (attachmentProfileVersion < 16)
            {
                mouthCornerUpperLipReachUv = 0.075f;
            }

            attachmentProfileVersion = 16;
        }

        public void ConfigureMouthCornerFollow(
            float strength,
            Vector2 radiusUv,
            float upperLipReachUv)
        {
            mouthCornerFollow = Mathf.Clamp(strength, 0f, 0.6f);
            if (jawBone != null)
            {
                jawBone.SetMandibleMouthCornerFollow(mouthCornerFollow);
            }
            mouthCornerFollowRadiusUv = ClampRadius(radiusUv);
            mouthCornerUpperLipReachUv = Mathf.Clamp(
                upperLipReachUv,
                0.01f,
                0.15f);
            RefreshMouthCornerFollowMasks();
            if (Application.isPlaying && bound)
            {
                ApplyCurrentBoneShape(true);
            }
        }

        public void SetMouthCornerFollowStrength(float strength)
        {
            ConfigureMouthCornerFollow(
                strength,
                mouthCornerFollowRadiusUv,
                mouthCornerUpperLipReachUv);
        }

        public DetachedSkinBoneBinding2D CreateDetachedPieceBinding(
            DeformableImageMesh detachedPiece)
        {
            if (detachedPiece == null || jawBone == null)
            {
                return null;
            }

            DetachedSkinBoneBinding2D binding =
                detachedPiece.GetComponent<DetachedSkinBoneBinding2D>();
            if (binding == null)
            {
                binding = detachedPiece.gameObject.AddComponent<DetachedSkinBoneBinding2D>();
            }

            return binding.Configure(
                detachedPiece,
                jawBone,
                jawRegionBottom,
                jawRegionPeak,
                jawRegionTop,
                influenceRadius,
                minimumUsefulWeight,
                response,
                maximumDisplacement,
                smoothingIterations,
                smoothingStrength,
                preserveDetachedPieceOriginalY)
                ? binding
                : null;
        }

        [ContextMenu("Bind Automatic Jaw Weights")]
        public void BindRestPose()
        {
            bound = false;
            if (faceMesh == null || jawBone == null)
            {
                lastStatus = "Assign both Face Mesh and Jaw Bone.";
                return;
            }

            Vector3[] contourWorld = jawBone.CopyLargestContourWorld();
            bindFaceVertices = faceMesh.CopyRestVertices();
            if (contourWorld.Length < 3 || bindFaceVertices.Length == 0)
            {
                lastStatus = "The face mesh or bone outer contour is empty.";
                return;
            }

            Vector2[] contour = ConvertContourToFaceLocal(contourWorld);
            Vector2[] contourUvs = ConvertContourToBoneUvs(contourWorld);
            int vertexCount = bindFaceVertices.Length;
            bindBoneAnchors = new Vector2[vertexCount];
            bindBoneAnchorUvs = new Vector2[vertexCount];
            bindFaceBoneUvs = new Vector2[vertexCount];
            bindFaceUvs = faceMesh.CopyNormalizedRestUvs();
            if (bindFaceUvs.Length != vertexCount)
            {
                bindFaceUvs = new Vector2[vertexCount];
            }
            jawWeights = new float[vertexCount];
            rigidLowerLipMasks = new float[vertexCount];
            lowerLipPoseRegionMasks = new float[vertexCount];
            lowerLipCenterFollowMasks = new float[vertexCount];
            upperLipPoseRegionMasks = new float[vertexCount];
            upperLipSideFollowMasks = new float[vertexCount];
            mouthCornerFollowMasks = new float[vertexCount];
            cheekExpansionMasks = new float[vertexCount];
            mandibleAnglePinMasks = new float[vertexCount];
            appliedBoneOffsets = new Vector3[vertexCount];
            rawOffsets = new Vector3[vertexCount];
            smoothOffsets = new Vector3[vertexCount];
            smoothingBuffer = new Vector3[vertexCount];
            IList<Vector2> mouthCurve = facialCurves != null
                ? facialCurves.GetSampledEditableCurve(2)
                : null;
            BuildNeighbourGraph();
            int weightedCount = 0;

            for (int index = 0; index < vertexCount; index++)
            {
                Vector2 facePoint = bindFaceVertices[index];
                mouthCornerFollowMasks[index] = EvaluateMouthCornerFollowMask(
                    bindFaceUvs[index],
                    mouthCurve);
                cheekExpansionMasks[index] = EvaluateCheekExpansionMask(
                    bindFaceUvs[index]);
                if (!TryClosestPointOnAttachmentContour(
                        facePoint,
                        contour,
                        contourUvs,
                        out Vector2 anchor,
                        out Vector2 anchorUv))
                {
                    continue;
                }

                bindBoneAnchors[index] = anchor;
                bindBoneAnchorUvs[index] = anchorUv;

                Vector3 worldPoint = faceMesh.transform.TransformPoint(bindFaceVertices[index]);
                Vector3 boneLocalPoint = jawBone.transform.InverseTransformPoint(worldPoint);
                Vector2 normalizedPoint = jawBone.LocalToNormalizedUnchecked(boneLocalPoint);
                bindFaceBoneUvs[index] = normalizedPoint;
                mandibleAnglePinMasks[index] = jawBone.EvaluateMandibleAnglePin(
                    normalizedPoint);
                lowerLipPoseRegionMasks[index] = EvaluateLowerLipPoseRegionMask(
                    index,
                    bindFaceUvs[index],
                    mouthCurve);
                lowerLipCenterFollowMasks[index] = EvaluateLowerLipCenterFollow(
                    bindFaceUvs[index],
                    mouthCurve);
                upperLipPoseRegionMasks[index] = EvaluateUpperLipPoseRegionMask(
                    index,
                    bindFaceUvs[index],
                    mouthCurve);
                upperLipSideFollowMasks[index] = EvaluateUpperLipSideFollow(
                    bindFaceUvs[index],
                    mouthCurve);
                rigidLowerLipMasks[index] = EvaluateRigidLowerLipMask(
                    index,
                    bindFaceUvs[index],
                    mouthCurve);
                float normalizedY = normalizedPoint.y;
                float verticalWeight = EvaluateJawBand(normalizedY);
                float distance = Vector2.Distance(facePoint, anchor);
                float distanceWeight = Mathf.SmoothStep(
                    0f,
                    1f,
                    1f - Mathf.Clamp01(distance / influenceRadius));
                float chinWeight = EvaluateEllipticalWeight(
                    normalizedPoint,
                    chinInfluenceCenterUv,
                    chinInfluenceRadiusUv) * chinInfluenceStrength;
                float edgeAttachment = Mathf.Max(verticalWeight, chinWeight)
                    * distanceWeight
                    * EvaluateOralAttachmentMultiplier(normalizedPoint);
                if (IsUpperLipRegion(index, bindFaceUvs[index], mouthCurve))
                {
                    edgeAttachment = 0f;
                }

                float lipAttachment = EvaluateEllipticalWeight(
                    normalizedPoint,
                    lipFollowCenterUv,
                    lipFollowRadiusUv)
                    * EvaluateLowerLipSide(index, bindFaceUvs[index], mouthCurve)
                    * lipFollowStrength;
                float weight = Mathf.Clamp01(Mathf.Max(edgeAttachment, lipAttachment));
                jawWeights[index] = weight >= minimumUsefulWeight ? weight : 0f;
                if (jawWeights[index] > 0f)
                {
                    weightedCount++;
                }
            }

            bound = true;
            lastStatus = "Bound " + weightedCount + " / " + vertexCount
                + " muscle vertices to jaw angles, chin and lips; tooth/gingiva contour "
                + "segments were excluded.";
            Debug.Log(lastStatus, this);
        }

        public void ApplyCurrentBoneShape()
        {
            ApplyCurrentBoneShape(false);
        }

        private void ApplyCurrentBoneShape(bool followBoundMandiblePose)
        {
            if (!bound || faceMesh == null || jawBone == null)
            {
                BindRestPose();
                if (!bound)
                {
                    return;
                }
            }

            Vector3[] contourWorld = jawBone.CopyLargestContourWorld();
            if (contourWorld.Length < 3)
            {
                lastStatus = "Bone change ignored: no retained outer contour.";
                return;
            }

            Vector2[] currentContour = ConvertContourToFaceLocal(contourWorld);
            Vector2[] currentContourUvs = ConvertContourToBoneUvs(contourWorld);
            for (int index = 0; index < bindFaceVertices.Length; index++)
            {
                bool hasRigidLipConstraint = followBoundMandiblePose
                    && index < rigidLowerLipMasks.Length
                    && rigidLowerLipMasks[index] > 0f;
                bool hasUpperLipConstraint = followBoundMandiblePose
                    && index < upperLipPoseRegionMasks.Length
                    && upperLipPoseRegionMasks[index] > 0f;
                bool hasCheekConstraint = followBoundMandiblePose
                    && index < cheekExpansionMasks.Length
                    && cheekExpansionMasks[index] > 0f;
                if (jawWeights[index] <= 0f
                    && !hasRigidLipConstraint
                    && !hasUpperLipConstraint
                    && !hasCheekConstraint)
                {
                    rawOffsets[index] = Vector3.zero;
                    continue;
                }

                Vector2 expectedAnchor = faceMesh.transform.InverseTransformPoint(
                    jawBone.NormalizedToWorldUnchecked(bindBoneAnchorUvs[index]));
                Vector2 newAnchor = expectedAnchor;
                if (!followBoundMandiblePose)
                {
                    if (TryClosestPointOnAttachmentContour(
                            expectedAnchor,
                            currentContour,
                            currentContourUvs,
                            out Vector2 contourAnchor,
                            out _))
                    {
                        newAnchor = contourAnchor;
                    }
                }

                Vector2 anchorDelta = Vector2.ClampMagnitude(
                    newAnchor - bindBoneAnchors[index],
                    maximumDisplacement);
                Vector3 anchorOffset = anchorDelta * (jawWeights[index] * response);
                if (followBoundMandiblePose)
                {
                    anchorOffset = ApplyLowerLipCenterFollow(index, anchorOffset);
                }
                rawOffsets[index] = followBoundMandiblePose
                    ? BlendUpperLipSideOffset(
                        index,
                        BlendRigidLowerLipOffset(index, anchorOffset))
                    : anchorOffset;
            }

            // A topology operation may change mechanical adjacency. A pose-only drag keeps
            // the existing graph and uses a short smoothing pass so jaw dragging stays live.
            if (!followBoundMandiblePose)
            {
                BuildNeighbourGraph();
            }

            SmoothOffsets(followBoundMandiblePose
                ? Mathf.Min(smoothingIterations, 6)
                : smoothingIterations);
            if (followBoundMandiblePose)
            {
                ReinforceRigidLowerLipBand();
                ReinforceMandibleAnglePins();
                ReinforceLowerLipCornerRelease();
                ReinforceUpperLipSideFollow();
                ReinforceMouthCornerFollow();
                ReinforceCheekExpansion();
            }
            Vector3[] currentVertices = faceMesh.CopyCurrentVertices();
            Vector3[] result = new Vector3[currentVertices.Length];
            for (int index = 0; index < result.Length; index++)
            {
                Vector3 manualOffset = preserveManualFaceOffsets
                    ? currentVertices[index] - bindFaceVertices[index] - appliedBoneOffsets[index]
                    : Vector3.zero;
                result[index] = bindFaceVertices[index] + manualOffset + smoothOffsets[index];
                appliedBoneOffsets[index] = smoothOffsets[index];
            }

            faceMesh.SetLogicalVertices(result);
            lastStatus = followBoundMandiblePose
                ? "Mandible drag stretched the edge-attached muscle vertices."
                : "Mandible edge revision transferred to the attached muscle vertices.";
        }

        public void ResetFaceToBoneShape()
        {
            if (!bound || faceMesh == null)
            {
                return;
            }

            Vector3[] result = new Vector3[bindFaceVertices.Length];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = bindFaceVertices[index] + appliedBoneOffsets[index];
            }

            faceMesh.SetLogicalVertices(result);
        }

        private void Subscribe()
        {
            if (jawBone != null)
            {
                jawBone.TopologyChanged -= HandleBoneTopologyChanged;
                jawBone.TopologyChanged += HandleBoneTopologyChanged;
                jawBone.PoseChanged -= HandleBonePoseChanged;
                jawBone.PoseChanged += HandleBonePoseChanged;
            }

            if (faceMesh != null)
            {
                faceMesh.MeshRebuilt -= HandleFaceMeshRebuilt;
                faceMesh.MeshRebuilt += HandleFaceMeshRebuilt;
                faceMesh.RestPoseChanged -= HandleFaceRestPoseChanged;
                faceMesh.RestPoseChanged += HandleFaceRestPoseChanged;
            }
        }

        private void Unsubscribe()
        {
            if (jawBone != null)
            {
                jawBone.TopologyChanged -= HandleBoneTopologyChanged;
                jawBone.PoseChanged -= HandleBonePoseChanged;
            }

            if (faceMesh != null)
            {
                faceMesh.MeshRebuilt -= HandleFaceMeshRebuilt;
                faceMesh.RestPoseChanged -= HandleFaceRestPoseChanged;
            }
        }

        private void HandleBoneTopologyChanged()
        {
            if (Application.isPlaying)
            {
                ApplyCurrentBoneShape();
            }
        }

        private void HandleBonePoseChanged()
        {
            if (Application.isPlaying)
            {
                ApplyCurrentBoneShape(true);
            }
        }

        private void HandleFaceMeshRebuilt()
        {
            if (Application.isPlaying)
            {
                BindRestPose();
            }
        }

        private void HandleFaceRestPoseChanged()
        {
            if (Application.isPlaying)
            {
                BindRestPose();
            }
        }

        private Vector2[] ConvertContourToFaceLocal(Vector3[] worldContour)
        {
            Vector2[] result = new Vector2[worldContour.Length];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = faceMesh.transform.InverseTransformPoint(worldContour[index]);
            }

            return result;
        }

        private Vector2[] ConvertContourToBoneUvs(Vector3[] worldContour)
        {
            Vector2[] result = new Vector2[worldContour.Length];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = jawBone.WorldToNormalizedUnchecked(worldContour[index]);
            }

            return result;
        }

        private float EvaluateJawBand(float normalizedY)
        {
            if (normalizedY <= jawRegionBottom || normalizedY >= jawRegionTop)
            {
                return 0f;
            }

            if (normalizedY <= jawRegionPeak)
            {
                return Mathf.SmoothStep(
                    0f,
                    1f,
                    Mathf.InverseLerp(jawRegionBottom, jawRegionPeak, normalizedY));
            }

            return Mathf.SmoothStep(
                0f,
                1f,
                1f - Mathf.InverseLerp(jawRegionPeak, jawRegionTop, normalizedY));
        }

        private float EvaluateOralAttachmentMultiplier(Vector2 normalizedPoint)
        {
            Vector2 radius = new Vector2(
                Mathf.Max(0.001f, oralExclusionRadiusUv.x),
                Mathf.Max(0.001f, oralExclusionRadiusUv.y));
            Vector2 delta = new Vector2(
                (normalizedPoint.x - oralExclusionCenterUv.x) / radius.x,
                (normalizedPoint.y - oralExclusionCenterUv.y) / radius.y);
            float ellipticalDistance = delta.magnitude;
            if (ellipticalDistance <= 1f)
            {
                return 0f;
            }

            if (oralExclusionFeather <= 0.0001f)
            {
                return 1f;
            }

            return Mathf.SmoothStep(
                0f,
                1f,
                Mathf.InverseLerp(
                    1f,
                    1f + oralExclusionFeather,
                    ellipticalDistance));
        }

        private static Vector2 ClampRadius(Vector2 radius)
        {
            return new Vector2(
                Mathf.Max(0.001f, radius.x),
                Mathf.Max(0.001f, radius.y));
        }

        private static float EvaluateEllipticalWeight(
            Vector2 normalizedPoint,
            Vector2 center,
            Vector2 radius)
        {
            radius = ClampRadius(radius);
            Vector2 delta = new Vector2(
                (normalizedPoint.x - center.x) / radius.x,
                (normalizedPoint.y - center.y) / radius.y);
            float distance = delta.magnitude;
            return distance < 1f
                ? 1f - Mathf.SmoothStep(0f, 1f, distance)
                : 0f;
        }

        private float EvaluateCheekExpansionMask(Vector2 faceUv)
        {
            float left = EvaluateEllipticalWeight(
                faceUv,
                cheekExpansionCenterUv,
                cheekExpansionRadiusUv);
            Vector2 mirroredCenter = new Vector2(
                1f - cheekExpansionCenterUv.x,
                cheekExpansionCenterUv.y);
            float right = EvaluateEllipticalWeight(
                faceUv,
                mirroredCenter,
                cheekExpansionRadiusUv);
            return Mathf.Max(left, right);
        }

        private float EvaluateMouthCornerFollowMask(
            Vector2 faceUv,
            IList<Vector2> mouthCurve)
        {
            GetCurveHorizontalRange(mouthCurve, out float minimumX, out float maximumX);
            Vector2 leftCenter = new Vector2(
                minimumX,
                EvaluateCurveYAtX(mouthCurve, minimumX, 0.337f));
            Vector2 rightCenter = new Vector2(
                maximumX,
                EvaluateCurveYAtX(mouthCurve, maximumX, 0.337f));
            float left = EvaluateEllipticalWeight(
                faceUv,
                leftCenter,
                mouthCornerFollowRadiusUv);
            float right = EvaluateEllipticalWeight(
                faceUv,
                rightCenter,
                mouthCornerFollowRadiusUv);

            // The base ellipses keep the authored mouth-cut endpoints and nearby
            // lower-lip tissue together. Add a separate upward lobe so the part
            // of the upper lip immediately beside each corner also follows the
            // mandible. Keeping this as a second smooth ellipse avoids widening
            // the lower influence or creating a hard horizontal boundary.
            Vector2 upperRadius = new Vector2(
                mouthCornerFollowRadiusUv.x,
                mouthCornerUpperLipReachUv);
            float upperCenterLift = mouthCornerUpperLipReachUv * 0.35f;
            float upperLeft = faceUv.y >= leftCenter.y
                ? EvaluateEllipticalWeight(
                    faceUv,
                    leftCenter + Vector2.up * upperCenterLift,
                    upperRadius)
                : 0f;
            float upperRight = faceUv.y >= rightCenter.y
                ? EvaluateEllipticalWeight(
                    faceUv,
                    rightCenter + Vector2.up * upperCenterLift,
                    upperRadius)
                : 0f;
            return Mathf.Max(
                Mathf.Max(left, right),
                Mathf.Max(upperLeft, upperRight));
        }

        private void RefreshMouthCornerFollowMasks()
        {
            if (bindFaceUvs == null || bindFaceUvs.Length == 0)
            {
                return;
            }

            if (mouthCornerFollowMasks == null
                || mouthCornerFollowMasks.Length != bindFaceUvs.Length)
            {
                mouthCornerFollowMasks = new float[bindFaceUvs.Length];
            }

            IList<Vector2> mouthCurve = facialCurves != null
                ? facialCurves.GetSampledEditableCurve(2)
                : null;
            for (int index = 0; index < bindFaceUvs.Length; index++)
            {
                mouthCornerFollowMasks[index] = EvaluateMouthCornerFollowMask(
                    bindFaceUvs[index],
                    mouthCurve);
            }
        }

        private float EvaluateLowerLipSide(
            int vertexIndex,
            Vector2 faceUv,
            IList<Vector2> mouthCurve)
        {
            if (!IsLowerSideOfMouthCut(vertexIndex, faceUv, mouthCurve))
            {
                return 0f;
            }

            float seamY = EvaluateCurveYAtX(mouthCurve, faceUv.x, 0.337f);
            float depthBelowIncision = Mathf.Max(0f, seamY - faceUv.y);
            return Mathf.SmoothStep(
                lowerLipInitialFollow,
                1f,
                Mathf.Clamp01(depthBelowIncision
                    / Mathf.Max(0.005f, lowerLipFullFollowDepthUv)));
        }

        private float EvaluateRigidLowerLipMask(
            int vertexIndex,
            Vector2 faceUv,
            IList<Vector2> mouthCurve)
        {
            if (!IsLowerSideOfMouthCut(vertexIndex, faceUv, mouthCurve))
            {
                return 0f;
            }

            float seamY = EvaluateCurveYAtX(mouthCurve, faceUv.x, 0.337f);
            float depthBelowIncision = Mathf.Max(0f, seamY - faceUv.y);
            float horizontalMask = EvaluateLowerLipCenterFollow(faceUv, mouthCurve);
            if (horizontalMask <= 0f)
            {
                return 0f;
            }

            float verticalMask = depthBelowIncision <= lowerLipRigidBandDepthUv
                ? 1f
                : 1f - Mathf.SmoothStep(
                    0f,
                    1f,
                    Mathf.Clamp01((depthBelowIncision - lowerLipRigidBandDepthUv)
                        / Mathf.Max(0.001f, lowerLipRigidTransitionDepthUv)));
            return horizontalMask * verticalMask;
        }

        private float EvaluateLowerLipPoseRegionMask(
            int vertexIndex,
            Vector2 faceUv,
            IList<Vector2> mouthCurve)
        {
            if (!IsLowerSideOfMouthCut(vertexIndex, faceUv, mouthCurve))
            {
                return 0f;
            }

            GetCurveHorizontalRange(mouthCurve, out float minimumX, out float maximumX);
            if (faceUv.x < minimumX || faceUv.x > maximumX)
            {
                return 0f;
            }

            float seamY = EvaluateCurveYAtX(mouthCurve, faceUv.x, 0.337f);
            float depthBelowIncision = Mathf.Max(0f, seamY - faceUv.y);
            float maximumDepth = lowerLipRigidBandDepthUv
                + lowerLipRigidTransitionDepthUv;
            if (depthBelowIncision >= maximumDepth)
            {
                return 0f;
            }

            return depthBelowIncision <= lowerLipRigidBandDepthUv
                ? 1f
                : 1f - Mathf.SmoothStep(
                    0f,
                    1f,
                    Mathf.InverseLerp(
                        lowerLipRigidBandDepthUv,
                        maximumDepth,
                        depthBelowIncision));
        }

        private float EvaluateLowerLipCenterFollow(
            Vector2 faceUv,
            IList<Vector2> mouthCurve)
        {
            GetCurveHorizontalRange(mouthCurve, out float minimumX, out float maximumX);
            float width = Mathf.Max(0.001f, maximumX - minimumX);
            float across = (faceUv.x - minimumX) / width;
            if (across < 0f || across > 1f)
            {
                return 0f;
            }

            float distanceFromCenter = Mathf.Abs(across - 0.5f) * 2f;
            float ovalProfile = lowerLipHorizontalFalloff != null
                && lowerLipHorizontalFalloff.length >= 2
                ? lowerLipHorizontalFalloff.Evaluate(distanceFromCenter)
                : Mathf.Cos(distanceFromCenter * Mathf.PI * 0.5f);
            return Mathf.Lerp(
                lowerLipCornerMinimumFollow,
                1f,
                Mathf.Clamp01(ovalProfile));
        }

        private float EvaluateUpperLipPoseRegionMask(
            int vertexIndex,
            Vector2 faceUv,
            IList<Vector2> mouthCurve)
        {
            if (IsLowerSideOfMouthCut(vertexIndex, faceUv, mouthCurve))
            {
                return 0f;
            }

            GetCurveHorizontalRange(mouthCurve, out float minimumX, out float maximumX);
            if (faceUv.x < minimumX || faceUv.x > maximumX)
            {
                return 0f;
            }

            float seamY = EvaluateCurveYAtX(mouthCurve, faceUv.x, 0.337f);
            float heightAboveIncision = faceUv.y - seamY;
            if (heightAboveIncision < -0.0015f
                || heightAboveIncision >= upperLipFollowHeightUv)
            {
                return 0f;
            }

            return 1f - Mathf.SmoothStep(
                0f,
                1f,
                Mathf.Clamp01(heightAboveIncision / upperLipFollowHeightUv));
        }

        private float EvaluateUpperLipSideFollow(
            Vector2 faceUv,
            IList<Vector2> mouthCurve)
        {
            GetCurveHorizontalRange(mouthCurve, out float minimumX, out float maximumX);
            float width = Mathf.Max(0.001f, maximumX - minimumX);
            float across = (faceUv.x - minimumX) / width;
            if (across < 0f || across > 1f)
            {
                return 0f;
            }

            float distanceFromCenter = Mathf.Abs(across - 0.5f) * 2f;
            if (distanceFromCenter <= upperLipPearlProtection)
            {
                return 0f;
            }

            float remappedDistance = Mathf.InverseLerp(
                upperLipPearlProtection,
                1f,
                distanceFromCenter);
            float lowerLipProfile = lowerLipHorizontalFalloff != null
                && lowerLipHorizontalFalloff.length >= 2
                ? lowerLipHorizontalFalloff.Evaluate(remappedDistance)
                : Mathf.Cos(remappedDistance * Mathf.PI * 0.5f);
            return 1f - Mathf.Clamp01(lowerLipProfile);
        }

        private static AnimationCurve CreateDefaultLowerLipHorizontalFalloff()
        {
            AnimationCurve curve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.25f, 0.95f),
                new Keyframe(0.5f, 0.78f),
                new Keyframe(0.75f, 0.5f),
                new Keyframe(1f, 0f));
            for (int index = 0; index < curve.length; index++)
            {
                curve.SmoothTangents(index, 0f);
            }

            return curve;
        }

        private bool IsLowerSideOfMouthCut(
            int vertexIndex,
            Vector2 faceUv,
            IList<Vector2> mouthCurve)
        {
            GetCurveHorizontalRange(mouthCurve, out float minimumX, out float maximumX);
            const float horizontalMargin = 0.03f;
            if (faceUv.x < minimumX - horizontalMargin
                || faceUv.x > maximumX + horizontalMargin)
            {
                return false;
            }

            float signedDistance = faceUv.y
                - EvaluateCurveYAtX(mouthCurve, faceUv.x, 0.337f);
            const float seamTolerance = 0.0015f;
            if (signedDistance < -seamTolerance)
            {
                return true;
            }

            if (signedDistance > seamTolerance
                || vertexIndex < 0
                || vertexIndex >= neighbours.Length)
            {
                return false;
            }

            // An open cut contains two vertices at the same curve position. Their
            // connected triangles reveal which one belongs to the lower lip. This is
            // deliberately topology-based; a coordinate-only test cannot distinguish
            // the coincident upper and lower incision edges.
            float lowerEvidence = 0f;
            float upperEvidence = 0f;
            List<int> connected = neighbours[vertexIndex];
            for (int neighbourIndex = 0; neighbourIndex < connected.Count; neighbourIndex++)
            {
                int neighbour = connected[neighbourIndex];
                if (neighbour < 0 || neighbour >= bindFaceUvs.Length)
                {
                    continue;
                }

                Vector2 neighbourUv = bindFaceUvs[neighbour];
                float neighbourSide = neighbourUv.y
                    - EvaluateCurveYAtX(mouthCurve, neighbourUv.x, 0.337f);
                if (neighbourSide < -seamTolerance)
                {
                    lowerEvidence += -neighbourSide;
                }
                else if (neighbourSide > seamTolerance)
                {
                    upperEvidence += neighbourSide;
                }
            }

            return lowerEvidence > upperEvidence && lowerEvidence > 0f;
        }

        private Vector3 BlendRigidLowerLipOffset(
            int vertexIndex,
            Vector3 anchorOffset)
        {
            if (vertexIndex < 0 || vertexIndex >= rigidLowerLipMasks.Length)
            {
                return anchorOffset;
            }

            float mask = rigidLowerLipMasks[vertexIndex];
            if (mask <= 0f)
            {
                return anchorOffset;
            }

            Vector3 rigidOffset = CalculateRigidLowerLipOffset();
            return Vector3.Lerp(anchorOffset, rigidOffset, mask);
        }

        private Vector3 BlendUpperLipSideOffset(
            int vertexIndex,
            Vector3 anchorOffset)
        {
            if (vertexIndex < 0
                || vertexIndex >= upperLipPoseRegionMasks.Length
                || vertexIndex >= upperLipSideFollowMasks.Length)
            {
                return anchorOffset;
            }

            float region = upperLipPoseRegionMasks[vertexIndex];
            if (region <= 0f)
            {
                return anchorOffset;
            }

            return Vector3.Lerp(
                anchorOffset,
                CalculateUpperLipSideOffset(vertexIndex),
                region);
        }

        private Vector3 ApplyLowerLipCenterFollow(
            int vertexIndex,
            Vector3 anchorOffset)
        {
            if (vertexIndex < 0
                || vertexIndex >= lowerLipPoseRegionMasks.Length
                || vertexIndex >= lowerLipCenterFollowMasks.Length)
            {
                return anchorOffset;
            }

            float region = lowerLipPoseRegionMasks[vertexIndex];
            if (region <= 0f)
            {
                return anchorOffset;
            }

            float centerFollow = lowerLipCenterFollowMasks[vertexIndex];
            return Vector3.Lerp(
                anchorOffset,
                anchorOffset * centerFollow,
                region);
        }

        private void ReinforceRigidLowerLipBand()
        {
            Vector3 rigidOffset = CalculateRigidLowerLipOffset();
            int count = Mathf.Min(smoothOffsets.Length, rigidLowerLipMasks.Length);
            for (int index = 0; index < count; index++)
            {
                float mask = rigidLowerLipMasks[index];
                if (mask <= 0f)
                {
                    continue;
                }

                // The authored mouth-cut lower edge is a shape-preserving band.
                // Reapplying it after smoothing prevents the center from becoming
                // a long triangle while still feathering into the chin below.
                smoothOffsets[index] = Vector3.Lerp(
                    smoothOffsets[index],
                    rigidOffset,
                    mask);
            }
        }

        private void ReinforceMandibleAnglePins()
        {
            int count = Mathf.Min(smoothOffsets.Length, mandibleAnglePinMasks.Length);
            for (int index = 0; index < count; index++)
            {
                float pin = mandibleAnglePinMasks[index];
                if (pin <= 0f)
                {
                    continue;
                }

                // Restore the authored small angle-follow target after smoothing so
                // chin motion cannot drag the angle farther than requested.
                smoothOffsets[index] = Vector3.Lerp(
                    smoothOffsets[index],
                    rawOffsets[index],
                    pin);
            }
        }

        private void ReinforceLowerLipCornerRelease()
        {
            int count = Mathf.Min(
                smoothOffsets.Length,
                Mathf.Min(
                    lowerLipPoseRegionMasks.Length,
                    lowerLipCenterFollowMasks.Length));
            for (int index = 0; index < count; index++)
            {
                float region = lowerLipPoseRegionMasks[index];
                if (region <= 0f)
                {
                    continue;
                }

                // The mandible opens the lower-lip center. The cut endpoints are
                // released from vertical jaw travel so both mouth corners stay put.
                float centerFollow = lowerLipCenterFollowMasks[index];
                smoothOffsets[index] = Vector3.Lerp(
                    smoothOffsets[index],
                    smoothOffsets[index] * centerFollow,
                    region);
            }
        }

        private void ReinforceUpperLipSideFollow()
        {
            int count = Mathf.Min(
                smoothOffsets.Length,
                Mathf.Min(
                    upperLipPoseRegionMasks.Length,
                    upperLipSideFollowMasks.Length));
            for (int index = 0; index < count; index++)
            {
                float region = upperLipPoseRegionMasks[index];
                if (region <= 0f)
                {
                    continue;
                }

                // The Cupid's bow remains independent while the upper-lip sides
                // increasingly share the mandible opening toward both corners.
                smoothOffsets[index] = Vector3.Lerp(
                    smoothOffsets[index],
                    CalculateUpperLipSideOffset(index),
                    region);
            }
        }

        private void ReinforceCheekExpansion()
        {
            int count = Mathf.Min(smoothOffsets.Length, cheekExpansionMasks.Length);
            for (int index = 0; index < count; index++)
            {
                float mask = cheekExpansionMasks[index];
                if (mask <= 0f)
                {
                    continue;
                }

                float outwardDirection = bindFaceUvs[index].x < 0.5f ? -1f : 1f;
                Vector3 worldOffset = jawBone.transform.TransformVector(
                    Vector3.right
                    * (outwardDirection
                        * jawBone.MandibleOpeningUv
                        * jawBone.Height
                        * cheekExpansionFollow
                        * mask));
                smoothOffsets[index] += faceMesh.transform.InverseTransformVector(
                    worldOffset);
            }
        }

        private void ReinforceMouthCornerFollow()
        {
            int count = Mathf.Min(smoothOffsets.Length, mouthCornerFollowMasks.Length);
            float followStrength = jawBone != null
                ? jawBone.MandibleMouthCornerFollow
                : mouthCornerFollow;
            Vector3 downwardAxis = faceMesh.transform.InverseTransformVector(
                jawBone.transform.TransformVector(Vector3.down)).normalized;
            for (int index = 0; index < count; index++)
            {
                float mask = mouthCornerFollowMasks[index];
                if (mask <= 0f)
                {
                    continue;
                }

                Vector3 worldTarget = jawBone.transform.TransformVector(
                    Vector3.down
                    * (jawBone.MandibleOpeningUv
                        * jawBone.Height
                        * followStrength
                        * mask));
                Vector3 localTarget = faceMesh.transform.InverseTransformVector(
                    worldTarget);
                float currentDownward = Vector3.Dot(
                    smoothOffsets[index],
                    downwardAxis);
                float targetDownward = Vector3.Dot(localTarget, downwardAxis);
                if (currentDownward < targetDownward)
                {
                    smoothOffsets[index] += downwardAxis
                        * (targetDownward - currentDownward);
                }
            }
        }

        private Vector3 CalculateRigidLowerLipOffset()
        {
            Vector3 worldOffset = jawBone.transform.TransformVector(
                Vector3.down
                * (jawBone.MandibleOpeningUv
                    * jawBone.Height
                    * lowerLipRigidFollow));
            return faceMesh.transform.InverseTransformVector(worldOffset);
        }

        private Vector3 CalculateUpperLipSideOffset(int vertexIndex)
        {
            float sideFollow = vertexIndex >= 0
                && vertexIndex < upperLipSideFollowMasks.Length
                ? upperLipSideFollowMasks[vertexIndex]
                : 0f;
            Vector3 worldOffset = jawBone.transform.TransformVector(
                Vector3.down
                * (jawBone.MandibleOpeningUv
                    * jawBone.Height
                    * upperLipCornerFollow
                    * sideFollow));
            return faceMesh.transform.InverseTransformVector(worldOffset);
        }

        private static void GetCurveHorizontalRange(
            IList<Vector2> curve,
            out float minimumX,
            out float maximumX)
        {
            if (curve == null || curve.Count < 2)
            {
                minimumX = 0.39f;
                maximumX = 0.64f;
                return;
            }

            minimumX = curve[0].x;
            maximumX = curve[0].x;
            for (int index = 1; index < curve.Count; index++)
            {
                minimumX = Mathf.Min(minimumX, curve[index].x);
                maximumX = Mathf.Max(maximumX, curve[index].x);
            }
        }

        private bool IsUpperLipRegion(
            int vertexIndex,
            Vector2 faceUv,
            IList<Vector2> mouthCurve)
        {
            if (mouthCurve == null || mouthCurve.Count < 2)
            {
                return faceUv.x >= 0.39f
                    && faceUv.x <= 0.64f
                    && faceUv.y >= 0.337f
                    && faceUv.y <= 0.407f;
            }

            float minimumX = mouthCurve[0].x;
            float maximumX = mouthCurve[0].x;
            for (int index = 1; index < mouthCurve.Count; index++)
            {
                minimumX = Mathf.Min(minimumX, mouthCurve[index].x);
                maximumX = Mathf.Max(maximumX, mouthCurve[index].x);
            }

            const float horizontalMargin = 0.025f;
            if (faceUv.x < minimumX - horizontalMargin
                || faceUv.x > maximumX + horizontalMargin)
            {
                return false;
            }

            if (IsLowerSideOfMouthCut(vertexIndex, faceUv, mouthCurve))
            {
                return false;
            }

            float seamY = EvaluateCurveYAtX(mouthCurve, faceUv.x, 0.337f);
            return faceUv.y >= seamY - 0.0015f
                && faceUv.y <= seamY + lipFollowRadiusUv.y;
        }

        private static float EvaluateCurveYAtX(
            IList<Vector2> curve,
            float x,
            float fallbackY)
        {
            if (curve == null || curve.Count == 0)
            {
                return fallbackY;
            }

            if (curve.Count == 1)
            {
                return curve[0].y;
            }

            float nearestDistance = float.PositiveInfinity;
            float nearestY = curve[0].y;
            for (int index = 0; index < curve.Count - 1; index++)
            {
                Vector2 start = curve[index];
                Vector2 end = curve[index + 1];
                float minimumX = Mathf.Min(start.x, end.x);
                float maximumX = Mathf.Max(start.x, end.x);
                if (x >= minimumX && x <= maximumX)
                {
                    float span = end.x - start.x;
                    float t = Mathf.Abs(span) > 0.00001f
                        ? Mathf.Clamp01((x - start.x) / span)
                        : 0.5f;
                    return Mathf.Lerp(start.y, end.y, t);
                }

                float startDistance = Mathf.Abs(x - start.x);
                if (startDistance < nearestDistance)
                {
                    nearestDistance = startDistance;
                    nearestY = start.y;
                }
            }

            Vector2 last = curve[curve.Count - 1];
            return Mathf.Abs(x - last.x) < nearestDistance ? last.y : nearestY;
        }

        private bool TryClosestPointOnAttachmentContour(
            Vector2 point,
            Vector2[] contour,
            Vector2[] contourUvs,
            out Vector2 closest,
            out Vector2 closestUv)
        {
            closest = point;
            closestUv = Vector2.zero;
            if (contour == null || contourUvs == null
                || contour.Length < 2 || contour.Length != contourUvs.Length)
            {
                return false;
            }

            bool found = false;
            float closestDistanceSquared = float.PositiveInfinity;
            int previous = contour.Length - 1;
            for (int current = 0; current < contour.Length; current++)
            {
                Vector2 segmentMidUv = (contourUvs[previous] + contourUvs[current]) * 0.5f;
                if (EvaluateOralAttachmentMultiplier(segmentMidUv) <= 0f)
                {
                    previous = current;
                    continue;
                }

                Vector2 candidate = ClosestPointOnSegment(
                    point,
                    contour[previous],
                    contour[current],
                    out float factor);
                Vector2 candidateUv = Vector2.Lerp(
                    contourUvs[previous],
                    contourUvs[current],
                    factor);
                if (EvaluateOralAttachmentMultiplier(candidateUv) <= 0f)
                {
                    previous = current;
                    continue;
                }

                float distanceSquared = (candidate - point).sqrMagnitude;
                if (distanceSquared < closestDistanceSquared)
                {
                    closest = candidate;
                    closestUv = candidateUv;
                    closestDistanceSquared = distanceSquared;
                    found = true;
                }

                previous = current;
            }

            return found;
        }

        private void BuildNeighbourGraph()
        {
            neighbours = new List<int>[bindFaceVertices.Length];
            for (int index = 0; index < neighbours.Length; index++)
            {
                neighbours[index] = new List<int>(6);
            }

            int[] triangles = faceMesh.CopyMechanicallyIntactTriangles();
            for (int triangle = 0; triangle < triangles.Length; triangle += 3)
            {
                AddNeighbourPair(triangles[triangle], triangles[triangle + 1]);
                AddNeighbourPair(triangles[triangle + 1], triangles[triangle + 2]);
                AddNeighbourPair(triangles[triangle + 2], triangles[triangle]);
            }
        }

        private void AddNeighbourPair(int first, int second)
        {
            if (!neighbours[first].Contains(second))
            {
                neighbours[first].Add(second);
            }

            if (!neighbours[second].Contains(first))
            {
                neighbours[second].Add(first);
            }
        }

        private void SmoothOffsets(int iterations)
        {
            Array.Copy(rawOffsets, smoothOffsets, rawOffsets.Length);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                for (int index = 0; index < smoothOffsets.Length; index++)
                {
                    if (jawWeights[index] <= 0f || neighbours[index].Count == 0)
                    {
                        smoothingBuffer[index] = Vector3.zero;
                        continue;
                    }

                    Vector3 average = Vector3.zero;
                    int contributing = 0;
                    for (int neighbourIndex = 0; neighbourIndex < neighbours[index].Count; neighbourIndex++)
                    {
                        int neighbour = neighbours[index][neighbourIndex];
                        if (jawWeights[neighbour] <= 0f)
                        {
                            continue;
                        }

                        average += smoothOffsets[neighbour];
                        contributing++;
                    }

                    if (contributing == 0)
                    {
                        smoothingBuffer[index] = rawOffsets[index];
                        continue;
                    }

                    average /= contributing;
                    float retainTarget = Mathf.Lerp(0.25f, 0.9f, jawWeights[index]);
                    Vector3 targetPreservingAverage = Vector3.Lerp(average, rawOffsets[index], retainTarget);
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

        private static Vector2 ClosestPointOnClosedContour(Vector2 point, Vector2[] contour)
        {
            Vector2 closest = contour[0];
            float closestDistanceSquared = float.PositiveInfinity;
            Vector2 previous = contour[contour.Length - 1];
            for (int index = 0; index < contour.Length; index++)
            {
                Vector2 current = contour[index];
                Vector2 candidate = ClosestPointOnSegment(point, previous, current);
                float distanceSquared = (candidate - point).sqrMagnitude;
                if (distanceSquared < closestDistanceSquared)
                {
                    closest = candidate;
                    closestDistanceSquared = distanceSquared;
                }

                previous = current;
            }

            return closest;
        }

        private static Vector2 ClosestPointOnSegment(Vector2 point, Vector2 start, Vector2 end)
        {
            return ClosestPointOnSegment(point, start, end, out _);
        }

        private static Vector2 ClosestPointOnSegment(
            Vector2 point,
            Vector2 start,
            Vector2 end,
            out float factor)
        {
            Vector2 segment = end - start;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared < 0.0000001f)
            {
                factor = 0f;
                return start;
            }

            factor = Mathf.Clamp01(Vector2.Dot(point - start, segment) / lengthSquared);
            return start + segment * factor;
        }

        private void OnDrawGizmosSelected()
        {
            if (!showWeightGizmos || !bound || faceMesh == null)
            {
                return;
            }

            Vector3[] vertices = faceMesh.CopyCurrentVertices();
            int count = Mathf.Min(vertices.Length, jawWeights.Length);
            for (int index = 0; index < count; index++)
            {
                float weight = jawWeights[index];
                if (weight <= 0f)
                {
                    continue;
                }

                Gizmos.color = Color.Lerp(new Color(0f, 0.2f, 1f), Color.red, weight);
                Gizmos.DrawSphere(
                    faceMesh.transform.TransformPoint(vertices[index]),
                    gizmoRadius);
            }
        }
    }
}
