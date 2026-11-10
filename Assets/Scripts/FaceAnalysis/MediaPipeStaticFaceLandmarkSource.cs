using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Unity.Experimental;
using UnityEngine;

namespace FaceAnalysis
{
    public enum FaceAnalysisInputMode
    {
        StaticSprite = 0,
        WebCamera = 1,
        RenderedCamera = 2
    }

    [DisallowMultipleComponent]
    public sealed class MediaPipeStaticFaceLandmarkSource : MonoBehaviour, IFaceLandmarkSource
    {
        [Header("Input Mode")]
        [SerializeField] private FaceAnalysisInputMode inputMode = FaceAnalysisInputMode.StaticSprite;
        [SerializeField] private bool flipHorizontally;
        [Tooltip("Must normally stay enabled: Unity pixel rows start at the bottom, while MediaPipe image coordinates start at the top.")]
        [SerializeField] private bool flipVertically = true;
        [Tooltip("Uses the forehead/chin anatomy order to correct an upside-down CPU texture automatically.")]
        [SerializeField] private bool autoCorrectVerticalLandmarkOrientation = true;

        [Header("Static Sprite")]
        [SerializeField] private Sprite sourceSprite;
        [SerializeField] private bool detectOnStart = true;

        [Header("Web Camera")]
        [SerializeField, Min(0)] private int webCameraDeviceIndex;
        [SerializeField, Min(64)] private int requestedWebCameraWidth = 640;
        [SerializeField, Min(64)] private int requestedWebCameraHeight = 480;
        [SerializeField, Min(1)] private int requestedWebCameraFps = 30;

        [Header("Rendered / Deformed Scene")]
        [Tooltip("Assign Main Camera to analyze the deformed face currently shown in the scene.")]
        [SerializeField] private Camera renderedCamera;
        [SerializeField, Min(64)] private int renderedWidth = 640;
        [SerializeField, Min(64)] private int renderedHeight = 640;

        [Header("Realtime Performance")]
        [Tooltip("CPU inference frequency. 5-10 fps is usually enough for measurement UI.")]
        [SerializeField, Range(1f, 30f)] private float analysisFramesPerSecond = 5f;

        [Header("MediaPipe CPU")]
        [SerializeField] private string modelFileName = "face_landmarker_v2.bytes";
        [SerializeField, Range(0f, 1f)] private float minimumDetectionConfidence = 0.5f;
        [SerializeField, Range(0f, 1f)] private float minimumPresenceConfidence = 0.5f;
        [SerializeField, TextArea(2, 5)] private string lastStatus = "尚未初始化。";

        private FaceLandmarker faceLandmarker;
        private TextureFrame textureFrame;
        private FaceLandmarkFrame latestFrame;
        private WebCamTexture webCamTexture;
        private RenderTexture renderedTexture;
        private Coroutine inputCoroutine;
        private RunningMode activeRunningMode;
        private bool detecting;
        private bool hasProcessedFrame;
        private long lastTimestampMilliseconds;
        private Matrix4x4 lastRenderedViewProjection;
        private bool hasLastRenderedViewProjection;
        private bool loggedLandmarkOrientation;

        internal static bool SuppressLandmarkRendering { get; set; }

        public string LastStatus { get { return lastStatus; } }
        public FaceAnalysisInputMode InputMode { get { return inputMode; } }
        public Sprite SourceSprite { get { return sourceSprite; } }
        public Camera RenderedCamera { get { return renderedCamera; } }
        public bool HasProcessedFrame { get { return hasProcessedFrame; } }
        public bool InputWasFlippedHorizontally { get { return flipHorizontally; } }
        public bool InputWasFlippedVertically { get { return flipVertically; } }
        public float AnalysisFramesPerSecond { get { return analysisFramesPerSecond; } }
        public Texture CurrentInputTexture
        {
            get
            {
                switch (inputMode)
                {
                    case FaceAnalysisInputMode.WebCamera:
                        return webCamTexture;
                    case FaceAnalysisInputMode.RenderedCamera:
                        return renderedTexture;
                    default:
                        return sourceSprite != null ? sourceSprite.texture : null;
                }
            }
        }

        public bool TryWorldToLastRenderedViewport(Vector3 worldPoint, out Vector3 viewportPoint)
        {
            viewportPoint = default(Vector3);
            if (!hasLastRenderedViewProjection)
            {
                return false;
            }

            Vector4 clip = lastRenderedViewProjection
                * new Vector4(worldPoint.x, worldPoint.y, worldPoint.z, 1f);
            if (clip.w <= 0.000001f)
            {
                return false;
            }

            float inverseW = 1f / clip.w;
            viewportPoint = new Vector3(
                clip.x * inverseW * 0.5f + 0.5f,
                clip.y * inverseW * 0.5f + 0.5f,
                clip.w);
            return true;
        }

