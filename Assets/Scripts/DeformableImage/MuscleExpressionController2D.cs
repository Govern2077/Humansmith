using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeformableImage
{
    [Serializable]
    public sealed class FacialMuscleInfluenceZone2D
    {
        [SerializeField] private string label;
        [SerializeField] private Vector2 centerUv;
        [SerializeField] private Vector2 radiusUv = new Vector2(0.1f, 0.1f);
        [SerializeField] private Vector2 displacementUv;
        [SerializeField, Range(0f, 2f)] private float strength = 1f;

        public string Label { get { return label; } }
        public Vector2 CenterUv
        {
            get { return centerUv; }
            set
            {
                centerUv = new Vector2(
                    Mathf.Clamp01(value.x),
                    Mathf.Clamp01(value.y));
            }
        }
        public Vector2 RadiusUv
        {
            get { return radiusUv; }
            set
            {
                radiusUv = new Vector2(
                    Mathf.Max(0.002f, value.x),
                    Mathf.Max(0.002f, value.y));
            }
        }
        public Vector2 DisplacementUv
        {
            get { return displacementUv; }
            set { displacementUv = value; }
        }
        public float Strength
        {
            get { return strength; }
            set { strength = Mathf.Clamp(value, 0f, 2f); }
        }

        public FacialMuscleInfluenceZone2D(
            string zoneLabel,
            Vector2 center,
            Vector2 radius,
            Vector2 displacement,
            float zoneStrength = 1f)
        {
            label = zoneLabel;
            centerUv = center;
            radiusUv = radius;
            displacementUv = displacement;
            strength = zoneStrength;
        }
    }

    [Serializable]
    public sealed class SpeechMouthPose2D
    {
        [SerializeField] private string label;
        [SerializeField] private KeyCode hotkey = KeyCode.None;
        [Tooltip("Normalized vertical mandible travel. Zero keeps the teeth together; larger values open the jaw.")]
        [SerializeField, Range(0f, 0.12f)] private float jawDropUv;
        [Tooltip("How much the left/right mandibular body follows the chin. Keep near zero to pin both sides.")]
        [SerializeField, Range(0f, 0.35f)] private float jawSideFollow;
        [Tooltip("Higher values concentrate jaw opening toward the center of the chin.")]
        [SerializeField, Range(0.5f, 12f)] private float jawChinFocusPower = 2.6f;
        [SerializeField, Range(0f, 1.5f)] private float defaultWeight = 1f;
        [SerializeField] private List<FacialMuscleInfluenceZone2D> muscleZones =
            new List<FacialMuscleInfluenceZone2D>();
        [SerializeField, HideInInspector] private Vector2[] bakedOffsets =
            Array.Empty<Vector2>();

        public string Label { get { return label; } }
        public KeyCode Hotkey { get { return hotkey; } set { hotkey = value; } }
        public float JawDropUv
        {
            get { return jawDropUv; }
            set { jawDropUv = Mathf.Clamp(value, 0f, 0.12f); }
        }
        public float JawSideFollow
        {
            get { return jawSideFollow; }
            set { jawSideFollow = Mathf.Clamp(value, 0f, 0.35f); }
        }
        public float JawChinFocusPower
        {
            get { return jawChinFocusPower; }
            set { jawChinFocusPower = Mathf.Clamp(value, 0.5f, 12f); }
        }
        public float DefaultWeight
        {
            get { return defaultWeight; }
            set { defaultWeight = Mathf.Clamp(value, 0f, 1.5f); }
        }
        public IList<FacialMuscleInfluenceZone2D> MuscleZones { get { return muscleZones; } }
        public Vector2[] BakedOffsets { get { return bakedOffsets; } }

        public SpeechMouthPose2D(
            string poseLabel,
            KeyCode poseHotkey,
            float poseJawDropUv,
            IEnumerable<FacialMuscleInfluenceZone2D> zones,
            float poseJawSideFollow = 0f,
            float poseJawChinFocusPower = 2.6f)
        {
            label = poseLabel;
            hotkey = poseHotkey;
            jawDropUv = poseJawDropUv;
            jawSideFollow = poseJawSideFollow;
            jawChinFocusPower = poseJawChinFocusPower;
            defaultWeight = 1f;
            muscleZones = zones != null
                ? new List<FacialMuscleInfluenceZone2D>(zones)
                : new List<FacialMuscleInfluenceZone2D>();
        }

        public bool HasValidBake(int vertexCount)
        {
            return bakedOffsets != null
                && bakedOffsets.Length == vertexCount
                && bakedOffsets.Length > 0;
        }

        public void SetBakedOffsets(Vector2[] value)
        {
            bakedOffsets = value ?? Array.Empty<Vector2>();
        }
    }

    /// <summary>
    /// Plays editor-baked muscle poses. Runtime switching only interpolates cached vertex
    /// offsets; the existing Muscle -> Fat -> Skin couplers propagate the result outward.
    /// </summary>
    [DefaultExecutionOrder(300)]
    [DisallowMultipleComponent]
    public sealed class MuscleExpressionController2D : MonoBehaviour
    {
        private const int CurrentBakeVersion = 17;
        private const int TargetCurveSamplesPerSpan = 12;

        [Header("Anatomy")]
        [SerializeField] private DeformableImageMesh muscleLayer;
        [SerializeField] private DeformableImageMesh skinLayer;
        [SerializeField] private FacialFeaturePrecut2D facialCurves;

        [Header("Hotkeys")]
        [SerializeField] private KeyCode neutralKey = KeyCode.F1;
        [SerializeField] private KeyCode openEyesKey = KeyCode.F2;
        [SerializeField] private KeyCode smileKey = KeyCode.F3;
        [SerializeField] private KeyCode sadnessKey = KeyCode.F4;
        [SerializeField] private KeyCode angerKey = KeyCode.F5;
        [SerializeField] private KeyCode painKey = KeyCode.F6;
        [SerializeField] private KeyCode randomTwitchKey = KeyCode.F7;
        [SerializeField] private bool enableHotkeys = true;

        [Header("Playback")]
        [SerializeField, Min(0f)] private float transitionDuration = 0.16f;
        [SerializeField] private bool initiallyOpenEyes;
        [SerializeField] private bool initiallySmiling;
        [SerializeField] private bool initiallySad;
        [SerializeField] private bool initiallyAngry;
        [SerializeField] private bool initiallyInPain;
        [SerializeField] private bool showRuntimeStatus = true;

        [Header("Random Muscle Twitch")]
        [SerializeField] private bool initiallyRandomTwitching;
        [SerializeField] private bool showRandomTwitchButton = true;
        [SerializeField, Min(0.03f)] private float randomTwitchMinInterval = 0.08f;
        [SerializeField, Min(0.03f)] private float randomTwitchMaxInterval = 0.38f;
        [SerializeField, Range(0.1f, 1.5f)] private float randomTwitchIntensity = 1f;

        [Header("Open-Eyes Bake Settings")]
        [Tooltip("Default height used only when resetting the editable target curves.")]
        [SerializeField, Range(0.001f, 0.07f)] private float eyelidMuscleLift = 0.034f;
        [Tooltip("How far above the eyelid curve the target displacement smoothly fades to zero.")]
        [SerializeField, Range(0.015f, 0.15f)] private float eyeInfluenceHeight = 0.085f;

        [Header("Editable Open-Eyes Target Curves (Skin UV)")]
        [SerializeField] private FacialCutCurve2D leftOpenEyelidTarget;
        [SerializeField] private FacialCutCurve2D rightOpenEyelidTarget;

        [Header("Smile Bake Settings")]
        [Tooltip("Default corner lift used only when resetting the editable smile curve.")]
        [SerializeField, Range(0.001f, 0.07f)] private float smileCornerLift = 0.026f;
        [Tooltip("Default horizontal widening on each side of the mouth.")]
        [SerializeField, Range(0f, 0.05f)] private float smileWiden = 0.016f;
        [Tooltip("Radius around the mouth curve affected by the smile deformation.")]
        [SerializeField, Range(0.02f, 0.2f)] private float smileInfluenceRadius = 0.105f;

        [Header("Editable Smile Target Curve (Skin UV)")]
        [SerializeField] private FacialCutCurve2D smileTarget;

        [Header("Smile Multi-Muscle Weight Field")]
        [Tooltip("Overlapping facial zones add to the mouth-curve deformation and to other expression channels.")]
        [SerializeField] private List<FacialMuscleInfluenceZone2D> smileMuscleZones =
            new List<FacialMuscleInfluenceZone2D>();

        [Header("Sadness Full-Face Weight Field")]
        [SerializeField] private List<FacialMuscleInfluenceZone2D> sadnessMuscleZones =
            new List<FacialMuscleInfluenceZone2D>();

        [Header("Anger Full-Face Weight Field")]
        [SerializeField] private List<FacialMuscleInfluenceZone2D> angerMuscleZones =
            new List<FacialMuscleInfluenceZone2D>();

        [Header("Pain Full-Face Weight Field")]
        [SerializeField] private List<FacialMuscleInfluenceZone2D> painMuscleZones =
            new List<FacialMuscleInfluenceZone2D>();

        [Header("Speech Mouth Poses (High-Priority Mouth Channels)")]
        [Tooltip("The five speech poses are mutually exclusive, while Eye/Smile/Emotion channels remain additive.")]
        [SerializeField] private List<SpeechMouthPose2D> speechMouthPoses =
            new List<SpeechMouthPose2D>();
        [SerializeField] private int initiallySpeechPose = -1;

        [Header("Nose Shape Preservation")]
        [Tooltip("Center of the nose region whose vertices should move almost rigidly instead of changing the nose size.")]
        [SerializeField] private Vector2 noseProtectionCenterUv = new Vector2(0.5f, 0.515f);
        [Tooltip("XY radius of the softly blended nose shape-preservation region.")]
        [SerializeField] private Vector2 noseProtectionRadiusUv = new Vector2(0.12f, 0.17f);
        [Tooltip("0 allows normal deformation; 1 makes the nose core use one shared translation.")]
        [SerializeField, Range(0f, 1f)] private float noseShapeProtection = 0.88f;

        [Header("Editor-Baked Pose")]
        [SerializeField, HideInInspector] private Vector2[] bakedOpenEyesOffsets =
            Array.Empty<Vector2>();
        [SerializeField, HideInInspector] private Vector2[] bakedSmileOffsets =
            Array.Empty<Vector2>();
        [SerializeField, HideInInspector] private Vector2[] bakedSadnessOffsets =
            Array.Empty<Vector2>();
        [SerializeField, HideInInspector] private Vector2[] bakedAngerOffsets =
            Array.Empty<Vector2>();
        [SerializeField, HideInInspector] private Vector2[] bakedPainOffsets =
            Array.Empty<Vector2>();
        [SerializeField, HideInInspector] private int bakedCurveHash;
        [SerializeField, HideInInspector] private int bakedVersion;
        [SerializeField, HideInInspector] private string bakeStatus = "Facial expression poses are not baked.";

        private Vector3[] neutralMuscleVertices = Array.Empty<Vector3>();
        private Vector3[] transitionStartVertices = Array.Empty<Vector3>();
        private Vector3[] transitionTargetVertices = Array.Empty<Vector3>();
        private Vector3[] transitionWorkingVertices = Array.Empty<Vector3>();
        private Vector3[] boneWorkingOffsets = Array.Empty<Vector3>();
        private Vector3[] boneTransitionStartOffsets = Array.Empty<Vector3>();
        private float transitionElapsed;
        private bool transitionActive;
        private bool eyesOpen;
        private bool smiling;
        private float eyeChannelWeight;
        private float smileChannelWeight;
        private float sadnessChannelWeight;
        private float angerChannelWeight;
        private float painChannelWeight;
        private int activeSpeechPoseIndex = -1;
        private float speechPoseWeight;
        private BoneSkinCoupler2D boneCoupler;
        private AnatomyBoneMesh jawBone;
        private float jawTransitionStartOpeningUv;
        private float jawTransitionTargetOpeningUv;
        private float jawTransitionStartSideFollow;
        private float jawTransitionTargetSideFollow;
        private float jawTransitionStartFocusPower;
        private float jawTransitionTargetFocusPower;
        private bool jawTransitionActive;
        private bool randomTwitching;
        private float nextRandomTwitchTime;
        private System.Random randomTwitchGenerator;
        private Vector2[] randomTwitchUvs = Array.Empty<Vector2>();
        private Vector2[] randomTwitchOffsets = Array.Empty<Vector2>();
        private float randomTwitchLocalWidth = 1f;
        private float randomTwitchLocalHeight = 1f;

        public bool EyesOpen { get { return eyesOpen; } }
        public bool Smiling { get { return smiling; } }
        public float EyeChannelWeight { get { return eyeChannelWeight; } }
        public float SmileChannelWeight { get { return smileChannelWeight; } }
        public float SadnessChannelWeight { get { return sadnessChannelWeight; } }
        public float AngerChannelWeight { get { return angerChannelWeight; } }
        public float PainChannelWeight { get { return painChannelWeight; } }
        public int ActiveSpeechPoseIndex { get { return activeSpeechPoseIndex; } }
        public float SpeechPoseWeight { get { return speechPoseWeight; } }
        public bool RandomTwitching { get { return randomTwitching; } }
        public string BakeStatus { get { return bakeStatus; } }
        public DeformableImageMesh MuscleLayer { get { return muscleLayer; } }
        public DeformableImageMesh SkinLayer { get { return skinLayer; } }
        public FacialFeaturePrecut2D FacialCurves { get { return facialCurves; } }
        public bool HasBakedOpenEyesPose
        {
            get
            {
                return muscleLayer != null
                    && bakedOpenEyesOffsets != null
                    && bakedOpenEyesOffsets.Length == muscleLayer.LogicalVertexCount
                    && bakedOpenEyesOffsets.Length > 0;
            }
        }
        public bool HasBakedSmilePose
        {
            get
            {
                return muscleLayer != null
                    && bakedSmileOffsets != null
                    && bakedSmileOffsets.Length == muscleLayer.LogicalVertexCount
                    && bakedSmileOffsets.Length > 0;
            }
        }
        public bool HasBakedSadnessPose { get { return HasValidBakedChannel(bakedSadnessOffsets); } }
        public bool HasBakedAngerPose { get { return HasValidBakedChannel(bakedAngerOffsets); } }
        public bool HasBakedPainPose { get { return HasValidBakedChannel(bakedPainOffsets); } }
        public bool HasBakedSpeechPoses
        {
            get
            {
                if (muscleLayer == null || speechMouthPoses == null
                    || speechMouthPoses.Count == 0)
                {
                    return false;
                }

                for (int index = 0; index < speechMouthPoses.Count; index++)
                {
                    if (speechMouthPoses[index] == null
                        || !speechMouthPoses[index].HasValidBake(
                            muscleLayer.LogicalVertexCount))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public bool NeedsRebake
        {
            get
            {
                return !HasBakedOpenEyesPose
                    || !HasBakedSmilePose
                    || !HasBakedSadnessPose
                    || !HasBakedAngerPose
                    || !HasBakedPainPose
                    || !HasBakedSpeechPoses
                    || facialCurves == null
                    || bakedCurveHash != CalculatePoseHash()
                    || bakedVersion != CurrentBakeVersion;
            }
        }

        public void Configure(
            DeformableImageMesh muscle,
            DeformableImageMesh skin,
            FacialFeaturePrecut2D curves)
        {
            muscleLayer = muscle;
            skinLayer = skin;
            facialCurves = curves;
            if (bakedVersion < CurrentBakeVersion)
            {
                eyelidMuscleLift = 0.034f;
                eyeInfluenceHeight = 0.085f;
                smileCornerLift = 0.026f;
                smileWiden = 0.016f;
                smileInfluenceRadius = 0.105f;
                EnsureTargetCurves();
                EnsureSmileMuscleZones();
                EnsureEmotionMuscleZones();
                if (bakedVersion < 13)
                {
                    ResetSpeechMouthPoses();
                }
                if (bakedVersion < 15)
                {
                    UpgradeSpeechJawAndCornerProfile();
                }
                if (bakedVersion < 16)
                {
                    UpgradeSpeechCornerAxisProfile();
                }
                if (bakedVersion < 17)
                {
                    // Version 17 replaces all five speech profiles as one coherent set.
                    // The references use deliberately different lip/teeth occlusion:
                    // U/O cover the teeth, E exposes only the upper teeth, while A-E
                    // and Ah progressively expose both dental rows.
                    ResetSpeechMouthPoses();
                }
                if (bakedVersion < 8)
                {
                    noseProtectionCenterUv = new Vector2(0.5f, 0.515f);
                    noseProtectionRadiusUv = new Vector2(0.12f, 0.17f);
                    noseShapeProtection = 0.88f;
                    ResetPainMuscleZones();
                }
            }
        }

        public FacialCutCurve2D GetOpenEyelidTarget(int eyeIndex)
        {
            EnsureTargetCurves();
            return eyeIndex == 0 ? leftOpenEyelidTarget : rightOpenEyelidTarget;
        }

        public List<Vector2> GetSampledOpenEyelidTarget(int eyeIndex)
        {
            FacialCutCurve2D curve = GetOpenEyelidTarget(eyeIndex);
            return SampleCurve(curve != null ? curve.ControlPoints : null);
        }

        public FacialCutCurve2D GetSmileTarget()
        {
            EnsureTargetCurves();
            return smileTarget;
        }

        public IList<FacialMuscleInfluenceZone2D> SmileMuscleZones
        {
            get { return smileMuscleZones; }
        }
        public IList<FacialMuscleInfluenceZone2D> SadnessMuscleZones { get { return sadnessMuscleZones; } }
        public IList<FacialMuscleInfluenceZone2D> AngerMuscleZones { get { return angerMuscleZones; } }
        public IList<FacialMuscleInfluenceZone2D> PainMuscleZones { get { return painMuscleZones; } }
        public IList<SpeechMouthPose2D> SpeechMouthPoses
        {
            get
            {
                EnsureSpeechMouthPoses();
                return speechMouthPoses;
            }
        }

        public List<Vector2> GetSampledSmileTarget()
        {
            FacialCutCurve2D curve = GetSmileTarget();
            return SampleCurve(curve != null ? curve.ControlPoints : null);
        }

        [ContextMenu("Reset Open-Eyes Target Curves")]
        public void ResetOpenEyesTargetCurves()
        {
            if (facialCurves == null)
            {
                return;
            }

            leftOpenEyelidTarget = CreateDefaultTargetCurve(
                facialCurves.GetEditableCurve(0),
                "Left Open Eyelid Target",
                new Color(0f, 1f, 0.75f));
            rightOpenEyelidTarget = CreateDefaultTargetCurve(
                facialCurves.GetEditableCurve(1),
                "Right Open Eyelid Target",
                new Color(1f, 0.75f, 0.1f));
        }

        [ContextMenu("Reset All Expression Target Curves")]
        public void ResetExpressionTargetCurves()
        {
            ResetOpenEyesTargetCurves();
            if (facialCurves == null)
            {
                return;
            }

            smileTarget = CreateDefaultSmileTargetCurve(
                facialCurves.GetEditableCurve(2));
            ResetSmileMuscleZones();
            ResetEmotionMuscleZones();
            ResetSpeechMouthPoses();
        }

        [ContextMenu("Reset Smile Multi-Muscle Zones")]
        public void ResetSmileMuscleZones()
        {
            smileMuscleZones = new List<FacialMuscleInfluenceZone2D>
            {
                new FacialMuscleInfluenceZone2D(
                    "Left Mouth Corner / Zygomaticus Major",
                    new Vector2(0.39f, 0.37f),
                    new Vector2(0.145f, 0.14f),
                    new Vector2(-0.008f, 0.012f)),
                new FacialMuscleInfluenceZone2D(
                    "Right Mouth Corner / Zygomaticus Major",
                    new Vector2(0.61f, 0.37f),
                    new Vector2(0.145f, 0.14f),
                    new Vector2(0.008f, 0.012f)),
                new FacialMuscleInfluenceZone2D(
                    "Left Cheek Lift",
                    new Vector2(0.36f, 0.46f),
                    new Vector2(0.18f, 0.17f),
                    new Vector2(-0.006f, 0.012f)),
                new FacialMuscleInfluenceZone2D(
                    "Right Cheek Lift",
                    new Vector2(0.64f, 0.46f),
                    new Vector2(0.18f, 0.17f),
                    new Vector2(0.006f, 0.012f)),
                new FacialMuscleInfluenceZone2D(
                    "Left Lower-Eyelid Cheek Support",
                    new Vector2(0.36f, 0.545f),
                    new Vector2(0.145f, 0.095f),
                    new Vector2(-0.002f, 0.005f),
                    0.8f),
                new FacialMuscleInfluenceZone2D(
                    "Right Lower-Eyelid Cheek Support",
                    new Vector2(0.64f, 0.545f),
                    new Vector2(0.145f, 0.095f),
                    new Vector2(0.002f, 0.005f),
                    0.8f),
                new FacialMuscleInfluenceZone2D(
                    "Upper Lip Lift",
                    new Vector2(0.5f, 0.375f),
                    new Vector2(0.17f, 0.09f),
                    new Vector2(0f, 0.003f),
                    0.75f),
                new FacialMuscleInfluenceZone2D(
                    "Lower Lip And Chin Support",
                    new Vector2(0.5f, 0.285f),
                    new Vector2(0.19f, 0.12f),
                    new Vector2(0f, 0.002f),
                    0.55f)
            };
        }

        [ContextMenu("Reset Sadness Anger Pain Zones")]
        public void ResetEmotionMuscleZones()
        {
            sadnessMuscleZones = new List<FacialMuscleInfluenceZone2D>
            {
                new FacialMuscleInfluenceZone2D("Sad Left Inner Brow", new Vector2(0.44f, 0.665f), new Vector2(0.105f, 0.09f), new Vector2(0.006f, 0.021f)),
                new FacialMuscleInfluenceZone2D("Sad Right Inner Brow", new Vector2(0.56f, 0.665f), new Vector2(0.105f, 0.09f), new Vector2(-0.006f, 0.021f)),
                new FacialMuscleInfluenceZone2D("Sad Left Brow Tail", new Vector2(0.32f, 0.65f), new Vector2(0.12f, 0.085f), new Vector2(0.002f, -0.009f), 0.8f),
                new FacialMuscleInfluenceZone2D("Sad Right Brow Tail", new Vector2(0.68f, 0.65f), new Vector2(0.12f, 0.085f), new Vector2(-0.002f, -0.009f), 0.8f),
                new FacialMuscleInfluenceZone2D("Sad Left Under-Eye", new Vector2(0.36f, 0.535f), new Vector2(0.15f, 0.09f), new Vector2(0f, 0.005f), 0.65f),
                new FacialMuscleInfluenceZone2D("Sad Right Under-Eye", new Vector2(0.64f, 0.535f), new Vector2(0.15f, 0.09f), new Vector2(0f, 0.005f), 0.65f),
                new FacialMuscleInfluenceZone2D("Sad Left Mouth Corner", new Vector2(0.405f, 0.345f), new Vector2(0.14f, 0.12f), new Vector2(0.008f, -0.018f)),
                new FacialMuscleInfluenceZone2D("Sad Right Mouth Corner", new Vector2(0.595f, 0.345f), new Vector2(0.14f, 0.12f), new Vector2(-0.008f, -0.018f)),
                new FacialMuscleInfluenceZone2D("Sad Chin Raise", new Vector2(0.5f, 0.275f), new Vector2(0.18f, 0.12f), new Vector2(0f, 0.009f), 0.75f)
            };

            angerMuscleZones = new List<FacialMuscleInfluenceZone2D>
            {
                new FacialMuscleInfluenceZone2D("Anger Left Inner Brow", new Vector2(0.44f, 0.66f), new Vector2(0.12f, 0.095f), new Vector2(0.012f, -0.024f)),
                new FacialMuscleInfluenceZone2D("Anger Right Inner Brow", new Vector2(0.56f, 0.66f), new Vector2(0.12f, 0.095f), new Vector2(-0.012f, -0.024f)),
                new FacialMuscleInfluenceZone2D("Anger Glabella Compression", new Vector2(0.5f, 0.635f), new Vector2(0.095f, 0.12f), new Vector2(0f, -0.012f), 0.9f),
                new FacialMuscleInfluenceZone2D("Anger Left Eye Tighten", new Vector2(0.36f, 0.58f), new Vector2(0.15f, 0.085f), new Vector2(0f, -0.008f), 0.75f),
                new FacialMuscleInfluenceZone2D("Anger Right Eye Tighten", new Vector2(0.64f, 0.58f), new Vector2(0.15f, 0.085f), new Vector2(0f, -0.008f), 0.75f),
                new FacialMuscleInfluenceZone2D("Anger Left Nose-Side Wrinkle", new Vector2(0.42f, 0.47f), new Vector2(0.095f, 0.115f), new Vector2(0.003f, 0.005f), 0.5f),
                new FacialMuscleInfluenceZone2D("Anger Right Nose-Side Wrinkle", new Vector2(0.58f, 0.47f), new Vector2(0.095f, 0.115f), new Vector2(-0.003f, 0.005f), 0.5f),
                new FacialMuscleInfluenceZone2D("Anger Left Mouth Tension", new Vector2(0.405f, 0.345f), new Vector2(0.15f, 0.11f), new Vector2(-0.008f, -0.006f), 0.75f),
                new FacialMuscleInfluenceZone2D("Anger Right Mouth Tension", new Vector2(0.595f, 0.345f), new Vector2(0.15f, 0.11f), new Vector2(0.008f, -0.006f), 0.75f),
                new FacialMuscleInfluenceZone2D("Anger Jaw Set", new Vector2(0.5f, 0.255f), new Vector2(0.24f, 0.15f), new Vector2(0f, -0.004f), 0.6f)
            };

            ResetPainMuscleZones();
        }

        [ContextMenu("Reset Pain Zones From Reference")]
        public void ResetPainMuscleZones()
        {
            painMuscleZones = new List<FacialMuscleInfluenceZone2D>
            {
                new FacialMuscleInfluenceZone2D("Pain Left Inner Brow Down/In", new Vector2(0.44f, 0.665f), new Vector2(0.12f, 0.1f), new Vector2(0.01f, -0.02f)),
                new FacialMuscleInfluenceZone2D("Pain Right Inner Brow Down/In", new Vector2(0.56f, 0.665f), new Vector2(0.12f, 0.1f), new Vector2(-0.01f, -0.02f)),
                new FacialMuscleInfluenceZone2D("Pain Glabella Compression", new Vector2(0.5f, 0.635f), new Vector2(0.09f, 0.105f), new Vector2(0f, -0.012f), 0.85f),
                new FacialMuscleInfluenceZone2D("Pain Left Upper-Eye Squeeze", new Vector2(0.36f, 0.595f), new Vector2(0.165f, 0.09f), new Vector2(0f, -0.016f)),
                new FacialMuscleInfluenceZone2D("Pain Right Upper-Eye Squeeze", new Vector2(0.64f, 0.595f), new Vector2(0.165f, 0.09f), new Vector2(0f, -0.016f)),
                new FacialMuscleInfluenceZone2D("Pain Left Lower-Eye Squeeze", new Vector2(0.36f, 0.525f), new Vector2(0.17f, 0.095f), new Vector2(0.002f, 0.014f)),
                new FacialMuscleInfluenceZone2D("Pain Right Lower-Eye Squeeze", new Vector2(0.64f, 0.525f), new Vector2(0.17f, 0.095f), new Vector2(-0.002f, 0.014f)),
                new FacialMuscleInfluenceZone2D("Pain Left Outer-Eye Cheek Squeeze", new Vector2(0.33f, 0.49f), new Vector2(0.15f, 0.14f), new Vector2(0.006f, 0.01f), 0.8f),
                new FacialMuscleInfluenceZone2D("Pain Right Outer-Eye Cheek Squeeze", new Vector2(0.67f, 0.49f), new Vector2(0.15f, 0.14f), new Vector2(-0.006f, 0.01f), 0.8f),
                new FacialMuscleInfluenceZone2D("Pain Left Nose-Side Wrinkle", new Vector2(0.42f, 0.47f), new Vector2(0.1f, 0.12f), new Vector2(0.004f, 0.008f), 0.6f),
                new FacialMuscleInfluenceZone2D("Pain Right Nose-Side Wrinkle", new Vector2(0.58f, 0.47f), new Vector2(0.1f, 0.12f), new Vector2(-0.004f, 0.008f), 0.6f),
                new FacialMuscleInfluenceZone2D("Pain Left Nasolabial Compression", new Vector2(0.39f, 0.425f), new Vector2(0.14f, 0.15f), new Vector2(0.007f, 0.007f), 0.85f),
                new FacialMuscleInfluenceZone2D("Pain Right Nasolabial Compression", new Vector2(0.61f, 0.425f), new Vector2(0.14f, 0.15f), new Vector2(-0.007f, 0.007f), 0.85f),
                new FacialMuscleInfluenceZone2D("Pain Upper Lip Raise", new Vector2(0.5f, 0.385f), new Vector2(0.17f, 0.085f), new Vector2(0f, 0.006f), 0.75f),
                new FacialMuscleInfluenceZone2D("Pain Left Mouth Corner In/Down", new Vector2(0.4f, 0.34f), new Vector2(0.15f, 0.12f), new Vector2(0.008f, -0.011f)),
                new FacialMuscleInfluenceZone2D("Pain Right Mouth Corner In/Down", new Vector2(0.6f, 0.34f), new Vector2(0.15f, 0.12f), new Vector2(-0.008f, -0.011f)),
                new FacialMuscleInfluenceZone2D("Pain Chin Tighten", new Vector2(0.5f, 0.27f), new Vector2(0.19f, 0.13f), new Vector2(0f, 0.01f), 0.85f)
            };
        }

        [ContextMenu("Reset Five Speech Mouth Poses From References")]
        public void ResetSpeechMouthPoses()
        {
            speechMouthPoses = new List<SpeechMouthPose2D>
            {
                new SpeechMouthPose2D(
                    "Speech 1 - Pucker / U",
                    KeyCode.Z,
                    0.009f,
                    new[]
                    {
                        new FacialMuscleInfluenceZone2D("U Left Corner In", new Vector2(0.405f, 0.34f), new Vector2(0.135f, 0.095f), new Vector2(0.028f, 0f), 1.15f),
                        new FacialMuscleInfluenceZone2D("U Right Corner In", new Vector2(0.595f, 0.34f), new Vector2(0.135f, 0.095f), new Vector2(-0.028f, 0f), 1.15f),
                        new FacialMuscleInfluenceZone2D("U Upper Lip Close", new Vector2(0.5f, 0.372f), new Vector2(0.16f, 0.065f), new Vector2(0f, -0.004f), 1f),
                        new FacialMuscleInfluenceZone2D("U Lower Lip Close", new Vector2(0.5f, 0.313f), new Vector2(0.175f, 0.065f), new Vector2(0f, 0.003f), 0.95f)
                    }),
                new SpeechMouthPose2D(
                    "Speech 2 - Open Teeth / A-E",
                    KeyCode.X,
                    0.036f,
                    new[]
                    {
                        new FacialMuscleInfluenceZone2D("A-E Left Corner Wide", new Vector2(0.405f, 0.34f), new Vector2(0.145f, 0.095f), new Vector2(-0.01f, 0f), 1.05f),
                        new FacialMuscleInfluenceZone2D("A-E Right Corner Wide", new Vector2(0.595f, 0.34f), new Vector2(0.145f, 0.095f), new Vector2(0.01f, 0f), 1.05f),
                        new FacialMuscleInfluenceZone2D("A-E Upper Lip Teeth Reveal", new Vector2(0.5f, 0.372f), new Vector2(0.185f, 0.06f), new Vector2(0f, 0.008f), 1.05f),
                        new FacialMuscleInfluenceZone2D("A-E Lower Lip Teeth Reveal", new Vector2(0.5f, 0.313f), new Vector2(0.205f, 0.06f), new Vector2(0f, -0.007f), 0.85f)
                    }),
                new SpeechMouthPose2D(
                    "Speech 3 - Narrow Teeth / E",
                    KeyCode.C,
                    0.014f,
                    new[]
                    {
                        new FacialMuscleInfluenceZone2D("E Left Corner Stretch", new Vector2(0.4f, 0.34f), new Vector2(0.15f, 0.085f), new Vector2(-0.016f, 0f), 1.1f),
                        new FacialMuscleInfluenceZone2D("E Right Corner Stretch", new Vector2(0.6f, 0.34f), new Vector2(0.15f, 0.085f), new Vector2(0.016f, 0f), 1.1f),
                        new FacialMuscleInfluenceZone2D("E Upper Lip Teeth Reveal", new Vector2(0.5f, 0.37f), new Vector2(0.185f, 0.055f), new Vector2(0f, 0.0045f), 1f),
                        new FacialMuscleInfluenceZone2D("E Lower Lip Teeth Cover", new Vector2(0.5f, 0.315f), new Vector2(0.205f, 0.055f), new Vector2(0f, 0.0035f), 0.85f)
                    }),
                new SpeechMouthPose2D(
                    "Speech 4 - Wide Ah",
                    KeyCode.V,
                    0.086f,
                    new[]
                    {
                        new FacialMuscleInfluenceZone2D("Ah Left Corner Round", new Vector2(0.405f, 0.34f), new Vector2(0.14f, 0.11f), new Vector2(0.004f, 0f), 1f),
                        new FacialMuscleInfluenceZone2D("Ah Right Corner Round", new Vector2(0.595f, 0.34f), new Vector2(0.14f, 0.11f), new Vector2(-0.004f, 0f), 1f),
                        new FacialMuscleInfluenceZone2D("Ah Upper Lip Raise", new Vector2(0.5f, 0.375f), new Vector2(0.18f, 0.065f), new Vector2(0f, 0.012f), 1.1f),
                        new FacialMuscleInfluenceZone2D("Ah Lower Lip Drop", new Vector2(0.5f, 0.31f), new Vector2(0.205f, 0.07f), new Vector2(0f, -0.012f), 0.9f)
                    }),
                new SpeechMouthPose2D(
                    "Speech 5 - Round O",
                    KeyCode.B,
                    0.038f,
                    new[]
                    {
                        new FacialMuscleInfluenceZone2D("O Left Corner In", new Vector2(0.405f, 0.34f), new Vector2(0.135f, 0.105f), new Vector2(0.028f, 0f), 1.15f),
                        new FacialMuscleInfluenceZone2D("O Right Corner In", new Vector2(0.595f, 0.34f), new Vector2(0.135f, 0.105f), new Vector2(-0.028f, 0f), 1.15f),
                        new FacialMuscleInfluenceZone2D("O Upper Lip Teeth Cover", new Vector2(0.5f, 0.374f), new Vector2(0.165f, 0.065f), new Vector2(0f, -0.0065f), 1f),
                        new FacialMuscleInfluenceZone2D("O Lower Lip Teeth Cover", new Vector2(0.5f, 0.314f), new Vector2(0.185f, 0.065f), new Vector2(0f, 0.006f), 0.9f)
                    })
            };
        }

        public int CalculatePoseHash()
        {
            unchecked
            {
                int hash = facialCurves != null
                    ? facialCurves.CalculateCurveHash()
                    : 17;
                hash = hash * 31 + eyeInfluenceHeight.GetHashCode();
                hash = hash * 31 + smileInfluenceRadius.GetHashCode();
                hash = hash * 31 + noseProtectionCenterUv.GetHashCode();
                hash = hash * 31 + noseProtectionRadiusUv.GetHashCode();
                hash = hash * 31 + noseShapeProtection.GetHashCode();
                for (int eye = 0; eye < 2; eye++)
                {
                    FacialCutCurve2D target = GetOpenEyelidTarget(eye);
                    List<Vector2> points = target != null ? target.ControlPoints : null;
                    hash = hash * 31 + (points != null ? points.Count : 0);
                    if (points == null)
                    {
                        continue;
                    }

                    for (int index = 0; index < points.Count; index++)
                    {
                        hash = hash * 31 + points[index].GetHashCode();
                    }
                }

                FacialCutCurve2D mouthTarget = GetSmileTarget();
                List<Vector2> mouthPoints = mouthTarget != null
                    ? mouthTarget.ControlPoints
                    : null;
                hash = hash * 31 + (mouthPoints != null ? mouthPoints.Count : 0);
                if (mouthPoints != null)
                {
                    for (int index = 0; index < mouthPoints.Count; index++)
                    {
                        hash = hash * 31 + mouthPoints[index].GetHashCode();
                    }
                }

                EnsureSmileMuscleZones();
                EnsureEmotionMuscleZones();
                EnsureSpeechMouthPoses();
                hash = AddZonesToHash(hash, smileMuscleZones);
                hash = AddZonesToHash(hash, sadnessMuscleZones);
                hash = AddZonesToHash(hash, angerMuscleZones);
                hash = AddZonesToHash(hash, painMuscleZones);
                for (int poseIndex = 0; poseIndex < speechMouthPoses.Count; poseIndex++)
                {
                    SpeechMouthPose2D pose = speechMouthPoses[poseIndex];
                    if (pose == null)
                    {
                        hash *= 31;
                        continue;
                    }

                    hash = hash * 31 + pose.JawDropUv.GetHashCode();
                    hash = hash * 31 + pose.JawSideFollow.GetHashCode();
                    hash = hash * 31 + pose.JawChinFocusPower.GetHashCode();
                    hash = hash * 31 + pose.DefaultWeight.GetHashCode();
                    hash = AddZonesToHash(hash, pose.MuscleZones);
                }

                return hash;
            }
        }

        private static int AddZonesToHash(
            int hash,
            IList<FacialMuscleInfluenceZone2D> zones)
        {
            unchecked
            {
                hash = hash * 31 + (zones != null ? zones.Count : 0);
                if (zones == null)
                {
                    return hash;
                }

                for (int index = 0; index < zones.Count; index++)
                {
                    FacialMuscleInfluenceZone2D zone = zones[index];
                    if (zone == null)
                    {
                        hash *= 31;
                        continue;
                    }

                    hash = hash * 31 + zone.CenterUv.GetHashCode();
                    hash = hash * 31 + zone.RadiusUv.GetHashCode();
                    hash = hash * 31 + zone.DisplacementUv.GetHashCode();
                    hash = hash * 31 + zone.Strength.GetHashCode();
                }

                return hash;
            }
        }

        private void Awake()
        {
            if (muscleLayer == null)
            {
                muscleLayer = GetComponent<DeformableImageMesh>();
            }

            if (facialCurves == null)
            {
                facialCurves = FindObjectOfType<FacialFeaturePrecut2D>(true);
            }

            if (facialCurves != null)
            {
                skinLayer = skinLayer != null ? skinLayer : facialCurves.SkinLayer;
            }

            boneCoupler = muscleLayer != null
                ? muscleLayer.GetComponent<BoneSkinCoupler2D>()
                : null;
            jawBone = boneCoupler != null ? boneCoupler.JawBone : null;

        }

        private void Start()
        {
            if (muscleLayer == null || muscleLayer.LogicalVertexCount == 0)
            {
                enabled = false;
                return;
            }

            // Normally all channels are serialized by the editor. This fallback covers
            // script recompiles that occur while Play Mode is already active; the next
            // return to Edit Mode still performs and saves the normal editor bake.
            if (NeedsRebake)
            {
                BakeAllExpressionPoses();
            }

            neutralMuscleVertices = muscleLayer.CopyCurrentVertices();
            transitionStartVertices = new Vector3[neutralMuscleVertices.Length];
            transitionTargetVertices = new Vector3[neutralMuscleVertices.Length];
            transitionWorkingVertices = new Vector3[neutralMuscleVertices.Length];
            boneWorkingOffsets = new Vector3[neutralMuscleVertices.Length];
            boneTransitionStartOffsets = new Vector3[neutralMuscleVertices.Length];
            randomTwitchUvs = muscleLayer.CopyNormalizedRestUvs();
            randomTwitchOffsets = new Vector2[neutralMuscleVertices.Length];
            Vector3[] restVertices = muscleLayer.CopyRestVertices();
            randomTwitchLocalWidth = 0f;
            randomTwitchLocalHeight = 0f;
            for (int index = 0; index < restVertices.Length; index++)
            {
                randomTwitchLocalWidth = Mathf.Max(
                    randomTwitchLocalWidth,
                    Mathf.Abs(restVertices[index].x) * 2f);
                randomTwitchLocalHeight = Mathf.Max(
                    randomTwitchLocalHeight,
                    Mathf.Abs(restVertices[index].y) * 2f);
            }

            randomTwitchLocalWidth = Mathf.Max(0.0001f, randomTwitchLocalWidth);
            randomTwitchLocalHeight = Mathf.Max(0.0001f, randomTwitchLocalHeight);
            eyesOpen = initiallyOpenEyes;
            smiling = initiallySmiling;
            eyeChannelWeight = eyesOpen ? 1f : 0f;
            smileChannelWeight = smiling ? 1f : 0f;
            sadnessChannelWeight = initiallySad ? 1f : 0f;
            angerChannelWeight = initiallyAngry ? 1f : 0f;
            painChannelWeight = initiallyInPain ? 1f : 0f;
            EnsureSpeechMouthPoses();
            activeSpeechPoseIndex = initiallySpeechPose >= 0
                && initiallySpeechPose < speechMouthPoses.Count
                    ? initiallySpeechPose
                    : -1;
            speechPoseWeight = activeSpeechPoseIndex >= 0
                ? speechMouthPoses[activeSpeechPoseIndex].DefaultWeight
                : 0f;
            ApplyCombinedExpression(true, true);
            randomTwitchGenerator = new System.Random(
                System.Environment.TickCount ^ GetInstanceID());
            randomTwitching = initiallyRandomTwitching;
            nextRandomTwitchTime = Time.time;
        }

        private void Update()
        {
            if (enableHotkeys)
            {
                if (Input.GetKeyDown(randomTwitchKey))
                {
                    ToggleRandomTwitching();
                }
                else if (Input.GetKeyDown(neutralKey))
                {
                    randomTwitching = false;
                    ResetAllExpressionWeights();
                }
                else if (Input.GetKeyDown(openEyesKey))
                {
                    randomTwitching = false;
                    SetEyesOpen(!eyesOpen);
                }
                else if (Input.GetKeyDown(smileKey))
                {
                    randomTwitching = false;
                    SetSmiling(!smiling);
                }
                else if (Input.GetKeyDown(sadnessKey))
                {
                    randomTwitching = false;
                    SetSadnessWeight(sadnessChannelWeight > 0.0001f ? 0f : 1f);
                }
                else if (Input.GetKeyDown(angerKey))
                {
                    randomTwitching = false;
                    SetAngerWeight(angerChannelWeight > 0.0001f ? 0f : 1f);
                }
                else if (Input.GetKeyDown(painKey))
                {
                    randomTwitching = false;
                    SetPainWeight(painChannelWeight > 0.0001f ? 0f : 1f);
                }

                for (int poseIndex = 0; poseIndex < speechMouthPoses.Count; poseIndex++)
                {
                    SpeechMouthPose2D pose = speechMouthPoses[poseIndex];
                    if (pose == null || pose.Hotkey == KeyCode.None
                        || !Input.GetKeyDown(pose.Hotkey))
                    {
                        continue;
                    }

                    if (activeSpeechPoseIndex == poseIndex
                        && speechPoseWeight > 0.0001f)
                    {
                        randomTwitching = false;
                        ClearSpeechPose();
                    }
                    else
                    {
                        randomTwitching = false;
                        SetSpeechPose(poseIndex, pose.DefaultWeight);
                    }

                    break;
                }
            }

            if (randomTwitching && Time.time >= nextRandomTwitchTime)
            {
                ApplyNextRandomTwitch();
            }

            UpdateTransition();
        }

        private void OnGUI()
        {
            if (!Application.isPlaying || !FaceRuntimeUILayout.Left(2))
            {
                return;
            }

            if (showRuntimeStatus)
            {
                GUI.Box(
                    FaceRuntimeUILayout.Enabled ? FaceRuntimeUILayout.Panel(false, 200f) : new Rect(12f, 100f, 700f, 112f),
                    "Eyes: " + eyeChannelWeight.ToString("0.00")
                    + " | Smile: " + smileChannelWeight.ToString("0.00")
                    + " | Sad: " + sadnessChannelWeight.ToString("0.00")
                    + " | Anger: " + angerChannelWeight.ToString("0.00")
                    + " | Pain: " + painChannelWeight.ToString("0.00")
                    + "\nMouth: " + GetActiveSpeechPoseLabel()
                    + " (" + speechPoseWeight.ToString("0.00") + ")"
                    + " | Random Twitch: " + (randomTwitching ? "ON" : "OFF")
                    + "\nF1 Reset | F2 Eyes | F3 Smile | F4 Sadness | F5 Anger | F6 Pain"
                    + " | F7 Random Twitch | Z/X/C/V/B Speech");
            }

            if (showRandomTwitchButton
                && GUI.Button(
                    FaceRuntimeUILayout.Enabled ? new Rect(12f, showRuntimeStatus ? 264f : 54f, FaceRuntimeUILayout.Width, 34f)
                        : new Rect(12f, showRuntimeStatus ? 218f : 100f, 240f, 34f),
                    randomTwitching
                        ? "停止随机肌肉抽搐 (F7)"
                        : "开始随机肌肉抽搐 (F7)"))
            {
                ToggleRandomTwitching();
            }
        }

        public void ToggleRandomTwitching()
        {
            SetRandomTwitching(!randomTwitching);
        }

        public void SetRandomTwitching(bool value)
        {
            randomTwitching = value;
            if (randomTwitching)
            {
                if (randomTwitchGenerator == null)
                {
                    randomTwitchGenerator = new System.Random(
                        System.Environment.TickCount ^ GetInstanceID());
                }

                nextRandomTwitchTime = Time.time;
                ApplyNextRandomTwitch();
            }
            else
            {
                ResetAllExpressionWeights();
            }
        }

        private void ApplyNextRandomTwitch()
        {
            if (!randomTwitching || randomTwitchGenerator == null)
            {
                return;
            }

            float intensity = Mathf.Clamp(randomTwitchIntensity, 0.1f, 1.5f);
            eyeChannelWeight = 0f;
            smileChannelWeight = 0f;
            sadnessChannelWeight = 0f;
            angerChannelWeight = 0f;
            painChannelWeight = 0f;
            eyesOpen = false;
            smiling = false;
            activeSpeechPoseIndex = -1;
            speechPoseWeight = 0f;
            BuildProceduralRandomTwitchOffsets(intensity);
            ApplyCombinedExpression(false, true);

            float minimum = Mathf.Max(0.03f, randomTwitchMinInterval);
            float maximum = Mathf.Max(minimum, randomTwitchMaxInterval);
            nextRandomTwitchTime = Time.time + RandomRange(minimum, maximum);
        }

        private void BuildProceduralRandomTwitchOffsets(float intensity)
        {
            if (randomTwitchOffsets == null
                || randomTwitchUvs == null
                || randomTwitchOffsets.Length != randomTwitchUvs.Length)
            {
                return;
            }

            Array.Clear(randomTwitchOffsets, 0, randomTwitchOffsets.Length);
            // Brief release frames make the local contractions read as twitches rather
            // than as a sequence of complete expression poses.
            if (NextRandom01() < 0.2f)
            {
                return;
            }

            int pulseCount = randomTwitchGenerator.Next(1, 4);
            bool primaryLeftSide = NextRandom01() < 0.5f;
            for (int pulse = 0; pulse < pulseCount; pulse++)
            {
                bool leftSide = pulse == 0 || NextRandom01() >= 0.18f
                    ? primaryLeftSide
                    : !primaryLeftSide;
                int facialBand = randomTwitchGenerator.Next(4);
                float centerY;
                float radiusY;
                switch (facialBand)
                {
                    case 0:
                        centerY = RandomRange(0.61f, 0.7f);
                        radiusY = RandomRange(0.045f, 0.085f);
                        break;
                    case 1:
                        centerY = RandomRange(0.51f, 0.6f);
                        radiusY = RandomRange(0.04f, 0.08f);
                        break;
                    case 2:
                        centerY = RandomRange(0.4f, 0.5f);
                        radiusY = RandomRange(0.06f, 0.12f);
                        break;
                    default:
                        centerY = RandomRange(0.27f, 0.38f);
                        radiusY = RandomRange(0.045f, 0.1f);
                        break;
                }

                float centerX = leftSide
                    ? RandomRange(0.28f, 0.46f)
                    : RandomRange(0.54f, 0.72f);
                Vector2 center = new Vector2(centerX, centerY);
                Vector2 radius = new Vector2(
                    RandomRange(0.045f, 0.12f),
                    radiusY);
                float angle = RandomRange(0f, Mathf.PI * 2f);
                float amplitude = RandomRange(0.0025f, 0.011f) * intensity;
                Vector2 displacementUv = new Vector2(
                    Mathf.Cos(angle) * amplitude,
                    Mathf.Sin(angle) * amplitude);
                float strength = RandomRange(0.55f, 1f);

                for (int vertex = 0; vertex < randomTwitchUvs.Length; vertex++)
                {
                    Vector2 uv = randomTwitchUvs[vertex];
                    Vector2 normalized = new Vector2(
                        (uv.x - center.x) / radius.x,
                        (uv.y - center.y) / radius.y);
                    float distance = normalized.magnitude;
                    if (distance >= 1f)
                    {
                        continue;
                    }

                    float falloff = SmoothFalloff(distance) * strength;
                    randomTwitchOffsets[vertex] += new Vector2(
                        displacementUv.x * randomTwitchLocalWidth * falloff,
                        displacementUv.y * randomTwitchLocalHeight * falloff);
                }
            }
        }

        private float NextRandom01()
        {
            return randomTwitchGenerator != null
                ? (float)randomTwitchGenerator.NextDouble()
                : 0f;
        }

        private float RandomRange(float minimum, float maximum)
        {
            return Mathf.Lerp(minimum, maximum, NextRandom01());
        }

        public void SetEyesOpen(bool value, bool immediate = false)
        {
            SetEyeWeight(value ? 1f : 0f, immediate);
        }

        public void SetSmiling(bool value, bool immediate = false)
        {
            SetSmileWeight(value ? 1f : 0f, immediate);
        }

        public void SetEyeWeight(float weight, bool immediate = false)
        {
            eyeChannelWeight = Mathf.Clamp01(weight);
            eyesOpen = eyeChannelWeight > 0.0001f;
            ApplyCombinedExpression(immediate);
        }

        public void SetSmileWeight(float weight, bool immediate = false)
        {
            smileChannelWeight = Mathf.Clamp01(weight);
            smiling = smileChannelWeight > 0.0001f;
            ApplyCombinedExpression(immediate);
        }

        public void SetSadnessWeight(float weight, bool immediate = false)
        {
            sadnessChannelWeight = Mathf.Clamp01(weight);
            ApplyCombinedExpression(immediate);
        }

        public void SetAngerWeight(float weight, bool immediate = false)
        {
            angerChannelWeight = Mathf.Clamp01(weight);
            ApplyCombinedExpression(immediate);
        }

        public void SetPainWeight(float weight, bool immediate = false)
        {
            painChannelWeight = Mathf.Clamp01(weight);
            ApplyCombinedExpression(immediate);
        }

        public void SetSpeechPose(
            int poseIndex,
            float weight = 1f,
            bool immediate = false)
        {
            EnsureSpeechMouthPoses();
            if (poseIndex < 0 || poseIndex >= speechMouthPoses.Count)
            {
                ClearSpeechPose(immediate);
                return;
            }

            activeSpeechPoseIndex = poseIndex;
            speechPoseWeight = Mathf.Clamp(weight, 0f, 1.5f);
            if (speechPoseWeight <= 0.0001f)
            {
                activeSpeechPoseIndex = -1;
            }

            ApplyCombinedExpression(immediate, true);
        }

        public void ClearSpeechPose(bool immediate = false)
        {
            activeSpeechPoseIndex = -1;
            speechPoseWeight = 0f;
            ApplyCombinedExpression(immediate, true);
        }

        public string GetActiveSpeechPoseLabel()
        {
            if (activeSpeechPoseIndex < 0 || speechMouthPoses == null
                || activeSpeechPoseIndex >= speechMouthPoses.Count
                || speechMouthPoses[activeSpeechPoseIndex] == null)
            {
                return "Neutral";
            }

            return speechMouthPoses[activeSpeechPoseIndex].Label;
        }

        public void ResetAllExpressionWeights(bool immediate = false)
        {
            randomTwitching = false;
            if (randomTwitchOffsets != null)
            {
                Array.Clear(randomTwitchOffsets, 0, randomTwitchOffsets.Length);
            }

            eyeChannelWeight = 0f;
            smileChannelWeight = 0f;
            sadnessChannelWeight = 0f;
            angerChannelWeight = 0f;
            painChannelWeight = 0f;
            eyesOpen = false;
            smiling = false;
            activeSpeechPoseIndex = -1;
            speechPoseWeight = 0f;
            ApplyCombinedExpression(immediate, true);
        }

        public void SetAllChannelWeights(
            float eyeWeight,
            float smileWeight,
            float sadnessWeight,
            float angerWeight,
            float painWeight,
            bool immediate = false)
        {
            eyeChannelWeight = Mathf.Clamp01(eyeWeight);
            smileChannelWeight = Mathf.Clamp01(smileWeight);
            sadnessChannelWeight = Mathf.Clamp01(sadnessWeight);
            angerChannelWeight = Mathf.Clamp01(angerWeight);
            painChannelWeight = Mathf.Clamp01(painWeight);
            eyesOpen = eyeChannelWeight > 0.0001f;
            smiling = smileChannelWeight > 0.0001f;
            ApplyCombinedExpression(immediate);
        }

        public void SetFeatureStates(
            bool openEyes,
            bool smile,
            bool immediate = false)
        {
            eyesOpen = openEyes;
            smiling = smile;
            eyeChannelWeight = eyesOpen ? 1f : 0f;
            smileChannelWeight = smiling ? 1f : 0f;
            ApplyCombinedExpression(immediate);
        }

        public void SetFeatureWeights(
            float eyeWeight,
            float smileWeight,
            bool immediate = false)
        {
            eyeChannelWeight = Mathf.Clamp01(eyeWeight);
            smileChannelWeight = Mathf.Clamp01(smileWeight);
            eyesOpen = eyeChannelWeight > 0.0001f;
            smiling = smileChannelWeight > 0.0001f;
            ApplyCombinedExpression(immediate);
        }

        private void ApplyCombinedExpression(
            bool immediate = false,
            bool driveJaw = false)
        {
            if (muscleLayer == null || neutralMuscleVertices.Length == 0)
            {
                return;
            }

            if ((eyeChannelWeight > 0.0001f && !HasBakedOpenEyesPose)
                || (smileChannelWeight > 0.0001f && !HasBakedSmilePose)
                || (sadnessChannelWeight > 0.0001f && !HasBakedSadnessPose)
                || (angerChannelWeight > 0.0001f && !HasBakedAngerPose)
                || (painChannelWeight > 0.0001f && !HasBakedPainPose)
                || (activeSpeechPoseIndex >= 0 && !HasBakedSpeechPoses))
            {
                return;
            }

            muscleLayer.CopyCurrentVerticesNonAlloc(transitionStartVertices);
            CopyCurrentBoneOffsets(boneTransitionStartOffsets);
            BuildExpressionTargetVertices();

            jawTransitionActive = driveJaw && jawBone != null;
            if (jawTransitionActive)
            {
                jawTransitionStartOpeningUv = jawBone.MandibleOpeningUv;
                jawTransitionStartSideFollow = jawBone.MandibleSideFollow;
                jawTransitionStartFocusPower = jawBone.MandibleChinFocusPower;
                if (activeSpeechPoseIndex >= 0
                    && activeSpeechPoseIndex < speechMouthPoses.Count)
                {
                    SpeechMouthPose2D activePose =
                        speechMouthPoses[activeSpeechPoseIndex];
                    jawTransitionTargetOpeningUv = activePose.JawDropUv
                        * speechPoseWeight;
                    jawTransitionTargetSideFollow = activePose.JawSideFollow;
                    jawTransitionTargetFocusPower = activePose.JawChinFocusPower;
                }
                else
                {
                    jawTransitionTargetOpeningUv = 0f;
                    jawTransitionTargetSideFollow = 0f;
                    jawTransitionTargetFocusPower = 2.6f;
                }
            }

            transitionElapsed = 0f;
            transitionActive = true;
            if (immediate || transitionDuration <= 0f)
            {
                if (jawTransitionActive)
                {
                    jawBone.SetMandibleOpenPose(
                        jawTransitionTargetOpeningUv,
                        jawTransitionTargetSideFollow,
                        jawTransitionTargetFocusPower);
                    jawTransitionActive = false;
                }

                CopyCurrentBoneOffsets(boneWorkingOffsets);
                for (int index = 0; index < transitionWorkingVertices.Length; index++)
                {
                    transitionWorkingVertices[index] = transitionTargetVertices[index]
                        + boneWorkingOffsets[index];
                }

                muscleLayer.SetLogicalVertices(transitionWorkingVertices);
                transitionActive = false;
            }
        }

        private void BuildExpressionTargetVertices()
        {
            for (int index = 0; index < transitionTargetVertices.Length; index++)
            {
                Vector2 offset = Vector2.zero;
                if (randomTwitching)
                {
                    if (randomTwitchOffsets != null
                        && index < randomTwitchOffsets.Length)
                    {
                        offset += randomTwitchOffsets[index];
                    }
                }
                else
                {
                    if (eyesOpen)
                    {
                        offset += bakedOpenEyesOffsets[index] * eyeChannelWeight;
                    }

                    if (smiling)
                    {
                        offset += bakedSmileOffsets[index] * smileChannelWeight;
                    }

                    if (sadnessChannelWeight > 0.0001f)
                    {
                        offset += bakedSadnessOffsets[index] * sadnessChannelWeight;
                    }

                    if (angerChannelWeight > 0.0001f)
                    {
                        offset += bakedAngerOffsets[index] * angerChannelWeight;
                    }

                    if (painChannelWeight > 0.0001f)
                    {
                        offset += bakedPainOffsets[index] * painChannelWeight;
                    }
                }

                if (!randomTwitching
                    && activeSpeechPoseIndex >= 0
                    && activeSpeechPoseIndex < speechMouthPoses.Count
                    && speechPoseWeight > 0.0001f)
                {
                    offset += speechMouthPoses[activeSpeechPoseIndex]
                        .BakedOffsets[index] * speechPoseWeight;
                }

                transitionTargetVertices[index] = neutralMuscleVertices[index]
                    + new Vector3(offset.x, offset.y, 0f);
            }
        }

        private void CopyCurrentBoneOffsets(Vector3[] destination)
        {
            if (destination == null)
            {
                return;
            }

            if (boneCoupler != null
                && boneCoupler.TryCopyAppliedBoneOffsets(destination))
            {
                return;
            }

            Array.Clear(destination, 0, destination.Length);
        }

        private void UpdateJawTransition(float eased)
        {
            if (!jawTransitionActive || jawBone == null)
            {
                return;
            }

            jawBone.SetMandibleOpenPose(
                Mathf.LerpUnclamped(
                    jawTransitionStartOpeningUv,
                    jawTransitionTargetOpeningUv,
                    eased),
                Mathf.LerpUnclamped(
                    jawTransitionStartSideFollow,
                    jawTransitionTargetSideFollow,
                    eased),
                Mathf.LerpUnclamped(
                    jawTransitionStartFocusPower,
                    jawTransitionTargetFocusPower,
                    eased));
        }

        private void UpdateTransition()
        {
            if (!transitionActive)
            {
                return;
            }

            transitionElapsed += Time.deltaTime;
            float normalizedTime = transitionDuration <= 0f
                ? 1f
                : Mathf.Clamp01(transitionElapsed / transitionDuration);
            float eased = normalizedTime * normalizedTime * (3f - 2f * normalizedTime);
            UpdateJawTransition(eased);
            CopyCurrentBoneOffsets(boneWorkingOffsets);
            for (int index = 0; index < transitionWorkingVertices.Length; index++)
            {
                Vector3 startWithoutBone = transitionStartVertices[index]
                    - boneTransitionStartOffsets[index];
                transitionWorkingVertices[index] = Vector3.LerpUnclamped(
                    startWithoutBone,
                    transitionTargetVertices[index],
                    eased) + boneWorkingOffsets[index];
            }

            muscleLayer.SetLogicalVertices(
                transitionWorkingVertices,
                normalizedTime >= 1f);
            if (normalizedTime >= 1f)
            {
                transitionActive = false;
                jawTransitionActive = false;
            }
        }

        [ContextMenu("Bake All Facial Muscle Poses")]
        public bool BakeExpressionPoses()
        {
            return BakeAllExpressionPoses();
        }

        // Kept as a compatibility entry point for existing editor tooling.
        public bool BakeOpenEyesPose()
        {
            return BakeAllExpressionPoses();
        }

        private bool BakeAllExpressionPoses()
        {
            if (muscleLayer == null || facialCurves == null)
            {
                bakeStatus = "Assign Muscle and FacialFeaturePrecut2D before baking.";
                return false;
            }

            Vector2[] uvs = muscleLayer.CopyNormalizedRestUvs();
            Vector3[] rest = muscleLayer.CopyRestVertices();
            if (uvs.Length == 0 || uvs.Length != rest.Length)
            {
                bakeStatus = "Muscle topology is not ready.";
                return false;
            }

            float localWidth = 0f;
            float localHeight = 0f;
            for (int index = 0; index < rest.Length; index++)
            {
                localWidth = Mathf.Max(localWidth, Mathf.Abs(rest[index].x) * 2f);
                localHeight = Mathf.Max(localHeight, Mathf.Abs(rest[index].y) * 2f);
            }

            localWidth = Mathf.Max(0.0001f, localWidth);
            localHeight = Mathf.Max(0.0001f, localHeight);
            EnsureTargetCurves();
            EnsureSmileMuscleZones();
            EnsureEmotionMuscleZones();
            EnsureSpeechMouthPoses();
            IList<Vector2> mouthCutCurve = facialCurves.GetSampledEditableCurve(2);
            bool[] lowerMouthSide = BuildLowerMouthSideMask(
                uvs,
                muscleLayer.CopyMechanicallyIntactTriangles(),
                mouthCutCurve);
            bakedOpenEyesOffsets = new Vector2[uvs.Length];
            bakedSmileOffsets = new Vector2[uvs.Length];
            bakedSadnessOffsets = new Vector2[uvs.Length];
            bakedAngerOffsets = new Vector2[uvs.Length];
            bakedPainOffsets = new Vector2[uvs.Length];
            BakeEyeOffsets(
                uvs,
                facialCurves.GetSampledEditableCurve(0),
                GetSampledOpenEyelidTarget(0),
                localWidth,
                localHeight);
            BakeSmileOffsets(
                uvs,
                facialCurves.GetSampledEditableCurve(2),
                GetSampledSmileTarget(),
                localWidth,
                localHeight);
            BakeMuscleZones(
                uvs,
                smileMuscleZones,
                bakedSmileOffsets,
                localWidth,
                localHeight);
            BakeMuscleZones(
                uvs,
                sadnessMuscleZones,
                bakedSadnessOffsets,
                localWidth,
                localHeight);
            BakeMuscleZones(
                uvs,
                angerMuscleZones,
                bakedAngerOffsets,
                localWidth,
                localHeight);
            BakeMuscleZones(
                uvs,
                painMuscleZones,
                bakedPainOffsets,
                localWidth,
                localHeight);
            for (int poseIndex = 0; poseIndex < speechMouthPoses.Count; poseIndex++)
            {
                SpeechMouthPose2D pose = speechMouthPoses[poseIndex];
                if (pose == null)
                {
                    continue;
                }

                Vector2[] poseOffsets = new Vector2[uvs.Length];
                BakeSpeechMouthZones(
                    uvs,
                    pose,
                    mouthCutCurve,
                    lowerMouthSide,
                    poseOffsets,
                    localWidth,
                    localHeight);
                pose.SetBakedOffsets(poseOffsets);
            }
            BakeEyeOffsets(
                uvs,
                facialCurves.GetSampledEditableCurve(1),
                GetSampledOpenEyelidTarget(1),
                localWidth,
                localHeight);

            PreserveNoseShape(uvs, bakedOpenEyesOffsets);
            PreserveNoseShape(uvs, bakedSmileOffsets);
            PreserveNoseShape(uvs, bakedSadnessOffsets);
            PreserveNoseShape(uvs, bakedAngerOffsets);
            PreserveNoseShape(uvs, bakedPainOffsets);

            bakedCurveHash = CalculatePoseHash();
            bakedVersion = CurrentBakeVersion;
            bakeStatus = "Baked additive Eyes, Smile, Sadness, Anger, Pain and five speech-mouth poses for "
                + uvs.Length
                + " muscle vertices. Speech poses are mutually exclusive and drive the mandible where required.";
            return true;
        }

        private void BakeEyeOffsets(
            Vector2[] uvs,
            IList<Vector2> sourceCurve,
            IList<Vector2> targetCurve,
            float localWidth,
            float localHeight)
        {
            if (sourceCurve == null || sourceCurve.Count < 2
                || targetCurve == null || targetCurve.Count < 2)
            {
                return;
            }

            for (int vertex = 0; vertex < uvs.Length; vertex++)
            {
                Vector2 uv = uvs[vertex];
                if (!TryFindClosestCurvePoint(
                        uv,
                        sourceCurve,
                        out Vector2 closest,
                        out Vector2 tangent,
                        out float pathPosition))
                {
                    continue;
                }

                Vector2 normal = new Vector2(-tangent.y, tangent.x).normalized;
                if (normal.y < 0f)
                {
                    normal = -normal;
                }

                float signedDistance = Vector2.Dot(uv - closest, normal);
                // Only the tissue above the authored eyelid incision is raised. Keeping
                // the lower side fixed exposes the user's eyeball model without changing
                // the original skin texture or pulling the lower lid downward.
                if (signedDistance < 0f || signedDistance > eyeInfluenceHeight)
                {
                    continue;
                }

                float verticalInfluence = SmoothFalloff(
                    signedDistance / eyeInfluenceHeight);
                Vector2 targetPoint = EvaluatePolylineAtNormalizedLength(
                    targetCurve,
                    pathPosition);
                Vector2 normalizedOffset = (targetPoint - closest)
                    * verticalInfluence;

                bakedOpenEyesOffsets[vertex] += new Vector2(
                    normalizedOffset.x * localWidth,
                    normalizedOffset.y * localHeight);
            }
        }

        private void BakeSmileOffsets(
            Vector2[] uvs,
            IList<Vector2> sourceCurve,
            IList<Vector2> targetCurve,
            float localWidth,
            float localHeight)
        {
            if (sourceCurve == null || sourceCurve.Count < 2
                || targetCurve == null || targetCurve.Count < 2)
            {
                return;
            }

            for (int vertex = 0; vertex < uvs.Length; vertex++)
            {
                Vector2 uv = uvs[vertex];
                if (!TryFindClosestCurvePoint(
                        uv,
                        sourceCurve,
                        out Vector2 closest,
                        out _,
                        out float pathPosition))
                {
                    continue;
                }

                float distance = Vector2.Distance(uv, closest);
                if (distance > smileInfluenceRadius)
                {
                    continue;
                }

                Vector2 targetPoint = EvaluatePolylineAtNormalizedLength(
                    targetCurve,
                    pathPosition);
                float influence = SmoothFalloff(distance / smileInfluenceRadius);
                Vector2 normalizedOffset = (targetPoint - closest) * influence;
                bakedSmileOffsets[vertex] += new Vector2(
                    normalizedOffset.x * localWidth,
                    normalizedOffset.y * localHeight);
            }
        }

        private static void BakeMuscleZones(
            Vector2[] uvs,
            IList<FacialMuscleInfluenceZone2D> zones,
            Vector2[] destinationOffsets,
            float localWidth,
            float localHeight)
        {
            if (zones == null || destinationOffsets == null
                || destinationOffsets.Length != uvs.Length)
            {
                return;
            }

            for (int zoneIndex = 0; zoneIndex < zones.Count; zoneIndex++)
            {
                FacialMuscleInfluenceZone2D zone = zones[zoneIndex];
                if (zone == null)
                {
                    continue;
                }

                Vector2 radius = new Vector2(
                    Mathf.Max(0.0001f, zone.RadiusUv.x),
                    Mathf.Max(0.0001f, zone.RadiusUv.y));
                for (int vertex = 0; vertex < uvs.Length; vertex++)
                {
                    Vector2 normalizedDelta = new Vector2(
                        (uvs[vertex].x - zone.CenterUv.x) / radius.x,
                        (uvs[vertex].y - zone.CenterUv.y) / radius.y);
                    float ellipticalDistance = normalizedDelta.magnitude;
                    if (ellipticalDistance >= 1f)
                    {
                        continue;
                    }

                    float weight = SmoothFalloff(ellipticalDistance)
                        * zone.Strength;
                    Vector2 displacement = zone.DisplacementUv * weight;
                    destinationOffsets[vertex] += new Vector2(
                        displacement.x * localWidth,
                        displacement.y * localHeight);
                }
            }
        }

        private static bool[] BuildLowerMouthSideMask(
            Vector2[] uvs,
            int[] triangles,
            IList<Vector2> mouthCutCurve)
        {
            bool[] result = new bool[uvs.Length];
            float[] signedDistances = new float[uvs.Length];
            List<int>[] topologyNeighbours = new List<int>[uvs.Length];
            for (int index = 0; index < uvs.Length; index++)
            {
                topologyNeighbours[index] = new List<int>(6);
                if (TryFindClosestCurvePoint(
                        uvs[index],
                        mouthCutCurve,
                        out Vector2 closest,
                        out Vector2 tangent,
                        out _))
                {
                    Vector2 normal = new Vector2(-tangent.y, tangent.x).normalized;
                    if (normal.y < 0f)
                    {
                        normal = -normal;
                    }

                    signedDistances[index] = Vector2.Dot(uvs[index] - closest, normal);
                }
                else
                {
                    signedDistances[index] = uvs[index].y - 0.337f;
                }
            }

            if (triangles != null)
            {
                for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
                {
                    AddTopologyNeighbour(topologyNeighbours, triangles[triangle], triangles[triangle + 1]);
                    AddTopologyNeighbour(topologyNeighbours, triangles[triangle + 1], triangles[triangle + 2]);
                    AddTopologyNeighbour(topologyNeighbours, triangles[triangle + 2], triangles[triangle]);
                }
            }

            const float seamTolerance = 0.0015f;
            for (int vertex = 0; vertex < uvs.Length; vertex++)
            {
                if (signedDistances[vertex] < -seamTolerance)
                {
                    result[vertex] = true;
                    continue;
                }

                if (signedDistances[vertex] > seamTolerance)
                {
                    continue;
                }

                float lowerEvidence = 0f;
                float upperEvidence = 0f;
                List<int> connected = topologyNeighbours[vertex];
                for (int neighbourIndex = 0; neighbourIndex < connected.Count; neighbourIndex++)
                {
                    float side = signedDistances[connected[neighbourIndex]];
                    if (side < -seamTolerance)
                    {
                        lowerEvidence += -side;
                    }
                    else if (side > seamTolerance)
                    {
                        upperEvidence += side;
                    }
                }

                result[vertex] = lowerEvidence > upperEvidence && lowerEvidence > 0f;
            }

            return result;
        }

        private static void AddTopologyNeighbour(
            List<int>[] topologyNeighbours,
            int first,
            int second)
        {
            if (first < 0 || second < 0
                || first >= topologyNeighbours.Length
                || second >= topologyNeighbours.Length)
            {
                return;
            }

            if (!topologyNeighbours[first].Contains(second))
            {
                topologyNeighbours[first].Add(second);
            }

            if (!topologyNeighbours[second].Contains(first))
            {
                topologyNeighbours[second].Add(first);
            }
        }

        private static void BakeSpeechMouthZones(
            Vector2[] uvs,
            SpeechMouthPose2D pose,
            IList<Vector2> mouthCutCurve,
            bool[] lowerMouthSide,
            Vector2[] destinationOffsets,
            float localWidth,
            float localHeight)
        {
            if (pose == null || pose.MuscleZones == null
                || destinationOffsets == null
                || destinationOffsets.Length != uvs.Length)
            {
                return;
            }

            for (int zoneIndex = 0; zoneIndex < pose.MuscleZones.Count; zoneIndex++)
            {
                FacialMuscleInfluenceZone2D zone = pose.MuscleZones[zoneIndex];
                if (zone == null)
                {
                    continue;
                }

                string label = zone.Label ?? string.Empty;
                bool upperOnly = label.IndexOf(
                    "Upper",
                    StringComparison.OrdinalIgnoreCase) >= 0;
                bool lowerOnly = label.IndexOf(
                    "Lower",
                    StringComparison.OrdinalIgnoreCase) >= 0
                    || label.IndexOf(
                        "Chin",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                bool rigidLowerLipZone = label.IndexOf(
                    "Lower Lip",
                    StringComparison.OrdinalIgnoreCase) >= 0;
                Vector2 radius = new Vector2(
                    Mathf.Max(0.0001f, zone.RadiusUv.x),
                    Mathf.Max(0.0001f, zone.RadiusUv.y));
                for (int vertex = 0; vertex < uvs.Length; vertex++)
                {
                    Vector2 uv = uvs[vertex];
                    float signedSide = 0f;
                    bool hasCurveSide = false;
                    if ((upperOnly || lowerOnly)
                        && TryFindClosestCurvePoint(
                            uv,
                            mouthCutCurve,
                            out Vector2 closest,
                            out Vector2 tangent,
                            out _))
                    {
                        Vector2 normal = new Vector2(-tangent.y, tangent.x).normalized;
                        if (normal.y < 0f)
                        {
                            normal = -normal;
                        }

                        signedSide = Vector2.Dot(uv - closest, normal);
                        hasCurveSide = true;
                        // The authored mouth incision is the strict boundary between
                        // upper- and lower-lip speech weights. This keeps a lower-lip
                        // drop from pulling the upper edge down and resealing the mouth.
                        bool belongsToLowerSide = lowerMouthSide != null
                            && vertex < lowerMouthSide.Length
                            ? lowerMouthSide[vertex]
                            : signedSide < 0f;
                        if ((upperOnly && belongsToLowerSide)
                            || (lowerOnly && !belongsToLowerSide))
                        {
                            continue;
                        }
                    }

                    if (rigidLowerLipZone && hasCurveSide)
                    {
                        float depthBelowCut = -signedSide;
                        float maximumDepth = Mathf.Max(0.035f, radius.y * 1.15f);
                        float horizontalDistance = Mathf.Abs(
                            (uv.x - zone.CenterUv.x) / radius.x);
                        if (depthBelowCut < 0f || depthBelowCut > maximumDepth
                            || horizontalDistance >= 1f)
                        {
                            continue;
                        }

                        // Speech shaping treats the lower lip as a band: most of its
                        // width and thickness share one displacement, with only soft
                        // endpoint/deep-edge feathers. This prevents the center from
                        // being pulled into a long triangular tongue-like shape.
                        float horizontalWeight = horizontalDistance <= 0.72f
                            ? 1f
                            : SmoothFalloff(
                                (horizontalDistance - 0.72f) / 0.28f);
                        float verticalWeight = depthBelowCut <= radius.y * 0.85f
                            ? 1f
                            : SmoothFalloff(
                                (depthBelowCut - radius.y * 0.85f)
                                / Mathf.Max(0.005f, maximumDepth - radius.y * 0.85f));
                        Vector2 rigidDisplacement = zone.DisplacementUv
                            * (horizontalWeight * verticalWeight * zone.Strength);
                        destinationOffsets[vertex] += new Vector2(
                            rigidDisplacement.x * localWidth,
                            rigidDisplacement.y * localHeight);
                        continue;
                    }

                    Vector2 normalizedDelta = new Vector2(
                        (uv.x - zone.CenterUv.x) / radius.x,
                        (uv.y - zone.CenterUv.y) / radius.y);
                    float ellipticalDistance = normalizedDelta.magnitude;
                    if (ellipticalDistance >= 1f)
                    {
                        continue;
                    }

                    float weight = SmoothFalloff(ellipticalDistance)
                        * zone.Strength;
                    Vector2 displacement = zone.DisplacementUv * weight;
                    destinationOffsets[vertex] += new Vector2(
                        displacement.x * localWidth,
                        displacement.y * localHeight);
                }
            }
        }

        private void PreserveNoseShape(Vector2[] uvs, Vector2[] offsets)
        {
            if (uvs == null || offsets == null || uvs.Length != offsets.Length
                || noseShapeProtection <= 0f)
            {
                return;
            }

            Vector2 radius = new Vector2(
                Mathf.Max(0.002f, noseProtectionRadiusUv.x),
                Mathf.Max(0.002f, noseProtectionRadiusUv.y));
            Vector2 sharedTranslation = Vector2.zero;
            float totalWeight = 0f;
            for (int vertex = 0; vertex < uvs.Length; vertex++)
            {
                float mask = GetEllipticalFalloff(
                    uvs[vertex],
                    noseProtectionCenterUv,
                    radius);
                if (mask <= 0f)
                {
                    continue;
                }

                sharedTranslation += offsets[vertex] * mask;
                totalWeight += mask;
            }

            if (totalWeight <= 0.0001f)
            {
                return;
            }

            sharedTranslation /= totalWeight;
            for (int vertex = 0; vertex < uvs.Length; vertex++)
            {
                float mask = GetEllipticalFalloff(
                    uvs[vertex],
                    noseProtectionCenterUv,
                    radius) * noseShapeProtection;
                if (mask <= 0f)
                {
                    continue;
                }

                // A common translation preserves the nose silhouette while the soft
                // boundary still follows wrinkles and cheek movement around it.
                offsets[vertex] = Vector2.Lerp(
                    offsets[vertex],
                    sharedTranslation,
                    mask);
            }
        }

        private static float GetEllipticalFalloff(
            Vector2 uv,
            Vector2 center,
            Vector2 radius)
        {
            Vector2 normalized = new Vector2(
                (uv.x - center.x) / radius.x,
                (uv.y - center.y) / radius.y);
            float distance = normalized.magnitude;
            return distance < 1f ? SmoothFalloff(distance) : 0f;
        }

        private void EnsureTargetCurves()
        {
            bool eyesInvalid = leftOpenEyelidTarget == null
                || leftOpenEyelidTarget.ControlPoints == null
                || leftOpenEyelidTarget.ControlPoints.Count < 2
                || rightOpenEyelidTarget == null
                || rightOpenEyelidTarget.ControlPoints == null
                || rightOpenEyelidTarget.ControlPoints.Count < 2;
            if (eyesInvalid)
            {
                ResetOpenEyesTargetCurves();
            }

            if (smileTarget == null
                || smileTarget.ControlPoints == null
                || smileTarget.ControlPoints.Count < 2)
            {
                smileTarget = facialCurves != null
                    ? CreateDefaultSmileTargetCurve(facialCurves.GetEditableCurve(2))
                    : null;
            }
        }

        private void EnsureSmileMuscleZones()
        {
            if (smileMuscleZones == null || smileMuscleZones.Count == 0)
            {
                ResetSmileMuscleZones();
            }
        }

        private void EnsureEmotionMuscleZones()
        {
            bool sadnessMissing = sadnessMuscleZones == null
                || sadnessMuscleZones.Count == 0;
            bool angerMissing = angerMuscleZones == null
                || angerMuscleZones.Count == 0;
            bool painMissing = painMuscleZones == null
                || painMuscleZones.Count == 0;
            if (!sadnessMissing && !angerMissing && !painMissing)
            {
                return;
            }

            List<FacialMuscleInfluenceZone2D> existingSadness = sadnessMuscleZones;
            List<FacialMuscleInfluenceZone2D> existingAnger = angerMuscleZones;
            List<FacialMuscleInfluenceZone2D> existingPain = painMuscleZones;
            ResetEmotionMuscleZones();
            if (!sadnessMissing)
            {
                sadnessMuscleZones = existingSadness;
            }

            if (!angerMissing)
            {
                angerMuscleZones = existingAnger;
            }

            if (!painMissing)
            {
                painMuscleZones = existingPain;
            }
        }

        private void EnsureSpeechMouthPoses()
        {
            if (speechMouthPoses == null || speechMouthPoses.Count != 5)
            {
                ResetSpeechMouthPoses();
                return;
            }

            for (int index = 0; index < speechMouthPoses.Count; index++)
            {
                SpeechMouthPose2D pose = speechMouthPoses[index];
                if (pose == null || pose.MuscleZones == null
                    || pose.MuscleZones.Count == 0)
                {
                    ResetSpeechMouthPoses();
                    return;
                }
            }
        }

        private void UpgradeSpeechJawAndCornerProfile()
        {
            EnsureSpeechMouthPoses();
            for (int poseIndex = 0; poseIndex < speechMouthPoses.Count; poseIndex++)
            {
                SpeechMouthPose2D pose = speechMouthPoses[poseIndex];
                if (pose == null)
                {
                    continue;
                }

                pose.JawSideFollow = 0.005f;
                IList<FacialMuscleInfluenceZone2D> zones = pose.MuscleZones;
                for (int zoneIndex = 0; zoneIndex < zones.Count; zoneIndex++)
                {
                    FacialMuscleInfluenceZone2D zone = zones[zoneIndex];
                    if (zone == null
                        || (zone.Label ?? string.Empty).IndexOf(
                            "Corner",
                            StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    Vector2 displacement = zone.DisplacementUv;
                    bool pullsLeftCornerOut = zone.CenterUv.x < 0.5f
                        && displacement.x < 0f;
                    bool pullsRightCornerOut = zone.CenterUv.x > 0.5f
                        && displacement.x > 0f;
                    if (pullsLeftCornerOut || pullsRightCornerOut)
                    {
                        displacement.x = 0f;
                        zone.DisplacementUv = displacement;
                    }
                }
            }
        }

        private void UpgradeSpeechCornerAxisProfile()
        {
            EnsureSpeechMouthPoses();
            for (int poseIndex = 0; poseIndex < speechMouthPoses.Count; poseIndex++)
            {
                SpeechMouthPose2D pose = speechMouthPoses[poseIndex];
                if (pose == null)
                {
                    continue;
                }

                pose.JawSideFollow = 0f;
                IList<FacialMuscleInfluenceZone2D> zones = pose.MuscleZones;
                for (int zoneIndex = 0; zoneIndex < zones.Count; zoneIndex++)
                {
                    FacialMuscleInfluenceZone2D zone = zones[zoneIndex];
                    if (zone == null)
                    {
                        continue;
                    }

                    string label = zone.Label ?? string.Empty;
                    Vector2 displacement = zone.DisplacementUv;
                    if (label.IndexOf("A-E Left Corner", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        displacement = new Vector2(-0.012f, 0f);
                    }
                    else if (label.IndexOf("A-E Right Corner", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        displacement = new Vector2(0.012f, 0f);
                    }
                    else if (label.IndexOf("E Left Corner", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        displacement = new Vector2(-0.018f, 0f);
                    }
                    else if (label.IndexOf("E Right Corner", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        displacement = new Vector2(0.018f, 0f);
                    }
                    else if (label.IndexOf("Ah Left Corner", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        displacement = new Vector2(-0.008f, 0f);
                    }
                    else if (label.IndexOf("Ah Right Corner", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        displacement = new Vector2(0.008f, 0f);
                    }
                    else
                    {
                        continue;
                    }

                    zone.DisplacementUv = displacement;
                }
            }
        }

        private bool HasValidBakedChannel(Vector2[] channel)
        {
            return muscleLayer != null
                && channel != null
                && channel.Length == muscleLayer.LogicalVertexCount
                && channel.Length > 0;
        }

        private FacialCutCurve2D CreateDefaultTargetCurve(
            FacialCutCurve2D source,
            string label,
            Color color)
        {
            if (source == null || source.ControlPoints == null)
            {
                return new FacialCutCurve2D(label, color);
            }

            Vector2[] targets = new Vector2[source.ControlPoints.Count];
            for (int index = 0; index < targets.Length; index++)
            {
                float t = targets.Length > 1
                    ? index / (float)(targets.Length - 1)
                    : 0.5f;
                float arc = Mathf.Pow(
                    Mathf.Max(0f, Mathf.Sin(t * Mathf.PI)),
                    0.82f);
                targets[index] = source.ControlPoints[index]
                    + Vector2.up * (eyelidMuscleLift * arc);
                targets[index].x = Mathf.Clamp01(targets[index].x);
                targets[index].y = Mathf.Clamp01(targets[index].y);
            }

            return new FacialCutCurve2D(label, color, targets);
        }

        private FacialCutCurve2D CreateDefaultSmileTargetCurve(
            FacialCutCurve2D source)
        {
            const string label = "Smile Target";
            Color color = new Color(1f, 0.2f, 0.75f);
            if (source == null || source.ControlPoints == null)
            {
                return new FacialCutCurve2D(label, color);
            }

            Vector2[] targets = new Vector2[source.ControlPoints.Count];
            for (int index = 0; index < targets.Length; index++)
            {
                float t = targets.Length > 1
                    ? index / (float)(targets.Length - 1)
                    : 0.5f;
                float signedSide = (t - 0.5f) * 2f;
                float cornerWeight = Mathf.Pow(Mathf.Abs(signedSide), 1.55f);
                Vector2 offset = new Vector2(
                    signedSide * smileWiden,
                    cornerWeight * smileCornerLift);
                targets[index] = source.ControlPoints[index] + offset;
                targets[index].x = Mathf.Clamp01(targets[index].x);
                targets[index].y = Mathf.Clamp01(targets[index].y);
            }

            return new FacialCutCurve2D(label, color, targets);
        }

        private static List<Vector2> SampleCurve(List<Vector2> controlPoints)
        {
            List<Vector2> result = new List<Vector2>();
            if (controlPoints == null || controlPoints.Count < 2)
            {
                return result;
            }

            for (int segment = 0; segment + 1 < controlPoints.Count; segment++)
            {
                Vector2 p0 = controlPoints[Mathf.Max(0, segment - 1)];
                Vector2 p1 = controlPoints[segment];
                Vector2 p2 = controlPoints[segment + 1];
                Vector2 p3 = controlPoints[Mathf.Min(controlPoints.Count - 1, segment + 2)];
                for (int sample = 0; sample < TargetCurveSamplesPerSpan; sample++)
                {
                    float t = sample / (float)TargetCurveSamplesPerSpan;
                    result.Add(CatmullRom(p0, p1, p2, p3, t));
                }
            }

            result.Add(controlPoints[controlPoints.Count - 1]);
            return result;
        }

        private static Vector2 CatmullRom(
            Vector2 p0,
            Vector2 p1,
            Vector2 p2,
            Vector2 p3,
            float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * ((2f * p1)
                + (-p0 + p2) * t
                + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private static Vector2 EvaluatePolylineAtNormalizedLength(
            IList<Vector2> curve,
            float normalizedPosition)
        {
            float totalLength = 0f;
            for (int index = 0; index + 1 < curve.Count; index++)
            {
                totalLength += Vector2.Distance(curve[index], curve[index + 1]);
            }

            if (totalLength <= 0.000001f)
            {
                return curve[0];
            }

            float targetLength = Mathf.Clamp01(normalizedPosition) * totalLength;
            float traversed = 0f;
            for (int index = 0; index + 1 < curve.Count; index++)
            {
                float segmentLength = Vector2.Distance(curve[index], curve[index + 1]);
                if (traversed + segmentLength >= targetLength)
                {
                    float t = segmentLength > 0.000001f
                        ? (targetLength - traversed) / segmentLength
                        : 0f;
                    return Vector2.LerpUnclamped(curve[index], curve[index + 1], t);
                }

                traversed += segmentLength;
            }

            return curve[curve.Count - 1];
        }

        private static bool TryFindClosestCurvePoint(
            Vector2 point,
            IList<Vector2> curve,
            out Vector2 closest,
            out Vector2 tangent,
            out float pathPosition)
        {
            closest = default(Vector2);
            tangent = Vector2.right;
            pathPosition = 0f;
            float totalLength = 0f;
            for (int index = 0; index + 1 < curve.Count; index++)
            {
                totalLength += Vector2.Distance(curve[index], curve[index + 1]);
            }

            if (totalLength <= 0.000001f)
            {
                return false;
            }

            float bestDistance = float.PositiveInfinity;
            float traversed = 0f;
            for (int index = 0; index + 1 < curve.Count; index++)
            {
                Vector2 start = curve[index];
                Vector2 delta = curve[index + 1] - start;
                float length = delta.magnitude;
                if (length <= 0.000001f)
                {
                    continue;
                }

                float segmentT = Mathf.Clamp01(Vector2.Dot(point - start, delta)
                    / delta.sqrMagnitude);
                Vector2 candidate = start + delta * segmentT;
                float distance = (point - candidate).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    closest = candidate;
                    tangent = delta / length;
                    pathPosition = (traversed + segmentT * length) / totalLength;
                }

                traversed += length;
            }

            return !float.IsPositiveInfinity(bestDistance);
        }

        private static float SmoothFalloff(float normalizedDistance)
        {
            float value = 1f - Mathf.Clamp01(normalizedDistance);
            return value * value * (3f - 2f * value);
        }
    }
}
