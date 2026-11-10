using System.Text;

namespace FaceAnalysis
{
    public static class FaceMeasurementFormatter
    {
        public static string Format(FaceMeasurementResult value)
        {
            if (!value.IsValid)
            {
                return "未获得有效的人脸基础数据。";
            }

            StringBuilder text = new StringBuilder(768);
            text.AppendLine("人脸基础测量（不包含综合评分）");
            text.AppendLine();
            text.Append("人脸状态：").AppendLine(value.HasFace ? "已检测" : "未检测");
            text.Append("关键点数量：").AppendLine(value.LandmarkCount.ToString());
            if (value.HasDetectionConfidence)
            {
                text.Append("检测置信度：").Append((value.DetectionConfidence * 100f).ToString("0.0")).AppendLine("%");
            }
            else
            {
                text.AppendLine("检测置信度：NaN");
            }
            text.Append("脸宽：").Append(value.FaceWidthPixels.ToString("0.0")).AppendLine(" px");
            text.Append("脸高：").Append(value.FaceHeightPixels.ToString("0.0")).AppendLine(" px");
            text.Append("脸宽/脸高：").AppendLine(value.FaceWidthHeightRatio.ToString("0.000"));
            text.AppendLine();
            text.Append("双眼外侧距离：").Append(value.OuterEyeDistancePixels.ToString("0.0"))
                .Append(" px（脸宽的 ").Append((value.OuterEyeDistanceToFaceWidth * 100f).ToString("0.0")).AppendLine("%）");
            text.Append("双眼内侧距离：").Append(value.InnerEyeDistancePixels.ToString("0.0"))
                .Append(" px（脸宽的 ").Append((value.InnerEyeDistanceToFaceWidth * 100f).ToString("0.0")).AppendLine("%）");
            text.Append("画面左眼宽：").Append(value.ImageLeftEyeWidthPixels.ToString("0.0"))
                .Append(" px（").Append((value.ImageLeftEyeWidthToFaceWidth * 100f).ToString("0.0")).AppendLine("%）");
            text.Append("画面右眼宽：").Append(value.ImageRightEyeWidthPixels.ToString("0.0"))
                .Append(" px（").Append((value.ImageRightEyeWidthToFaceWidth * 100f).ToString("0.0")).AppendLine("%）");
            text.Append("画面左右眼宽差：").Append((value.EyeWidthDifferenceRatio * 100f).ToString("0.0")).AppendLine("%");
            text.Append("眼线倾斜：").Append(value.EyeLineRollDegrees.ToString("+0.00;-0.00;0.00")).AppendLine("°");
            text.AppendLine();
            text.Append("鼻翼宽：").Append(value.NoseWidthPixels.ToString("0.0"))
                .Append(" px（脸宽的 ").Append((value.NoseWidthToFaceWidth * 100f).ToString("0.0")).AppendLine("%）");
            text.Append("嘴宽：").Append(value.MouthWidthPixels.ToString("0.0"))
                .Append(" px（脸宽的 ").Append((value.MouthWidthToFaceWidth * 100f).ToString("0.0")).AppendLine("%）");
            text.Append("嘴部开合：").Append(value.MouthOpeningPixels.ToString("0.0"))
                .Append(" px（脸高的 ").Append((value.MouthOpeningToFaceHeight * 100f).ToString("0.00")).AppendLine("%）");
            text.Append("嘴角高度差/脸宽：").Append((value.MouthCornerHeightDifferenceToFaceWidth * 100f).ToString("0.00")).AppendLine("%");
            text.Append("鼻尖中线偏移/脸宽：").Append((value.NoseCenterOffsetToFaceWidth * 100f).ToString("+0.00;-0.00;0.00")).AppendLine("%");
            text.Append("嘴部中线偏移/脸宽：").Append((value.MouthCenterOffsetToFaceWidth * 100f).ToString("+0.00;-0.00;0.00")).AppendLine("%");

            if (value.HasHeadPose)
            {
                text.AppendLine();
                text.Append("头部俯仰 Pitch：").Append(value.HeadEulerAngles.x.ToString("+0.0;-0.0;0.0")).AppendLine("°");
                text.Append("头部偏航 Yaw：").Append(value.HeadEulerAngles.y.ToString("+0.0;-0.0;0.0")).AppendLine("°");
                text.Append("头部翻滚 Roll：").Append(value.HeadEulerAngles.z.ToString("+0.0;-0.0;0.0")).AppendLine("°");
            }
            else
            {
                text.AppendLine();
                text.AppendLine("头部俯仰 Pitch：NaN");
                text.AppendLine("头部偏航 Yaw：NaN");
                text.AppendLine("头部翻滚 Roll：NaN");
            }

            return text.ToString();
        }
    }
}
