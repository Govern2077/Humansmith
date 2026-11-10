using UnityEditor;
using UnityEngine;

namespace DeformableImage.Editor
{
    [CustomEditor(typeof(SoftTissueLayerCoupler2D))]
    public sealed class SoftTissueLayerCoupler2DEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            SoftTissueLayerCoupler2D coupler = (SoftTissueLayerCoupler2D)target;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Links exactly two adjacent soft-tissue layers. Build the complete chain as "
                + "Muscle -> Fat and Fat -> Skin; do not skip a layer.",
                MessageType.Info);

            if (GUILayout.Button("Bind Adjacent Tissue Layers"))
            {
                coupler.BindRestPose();
            }

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Apply Current Source Shape"))
                {
                    coupler.ApplyCurrentSourceShape();
                }
            }

            EditorGUILayout.HelpBox(coupler.LastStatus, MessageType.None);
        }
    }
}
