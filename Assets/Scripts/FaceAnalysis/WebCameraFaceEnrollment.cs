using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FaceAnalysis
{
    /// <summary>
    /// A lightweight active-liveness enrollment flow for game character creation.
    /// It validates a sequence of gestures, samples only returned neutral frames, stores
    /// median face geometry, then asks PhotoFaceRestPoseFitter to deform the standard face.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WebCameraFaceEnrollment : MonoBehaviour
    {
        private enum CaptureStage
        {
            Idle,
            WaitingForFace,
            NeutralCalibration,
            Nod,
            Blink,
            MouthOpen,
            TurnFirstSide,
            TurnOppositeSide,
            FinalNeutralCapture,
            Applying,
            Completed,
            Failed
        }

        [Header("Required References")]
        [SerializeField] private MediaPipeStaticFaceLandmarkSource landmarkSource;
        [SerializeField] private PhotoFaceRestPoseFitter faceFitter;

        [Header("Capture")]
        [SerializeField, Range(8f, 30f)] private float analysisFramesPerSecond = 15f;
        [SerializeField, Range(4, 30)] private int calibrationFrameCount = 12;
        [SerializeField, Range(8, 60)] private int finalNeutralFrameCount = 24;
        [SerializeField, Range(5f, 30f)] private float stageTimeoutSeconds = 18f;
        [SerializeField, Range(0.1f, 0.8f)] private float minimumFaceWidth = 0.2f;
        [SerializeField, Range(0.3f, 1f)] private float maximumFaceWidth = 0.75f;

        [Header("Gesture Thresholds")]
        [SerializeField, Range(3f, 20f)] private float nodDegrees = 8f;
        [SerializeField, Range(5f, 30f)] private float turnDegrees = 13f;
        [SerializeField, Range(1f, 10f)] private float returnPoseTolerance = 4f;
        [SerializeField, Range(0.3f, 0.85f)] private float blinkClosedRatio = 0.62f;
        [SerializeField, Range(0.03f, 0.3f)] private float mouthOpenIncrease = 0.09f;
        [SerializeField, Range(5f, 30f)] private float maximumInitialPose = 18f;

        [Header("Persistence")]
        [Tooltip("Only normalized landmark geometry is stored by default; no camera image is written to disk.")]
        [SerializeField] private string captureFileName = "PlayerFaceCapture.json";

        [Header("Runtime Panel")]
        [SerializeField] private bool showRuntimePanel = true;
        [SerializeField] private KeyCode togglePanelKey = KeyCode.F8;
        [SerializeField, Min(360f)] private float panelWidth = 480f;

        [Header("Status")]
        [SerializeField, TextArea(3, 8)] private string lastStatus =
            "点击“开始摄像头采集”，然后按提示完成动作。";

        private CaptureStage stage = CaptureStage.Idle;
        private readonly List<FaceLandmarkFrame> calibrationFrames = new List<FaceLandmarkFrame>();
        private readonly List<FaceLandmarkFrame> neutralFrames = new List<FaceLandmarkFrame>();
        private FaceLandmarkFrame lastProcessedFrame;
        private float stageStartedAt;
        private bool gestureReached;
        private float firstTurnDirection;
        private float baselinePitch;
        private float baselineYaw;
        private float baselineRoll;
        private float baselineLeftEyeRatio;
        private float baselineRightEyeRatio;
        private float baselineMouthRatio;
        private PoseSignals lastSignals;
        private bool hasSignals;
        private FaceAnalysisInputMode previousInputMode;
        private Sprite previousSourceSprite;
        private float previousAnalysisFrameRate;
        private Texture2D capturedTexture;
        private Sprite capturedSprite;
        private bool panelVisible;
        private string savedDataPath = string.Empty;

        public bool IsCapturing
        {
            get
            {
                return stage != CaptureStage.Idle
                    && stage != CaptureStage.Completed
                    && stage != CaptureStage.Failed;
            }
        }

        private void Awake()
        {
            ResolveReferences();
            panelVisible = showRuntimePanel;
        }

        private void OnValidate()
        {
            analysisFramesPerSecond = Mathf.Clamp(analysisFramesPerSecond, 8f, 30f);
            calibrationFrameCount = Mathf.Clamp(calibrationFrameCount, 4, 30);
            finalNeutralFrameCount = Mathf.Clamp(finalNeutralFrameCount, 8, 60);
            stageTimeoutSeconds = Mathf.Clamp(stageTimeoutSeconds, 5f, 30f);
            minimumFaceWidth = Mathf.Clamp(minimumFaceWidth, 0.1f, 0.8f);
            maximumFaceWidth = Mathf.Max(minimumFaceWidth + 0.05f, maximumFaceWidth);
            returnPoseTolerance = Mathf.Clamp(returnPoseTolerance, 1f, 10f);
            panelWidth = Mathf.Max(360f, panelWidth);
        }

        private void Update()
        {
            if (Input.GetKeyDown(togglePanelKey))
            {
                panelVisible = !panelVisible;
            }

            if (!IsCapturing || stage == CaptureStage.Applying)
            {
                return;
            }

            if (Time.realtimeSinceStartup - stageStartedAt > stageTimeoutSeconds
                && stage != CaptureStage.WaitingForFace)
            {
                gestureReached = false;
                stageStartedAt = Time.realtimeSinceStartup;
                lastStatus = StageInstruction(stage) + " 动作等待超时，请重新完成当前动作。";
            }

            if (landmarkSource == null
                || !landmarkSource.TryGetLatestFrame(out FaceLandmarkFrame frame)
                || ReferenceEquals(frame, lastProcessedFrame))
            {
                return;
            }

            lastProcessedFrame = frame;
            if (frame == null || !frame.IsValid || !TryGetSignals(frame, out PoseSignals signals))
            {
                hasSignals = false;
                lastStatus = "未检测到完整面部，请正对摄像头并保证光线充足。";
                return;
            }

            lastSignals = signals;
            hasSignals = true;
            if (!IsFaceWellFramed(frame, signals))
            {
                lastStatus = "请调整距离：完整面部需要位于画面中央，不能过近或过远。";
                return;
            }

            ProcessFrame(frame, signals);
        }

        private void OnDisable()
        {
            if (IsCapturing)
            {
                RestorePreviousInput();
            }

            MediaPipeStaticFaceLandmarkSource.SuppressLandmarkRendering = false;
        }

        private void OnDestroy()
        {
            DestroyRuntimeObject(capturedSprite);
            DestroyRuntimeObject(capturedTexture);
        }

        [ContextMenu("Start Camera Face Enrollment")]
        public void StartEnrollment()
        {
            ResolveReferences();
            if (!Application.isPlaying)
            {
                lastStatus = "请先进入 Play Mode。";
                return;
            }

            if (landmarkSource == null || faceFitter == null)
            {
                lastStatus = "请配置 Landmark Source 和 Face Fitter。";
                return;
            }

            previousInputMode = landmarkSource.InputMode;
            previousSourceSprite = landmarkSource.SourceSprite;
            previousAnalysisFrameRate = landmarkSource.AnalysisFramesPerSecond;
            calibrationFrames.Clear();
            neutralFrames.Clear();
            lastProcessedFrame = null;
            gestureReached = false;
            firstTurnDirection = 0f;
            savedDataPath = string.Empty;
            MediaPipeStaticFaceLandmarkSource.SuppressLandmarkRendering = true;
            landmarkSource.SetAnalysisFrameRate(analysisFramesPerSecond);
            BeginStage(CaptureStage.WaitingForFace, "正在启动摄像头，请将完整面部放入画面中央。", false);
            landmarkSource.SetInputMode(FaceAnalysisInputMode.WebCamera, true);
        }

        [ContextMenu("Cancel Camera Face Enrollment")]
        public void CancelEnrollment()
        {
            RestorePreviousInput();
            stage = CaptureStage.Idle;
            lastStatus = "摄像头采集已取消。";
            MediaPipeStaticFaceLandmarkSource.SuppressLandmarkRendering = false;
        }

        private void ProcessFrame(FaceLandmarkFrame frame, PoseSignals signals)
        {
            float pitchDelta = Mathf.DeltaAngle(baselinePitch, signals.Pitch);
            float yawDelta = Mathf.DeltaAngle(baselineYaw, signals.Yaw);
            float rollDelta = Mathf.DeltaAngle(baselineRoll, signals.Roll);

            switch (stage)
            {
                case CaptureStage.WaitingForFace:
                    BeginStage(CaptureStage.NeutralCalibration, "保持正视、自然闭嘴、睁开双眼。", false);
                    break;

                case CaptureStage.NeutralCalibration:
                    if (Mathf.Abs(signals.Pitch) > maximumInitialPose
                        || Mathf.Abs(signals.Yaw) > maximumInitialPose
                        || Mathf.Abs(signals.Roll) > maximumInitialPose
                        || signals.LeftEyeRatio < 0.1f
                        || signals.RightEyeRatio < 0.1f
                        || signals.MouthRatio > 0.22f)
                    {
                        calibrationFrames.Clear();
                        lastStatus = "请正视摄像头、睁眼并自然闭嘴，正在建立中性基准。";
                        return;
                    }

                    calibrationFrames.Add(CloneFrame(frame));
                    lastStatus = "保持不动，正在建立中性基准："
                        + calibrationFrames.Count + "/" + calibrationFrameCount;
                    if (calibrationFrames.Count >= calibrationFrameCount)
                    {
                        CalculateBaseline(calibrationFrames);
                        BeginStage(CaptureStage.Nod, "请点头一次，然后回到正视位置。", true);
                    }
                    break;

                case CaptureStage.Nod:
                    if (!gestureReached && Mathf.Abs(pitchDelta) >= nodDegrees)
                    {
                        gestureReached = true;
                        lastStatus = "已检测到点头，请回到正视位置。";
                    }
                    else if (gestureReached && Mathf.Abs(pitchDelta) <= returnPoseTolerance)
                    {
                        BeginStage(CaptureStage.Blink, "请同时眨一下双眼。", true);
                    }
                    break;

                case CaptureStage.Blink:
                    bool eyesClosed = signals.LeftEyeRatio
                            <= baselineLeftEyeRatio * blinkClosedRatio
                        && signals.RightEyeRatio
                            <= baselineRightEyeRatio * blinkClosedRatio;
                    bool eyesReopened = signals.LeftEyeRatio >= baselineLeftEyeRatio * 0.82f
                        && signals.RightEyeRatio >= baselineRightEyeRatio * 0.82f;
                    if (!gestureReached && eyesClosed)
                    {
                        gestureReached = true;
                        lastStatus = "已检测到闭眼，请重新睁开双眼。";
                    }
                    else if (gestureReached && eyesReopened)
                    {
                        BeginStage(CaptureStage.MouthOpen, "请张开嘴，然后自然闭合。", true);
                    }
                    break;

                case CaptureStage.MouthOpen:
                    if (!gestureReached
                        && signals.MouthRatio >= baselineMouthRatio + mouthOpenIncrease)
                    {
                        gestureReached = true;
                        lastStatus = "已检测到张嘴，请自然闭嘴。";
                    }
                    else if (gestureReached
                        && signals.MouthRatio <= baselineMouthRatio + mouthOpenIncrease * 0.35f)
                    {
                        BeginStage(CaptureStage.TurnFirstSide, "请向一侧转头，然后回到正视。", true);
                    }
                    break;

                case CaptureStage.TurnFirstSide:
                    if (!gestureReached && Mathf.Abs(yawDelta) >= turnDegrees)
                    {
                        gestureReached = true;
                        firstTurnDirection = Mathf.Sign(yawDelta);
                        lastStatus = "已检测到第一侧转头，请回到正视位置。";
                    }
                    else if (gestureReached && Mathf.Abs(yawDelta) <= returnPoseTolerance)
                    {
                        BeginStage(CaptureStage.TurnOppositeSide, "请转向另一侧，然后回到正视。", true);
                    }
                    break;

                case CaptureStage.TurnOppositeSide:
                    if (!gestureReached
                        && yawDelta * firstTurnDirection <= -turnDegrees)
                    {
                        gestureReached = true;
                        lastStatus = "已检测到另一侧转头，请回到正视位置。";
                    }
                    else if (gestureReached && Mathf.Abs(yawDelta) <= returnPoseTolerance)
                    {
                        neutralFrames.Clear();
                        BeginStage(
                            CaptureStage.FinalNeutralCapture,
                            "验证完成。请保持正视、睁眼、闭嘴，正在采集最终脸型。",
                            false);
                    }
                    break;

                case CaptureStage.FinalNeutralCapture:
                    bool neutralPose = Mathf.Abs(pitchDelta) <= returnPoseTolerance
                        && Mathf.Abs(yawDelta) <= returnPoseTolerance
                        && Mathf.Abs(rollDelta) <= returnPoseTolerance;
                    bool neutralExpression = signals.LeftEyeRatio >= baselineLeftEyeRatio * 0.8f
                        && signals.RightEyeRatio >= baselineRightEyeRatio * 0.8f
                        && signals.MouthRatio <= baselineMouthRatio + mouthOpenIncrease * 0.35f;
                    if (!neutralPose || !neutralExpression)
                    {
                        lastStatus = "请保持正视、睁眼并闭嘴；不合格帧不会写入最终数据。";
                        return;
                    }

                    neutralFrames.Add(CloneFrame(frame));
                    lastStatus = "正在采集稳定中性脸："
                        + neutralFrames.Count + "/" + finalNeutralFrameCount;
                    if (neutralFrames.Count >= finalNeutralFrameCount)
                    {
                        FinishEnrollment(frame);
                    }
                    break;
            }
        }

        private void FinishEnrollment(FaceLandmarkFrame latestFrame)
        {
            stage = CaptureStage.Applying;
            lastStatus = "动作验证通过，正在聚合面部数据并生成原初脸型。";
            FaceLandmarkFrame medianFrame = BuildMedianFrame(neutralFrames, latestFrame);
            if (medianFrame == null || !medianFrame.IsValid)
            {
                Fail("最终面部数据不足，请重新采集。");
                return;
            }

            savedDataPath = SaveCapture(medianFrame);
            Texture inputTexture = landmarkSource.CurrentInputTexture;
            Texture2D newTexture = MakeReadableCopy(inputTexture);
            if (newTexture == null)
            {
                Fail("无法保存摄像头最终帧用于宽高比映射。");
                return;
            }

            Sprite newSprite = Sprite.Create(
                newTexture,
                new Rect(0f, 0f, newTexture.width, newTexture.height),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            newSprite.name = "Runtime Camera Enrollment Face";
            DestroyRuntimeObject(capturedSprite);
            DestroyRuntimeObject(capturedTexture);
            capturedTexture = newTexture;
            capturedSprite = newSprite;
            faceFitter.GenerateFromCapturedCameraFrame(
                capturedSprite,
                medianFrame,
                HandleFitCompleted);
        }

        private void HandleFitCompleted(bool success, string message)
        {
            MediaPipeStaticFaceLandmarkSource.SuppressLandmarkRendering = false;
            if (success)
            {
                stage = CaptureStage.Completed;
                lastStatus = "采集完成并已生成新的原初面容。数据保存于：" + savedDataPath;
            }
            else
            {
                stage = CaptureStage.Failed;
                lastStatus = "面部数据已采集，但脸型生成失败：" + message;
            }
        }

        private bool TryGetSignals(FaceLandmarkFrame frame, out PoseSignals signals)
        {
            signals = default(PoseSignals);
            Vector3[] landmarks = frame.NormalizedLandmarks;
            if (landmarks == null || landmarks.Length < MediaPipeFaceLandmarkIndices.MinimumLandmarkCount)
            {
                return false;
            }

            float aspect = frame.ImageWidth / (float)Mathf.Max(1, frame.ImageHeight);
            float leftEyeRatio = Distance(landmarks[159], landmarks[145], aspect)
                / Mathf.Max(0.0001f, Distance(landmarks[33], landmarks[133], aspect));
            float rightEyeRatio = Distance(landmarks[386], landmarks[374], aspect)
                / Mathf.Max(0.0001f, Distance(landmarks[362], landmarks[263], aspect));
            float mouthRatio = Distance(landmarks[13], landmarks[14], aspect)
                / Mathf.Max(0.0001f, Distance(landmarks[61], landmarks[291], aspect));

            Vector3 pose = frame.HasHeadPose
                ? frame.HeadEulerAngles
                : EstimatePoseFromLandmarks(landmarks, aspect);
            if (!IsFinite(pose)
                || float.IsNaN(leftEyeRatio) || float.IsInfinity(leftEyeRatio)
                || float.IsNaN(rightEyeRatio) || float.IsInfinity(rightEyeRatio)
                || float.IsNaN(mouthRatio) || float.IsInfinity(mouthRatio))
            {
                return false;
            }

            signals = new PoseSignals(
                pose.x,
                pose.y,
                pose.z,
                leftEyeRatio,
                rightEyeRatio,
                mouthRatio);
            return true;
        }

        private bool IsFaceWellFramed(FaceLandmarkFrame frame, PoseSignals signals)
        {
            Vector3[] landmarks = frame.NormalizedLandmarks;
            float faceWidth = Mathf.Abs(
                landmarks[MediaPipeFaceLandmarkIndices.RightCheek].x
                - landmarks[MediaPipeFaceLandmarkIndices.LeftCheek].x);
            Vector2 eyeCenter = ((Vector2)landmarks[MediaPipeFaceLandmarkIndices.ImageLeftEyeOuter]
                + (Vector2)landmarks[MediaPipeFaceLandmarkIndices.ImageRightEyeOuter]) * 0.5f;
            return faceWidth >= minimumFaceWidth
                && faceWidth <= maximumFaceWidth
                && eyeCenter.x >= 0.18f && eyeCenter.x <= 0.82f
                && eyeCenter.y >= 0.12f && eyeCenter.y <= 0.72f;
        }

        private void CalculateBaseline(List<FaceLandmarkFrame> frames)
        {
            List<float> pitch = new List<float>(frames.Count);
            List<float> yaw = new List<float>(frames.Count);
            List<float> roll = new List<float>(frames.Count);
            List<float> leftEye = new List<float>(frames.Count);
            List<float> rightEye = new List<float>(frames.Count);
            List<float> mouth = new List<float>(frames.Count);
            for (int index = 0; index < frames.Count; index++)
            {
                if (!TryGetSignals(frames[index], out PoseSignals signals))
                {
                    continue;
                }

                pitch.Add(signals.Pitch);
                yaw.Add(signals.Yaw);
                roll.Add(signals.Roll);
                leftEye.Add(signals.LeftEyeRatio);
                rightEye.Add(signals.RightEyeRatio);
                mouth.Add(signals.MouthRatio);
            }

            baselinePitch = Median(pitch);
            baselineYaw = Median(yaw);
            baselineRoll = Median(roll);
            baselineLeftEyeRatio = Median(leftEye);
            baselineRightEyeRatio = Median(rightEye);
            baselineMouthRatio = Median(mouth);
        }

        private static FaceLandmarkFrame BuildMedianFrame(
            List<FaceLandmarkFrame> frames,
            FaceLandmarkFrame fallback)
        {
            if (frames == null || frames.Count == 0 || fallback == null)
            {
                return null;
            }

            int count = fallback.NormalizedLandmarks.Length;
            Vector3[] landmarks = new Vector3[count];
            float[] values = new float[frames.Count];
            for (int landmark = 0; landmark < count; landmark++)
            {
                for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    values[frameIndex] = frames[frameIndex].NormalizedLandmarks[landmark].x;
                }
                Array.Sort(values);
                float x = MedianSorted(values);

                for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    values[frameIndex] = frames[frameIndex].NormalizedLandmarks[landmark].y;
                }
                Array.Sort(values);
                float y = MedianSorted(values);

                for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    values[frameIndex] = frames[frameIndex].NormalizedLandmarks[landmark].z;
                }
                Array.Sort(values);
                float z = MedianSorted(values);
                landmarks[landmark] = new Vector3(x, y, z);
            }

            return new FaceLandmarkFrame(
                landmarks,
                fallback.ImageWidth,
                fallback.ImageHeight,
                fallback.DetectionConfidence,
                fallback.HasHeadPose,
                new Vector3(float.NaN, float.NaN, float.NaN),
                fallback.HasDetectionConfidence);
        }

        private string SaveCapture(FaceLandmarkFrame frame)
        {
            try
            {
                int count = frame.NormalizedLandmarks.Length;
                SavedFaceCapture data = new SavedFaceCapture
                {
                    version = 1,
                    capturedUtc = DateTime.UtcNow.ToString("o"),
                    imageWidth = frame.ImageWidth,
                    imageHeight = frame.ImageHeight,
                    landmarkX = new float[count],
                    landmarkY = new float[count],
                    landmarkZ = new float[count],
                    verifiedNod = true,
                    verifiedBlink = true,
                    verifiedMouthOpen = true,
                    verifiedTurnBothSides = true,
                    neutralFrameCount = neutralFrames.Count
                };
                for (int index = 0; index < count; index++)
                {
                    data.landmarkX[index] = frame.NormalizedLandmarks[index].x;
                    data.landmarkY[index] = frame.NormalizedLandmarks[index].y;
                    data.landmarkZ[index] = frame.NormalizedLandmarks[index].z;
                }

                string path = Path.Combine(Application.persistentDataPath, captureFileName);
                File.WriteAllText(path, JsonUtility.ToJson(data, true));
                return path;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                return "保存失败：" + exception.Message;
            }
        }

        private void BeginStage(CaptureStage nextStage, string message, bool resetGesture)
        {
            stage = nextStage;
            stageStartedAt = Time.realtimeSinceStartup;
            if (resetGesture)
            {
                gestureReached = false;
            }

            lastStatus = message;
        }

        private void Fail(string message)
        {
            stage = CaptureStage.Failed;
            lastStatus = message;
            RestorePreviousInput();
            MediaPipeStaticFaceLandmarkSource.SuppressLandmarkRendering = false;
        }

        private void RestorePreviousInput()
        {
            if (landmarkSource == null)
            {
                return;
            }

            landmarkSource.SetAnalysisFrameRate(previousAnalysisFrameRate > 0f
                ? previousAnalysisFrameRate
                : 5f);
            if (previousInputMode == FaceAnalysisInputMode.StaticSprite)
            {
                landmarkSource.SetStaticSprite(previousSourceSprite, true);
            }
            else
            {
                landmarkSource.SetInputMode(previousInputMode, true);
            }
        }

        private void ResolveReferences()
        {
            if (landmarkSource == null)
            {
                landmarkSource = GetComponent<MediaPipeStaticFaceLandmarkSource>();
            }

            if (faceFitter == null)
            {
                faceFitter = GetComponent<PhotoFaceRestPoseFitter>();
            }
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

        private static float Distance(Vector3 a, Vector3 b, float aspect)
        {
            Vector2 delta = new Vector2((a.x - b.x) * aspect, a.y - b.y);
            return delta.magnitude;
        }

        private static Vector3 EstimatePoseFromLandmarks(Vector3[] landmarks, float aspect)
        {
            Vector2 leftEye = new Vector2(
                landmarks[MediaPipeFaceLandmarkIndices.ImageLeftEyeOuter].x * aspect,
                landmarks[MediaPipeFaceLandmarkIndices.ImageLeftEyeOuter].y);
            Vector2 rightEye = new Vector2(
                landmarks[MediaPipeFaceLandmarkIndices.ImageRightEyeOuter].x * aspect,
                landmarks[MediaPipeFaceLandmarkIndices.ImageRightEyeOuter].y);
            Vector2 eyeVector = rightEye - leftEye;
            Vector2 eyeCenter = (leftEye + rightEye) * 0.5f;
            Vector2 nose = new Vector2(
                landmarks[MediaPipeFaceLandmarkIndices.NoseTip].x * aspect,
                landmarks[MediaPipeFaceLandmarkIndices.NoseTip].y);
            float eyeDistance = Mathf.Max(0.0001f, eyeVector.magnitude);
            float roll = Mathf.Atan2(eyeVector.y, eyeVector.x) * Mathf.Rad2Deg;
            float yawProxy = (nose.x - eyeCenter.x) / eyeDistance * 55f;
            float pitchProxy = landmarks[MediaPipeFaceLandmarkIndices.NoseTip].z * 80f;
            return new Vector3(pitchProxy, yawProxy, roll);
        }

        private static float Median(List<float> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0f;
            }

            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 0
                ? (values[middle - 1] + values[middle]) * 0.5f
                : values[middle];
        }

        private static float MedianSorted(float[] values)
        {
            int middle = values.Length / 2;
            return values.Length % 2 == 0
                ? (values[middle - 1] + values[middle]) * 0.5f
                : values[middle];
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
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

        private static void DestroyRuntimeObject(UnityEngine.Object target)
        {
            if (target != null)
            {
                Destroy(target);
            }
        }

        private string StageInstruction(CaptureStage current)
        {
            switch (current)
            {
                case CaptureStage.NeutralCalibration: return "请正视摄像头并保持不动。";
                case CaptureStage.Nod: return "请点头一次并回正。";
                case CaptureStage.Blink: return "请同时眨一下双眼。";
                case CaptureStage.MouthOpen: return "请张嘴后闭合。";
                case CaptureStage.TurnFirstSide: return "请向一侧转头并回正。";
                case CaptureStage.TurnOppositeSide: return "请转向另一侧并回正。";
                case CaptureStage.FinalNeutralCapture: return "请保持正视、睁眼、闭嘴。";
                default: return "请将面部放入摄像头画面。";
            }
        }

        private float CaptureProgress()
        {
            switch (stage)
            {
                case CaptureStage.NeutralCalibration:
                    return 0.1f * calibrationFrames.Count / Mathf.Max(1f, calibrationFrameCount);
                case CaptureStage.Nod: return 0.16f;
                case CaptureStage.Blink: return 0.3f;
                case CaptureStage.MouthOpen: return 0.44f;
                case CaptureStage.TurnFirstSide: return 0.58f;
                case CaptureStage.TurnOppositeSide: return 0.7f;
                case CaptureStage.FinalNeutralCapture:
                    return 0.82f + 0.16f * neutralFrames.Count / Mathf.Max(1f, finalNeutralFrameCount);
                case CaptureStage.Applying: return 0.99f;
                case CaptureStage.Completed: return 1f;
                default: return 0f;
            }
        }

        private void OnGUI()
        {
            if (!panelVisible || !Application.isPlaying)
            {
                return;
            }

            Rect panel = new Rect(12f, 12f, panelWidth, 590f);
            GUILayout.BeginArea(panel, GUI.skin.box);
            GUILayout.Label("摄像头面部采集 / 活体动作验证");
            Texture preview = landmarkSource != null ? landmarkSource.CurrentInputTexture : null;
            Rect previewRect = GUILayoutUtility.GetRect(panelWidth - 24f, 320f);
            if (preview != null && IsCapturing)
            {
                GUI.DrawTexture(previewRect, preview, ScaleMode.ScaleToFit, false);
            }
            else
            {
                GUI.Box(previewRect, "摄像头预览");
            }

            GUILayout.Label("当前阶段：" + StageInstruction(stage));
            GUILayout.HorizontalSlider(CaptureProgress(), 0f, 1f);
            GUILayout.Label(lastStatus);
            if (hasSignals && IsCapturing)
            {
                GUILayout.Label(
                    "姿态  Pitch " + lastSignals.Pitch.ToString("0.0")
                    + "°  Yaw " + lastSignals.Yaw.ToString("0.0")
                    + "°  Roll " + lastSignals.Roll.ToString("0.0") + "°");
            }

            GUI.enabled = !IsCapturing;
            if (GUILayout.Button(stage == CaptureStage.Idle ? "开始摄像头采集" : "重新开始摄像头采集"))
            {
                StartEnrollment();
            }

            GUI.enabled = IsCapturing && stage != CaptureStage.Applying;
            if (GUILayout.Button("取消采集"))
            {
                CancelEnrollment();
            }

            GUI.enabled = true;
            GUILayout.Label("F8：显示/隐藏采集面板");
            GUILayout.EndArea();
        }

        private readonly struct PoseSignals
        {
            public readonly float Pitch;
            public readonly float Yaw;
            public readonly float Roll;
            public readonly float LeftEyeRatio;
            public readonly float RightEyeRatio;
            public readonly float MouthRatio;

            public PoseSignals(
                float pitch,
                float yaw,
                float roll,
                float leftEyeRatio,
                float rightEyeRatio,
                float mouthRatio)
            {
                Pitch = pitch;
                Yaw = yaw;
                Roll = roll;
                LeftEyeRatio = leftEyeRatio;
                RightEyeRatio = rightEyeRatio;
                MouthRatio = mouthRatio;
            }
        }

        [Serializable]
        private sealed class SavedFaceCapture
        {
            public int version;
            public string capturedUtc;
            public int imageWidth;
            public int imageHeight;
            public float[] landmarkX;
            public float[] landmarkY;
            public float[] landmarkZ;
            public bool verifiedNod;
            public bool verifiedBlink;
            public bool verifiedMouthOpen;
            public bool verifiedTurnBothSides;
            public int neutralFrameCount;
        }
    }
}
