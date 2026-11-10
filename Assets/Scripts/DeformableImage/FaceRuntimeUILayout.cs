using UnityEngine;

namespace DeformableImage
{
    /// <summary>Shared side docks for the face prototype. Other scenes retain their original layout.</summary>
    public static class FaceRuntimeUILayout
    {
        public static bool Enabled { get; set; }
        public static int LeftTab { get; private set; }
        public static int RightTab { get; private set; }
        public static bool Left(int tab) { return !Enabled || LeftTab == tab; }
        public static bool Right(int tab) { return !Enabled || RightTab == tab; }
        public static float Width { get { return Mathf.Max(140f, Mathf.Min(330f, (Screen.width - 36f) * 0.27f)); } }
        public static Rect Panel(bool right, float preferredHeight)
        {
            return new Rect(right ? Screen.width - Width - 12f : 12f, 54f,
                Width, Mathf.Max(40f, Mathf.Min(preferredHeight, Screen.height - 66f)));
        }
        public static void DrawNavigation()
        {
            var style = new GUIStyle(GUI.skin.button) { fontSize = 12, wordWrap = true };
            LeftTab = GUI.Toolbar(new Rect(12f, 12f, Width, 34f), LeftTab,
                new[] { "面部数据", "操作工具", "表情" }, style);
            RightTab = GUI.Toolbar(new Rect(Screen.width - Width - 12f, 12f, Width, 34f), RightTab,
                new[] { "术前标记", "照片", "摄像头" }, style);
        }
    }
}