        private void Start()
        {
            RestartInput();
        }

        private void OnValidate()
        {
            // TextureFrame receives Unity textures, whose row origin differs from MediaPipe.
            // Keep this correction enabled so serialized scenes cannot silently regress.
            flipVertically = true;
            requestedWebCameraWidth = Mathf.Max(64, requestedWebCameraWidth);
            requestedWebCameraHeight = Mathf.Max(64, requestedWebCameraHeight);
            requestedWebCameraFps = Mathf.Max(1, requestedWebCameraFps);
            renderedWidth = Mathf.Max(64, renderedWidth);
            renderedHeight = Mathf.Max(64, renderedHeight);
            analysisFramesPerSecond = Mathf.Clamp(analysisFramesPerSecond, 1f, 30f);
        }

        private void OnDestroy()
        {
            CleanupInput();
            DisposeMediaPipe();
        }

        public bool TryGetLatestFrame(out FaceLandmarkFrame frame)
        {
            frame = latestFrame;
            return hasProcessedFrame && frame != null;
        }

        /// <summary>
        /// Changes the still image used by the detector. This is used by the photo-to-face
        /// fitting pass to analyze the template and player photographs with one detector.
        /// </summary>
        public void SetStaticSprite(Sprite sprite, bool restartImmediately = true)
        {
            inputMode = FaceAnalysisInputMode.StaticSprite;
            sourceSprite = sprite;
            latestFrame = null;
            hasProcessedFrame = false;

            if (restartImmediately && isActiveAndEnabled && Application.isPlaying)
            {
                RestartInput();
            }
        }

        public void SetInputMode(FaceAnalysisInputMode mode, bool restartImmediately = true)
        {
            inputMode = mode;
            latestFrame = null;
            hasProcessedFrame = false;
            if (restartImmediately && isActiveAndEnabled && Application.isPlaying)
            {
                RestartInput();
            }
        }

        public void SetAnalysisFrameRate(float framesPerSecond)
        {
            analysisFramesPerSecond = Mathf.Clamp(framesPerSecond, 1f, 30f);
        }

        [ContextMenu("Restart Analysis Input")]
        public void RestartInput()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }

            if (inputCoroutine != null)
            {
                StopCoroutine(inputCoroutine);
            }

