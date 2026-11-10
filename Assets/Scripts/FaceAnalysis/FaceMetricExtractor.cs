using UnityEngine;

namespace FaceAnalysis
{
    public static class FaceMetricExtractor
    {
        private const float MinimumFaceSizePixels = 2f;

        public static bool TryExtract(FaceLandmarkFrame frame, out FaceMeasurementResult result)
        {
            result = CreateNaNResult();
            if (frame == null)
            {
                return false;
            }

            Vector3[] landmarks = frame.NormalizedLandmarks;
            result.IsValid = true;
            result.HasFace = landmarks != null && landmarks.Length > 0;
            result.LandmarkCount = landmarks != null ? landmarks.Length : 0;
            result.HasDetectionConfidence = frame.HasDetectionConfidence;
            result.DetectionConfidence = frame.HasDetectionConfidence
                ? frame.DetectionConfidence
                : float.NaN;
            result.HasHeadPose = frame.HasHeadPose && IsFinite(frame.HeadEulerAngles);
            result.HeadEulerAngles = result.HasHeadPose
                ? frame.HeadEulerAngles
                : NaNVector();

            bool hasForehead = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.ForeheadCenter, frame, out Vector2 forehead);
            bool hasChin = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.Chin, frame, out Vector2 chin);
            bool hasLeftCheek = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.LeftCheek, frame, out Vector2 leftCheek);
            bool hasRightCheek = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.RightCheek, frame, out Vector2 rightCheek);

            float faceWidth = DistanceOrNaN(hasLeftCheek, leftCheek, hasRightCheek, rightCheek);
            float faceHeight = DistanceOrNaN(hasForehead, forehead, hasChin, chin);
            if (IsFinite(faceWidth) && faceWidth < MinimumFaceSizePixels)
            {
                faceWidth = float.NaN;
            }
            if (IsFinite(faceHeight) && faceHeight < MinimumFaceSizePixels)
            {
                faceHeight = float.NaN;
            }

            result.FaceWidthPixels = faceWidth;
            result.FaceHeightPixels = faceHeight;
            result.FaceWidthHeightRatio = DivideOrNaN(faceWidth, faceHeight);

            bool hasLeftEyeOuter = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.ImageLeftEyeOuter, frame, out Vector2 leftEyeOuter);
            bool hasLeftEyeInner = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.ImageLeftEyeInner, frame, out Vector2 leftEyeInner);
            bool hasRightEyeInner = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.ImageRightEyeInner, frame, out Vector2 rightEyeInner);
            bool hasRightEyeOuter = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.ImageRightEyeOuter, frame, out Vector2 rightEyeOuter);

            float outerEyeDistance = DistanceOrNaN(hasLeftEyeOuter, leftEyeOuter, hasRightEyeOuter, rightEyeOuter);
            float innerEyeDistance = DistanceOrNaN(hasLeftEyeInner, leftEyeInner, hasRightEyeInner, rightEyeInner);
            float leftEyeWidth = DistanceOrNaN(hasLeftEyeOuter, leftEyeOuter, hasLeftEyeInner, leftEyeInner);
            float rightEyeWidth = DistanceOrNaN(hasRightEyeInner, rightEyeInner, hasRightEyeOuter, rightEyeOuter);
            result.OuterEyeDistancePixels = outerEyeDistance;
            result.InnerEyeDistancePixels = innerEyeDistance;
            result.ImageLeftEyeWidthPixels = leftEyeWidth;
            result.ImageRightEyeWidthPixels = rightEyeWidth;
            result.OuterEyeDistanceToFaceWidth = DivideOrNaN(outerEyeDistance, faceWidth);
            result.InnerEyeDistanceToFaceWidth = DivideOrNaN(innerEyeDistance, faceWidth);
            result.ImageLeftEyeWidthToFaceWidth = DivideOrNaN(leftEyeWidth, faceWidth);
            result.ImageRightEyeWidthToFaceWidth = DivideOrNaN(rightEyeWidth, faceWidth);

            if (IsFinite(leftEyeWidth) && IsFinite(rightEyeWidth))
            {
                float averageEyeWidth = (leftEyeWidth + rightEyeWidth) * 0.5f;
                result.EyeWidthDifferenceRatio = averageEyeWidth > 0.0001f
                    ? Mathf.Abs(leftEyeWidth - rightEyeWidth) / averageEyeWidth
                    : float.NaN;
            }

            bool hasLeftEyeCenter = hasLeftEyeOuter && hasLeftEyeInner;
            bool hasRightEyeCenter = hasRightEyeInner && hasRightEyeOuter;
            if (hasLeftEyeCenter && hasRightEyeCenter)
            {
                Vector2 leftEyeCenter = (leftEyeOuter + leftEyeInner) * 0.5f;
                Vector2 rightEyeCenter = (rightEyeInner + rightEyeOuter) * 0.5f;
                result.EyeLineRollDegrees = Mathf.Atan2(
                    rightEyeCenter.y - leftEyeCenter.y,
                    rightEyeCenter.x - leftEyeCenter.x) * Mathf.Rad2Deg;
            }

            bool hasNoseLeft = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.NoseLeftWing, frame, out Vector2 noseLeft);
            bool hasNoseRight = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.NoseRightWing, frame, out Vector2 noseRight);
            bool hasNoseTip = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.NoseTip, frame, out Vector2 noseTip);
            float noseWidth = DistanceOrNaN(hasNoseLeft, noseLeft, hasNoseRight, noseRight);
            result.NoseWidthPixels = noseWidth;
            result.NoseWidthToFaceWidth = DivideOrNaN(noseWidth, faceWidth);

            bool hasMouthLeft = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.MouthLeftCorner, frame, out Vector2 mouthLeft);
            bool hasMouthRight = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.MouthRightCorner, frame, out Vector2 mouthRight);
            bool hasUpperLip = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.UpperLipCenter, frame, out Vector2 upperLip);
            bool hasLowerLip = TryPoint(landmarks, MediaPipeFaceLandmarkIndices.LowerLipCenter, frame, out Vector2 lowerLip);
            float mouthWidth = DistanceOrNaN(hasMouthLeft, mouthLeft, hasMouthRight, mouthRight);
            float mouthOpening = DistanceOrNaN(hasUpperLip, upperLip, hasLowerLip, lowerLip);
            result.MouthWidthPixels = mouthWidth;
            result.MouthOpeningPixels = mouthOpening;
            result.MouthWidthToFaceWidth = DivideOrNaN(mouthWidth, faceWidth);
            result.MouthOpeningToFaceHeight = DivideOrNaN(mouthOpening, faceHeight);
            if (hasMouthLeft && hasMouthRight)
            {
                result.MouthCornerHeightDifferenceToFaceWidth = DivideOrNaN(
                    Mathf.Abs(mouthLeft.y - mouthRight.y),
                    faceWidth);
            }

            bool hasMidline = hasForehead && hasChin
                && Vector2.Distance(forehead, chin) > 0.0001f;
            if (hasMidline && hasNoseTip)
            {
                result.NoseCenterOffsetToFaceWidth = DivideOrNaN(
                    SignedDistanceToLine(noseTip, forehead, chin),
                    faceWidth);
            }
            if (hasMidline && hasMouthLeft && hasMouthRight)
            {
                Vector2 mouthCenter = (mouthLeft + mouthRight) * 0.5f;
                result.MouthCenterOffsetToFaceWidth = DivideOrNaN(
                    SignedDistanceToLine(mouthCenter, forehead, chin),
                    faceWidth);
            }

            return true;
        }

        private static FaceMeasurementResult CreateNaNResult()
        {
            float nan = float.NaN;
            return new FaceMeasurementResult
            {
                IsValid = false,
                HasFace = false,
                LandmarkCount = 0,
                DetectionConfidence = nan,
                HasDetectionConfidence = false,
                FaceWidthPixels = nan,
                FaceHeightPixels = nan,
                FaceWidthHeightRatio = nan,
                OuterEyeDistancePixels = nan,
                InnerEyeDistancePixels = nan,
                ImageLeftEyeWidthPixels = nan,
                ImageRightEyeWidthPixels = nan,
                NoseWidthPixels = nan,
                MouthWidthPixels = nan,
                MouthOpeningPixels = nan,
                OuterEyeDistanceToFaceWidth = nan,
                InnerEyeDistanceToFaceWidth = nan,
                ImageLeftEyeWidthToFaceWidth = nan,
                ImageRightEyeWidthToFaceWidth = nan,
                NoseWidthToFaceWidth = nan,
                MouthWidthToFaceWidth = nan,
                MouthOpeningToFaceHeight = nan,
                EyeWidthDifferenceRatio = nan,
                MouthCornerHeightDifferenceToFaceWidth = nan,
                NoseCenterOffsetToFaceWidth = nan,
                MouthCenterOffsetToFaceWidth = nan,
                EyeLineRollDegrees = nan,
                HasHeadPose = false,
                HeadEulerAngles = NaNVector()
            };
        }

        private static bool TryPoint(Vector3[] landmarks, int index, FaceLandmarkFrame frame, out Vector2 point)
        {
            point = default(Vector2);
            if (landmarks == null || index < 0 || index >= landmarks.Length)
            {
                return false;
            }

            Vector3 normalized = landmarks[index];
            if (!IsFinite(normalized))
            {
                return false;
            }

            point = new Vector2(normalized.x * frame.ImageWidth, (1f - normalized.y) * frame.ImageHeight);
            return IsFinite(point.x) && IsFinite(point.y);
        }

        private static float DistanceOrNaN(bool hasA, Vector2 a, bool hasB, Vector2 b)
        {
            return hasA && hasB ? Vector2.Distance(a, b) : float.NaN;
        }

        private static float DivideOrNaN(float numerator, float denominator)
        {
            return IsFinite(numerator) && IsFinite(denominator) && Mathf.Abs(denominator) > 0.0001f
                ? numerator / denominator
                : float.NaN;
        }

        private static float SignedDistanceToLine(Vector2 point, Vector2 lineStart, Vector2 lineEnd)
        {
            Vector2 direction = lineEnd - lineStart;
            float length = direction.magnitude;
            if (length < 0.0001f)
            {
                return float.NaN;
            }

            return (direction.x * (point.y - lineStart.y)
                - direction.y * (point.x - lineStart.x)) / length;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static Vector3 NaNVector()
        {
            return new Vector3(float.NaN, float.NaN, float.NaN);
        }
    }
}
