using TMPro;
using UnityEngine;

namespace FaceAnalysis
{
    [DisallowMultipleComponent]
    public sealed class FaceMeasurementController : MonoBehaviour
    {
        [Tooltip("Assign a MonoBehaviour that implements IFaceLandmarkSource.")]
        [SerializeField] private MonoBehaviour landmarkSourceComponent;
        [Tooltip("Measure the landmarks after the skin mesh has been dragged or changed by bones.")]
        [SerializeField] private bool measureDeformedSurface = true;
        [SerializeField] private FaceLandmarkVisualizer deformedLandmarkVisualizer;
        [SerializeField] private TMP_Text outputText;
        [SerializeField, Min(0.02f)] private float refreshInterval = 0.05f;

        private IFaceLandmarkSource landmarkSource;
        private float nextRefreshTime;

        public string CurrentText { get; private set; } = string.Empty;

        private void Awake()
        {
            ResolveSource();
            ResolveDeformedVisualizer();
            ShowWaitingState();
        }

        private void OnValidate()
        {
            refreshInterval = Mathf.Max(0.02f, refreshInterval);
            ResolveSource();
            ResolveDeformedVisualizer();
        }

        private void Update()
        {
            if (Time.unscaledTime < nextRefreshTime)
            {
                return;
            }

            nextRefreshTime = Time.unscaledTime + refreshInterval;
            if (landmarkSource == null)
            {
                ResolveSource();
            }

            FaceLandmarkFrame frame = null;
            bool usingDeformedFrame = measureDeformedSurface
                && deformedLandmarkVisualizer != null
                && deformedLandmarkVisualizer.TryGetCurrentDeformedFrame(out frame);
            bool hasFrame = usingDeformedFrame;
            if (!hasFrame && landmarkSource != null)
            {
                hasFrame = landmarkSource.TryGetLatestFrame(out frame);
            }

            if (!hasFrame || frame == null)
            {
                ShowWaitingState();
                return;
            }

            if (!FaceMetricExtractor.TryExtract(frame, out FaceMeasurementResult result))
            {
                SetText("检测到了输入，但关键点数量或脸部尺寸无效。");
                return;
            }

            string formatted = FaceMeasurementFormatter.Format(result);
            formatted += usingDeformedFrame
                ? "\n测量来源：当前变形皮肤 Mesh"
                : "\n测量来源：MediaPipe 原始图像";
            if (landmarkSourceComponent is MediaPipeStaticFaceLandmarkSource mediaPipeSource)
            {
                formatted += "\nMediaPipe 状态：" + mediaPipeSource.LastStatus;
            }

            SetText(formatted);
        }

        public void Configure(MonoBehaviour source, TMP_Text textTarget)
        {
            landmarkSourceComponent = source;
            outputText = textTarget;
            ResolveSource();
            ShowWaitingState();
        }

        private void ResolveSource()
        {
            landmarkSource = landmarkSourceComponent as IFaceLandmarkSource;
        }

        private void ResolveDeformedVisualizer()
        {
            if (deformedLandmarkVisualizer == null)
            {
                deformedLandmarkVisualizer = GetComponent<FaceLandmarkVisualizer>();
            }
        }

        private void ShowWaitingState()
        {
            string message = landmarkSourceComponent == null
                ? "基础测量模块已就绪。下一步需要接入人脸关键点来源。"
                : landmarkSource == null
                    ? "指定的组件没有实现 IFaceLandmarkSource。"
                    : BuildWaitingMessage();
            SetText(message);
        }

        private string BuildWaitingMessage()
        {
            MediaPipeStaticFaceLandmarkSource mediaPipeSource =
                landmarkSourceComponent as MediaPipeStaticFaceLandmarkSource;
            return mediaPipeSource == null
                ? "等待检测到一张有效人脸……"
                : "等待检测到一张有效人脸……\nMediaPipe 状态：" + mediaPipeSource.LastStatus;
        }

        private void SetText(string value)
        {
            CurrentText = value ?? string.Empty;
            if (outputText != null)
            {
                outputText.text = CurrentText;
            }
        }
    }
}
