namespace FaceAnalysis
{
    public static class MediaPipeFaceLandmarkIndices
    {
        public const int MinimumLandmarkCount = 468;

        public const int ForeheadCenter = 10;
        public const int Chin = 152;
        public const int LeftCheek = 234;
        public const int RightCheek = 454;

        // Image-space naming avoids confusing the subject's anatomical left/right with
        // a mirrored webcam preview. The provider can swap these later when required.
        public const int ImageLeftEyeOuter = 33;
        public const int ImageLeftEyeInner = 133;
        public const int ImageRightEyeInner = 362;
        public const int ImageRightEyeOuter = 263;

        public const int NoseTip = 4;
        public const int NoseLeftWing = 98;
        public const int NoseRightWing = 327;

        public const int MouthLeftCorner = 61;
        public const int MouthRightCorner = 291;
        public const int UpperLipCenter = 13;
        public const int LowerLipCenter = 14;
    }
}
