using UnityEngine;

namespace FaceAnalysis
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(FaceMeasurementController))]
    public sealed class FaceMeasurementRuntimeOverlay : MonoBehaviour
    {
        [SerializeField] private FaceMeasurementController controller;
        [SerializeField] private bool visible = true;
        [SerializeField] private KeyCode toggleKey = KeyCode.F6;
        [SerializeField, Min(280f)] private float panelWidth = 470f;
        [SerializeField, Min(240f)] private float panelHeight = 660f;
        [SerializeField, Range(11, 28)] private int fontSize = 16;

        private Vector2 scrollPosition;
        private GUIStyle bodyStyle;

        private void Awake()
        {
            if (controller == null)
            {
                controller = GetComponent<FaceMeasurementController>();
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                visible = !visible;
            }
        }

        private void OnGUI()
        {
            if (!visible || controller == null)
            {
                return;
            }

            bodyStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                wordWrap = true,
                richText = false,
                alignment = TextAnchor.UpperLeft
            };

            float width = Mathf.Min(panelWidth, Screen.width - 24f);
            float height = Mathf.Min(panelHeight, Screen.height - 24f);
            Rect panel = new Rect(12f, 12f, width, height);
            GUI.Box(panel, "");
            GUILayout.BeginArea(new Rect(panel.x + 12f, panel.y + 10f, panel.width - 24f, panel.height - 20f));
            GUILayout.Label("基础面部数据（F6 隐藏/显示）", new GUIStyle(bodyStyle) { fontStyle = FontStyle.Bold });
            GUILayout.Space(6f);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);
            GUILayout.Label(controller.CurrentText, bodyStyle);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
