using System.Collections;
using System.Collections.Generic;
using DeformableImage;
using UnityEngine;

namespace FaceAnalysis
{
    /// <summary>
    /// Fits the material-space proportions of a standard deformable face to one neutral,
    /// frontal player photograph. The fitted result becomes the mesh's mechanical home pose;
    /// UV and cutting coordinates remain attached to the original standard image.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PhotoFaceRestPoseFitter : MonoBehaviour
    {
        [Header("Required References")]
        [SerializeField] private DeformableImageMesh targetMesh;
        [SerializeField] private MediaPipeStaticFaceLandmarkSource landmarkSource;
        [SerializeField] private FaceLandmarkVisualizer landmarkVisualizer;

        [Header("Photographs")]
        [Tooltip("The unmodified standard face image. If empty, the target mesh front sprite is used.")]
        [SerializeField] private Sprite standardReferencePhoto;
        [Tooltip("Assign the player's neutral, front-facing, unobstructed photograph here.")]
        [SerializeField] private Sprite playerPhoto;

        [Header("Fit")]
        [Tooltip("0 keeps the standard shape; 1 applies the complete detected proportion change.")]
        [SerializeField, Range(0f, 1.25f)] private float fitStrength = 1f;
        [Tooltip("Prevents one inaccurate landmark from folding the regular skin grid.")]
        [SerializeField, Range(0.01f, 0.35f)] private float maximumNormalizedDisplacement = 0.2f;
        [SerializeField] private bool autoGenerateOnStart;

        [Header("Mild Pose Correction")]
        [SerializeField] private bool autoCorrectMildPose = true;
        [SerializeField, Range(0f, 30f)] private float maximumRollDegrees = 15f;
        [SerializeField, Range(0f, 30f)] private float maximumYawDegrees = 15f;
        [SerializeField, Range(0f, 30f)] private float maximumPitchDegrees = 12f;

        [Header("Runtime Test Panel")]
        [SerializeField] private bool showRuntimePanel = true;
        [SerializeField] private KeyCode togglePanelKey = KeyCode.F7;
        [SerializeField, Min(260f)] private float panelWidth = 430f;

        [Header("Status")]
        [SerializeField, TextArea(3, 7)] private string lastStatus =
            "Assign Player Photo, enter Play Mode, then press Generate Player Rest Face.";

        private FaceLandmarkFrame templateFrame;
        private FaceLandmarkFrame playerFrame;
        private Vector3[] standardRestPose;
        private Coroutine generationRoutine;
        private bool panelVisible = true;
        private Sprite originalFrontSprite;
        private Texture2D generatedFaceTexture;
        private Sprite generatedFaceSprite;
        private Sprite lastReferenceSprite;
        private List<ControlPoint> lastControls;
        private List<Triangle> lastTriangles;
        private string poseCorrectionStatus = "Pose correction not evaluated.";

        public string LastStatus { get { return lastStatus; } }
        public bool IsGenerating { get { return generationRoutine != null; } }

        /// <summary>
        /// Makes the face currently assigned to DeformableImageMesh the new standard
        /// image. This is deliberately different from swapping only the appearance:
        /// the mesh aspect, MediaPipe frame and barycentric landmark bindings are all
        /// rebuilt so no coordinates from the previous face survive.
        /// </summary>
        [ContextMenu("Synchronize Current Mesh Face And Redetect")]
        public void SynchronizeCurrentMeshFaceAndRedetect()
        {
            ResolveReferences();
            if (!Application.isPlaying)
            {
                lastStatus = "Enter Play Mode, then synchronize the current mesh face.";
                Debug.LogWarning(lastStatus, this);
                return;
            }

            if (targetMesh == null || landmarkSource == null)
            {
                lastStatus = "Assign Target Mesh and Landmark Source.";
                Debug.LogError(lastStatus, this);
                return;
            }

            Sprite newStandard = targetMesh.FrontSprite;
            if (newStandard == null)
            {
                lastStatus = "The target mesh has no front sprite.";
                Debug.LogWarning(lastStatus, this);
                return;
            }

            if (generationRoutine != null)
            {
                StopCoroutine(generationRoutine);
                generationRoutine = null;
            }

            DestroyGeneratedAppearance();
            originalFrontSprite = newStandard;
            standardReferencePhoto = newStandard;
            templateFrame = null;
            playerFrame = null;
            standardRestPose = null;
            lastReferenceSprite = null;
            lastControls = null;
            lastTriangles = null;

            // ConfigureSprites performs a real topology/rest-pose rebuild. A texture-only
            // swap is not valid when a replacement image has a different aspect or rect.
            targetMesh.ConfigureSprites(newStandard, targetMesh.BackSprite);
            CaptureStandardRestPose();
            landmarkVisualizer?.InvalidateBindings();
            landmarkSource.SetStaticSprite(newStandard, true);
            generationRoutine = StartCoroutine(WaitForStandardFaceRefresh(newStandard));
        }

        private IEnumerator WaitForStandardFaceRefresh(Sprite expectedSprite)
        {
            lastStatus = "Rebuilding the standard face and detecting fresh landmarks...";
            FaceLandmarkFrame refreshedFrame = null;
            yield return WaitForValidFrame(5f, frame => refreshedFrame = CloneFrame(frame));

            if (refreshedFrame == null || !refreshedFrame.IsValid)
            {
                lastStatus = "No face was detected in the new standard image. Use one centered, frontal, neutral, open-eye face.";
                generationRoutine = null;
                yield break;
            }

            landmarkVisualizer?.InvalidateBindings();
            templateFrame = refreshedFrame;
            Rect rect = expectedSprite.textureRect;
            lastStatus = "New standard face synchronized: "
                + Mathf.RoundToInt(rect.width) + " x " + Mathf.RoundToInt(rect.height)
                + ". Fresh landmark bindings are active.";
            generationRoutine = null;
        }

        public void GenerateFromCapturedCameraFrame(
            Sprite capturedPlayerSprite,
            FaceLandmarkFrame capturedFrame,
            System.Action<bool, string> completed = null)
        {
            if (!Application.isPlaying || capturedPlayerSprite == null
                || capturedFrame == null || !capturedFrame.IsValid)
            {
                const string message = "Captured camera face data is invalid.";
                lastStatus = message;
                completed?.Invoke(false, message);
                return;
            }

            ResolveReferences();
            if (targetMesh == null || landmarkSource == null)
            {
                const string message = "Assign Target Mesh and Landmark Source.";
                lastStatus = message;
                completed?.Invoke(false, message);
                return;
            }

            if (generationRoutine != null)
            {
                StopCoroutine(generationRoutine);
            }

            playerPhoto = capturedPlayerSprite;
            generationRoutine = StartCoroutine(
                GenerateFromCapturedRoutine(capturedPlayerSprite, CloneFrame(capturedFrame), completed));
        }

        private void Awake()
        {
            ResolveReferences();
            originalFrontSprite = targetMesh != null ? targetMesh.FrontSprite : null;
            if (standardReferencePhoto == null)
            {
                standardReferencePhoto = originalFrontSprite;
            }

            // A replacement sprite is commonly assigned only on DeformableImageMesh.
            // Keep static analysis on that exact same asset from the first frame instead
            // of silently displaying a new face with the old face's landmarks.
            if (originalFrontSprite != null
                && landmarkSource != null
                && landmarkSource.InputMode == FaceAnalysisInputMode.StaticSprite
                && landmarkSource.SourceSprite != originalFrontSprite)
            {
                landmarkSource.SetStaticSprite(originalFrontSprite, false);
                landmarkVisualizer?.InvalidateBindings();
            }

            panelVisible = showRuntimePanel;
        }

        private IEnumerator Start()
        {
            // Let DeformableImageMesh and the depth-map component establish their initial pose.
            yield return null;
            CaptureStandardRestPose();
            if (autoGenerateOnStart && playerPhoto != null)
            {
                GeneratePlayerRestFace();
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(togglePanelKey))
            {
                panelVisible = !panelVisible;
            }
        }

        private void OnValidate()
        {
            fitStrength = Mathf.Clamp(fitStrength, 0f, 1.25f);
            maximumNormalizedDisplacement = Mathf.Clamp(maximumNormalizedDisplacement, 0.01f, 0.35f);
            maximumRollDegrees = Mathf.Clamp(maximumRollDegrees, 0f, 30f);
            maximumYawDegrees = Mathf.Clamp(maximumYawDegrees, 0f, 30f);
            maximumPitchDegrees = Mathf.Clamp(maximumPitchDegrees, 0f, 30f);
            panelWidth = Mathf.Max(260f, panelWidth);
        }

        private void OnDestroy()
        {
            DestroyGeneratedAppearance();
        }

        [ContextMenu("Generate Player Rest Face")]
        public void GeneratePlayerRestFace()
        {
            if (!Application.isPlaying)
            {
                lastStatus = "Enter Play Mode before running MediaPipe photo fitting.";
                Debug.LogWarning(lastStatus, this);
                return;
            }

            ResolveReferences();
            if (targetMesh == null || landmarkSource == null)
            {
                lastStatus = "Assign Target Mesh and Landmark Source.";
                Debug.LogError(lastStatus, this);
                return;
            }

            if (playerPhoto == null)
            {
                lastStatus = "Player Photo is empty.";
                Debug.LogWarning(lastStatus, this);
                return;
            }

            if (generationRoutine != null)
            {
                StopCoroutine(generationRoutine);
            }

            generationRoutine = StartCoroutine(GenerateRoutine());
        }

        [ContextMenu("Restore Standard Rest Face")]
        public void RestoreStandardRestFace()
        {
            ResolveReferences();
            if (targetMesh == null)
            {
                return;
            }

            if (standardRestPose == null || standardRestPose.Length != targetMesh.LogicalVertexCount)
            {
                lastStatus = "The standard rest pose has not been captured in this Play session.";
                return;
            }

            targetMesh.ApplyRestPose(standardRestPose, false);
            UseOriginalFacePattern(false);
            lastStatus = "Restored the standard face as the mechanical rest pose.";
        }

        [ContextMenu("Apply Player Photo Pattern")]
        public void ApplyPlayerPhotoPattern()
        {
            if (!Application.isPlaying)
            {
                lastStatus = "Enter Play Mode before applying a runtime face pattern.";
                return;
            }

            if (generationRoutine != null)
            {
                lastStatus = "Wait for face-shape generation to finish first.";
                return;
            }

            if (lastReferenceSprite == null || playerPhoto == null
                || lastControls == null || lastControls.Count == 0
                || lastTriangles == null || lastTriangles.Count == 0)
            {
                lastStatus = "Generate Player Rest Face once before applying the player photo pattern.";
                return;
            }

            lastStatus = "Reprojecting the optional player photo pattern...";
            if (TryApplyPlayerTexture(lastReferenceSprite, playerPhoto, lastControls, lastTriangles))
            {
                lastStatus = "Player photo pattern applied. Face geometry was not changed again.";
            }
            else
            {
                lastStatus = "Player photo pattern could not be generated.";
            }
        }

        [ContextMenu("Use Original Face Pattern")]
        public void UseOriginalFacePattern()
        {
            UseOriginalFacePattern(true);
        }

        private IEnumerator GenerateRoutine()
        {
            lastStatus = "Analyzing the standard reference face...";
            CaptureStandardRestPose();
            Sprite reference = standardReferencePhoto != null
                ? standardReferencePhoto
                : originalFrontSprite != null ? originalFrontSprite : targetMesh.FrontSprite;
            if (reference == null)
            {
                FinishWithError("No standard reference photo is available.");
                yield break;
            }

            // Shape generation always starts from and keeps the standard appearance.
            // Replacing the visible pattern is an explicit, separate user action.
            UseOriginalFacePattern(false);

            landmarkSource.SetStaticSprite(reference, true);
            yield return WaitForValidFrame(5f, frame => templateFrame = CloneFrame(frame));
            if (templateFrame == null || !templateFrame.IsValid)
            {
                FinishWithError("MediaPipe could not detect the standard reference face.");
                yield break;
            }

            lastStatus = "Analyzing the player photograph...";
            landmarkSource.SetStaticSprite(playerPhoto, true);
            yield return WaitForValidFrame(5f, frame => playerFrame = CloneFrame(frame));
            if (playerFrame == null || !playerFrame.IsValid)
            {
                FinishWithError("MediaPipe could not detect the player face. Use a clear, neutral frontal photo.");
                yield break;
            }

            if (!TryBuildControls(
                    reference,
                    playerPhoto,
                    templateFrame,
                    playerFrame,
                    out List<ControlPoint> controls,
                    out List<Triangle> triangles)
                || !TryApplyFit(controls, triangles))
            {
                FinishWithError(
                    "The aspect-correct landmark mesh could not be built, so the face was not changed. "
                    + poseCorrectionStatus);
                yield break;
            }

            lastReferenceSprite = reference;
            lastControls = controls;
            lastTriangles = triangles;

            // Existing measurement/visualization components must stay bound to the
            // standard material-space anchors. The current surface beneath those anchors
            // has moved to the player proportions, so UI values still update correctly.
            lastStatus = "Refreshing standard landmark anchors for live measurements...";
            landmarkSource.SetStaticSprite(reference, true);
            FaceLandmarkFrame restoredReference = null;
            yield return WaitForValidFrame(5f, frame => restoredReference = frame);
            lastStatus = restoredReference != null && restoredReference.IsValid
                ? "Player proportions applied to the original face pattern. "
                    + poseCorrectionStatus
                    + " Use the separate pattern button only if needed."
                : "Player proportions applied, but the live measurement anchors could not be refreshed.";
            generationRoutine = null;
        }

        private IEnumerator GenerateFromCapturedRoutine(
            Sprite capturedPlayerSprite,
            FaceLandmarkFrame capturedFrame,
            System.Action<bool, string> completed)
        {
            CaptureStandardRestPose();
            Sprite reference = standardReferencePhoto != null
                ? standardReferencePhoto
                : originalFrontSprite != null ? originalFrontSprite : targetMesh.FrontSprite;
            if (reference == null)
            {
                const string message = "No standard reference photo is available.";
                lastStatus = message;
                generationRoutine = null;
                completed?.Invoke(false, message);
                yield break;
            }

            UseOriginalFacePattern(false);
            lastStatus = "Analyzing standard anchors for the completed camera capture...";
            landmarkSource.SetStaticSprite(reference, true);
            templateFrame = null;
            yield return WaitForValidFrame(5f, frame => templateFrame = CloneFrame(frame));
            if (templateFrame == null || !templateFrame.IsValid)
            {
                const string message = "MediaPipe could not detect the standard reference face.";
                lastStatus = message;
                generationRoutine = null;
                completed?.Invoke(false, message);
                yield break;
            }

            if (!TryBuildControls(
                    reference,
                    capturedPlayerSprite,
                    templateFrame,
                    capturedFrame,
                    out List<ControlPoint> controls,
                    out List<Triangle> triangles)
                || !TryApplyFit(controls, triangles))
            {
                string message = "Camera face fitting failed. " + poseCorrectionStatus;
                lastStatus = message;
                generationRoutine = null;
                completed?.Invoke(false, message);
                yield break;
            }

            lastReferenceSprite = reference;
            lastControls = controls;
            lastTriangles = triangles;
            lastStatus = "Camera enrollment completed. The original standard pattern now uses the captured face proportions. "
                + poseCorrectionStatus;
            generationRoutine = null;
            completed?.Invoke(true, lastStatus);
        }

        private IEnumerator WaitForValidFrame(float timeoutSeconds, System.Action<FaceLandmarkFrame> capture)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (landmarkSource.TryGetLatestFrame(out FaceLandmarkFrame frame))
                {
                    if (frame != null && frame.IsValid)
                    {
                        capture(frame);
                    }

                    yield break;
                }

                yield return null;
            }
        }

        private bool TryBuildControls(
            Sprite referenceSprite,
            Sprite playerSprite,
            FaceLandmarkFrame reference,
            FaceLandmarkFrame player,
            out List<ControlPoint> controls,
            out List<Triangle> triangles)
        {
            controls = new List<ControlPoint>(480);
            triangles = new List<Triangle>();
            Vector3[] referenceLandmarks = reference.NormalizedLandmarks;
            Vector3[] playerLandmarks = player.NormalizedLandmarks;
            int count = Mathf.Min(
                MediaPipeFaceLandmarkIndices.MinimumLandmarkCount,
                Mathf.Min(referenceLandmarks.Length, playerLandmarks.Length));
            if (count < MediaPipeFaceLandmarkIndices.MinimumLandmarkCount)
            {
                return false;
            }

            float referenceAspect = SpriteAspect(referenceSprite);
            float playerAspect = SpriteAspect(playerSprite);
            poseCorrectionStatus = "Pose correction data is invalid.";
            int leftEye = MediaPipeFaceLandmarkIndices.ImageLeftEyeOuter;
            int rightEye = MediaPipeFaceLandmarkIndices.ImageRightEyeOuter;
            Vector2 referenceLeft = LandmarkToSpriteLocal(referenceLandmarks[leftEye], reference, referenceSprite);
            Vector2 referenceRight = LandmarkToSpriteLocal(referenceLandmarks[rightEye], reference, referenceSprite);
            Vector2 playerLeft = LandmarkToSpriteLocal(playerLandmarks[leftEye], player, playerSprite);
            Vector2 playerRight = LandmarkToSpriteLocal(playerLandmarks[rightEye], player, playerSprite);
            Vector2 referenceLeftMetric = ToAspectMetric(referenceLeft, referenceAspect);
            Vector2 referenceRightMetric = ToAspectMetric(referenceRight, referenceAspect);
            Vector2 playerLeftMetric = ToAspectMetric(playerLeft, playerAspect);
            Vector2 playerRightMetric = ToAspectMetric(playerRight, playerAspect);
            Vector2 referenceEyeVector = referenceRightMetric - referenceLeftMetric;
            Vector2 playerEyeVector = playerRightMetric - playerLeftMetric;
            float referenceEyeDistance = referenceEyeVector.magnitude;
            float playerEyeDistance = playerEyeVector.magnitude;
            if (referenceEyeDistance < 0.0001f || playerEyeDistance < 0.0001f)
            {
                return false;
            }

            Vector2 referenceEyeCenter = (referenceLeftMetric + referenceRightMetric) * 0.5f;
            Vector2 playerEyeCenter = (playerLeftMetric + playerRightMetric) * 0.5f;
            float alignmentScale = referenceEyeDistance / playerEyeDistance;
            float alignmentAngle = Vector2.SignedAngle(playerEyeVector, referenceEyeVector) * Mathf.Deg2Rad;
            float cosine = Mathf.Cos(alignmentAngle);
            float sine = Mathf.Sin(alignmentAngle);

            FaceBasis referenceBasis = default(FaceBasis);
            FaceBasis playerBasis = default(FaceBasis);
            bool hasReferenceBasis = TryCreateFaceBasis(
                referenceLandmarks,
                reference,
                referenceSprite,
                referenceAspect,
                out referenceBasis);
            bool hasPlayerBasis = TryCreateFaceBasis(
                playerLandmarks,
                player,
                playerSprite,
                playerAspect,
                out playerBasis);
            bool has3DFrames = hasReferenceBasis && hasPlayerBasis;
            if (autoCorrectMildPose && !has3DFrames)
            {
                return false;
            }

            float relativeRoll = 0f;
            float relativeYaw = 0f;
            float relativePitch = 0f;
            float poseAlignmentScale = alignmentScale;
            if (autoCorrectMildPose)
            {
                relativeRoll = Mathf.DeltaAngle(referenceBasis.Roll, playerBasis.Roll);
                relativeYaw = Mathf.DeltaAngle(referenceBasis.Yaw, playerBasis.Yaw);
                relativePitch = Mathf.DeltaAngle(referenceBasis.Pitch, playerBasis.Pitch);
                poseCorrectionStatus = "Detected pose: roll " + relativeRoll.ToString("0.0")
                    + "°, yaw " + relativeYaw.ToString("0.0")
                    + "°, pitch " + relativePitch.ToString("0.0") + "°.";
                if (Mathf.Abs(relativeRoll) > maximumRollDegrees
                    || Mathf.Abs(relativeYaw) > maximumYawDegrees
                    || Mathf.Abs(relativePitch) > maximumPitchDegrees)
                {
                    poseCorrectionStatus += " This exceeds the configured safe correction range; use a more frontal photo.";
                    return false;
                }

                poseAlignmentScale = referenceBasis.EyeDistance
                    / Mathf.Max(0.0001f, playerBasis.EyeDistance);
                poseCorrectionStatus += " Mild pose was corrected before fitting.";
            }
            else
            {
                poseCorrectionStatus = "Automatic 3D pose correction is disabled; only eye-line roll alignment was used.";
            }

            for (int index = 0; index < count; index++)
            {
                Vector2 referencePoint = LandmarkToSpriteLocal(referenceLandmarks[index], reference, referenceSprite);
                Vector2 playerPoint = LandmarkToSpriteLocal(playerLandmarks[index], player, playerSprite);
                Vector2 alignedPlayerMetric;
                if (autoCorrectMildPose)
                {
                    Vector3 playerMetric3D = LandmarkToAspectMetric3D(
                        playerLandmarks[index],
                        player,
                        playerSprite,
                        playerAspect);
                    Vector3 coordinates = playerBasis.ToLocal(playerMetric3D);
                    Vector3 aligned3D = referenceBasis.FromLocal(coordinates * poseAlignmentScale);
                    alignedPlayerMetric = new Vector2(aligned3D.x, aligned3D.y);
                }
                else
                {
                    Vector2 playerOffset = ToAspectMetric(playerPoint, playerAspect) - playerEyeCenter;
                    Vector2 rotatedOffset = new Vector2(
                        playerOffset.x * cosine - playerOffset.y * sine,
                        playerOffset.x * sine + playerOffset.y * cosine);
                    alignedPlayerMetric = referenceEyeCenter + rotatedOffset * alignmentScale;
                }

                Vector2 alignedPlayer = FromAspectMetric(alignedPlayerMetric, referenceAspect);
                Vector2 displacement = Vector2.ClampMagnitude(
                    alignedPlayer - referencePoint,
                    maximumNormalizedDisplacement);
                controls.Add(new ControlPoint(
                    referencePoint,
                    referencePoint + displacement,
                    playerPoint));
            }

            AddBoundaryControls(
                controls,
                referenceAspect,
                playerAspect,
                referenceEyeCenter,
                playerEyeCenter,
                alignmentScale,
                cosine,
                sine);
            triangles = BuildDelaunay(controls);
            return triangles.Count > 0;
        }

        private bool TryApplyFit(List<ControlPoint> controls, List<Triangle> triangles)
        {
            Vector3[] materialVertices = targetMesh.CopyMaterialSpaceVertices();
            Vector3[] sourcePose = standardRestPose != null
                && standardRestPose.Length == materialVertices.Length
                    ? standardRestPose
                    : targetMesh.CopyRestVertices();
            Vector2[] materialUvs = targetMesh.CopyNormalizedRestUvs();
            if (sourcePose.Length != materialUvs.Length || sourcePose.Length == 0)
            {
                return false;
            }

            Bounds materialBounds = CalculateBounds(materialVertices);
            float width = Mathf.Max(0.0001f, materialBounds.size.x);
            float height = Mathf.Max(0.0001f, materialBounds.size.y);
            Vector3[] fittedPose = new Vector3[sourcePose.Length];
            for (int vertex = 0; vertex < fittedPose.Length; vertex++)
            {
                Vector2 query = new Vector2(materialUvs[vertex].x, 1f - materialUvs[vertex].y);
                if (!TryFindTriangle(query, controls, triangles, out Triangle triangle, out Vector3 barycentric))
                {
                    fittedPose[vertex] = sourcePose[vertex];
                    continue;
                }

                Vector2 mapped = controls[triangle.A].ShapeTarget * barycentric.x
                    + controls[triangle.B].ShapeTarget * barycentric.y
                    + controls[triangle.C].ShapeTarget * barycentric.z;
                Vector2 normalizedDelta = Vector2.ClampMagnitude(
                    mapped - query,
                    maximumNormalizedDisplacement) * fitStrength;

                fittedPose[vertex] = sourcePose[vertex] + new Vector3(
                    normalizedDelta.x * width,
                    -normalizedDelta.y * height,
                    0f);
            }

            return targetMesh.ApplyRestPose(fittedPose, false);
        }

        private bool TryApplyPlayerTexture(
            Sprite referenceSprite,
            Sprite sourcePlayerSprite,
            List<ControlPoint> controls,
            List<Triangle> triangles)
        {
            if (referenceSprite == null || referenceSprite.texture == null
                || sourcePlayerSprite == null || sourcePlayerSprite.texture == null)
            {
                return false;
            }

            Texture2D readableReference = MakeReadableCopy(referenceSprite.texture);
            Texture2D readablePlayer = MakeReadableCopy(sourcePlayerSprite.texture);
            if (readableReference == null || readablePlayer == null)
            {
                DestroyRuntimeObject(readableReference);
                DestroyRuntimeObject(readablePlayer);
                return false;
            }

            Rect referenceRect = referenceSprite.textureRect;
            Rect playerRect = sourcePlayerSprite.textureRect;
            int outputWidth = readableReference.width;
            int outputHeight = readableReference.height;
            int rectWidth = Mathf.Max(1, Mathf.RoundToInt(referenceRect.width));
            int rectHeight = Mathf.Max(1, Mathf.RoundToInt(referenceRect.height));
            int referenceOriginX = Mathf.RoundToInt(referenceRect.xMin);
            int referenceOriginY = Mathf.RoundToInt(referenceRect.yMin);
            Color32[] referencePixels = readableReference.GetPixels32();
            Color32[] playerPixels = readablePlayer.GetPixels32();
            Color32[] outputPixels = new Color32[outputWidth * outputHeight];

            for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
            {
                Triangle triangle = triangles[triangleIndex];
                Vector2 a = controls[triangle.A].Reference;
                Vector2 b = controls[triangle.B].Reference;
                Vector2 c = controls[triangle.C].Reference;
                int minimumX = Mathf.Clamp(
                    Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) * rectWidth),
                    0,
                    rectWidth - 1);
                int maximumX = Mathf.Clamp(
                    Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) * rectWidth),
                    0,
                    rectWidth - 1);
                int minimumTopY = Mathf.Clamp(
                    Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) * rectHeight),
                    0,
                    rectHeight - 1);
                int maximumTopY = Mathf.Clamp(
                    Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) * rectHeight),
                    0,
                    rectHeight - 1);

                for (int topY = minimumTopY; topY <= maximumTopY; topY++)
                {
                    float normalizedY = (topY + 0.5f) / rectHeight;
                    int localBottomY = rectHeight - 1 - topY;
                    int outputY = referenceOriginY + localBottomY;
                    if (outputY < 0 || outputY >= outputHeight)
                    {
                        continue;
                    }

                    for (int x = minimumX; x <= maximumX; x++)
                    {
                        Vector2 query = new Vector2((x + 0.5f) / rectWidth, normalizedY);
                        if (!TryGetBarycentric(query, a, b, c, out Vector3 barycentric)
                            || barycentric.x < -0.0001f
                            || barycentric.y < -0.0001f
                            || barycentric.z < -0.0001f)
                        {
                            continue;
                        }

                        Vector2 playerLocal = controls[triangle.A].PlayerSource * barycentric.x
                            + controls[triangle.B].PlayerSource * barycentric.y
                            + controls[triangle.C].PlayerSource * barycentric.z;
                        float playerPixelX = playerRect.xMin
                            + Mathf.Clamp01(playerLocal.x) * playerRect.width;
                        float playerPixelY = playerRect.yMax
                            - Mathf.Clamp01(playerLocal.y) * playerRect.height;
                        Color playerColor = SampleBilinear(
                            playerPixels,
                            readablePlayer.width,
                            readablePlayer.height,
                            playerPixelX / readablePlayer.width,
                            playerPixelY / readablePlayer.height);

                        int outputX = referenceOriginX + x;
                        if (outputX < 0 || outputX >= outputWidth)
                        {
                            continue;
                        }

                        int outputIndex = outputY * outputWidth + outputX;
                        byte referenceAlpha = referencePixels[outputIndex].a;
                        outputPixels[outputIndex] = new Color(
                            playerColor.r,
                            playerColor.g,
                            playerColor.b,
                            playerColor.a * (referenceAlpha / 255f));
                    }
                }
            }

            Texture2D newTexture = new Texture2D(
                outputWidth,
                outputHeight,
                TextureFormat.RGBA32,
                false,
                false)
            {
                name = "Generated Player Face Texture",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            newTexture.SetPixels32(outputPixels);
            newTexture.Apply(false, false);
            Vector2 normalizedPivot = new Vector2(
                referenceSprite.pivot.x / Mathf.Max(1f, referenceSprite.rect.width),
                referenceSprite.pivot.y / Mathf.Max(1f, referenceSprite.rect.height));
            Sprite newSprite = Sprite.Create(
                newTexture,
                referenceRect,
                normalizedPivot,
                referenceSprite.pixelsPerUnit,
                0,
                SpriteMeshType.FullRect,
                referenceSprite.border);
            newSprite.name = "Generated Player Face Sprite";

            Texture2D previousTexture = generatedFaceTexture;
            Sprite previousSprite = generatedFaceSprite;
            generatedFaceTexture = newTexture;
            generatedFaceSprite = newSprite;
            targetMesh.SetFrontSpriteWithoutRebuild(generatedFaceSprite);
            DestroyRuntimeObject(previousSprite);
            DestroyRuntimeObject(previousTexture);
            DestroyRuntimeObject(readableReference);
            DestroyRuntimeObject(readablePlayer);
            return true;
        }

        private Vector2 LandmarkToSpriteLocal(
            Vector3 landmark,
            FaceLandmarkFrame frame,
            Sprite sprite)
        {
            Rect rect = sprite.textureRect;
            float x = landmarkSource != null && landmarkSource.InputWasFlippedHorizontally
                ? 1f - landmark.x
                : landmark.x;
            float pixelX = x * frame.ImageWidth;
            float pixelTopY = landmark.y * frame.ImageHeight;
            float rectTopY = frame.ImageHeight - rect.yMax;
            return new Vector2(
                (pixelX - rect.xMin) / Mathf.Max(1f, rect.width),
                (pixelTopY - rectTopY) / Mathf.Max(1f, rect.height));
        }

        private static float SpriteAspect(Sprite sprite)
        {
            Rect rect = sprite.textureRect;
            return rect.width / Mathf.Max(1f, rect.height);
        }

        private static Vector2 ToAspectMetric(Vector2 normalized, float aspect)
        {
            return new Vector2(normalized.x * aspect, normalized.y);
        }

        private static Vector2 FromAspectMetric(Vector2 metric, float aspect)
        {
            return new Vector2(metric.x / Mathf.Max(0.0001f, aspect), metric.y);
        }

        private Vector3 LandmarkToAspectMetric3D(
            Vector3 landmark,
            FaceLandmarkFrame frame,
            Sprite sprite,
            float aspect)
        {
            Vector2 local = LandmarkToSpriteLocal(landmark, frame, sprite);
            // MediaPipe Z uses approximately the same scale as normalized image X.
            // Convert all three components into image-height units before rotation so a
            // portrait photograph is never treated as a square coordinate system.
            float zInHeightUnits = landmark.z * frame.ImageWidth
                / Mathf.Max(1f, sprite.textureRect.height);
            return new Vector3(local.x * aspect, local.y, zInHeightUnits);
        }

        private bool TryCreateFaceBasis(
            Vector3[] landmarks,
            FaceLandmarkFrame frame,
            Sprite sprite,
            float aspect,
            out FaceBasis basis)
        {
            basis = default(FaceBasis);
            int required = Mathf.Max(
                MediaPipeFaceLandmarkIndices.Chin,
                Mathf.Max(
                    MediaPipeFaceLandmarkIndices.ImageRightEyeOuter,
                    MediaPipeFaceLandmarkIndices.ForeheadCenter));
            if (landmarks == null || landmarks.Length <= required)
            {
                return false;
            }

            Vector3 leftEye = LandmarkToAspectMetric3D(
                landmarks[MediaPipeFaceLandmarkIndices.ImageLeftEyeOuter],
                frame,
                sprite,
                aspect);
            Vector3 rightEye = LandmarkToAspectMetric3D(
                landmarks[MediaPipeFaceLandmarkIndices.ImageRightEyeOuter],
                frame,
                sprite,
                aspect);
            Vector3 forehead = LandmarkToAspectMetric3D(
                landmarks[MediaPipeFaceLandmarkIndices.ForeheadCenter],
                frame,
                sprite,
                aspect);
            Vector3 chin = LandmarkToAspectMetric3D(
                landmarks[MediaPipeFaceLandmarkIndices.Chin],
                frame,
                sprite,
                aspect);

            Vector3 xAxis = rightEye - leftEye;
            float eyeDistance = xAxis.magnitude;
            if (eyeDistance < 0.0001f)
            {
                return false;
            }

            xAxis /= eyeDistance;
            Vector3 yAxis = chin - forehead;
            yAxis -= xAxis * Vector3.Dot(yAxis, xAxis);
            if (yAxis.sqrMagnitude < 0.0000001f)
            {
                return false;
            }

            yAxis.Normalize();
            Vector3 zAxis = Vector3.Cross(xAxis, yAxis);
            if (zAxis.sqrMagnitude < 0.0000001f)
            {
                return false;
            }

            zAxis.Normalize();
            yAxis = Vector3.Cross(zAxis, xAxis).normalized;
            basis = new FaceBasis(
                (leftEye + rightEye) * 0.5f,
                xAxis,
                yAxis,
                zAxis,
                eyeDistance);
            return true;
        }

        private static void AddBoundaryControls(
            List<ControlPoint> controls,
            float referenceAspect,
            float playerAspect,
            Vector2 referenceEyeCenter,
            Vector2 playerEyeCenter,
            float alignmentScale,
            float cosine,
            float sine)
        {
            Vector2[] boundary =
            {
                new Vector2(0f, 0f), new Vector2(0.5f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 0.5f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, 1f), new Vector2(0f, 0.5f)
            };
            float inverseScale = 1f / Mathf.Max(0.0001f, alignmentScale);
            for (int index = 0; index < boundary.Length; index++)
            {
                Vector2 referencePoint = boundary[index];
                Vector2 alignedOffset = ToAspectMetric(referencePoint, referenceAspect)
                    - referenceEyeCenter;
                Vector2 inverseRotated = new Vector2(
                    alignedOffset.x * cosine + alignedOffset.y * sine,
                    -alignedOffset.x * sine + alignedOffset.y * cosine);
                Vector2 playerMetric = playerEyeCenter + inverseRotated * inverseScale;
                controls.Add(new ControlPoint(
                    referencePoint,
                    referencePoint,
                    FromAspectMetric(playerMetric, playerAspect)));
            }
        }

        private static List<Triangle> BuildDelaunay(List<ControlPoint> controls)
        {
            int originalCount = controls.Count;
            List<Vector2> points = new List<Vector2>(originalCount + 3);
            for (int index = 0; index < originalCount; index++)
            {
                points.Add(controls[index].Reference);
            }

            points.Add(new Vector2(-16f, -8f));
            points.Add(new Vector2(16f, -8f));
            points.Add(new Vector2(0f, 16f));
            int superA = originalCount;
            int superB = originalCount + 1;
            int superC = originalCount + 2;
            List<Triangle> result = new List<Triangle> { new Triangle(superA, superB, superC) };

            for (int pointIndex = 0; pointIndex < originalCount; pointIndex++)
            {
                Dictionary<ulong, EdgeUse> edgeUses = new Dictionary<ulong, EdgeUse>();
                for (int triangleIndex = result.Count - 1; triangleIndex >= 0; triangleIndex--)
                {
                    Triangle triangle = result[triangleIndex];
                    if (!PointInsideCircumcircle(
                            points[pointIndex],
                            points[triangle.A],
                            points[triangle.B],
                            points[triangle.C]))
                    {
                        continue;
                    }

                    AddEdgeUse(edgeUses, triangle.A, triangle.B);
                    AddEdgeUse(edgeUses, triangle.B, triangle.C);
                    AddEdgeUse(edgeUses, triangle.C, triangle.A);
                    result.RemoveAt(triangleIndex);
                }

                foreach (KeyValuePair<ulong, EdgeUse> pair in edgeUses)
                {
                    EdgeUse edge = pair.Value;
                    if (edge.Count == 1
                        && Mathf.Abs(Cross(
                            points[edge.A],
                            points[edge.B],
                            points[pointIndex])) > 0.0000001f)
                    {
                        result.Add(new Triangle(edge.A, edge.B, pointIndex));
                    }
                }
            }

            result.RemoveAll(triangle =>
                triangle.A >= originalCount
                || triangle.B >= originalCount
                || triangle.C >= originalCount);
            return result;
        }

        private static bool TryFindTriangle(
            Vector2 point,
            List<ControlPoint> controls,
            List<Triangle> triangles,
            out Triangle result,
            out Vector3 barycentric)
        {
            for (int index = 0; index < triangles.Count; index++)
            {
                Triangle triangle = triangles[index];
                Vector2 a = controls[triangle.A].Reference;
                Vector2 b = controls[triangle.B].Reference;
                Vector2 c = controls[triangle.C].Reference;
                if (TryGetBarycentric(point, a, b, c, out barycentric)
                    && barycentric.x >= -0.0001f
                    && barycentric.y >= -0.0001f
                    && barycentric.z >= -0.0001f)
                {
                    result = triangle;
                    return true;
                }
            }

            result = default(Triangle);
            barycentric = default(Vector3);
            return false;
        }

        private static bool TryGetBarycentric(
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

        private static bool PointInsideCircumcircle(
            Vector2 point,
            Vector2 a,
            Vector2 b,
            Vector2 c)
        {
            float denominator = 2f * (a.x * (b.y - c.y)
                + b.x * (c.y - a.y)
                + c.x * (a.y - b.y));
            if (Mathf.Abs(denominator) < 0.0000001f)
            {
                return false;
            }

            float aLength = a.sqrMagnitude;
            float bLength = b.sqrMagnitude;
            float cLength = c.sqrMagnitude;
            Vector2 center = new Vector2(
                (aLength * (b.y - c.y)
                    + bLength * (c.y - a.y)
                    + cLength * (a.y - b.y)) / denominator,
                (aLength * (c.x - b.x)
                    + bLength * (a.x - c.x)
                    + cLength * (b.x - a.x)) / denominator);
            float radiusSquared = (a - center).sqrMagnitude;
            return (point - center).sqrMagnitude <= radiusSquared + 0.0000001f;
        }

        private static void AddEdgeUse(Dictionary<ulong, EdgeUse> edges, int a, int b)
        {
            int minimum = Mathf.Min(a, b);
            int maximum = Mathf.Max(a, b);
            ulong key = ((ulong)(uint)minimum << 32) | (uint)maximum;
            if (edges.TryGetValue(key, out EdgeUse edge))
            {
                edge.Count++;
                edges[key] = edge;
            }
            else
            {
                edges.Add(key, new EdgeUse(minimum, maximum));
            }
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        private static Texture2D MakeReadableCopy(Texture source)
        {
            if (source == null)
            {
                return null;
            }

            RenderTexture temporary = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0, false);
                copy.Apply(false, false);
                return copy;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
            }
        }

        private static Color SampleBilinear(
            Color32[] pixels,
            int width,
            int height,
            float u,
            float v)
        {
            float x = Mathf.Clamp01(u) * (width - 1);
            float y = Mathf.Clamp01(v) * (height - 1);
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            int x1 = Mathf.Min(width - 1, x0 + 1);
            int y1 = Mathf.Min(height - 1, y0 + 1);
            float tx = x - x0;
            float ty = y - y0;
            Color bottom = Color.Lerp(pixels[y0 * width + x0], pixels[y0 * width + x1], tx);
            Color top = Color.Lerp(pixels[y1 * width + x0], pixels[y1 * width + x1], tx);
            return Color.Lerp(bottom, top, ty);
        }

        private void DestroyGeneratedAppearance()
        {
            DestroyRuntimeObject(generatedFaceSprite);
            DestroyRuntimeObject(generatedFaceTexture);
            generatedFaceSprite = null;
            generatedFaceTexture = null;
        }

        private void UseOriginalFacePattern(bool updateStatus)
        {
            ResolveReferences();
            if (targetMesh != null && originalFrontSprite != null)
            {
                targetMesh.SetFrontSpriteWithoutRebuild(originalFrontSprite);
            }

            DestroyGeneratedAppearance();
            if (updateStatus)
            {
                lastStatus = "Using the original standard face pattern. The fitted geometry is unchanged.";
            }
        }

        private static void DestroyRuntimeObject(Object target)
        {
            if (target != null)
            {
                Destroy(target);
            }
        }

        private readonly struct FaceBasis
        {
            public readonly Vector3 Origin;
            public readonly Vector3 X;
            public readonly Vector3 Y;
            public readonly Vector3 Z;
            public readonly float EyeDistance;
            public readonly float Roll;
            public readonly float Yaw;
            public readonly float Pitch;

            public FaceBasis(
                Vector3 origin,
                Vector3 x,
                Vector3 y,
                Vector3 z,
                float eyeDistance)
            {
                Origin = origin;
                X = x;
                Y = y;
                Z = z;
                EyeDistance = eyeDistance;
                Roll = Mathf.Atan2(x.y, x.x) * Mathf.Rad2Deg;
                Yaw = Mathf.Atan2(x.z, Mathf.Sqrt(x.x * x.x + x.y * x.y)) * Mathf.Rad2Deg;
                Pitch = Mathf.Atan2(y.z, Mathf.Sqrt(y.x * y.x + y.y * y.y)) * Mathf.Rad2Deg;
            }

            public Vector3 ToLocal(Vector3 point)
            {
                Vector3 offset = point - Origin;
                return new Vector3(
                    Vector3.Dot(offset, X),
                    Vector3.Dot(offset, Y),
                    Vector3.Dot(offset, Z));
            }

            public Vector3 FromLocal(Vector3 point)
            {
                return Origin + X * point.x + Y * point.y + Z * point.z;
            }
        }

        private readonly struct ControlPoint
        {
            public readonly Vector2 Reference;
            public readonly Vector2 ShapeTarget;
            public readonly Vector2 PlayerSource;

            public ControlPoint(Vector2 reference, Vector2 shapeTarget, Vector2 playerSource)
            {
                Reference = reference;
                ShapeTarget = shapeTarget;
                PlayerSource = playerSource;
            }
        }

        private readonly struct Triangle
        {
            public readonly int A;
            public readonly int B;
            public readonly int C;

            public Triangle(int a, int b, int c)
            {
                A = a;
                B = b;
                C = c;
            }
        }

        private struct EdgeUse
        {
            public int A;
            public int B;
            public int Count;

            public EdgeUse(int a, int b)
            {
                A = a;
                B = b;
                Count = 1;
            }
        }

        private void CaptureStandardRestPose()
        {
            if (targetMesh == null)
            {
                ResolveReferences();
            }

            if (targetMesh != null
                && (standardRestPose == null || standardRestPose.Length != targetMesh.LogicalVertexCount))
            {
                standardRestPose = targetMesh.CopyRestVertices();
            }
        }

        private void ResolveReferences()
        {
            if (targetMesh == null)
            {
                targetMesh = FindObjectOfType<DeformableImageMesh>();
            }

            if (landmarkSource == null)
            {
                landmarkSource = GetComponent<MediaPipeStaticFaceLandmarkSource>();
                if (landmarkSource == null)
                {
                    landmarkSource = FindObjectOfType<MediaPipeStaticFaceLandmarkSource>();
                }
            }

            if (landmarkVisualizer == null)
            {
                landmarkVisualizer = GetComponent<FaceLandmarkVisualizer>();
                if (landmarkVisualizer == null)
                {
                    landmarkVisualizer = FindObjectOfType<FaceLandmarkVisualizer>();
                }
            }
        }

        private void FinishWithError(string message)
        {
            lastStatus = message;
            generationRoutine = null;
            Debug.LogError(message, this);
        }

        private static FaceLandmarkFrame CloneFrame(FaceLandmarkFrame source)
        {
            return new FaceLandmarkFrame(
                (Vector3[])source.NormalizedLandmarks.Clone(),
                source.ImageWidth,
                source.ImageHeight,
                source.DetectionConfidence,
                source.HasHeadPose,
                source.HeadEulerAngles,
                source.HasDetectionConfidence);
        }

        private static Bounds CalculateBounds(Vector3[] vertices)
        {
            Bounds bounds = new Bounds(vertices[0], Vector3.zero);
            for (int index = 1; index < vertices.Length; index++)
            {
                bounds.Encapsulate(vertices[index]);
            }

            return bounds;
        }

        private Vector2 runtimePanelScroll;

        private void OnGUI()
        {
            if ((!panelVisible && !DeformableImage.FaceRuntimeUILayout.Enabled) || !Application.isPlaying
                || !DeformableImage.FaceRuntimeUILayout.Right(1))
            {
                return;
            }

            GUILayout.BeginArea(
                DeformableImage.FaceRuntimeUILayout.Enabled ? DeformableImage.FaceRuntimeUILayout.Panel(true, 470f)
                    : new Rect(Screen.width - panelWidth - 12f, 12f, panelWidth, 320f),
                GUI.skin.box);
            runtimePanelScroll = GUILayout.BeginScrollView(runtimePanelScroll);
            GUILayout.Label("Player Photo → Rest Face");
            GUILayout.Label(playerPhoto != null
                ? "Player Photo: " + playerPhoto.name
                : "Player Photo: not assigned");
            GUILayout.Label(lastStatus);
            GUI.enabled = generationRoutine == null && targetMesh != null && targetMesh.FrontSprite != null;
            if (GUILayout.Button("Sync Current Mesh Face + Redetect"))
            {
                SynchronizeCurrentMeshFaceAndRedetect();
            }

            GUI.enabled = generationRoutine == null && playerPhoto != null;
            if (GUILayout.Button("Generate Player Rest Face"))
            {
                GeneratePlayerRestFace();
            }

            GUILayout.Label("Optional appearance (does not change face shape):");
            GUI.enabled = generationRoutine == null
                && lastControls != null
                && lastTriangles != null;
            if (GUILayout.Button("Apply Player Photo Pattern"))
            {
                ApplyPlayerPhotoPattern();
            }

            GUI.enabled = generationRoutine == null && originalFrontSprite != null;
            if (GUILayout.Button("Use Original Face Pattern"))
            {
                UseOriginalFacePattern();
            }

            GUI.enabled = generationRoutine == null && standardRestPose != null;
            if (GUILayout.Button("Restore Standard Rest Face"))
            {
                RestoreStandardRestFace();
            }

            GUI.enabled = true;
            GUILayout.Label("F7: show/hide this panel");
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