            CleanupInput();
            DisposeMediaPipe();
            latestFrame = null;
            hasProcessedFrame = false;
            lastTimestampMilliseconds = 0;
            hasLastRenderedViewProjection = false;
            loggedLandmarkOrientation = false;
            inputCoroutine = StartCoroutine(RunSelectedInput());
        }

        [ContextMenu("Detect Current Input Now")]
        public void DetectNow()
        {
            if (detecting)
            {
                return;
            }

            Texture texture = PrepareCurrentTexture();
            if (texture == null)
            {
                SetEmptyFrame(1, 1, "当前输入尚未准备好。");
                return;
            }

            AnalyzeTexture(texture, inputMode == FaceAnalysisInputMode.StaticSprite);
        }

        private IEnumerator RunSelectedInput()
        {
            yield return null;

            if (inputMode == FaceAnalysisInputMode.StaticSprite)
            {
                if (detectOnStart)
                {
                    DetectNow();
                }

                inputCoroutine = null;
                yield break;
            }

            if (inputMode == FaceAnalysisInputMode.WebCamera)
            {
                if (WebCamTexture.devices.Length == 0)
                {
                    SetEmptyFrame(1, 1, "没有找到可用摄像头。");
                    inputCoroutine = null;
                    yield break;
                }

                int deviceIndex = Mathf.Clamp(webCameraDeviceIndex, 0, WebCamTexture.devices.Length - 1);
                string deviceName = WebCamTexture.devices[deviceIndex].name;
                webCamTexture = new WebCamTexture(deviceName, requestedWebCameraWidth, requestedWebCameraHeight, requestedWebCameraFps);
                webCamTexture.Play();
                lastStatus = "正在等待摄像头启动：" + deviceName;

                float timeout = Time.realtimeSinceStartup + 8f;
                while (webCamTexture != null
                    && webCamTexture.width <= 16
                    && Time.realtimeSinceStartup < timeout)
                {
                    yield return null;
                }

                if (webCamTexture == null || webCamTexture.width <= 16)
                {
                    SetEmptyFrame(1, 1, "摄像头启动超时或权限未授予。");
                    inputCoroutine = null;
                    yield break;
                }
            }
            else if (renderedCamera == null)
            {
                SetEmptyFrame(renderedWidth, renderedHeight, "Rendered Camera 模式尚未指定相机。");
                inputCoroutine = null;
                yield break;
            }

            float nextAnalysisTime = 0f;
            WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();
            while (isActiveAndEnabled)
            {
                yield return endOfFrame;
                if (Time.unscaledTime < nextAnalysisTime)
                {
                    continue;
                }

                if (inputMode == FaceAnalysisInputMode.WebCamera
                    && (webCamTexture == null || !webCamTexture.didUpdateThisFrame))
                {
                    continue;
                }

                nextAnalysisTime = Time.unscaledTime + 1f / Mathf.Max(1f, analysisFramesPerSecond);
                Texture texture = PrepareCurrentTexture();
                if (texture != null)
                {
                    AnalyzeTexture(texture, false);
                }

                yield return null;
            }

            inputCoroutine = null;
        }

        private Texture PrepareCurrentTexture()
        {
            switch (inputMode)
            {
                case FaceAnalysisInputMode.WebCamera:
                    return webCamTexture != null && webCamTexture.width > 16 ? webCamTexture : null;
                case FaceAnalysisInputMode.RenderedCamera:
                    return CaptureRenderedCamera();
                default:
                    return sourceSprite != null ? sourceSprite.texture : null;
            }
        }

        private RenderTexture CaptureRenderedCamera()
        {
            if (renderedCamera == null)
            {
                return null;
            }

            if (renderedTexture == null
                || renderedTexture.width != renderedWidth
                || renderedTexture.height != renderedHeight)
            {
                ReleaseRenderedTexture();
                renderedTexture = new RenderTexture(renderedWidth, renderedHeight, 24, RenderTextureFormat.ARGB32)
                {
                    name = "Face Analysis Camera Capture",
                    hideFlags = HideFlags.DontSave
                };
                renderedTexture.Create();
            }

            RenderTexture previousTarget = renderedCamera.targetTexture;
            bool previousSuppression = SuppressLandmarkRendering;
            try
            {
                SuppressLandmarkRendering = true;
                renderedCamera.targetTexture = renderedTexture;
                lastRenderedViewProjection = renderedCamera.projectionMatrix
                    * renderedCamera.worldToCameraMatrix;
                hasLastRenderedViewProjection = true;
                renderedCamera.Render();
            }
            finally
            {
                renderedCamera.targetTexture = previousTarget;
                SuppressLandmarkRendering = previousSuppression;
            }

            return renderedTexture;
        }

        private void AnalyzeTexture(Texture texture, bool useImageMode)
        {
            if (texture == null || detecting)
            {
                return;
            }

            detecting = true;
            try
            {
                RunningMode requiredMode = useImageMode ? RunningMode.IMAGE : RunningMode.VIDEO;
                if (!EnsureMediaPipe(requiredMode))
                {
                    SetEmptyFrame(texture.width, texture.height, lastStatus);
                    return;
                }

                EnsureTextureFrame(texture.width, texture.height);
                // Required by MediaPipeUnityPlugin's Task API coordinate contract:
                // Unity pixel data is bottom-left-origin, MediaPipe is top-left-origin.
                textureFrame.ReadTextureOnCPU(texture, flipHorizontally, true);
                using (Mediapipe.Image image = textureFrame.BuildCPUImage())
                {
                    FaceLandmarkerResult result = requiredMode == RunningMode.IMAGE
                        ? faceLandmarker.Detect(image)
                        : faceLandmarker.DetectForVideo(image, NextTimestampMilliseconds());
                    StoreResult(result, texture.width, texture.height);
                }
            }
            catch (Exception exception)
            {
                SetEmptyFrame(Mathf.Max(1, texture.width), Mathf.Max(1, texture.height), "MediaPipe 人脸检测失败：" + exception.Message);
                Debug.LogException(exception, this);
            }
            finally
            {
                detecting = false;
            }
        }

        private bool EnsureMediaPipe(RunningMode runningMode)
        {
            if (faceLandmarker != null && activeRunningMode == runningMode)
            {
                return true;
            }

            DisposeMediaPipe();
            string modelPath = Path.Combine(Application.streamingAssetsPath, modelFileName);
            if (!File.Exists(modelPath))
            {
                lastStatus = "缺少 MediaPipe 模型文件：" + modelPath;
                Debug.LogError(lastStatus, this);
                return false;
            }

            BaseOptions baseOptions = new BaseOptions(BaseOptions.Delegate.CPU, modelAssetPath: modelPath);
            FaceLandmarkerOptions options = new FaceLandmarkerOptions(
                baseOptions,
                runningMode: runningMode,
                numFaces: 1,
                minFaceDetectionConfidence: minimumDetectionConfidence,
                minFacePresenceConfidence: minimumPresenceConfidence,
                minTrackingConfidence: 0.5f,
                outputFaceBlendshapes: false,
                outputFaceTransformationMatrixes: true);
            faceLandmarker = FaceLandmarker.CreateFromOptions(options);
            activeRunningMode = runningMode;
            return true;
        }

        private void EnsureTextureFrame(int width, int height)
        {
            if (textureFrame != null && textureFrame.width == width && textureFrame.height == height)
            {
                return;
            }

            if (textureFrame != null)
            {
                textureFrame.Dispose();
            }

            textureFrame = new TextureFrame(width, height, TextureFormat.RGBA32);
        }

        private void StoreResult(FaceLandmarkerResult result, int imageWidth, int imageHeight)
        {
            if (result.faceLandmarks == null || result.faceLandmarks.Count == 0)
            {
                SetEmptyFrame(imageWidth, imageHeight, "当前画面未检测到人脸，相关数值输出 NaN。");
                return;
            }

            List<Mediapipe.Tasks.Components.Containers.NormalizedLandmark> sourceLandmarks = result.faceLandmarks[0].landmarks;
            Vector3[] landmarks = new Vector3[sourceLandmarks.Count];
            for (int index = 0; index < landmarks.Length; index++)
            {
                var landmark = sourceLandmarks[index];
                landmarks[index] = new Vector3(landmark.x, landmark.y, landmark.z);
            }

            float rawForeheadY = landmarks.Length > MediaPipeFaceLandmarkIndices.Chin
                ? landmarks[MediaPipeFaceLandmarkIndices.ForeheadCenter].y
                : float.NaN;
            float rawChinY = landmarks.Length > MediaPipeFaceLandmarkIndices.Chin
                ? landmarks[MediaPipeFaceLandmarkIndices.Chin].y
                : float.NaN;
            bool correctedVerticalOrientation = autoCorrectVerticalLandmarkOrientation
                && landmarks.Length > MediaPipeFaceLandmarkIndices.Chin
                && rawForeheadY > rawChinY;
            if (correctedVerticalOrientation)
            {
                for (int index = 0; index < landmarks.Length; index++)
                {
                    landmarks[index].y = 1f - landmarks[index].y;
                }
            }

            bool hasPose = result.facialTransformationMatrixes != null && result.facialTransformationMatrixes.Count > 0;
            Vector3 poseEuler = hasPose
                ? NormalizeEuler(result.facialTransformationMatrixes[0].rotation.eulerAngles)
                : NaNVector();
            latestFrame = new FaceLandmarkFrame(landmarks, imageWidth, imageHeight, 0f, hasPose, poseEuler, false);
            hasProcessedFrame = true;
            lastStatus = "已检测到 " + landmarks.Length + " 个人脸关键点。"
                + (correctedVerticalOrientation ? " 已自动修正纵向纹理方向。" : string.Empty);
            if (!loggedLandmarkOrientation)
            {
                loggedLandmarkOrientation = true;
                Debug.Log(
                    "Face landmark input verified: flipY=true, foreheadY="
                    + rawForeheadY.ToString("0.000")
                    + ", chinY=" + rawChinY.ToString("0.000")
                    + ", postCorrection=" + correctedVerticalOrientation,
                    this);
            }
        }

        private void SetEmptyFrame(int imageWidth, int imageHeight, string status)
        {
            latestFrame = new FaceLandmarkFrame(
                Array.Empty<Vector3>(),
                Mathf.Max(1, imageWidth),
                Mathf.Max(1, imageHeight),
                0f,
                false,
                NaNVector(),
                false);
            hasProcessedFrame = true;
            lastStatus = status;
        }

        private long NextTimestampMilliseconds()
        {
            long timestamp = (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
            lastTimestampMilliseconds = Math.Max(lastTimestampMilliseconds + 1, timestamp);
            return lastTimestampMilliseconds;
        }

        private void CleanupInput()
        {
            if (webCamTexture != null)
            {
                if (webCamTexture.isPlaying)
                {
                    webCamTexture.Stop();
                }

                Destroy(webCamTexture);
                webCamTexture = null;
            }

            ReleaseRenderedTexture();
        }

        private void ReleaseRenderedTexture()
        {
            if (renderedTexture == null)
            {
                return;
            }

            renderedTexture.Release();
            Destroy(renderedTexture);
            renderedTexture = null;
        }

        private void DisposeMediaPipe()
        {
            if (faceLandmarker != null)
            {
                ((IDisposable)faceLandmarker).Dispose();
                faceLandmarker = null;
            }

            if (textureFrame != null)
            {
                textureFrame.Dispose();
                textureFrame = null;
            }
        }

        private static Vector3 NormalizeEuler(Vector3 euler)
        {
            return new Vector3(NormalizeAngle(euler.x), NormalizeAngle(euler.y), NormalizeAngle(euler.z));
        }

        private static float NormalizeAngle(float angle)
        {
            return angle > 180f ? angle - 360f : angle;
        }

        private static Vector3 NaNVector()
        {
            return new Vector3(float.NaN, float.NaN, float.NaN);
        }
    }
}
