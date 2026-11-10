using UnityEngine;

namespace FaceAnalysis
{
    public struct FaceMeasurementResult
    {
        public bool IsValid;
        public bool HasFace;
        public int LandmarkCount;
        public float DetectionConfidence;
        public bool HasDetectionConfidence;

        public float FaceWidthPixels;
        public float FaceHeightPixels;
        public float FaceWidthHeightRatio;

        public float OuterEyeDistancePixels;
        public float InnerEyeDistancePixels;
        public float ImageLeftEyeWidthPixels;
        public float ImageRightEyeWidthPixels;
        public float NoseWidthPixels;
        public float MouthWidthPixels;
        public float MouthOpeningPixels;

        public float OuterEyeDistanceToFaceWidth;
        public float InnerEyeDistanceToFaceWidth;
        public float ImageLeftEyeWidthToFaceWidth;
        public float ImageRightEyeWidthToFaceWidth;
        public float NoseWidthToFaceWidth;
        public float MouthWidthToFaceWidth;
        public float MouthOpeningToFaceHeight;

        public float EyeWidthDifferenceRatio;
        public float MouthCornerHeightDifferenceToFaceWidth;
        public float NoseCenterOffsetToFaceWidth;
        public float MouthCenterOffsetToFaceWidth;
        public float EyeLineRollDegrees;

        public bool HasHeadPose;
        public Vector3 HeadEulerAngles;
    }
}
