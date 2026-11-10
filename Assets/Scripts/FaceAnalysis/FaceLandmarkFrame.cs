using System;
using UnityEngine;

namespace FaceAnalysis
{
    public interface IFaceLandmarkSource
    {
        bool TryGetLatestFrame(out FaceLandmarkFrame frame);
    }

    [Serializable]
    public sealed class FaceLandmarkFrame
    {
        public FaceLandmarkFrame(
            Vector3[] normalizedLandmarks,
            int imageWidth,
            int imageHeight,
            float detectionConfidence,
            bool hasHeadPose = false,
            Vector3 headEulerAngles = default(Vector3),
            bool hasDetectionConfidence = true)
        {
            NormalizedLandmarks = normalizedLandmarks ?? Array.Empty<Vector3>();
            ImageWidth = Mathf.Max(1, imageWidth);
            ImageHeight = Mathf.Max(1, imageHeight);
            HasDetectionConfidence = hasDetectionConfidence;
            DetectionConfidence = hasDetectionConfidence
                ? Mathf.Clamp01(detectionConfidence)
                : 0f;
            HasHeadPose = hasHeadPose;
            HeadEulerAngles = headEulerAngles;
        }

        public Vector3[] NormalizedLandmarks { get; }
        public int ImageWidth { get; }
        public int ImageHeight { get; }
        public float DetectionConfidence { get; }
        public bool HasDetectionConfidence { get; }
        public bool HasHeadPose { get; }
        public Vector3 HeadEulerAngles { get; }

        public bool IsValid
        {
            get
            {
                return NormalizedLandmarks != null
                    && NormalizedLandmarks.Length >= MediaPipeFaceLandmarkIndices.MinimumLandmarkCount;
            }
        }
    }
}
